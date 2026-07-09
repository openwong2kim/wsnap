// wsnap — macOS-style screen capture for Windows.
// Copyright (C) 2026 openwong2kim and wsnap contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License version 3, as published
// by the Free Software Foundation.
//
// This program is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
// or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License
// for more details. You should have received a copy of the GNU General
// Public License along with this program. If not, see
// <https://www.gnu.org/licenses/>.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Wsnap.Control;

namespace Wsnap;

/// <summary>
/// The tray App is the resident host: it lends the command bus its interactive/recording/UI
/// abilities (overlay drag, GIF/video/scroll, thumbnails). Headless commands go straight to
/// <see cref="CaptureCore"/>; only what genuinely needs WPF/STA lands here. All entry points
/// marshal onto the dispatcher, so an incoming pipe/MCP request runs on the UI thread safely.
/// </summary>
public partial class App : IResidentHost
{
    public bool IsResident => true;

    // Active recordings keyed by a short id, so stop_recording / gif stop can target them and
    // an until_stop recording can be ended out-of-band by CLI/MCP.
    private sealed class GifSession
    {
        public GifRecorder Rec = null!;
        public string? Path;
        public int W, H;
        public TaskCompletionSource<CommandResult>? StopTcs;
    }
    private sealed class VideoSession
    {
        public VideoRecorder Rec = null!;
        public int W, H;
        public TaskCompletionSource<CommandResult>? StopTcs;
    }
    private readonly Dictionary<string, GifSession> _gifs = new();
    private readonly Dictionary<string, VideoSession> _videos = new();

    /// <summary>Attach the normal capture UX (auto-copy + floating thumbnail + memory trim) to a
    /// headless capture that ran while the tray app is up, so delegated captures behave like native ones.</summary>
    public void PresentCapture(string path)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (Settings.Current.AutoCopyOnCapture) ImageClipboard.CopyImageFile(path);
                new ThumbnailWindow(path).Show();
                ScheduleTrim();
            }
            catch (Exception ex) { CrashLog.Write("present-capture", ex); }
        });
    }

    /// <summary>Run an interactive/recording/UI command on the UI thread and await its completion
    /// (drag end, recording stop, etc.), flattening the inner task so the caller gets one await.</summary>
    public Task<CommandResult> ExecuteInteractiveAsync(WsnapCommand cmd, CancellationToken ct)
        => Dispatcher.InvokeAsync(() => Route(cmd, ct)).Task.Unwrap();

    private Task<CommandResult> Route(WsnapCommand cmd, CancellationToken ct) => cmd.Kind switch
    {
        CommandKind.CaptureInteractive => InteractiveCapture(cmd),
        CommandKind.OcrInteractive     => InteractiveOcr(),
        CommandKind.ColorPick          => InteractiveColor(),
        CommandKind.CaptureRepeat      => Task.FromResult(RepeatRegion()),
        CommandKind.CaptureDelayed     => DelayedThenCapture(Math.Clamp(ArgReader.Int(cmd.Args, "seconds", 3), 1, 60)),
        CommandKind.Gif                => StartGif(cmd),
        CommandKind.GifStop            => StopRecording(ArgReader.Str(cmd.Args, "recording_id")),
        CommandKind.Video              => StartVideo(cmd),
        CommandKind.Scroll             => StartScroll(cmd),
        CommandKind.ShowHistory        => Ack(() => HistoryWindow.ShowSingleton()),
        CommandKind.ClearThumbnails    => Ack(() => ThumbnailWindow.ClearAll()),
        CommandKind.OpenSettings       => Ack(() => SettingsWindow.ShowSingleton(ApplyRuntime)),
        CommandKind.SettingsSet        => Task.FromResult(ApplySetting(cmd)),
        _                              => Task.FromResult(CommandResult.Fail("unknown_cmd", cmd.Kind.ToString()))
    };

    private static Task<CommandResult> Ack(Action act)
    {
        try { act(); return Task.FromResult(CommandResult.Ack()); }
        catch (Exception ex) { CrashLog.Write("resident-ui", ex); return Task.FromResult(CommandResult.Fail("internal", ex.Message)); }
    }

    // ---------------- interactive capture / ocr / color ----------------

    private Task<CommandResult> InteractiveCapture(WsnapCommand cmd)
    {
        string mode = ArgReader.Str(cmd.Args, "mode", "region") ?? "region";
        if (mode == "ocr") return InteractiveOcr();
        if (mode == "color") return InteractiveColor();
        if (_overlayOpen) return Task.FromResult(CommandResult.Fail("busy", "an overlay is already open"));

        _overlayOpen = true;
        var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlay = new CaptureOverlay(CaptureMode.Capture) { NameCtx = CaptureCore.ForegroundContext() };
        overlay.Closed += (_, _) =>
        {
            _overlayOpen = false;
            try
            {
                RouteCapture(overlay);   // keep the native toolbar/thumbnail behaviour
                string? p = overlay.ResultPath;
                tcs.TrySetResult(p != null
                    ? CommandResult.FileSaved(p, overlay.RegionPx?.Width ?? 0, overlay.RegionPx?.Height ?? 0)
                    : CommandResult.Fail("cancelled", "capture cancelled"));
            }
            catch (Exception ex) { CrashLog.Write("interactive-capture", ex); tcs.TrySetResult(CommandResult.Fail("internal", ex.Message)); }
        };
        overlay.Show(); overlay.Activate();
        return tcs.Task;
    }

    private Task<CommandResult> InteractiveOcr()
    {
        if (_overlayOpen) return Task.FromResult(CommandResult.Fail("busy", "an overlay is already open"));
        _overlayOpen = true;
        var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlay = new CaptureOverlay(CaptureMode.OcrText);
        overlay.Closed += async (_, _) =>
        {
            _overlayOpen = false;
            var bmp = overlay.ResultBitmap;
            if (bmp == null) { tcs.TrySetResult(CommandResult.Fail("cancelled", "capture cancelled")); return; }
            try
            {
                string? text = await Ocr.RecognizeAsync(bmp);
                tcs.TrySetResult(text == null
                    ? CommandResult.Fail("ocr_unavailable", "OCR engine unavailable")
                    : CommandResult.OcrText(text, Ocr.CurrentLanguage.Code));
            }
            catch (Exception ex) { CrashLog.Write("interactive-ocr", ex); tcs.TrySetResult(CommandResult.Fail("internal", ex.Message)); }
            finally { bmp.Dispose(); MemoryTrim.TrimNow(); }
        };
        overlay.Show(); overlay.Activate();
        return tcs.Task;
    }

    private Task<CommandResult> InteractiveColor()
    {
        if (_overlayOpen) return Task.FromResult(CommandResult.Fail("busy", "an overlay is already open"));
        _overlayOpen = true;
        var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlay = new CaptureOverlay(CaptureMode.ColorPick);
        // The color-pick overlay copies the HEX to the clipboard itself; we just report completion.
        overlay.Closed += (_, _) => { _overlayOpen = false; tcs.TrySetResult(CommandResult.Ack()); };
        overlay.Show(); overlay.Activate();
        return tcs.Task;
    }

    private CommandResult RepeatRegion()
    {
        if (CaptureOverlay.LastRegion is not { } r) return CommandResult.Fail("no_region", "no previous region to repeat");
        var res = CaptureCore.CaptureRegion(r.X, r.Y, r.Width, r.Height);
        if (res.Ok && res.Path != null) PresentCapture(res.Path);
        return res;
    }

    private async Task<CommandResult> DelayedThenCapture(int seconds)
    {
        for (int i = seconds; i > 0; i--) { Toast.Show(L.T("toast.countdown", i), 950); await Task.Delay(1000); }
        return await InteractiveCapture(new WsnapCommand(CommandKind.CaptureInteractive));
    }

    // ---------------- GIF (fixed duration or until_stop) ----------------

    private Task<CommandResult> StartGif(WsnapCommand cmd)
    {
        var (x, y, w, h) = ArgReader.Rect(cmd.Args);
        if (w < 2 || h < 2) return Task.FromResult(CommandResult.Fail("no_region", "gif needs a region of at least 2x2"));

        int fps = Math.Clamp(ArgReader.Int(cmd.Args, "fps", 12), 1, 30);
        double durArg = ArgReader.Double(cmd.Args, "duration_s", 5);
        string mode = ArgReader.Str(cmd.Args, "mode", durArg > 0 ? "fixed" : "until_stop") ?? "fixed";
        bool untilStop = mode == "until_stop";
        int maxSeconds = untilStop ? 30 : Math.Clamp((int)Math.Round(durArg), 1, 120);

        string id = Guid.NewGuid().ToString("N").Substring(0, 8);
        var s = new GifSession { W = w, H = h };
        var startTcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // showControl:true — the red "recording" badge stays as a visibility + click-to-kill signal
        // for external/agent-initiated recording (a privacy requirement, not just UI).
        s.Rec = new GifRecorder(new System.Windows.Int32Rect(x, y, w, h), p => s.Path = p, maxSeconds, showControl: true, fps: fps);
        s.Rec.Finished += () =>
        {
            _gifs.Remove(id);
            CommandResult res = s.Path != null
                ? CommandResult.RecordingSaved(s.Path, w, h, s.Rec.FrameCount, s.Rec.Seconds)
                : CommandResult.Fail("cancelled", "recording produced no frames");
            if (s.Path != null) PresentCapture(s.Path);
            s.StopTcs?.TrySetResult(res);
            if (!untilStop) startTcs.TrySetResult(res);
        };
        _gifs[id] = s; s.Rec.Start();

        return untilStop ? Task.FromResult(CommandResult.RecordingStarted(id)) : startTcs.Task;
    }

    // ---------------- video / scroll ----------------

    private Task<CommandResult> StartVideo(WsnapCommand cmd)
    {
        var (x, y, w, h) = ArgReader.Rect(cmd.Args);
        if (w < 2 || h < 2) return Task.FromResult(CommandResult.Fail("no_region", "video needs a region of at least 2x2"));
        if (!VideoRecorder.IsAvailable) return Task.FromResult(CommandResult.Fail("unavailable", "ffmpeg not available"));

        var fmt = (ArgReader.Str(cmd.Args, "format", "mp4") == "apng") ? VideoFormat.Apng : VideoFormat.Mp4;
        int? fps = ArgReader.HasProp(cmd.Args, "fps") ? ArgReader.Int(cmd.Args, "fps") : null;
        double dur = ArgReader.Double(cmd.Args, "duration_s", 0);
        bool fixedLen = dur > 0;

        string id = Guid.NewGuid().ToString("N").Substring(0, 8);
        var s = new VideoSession { W = w, H = h };
        var startTcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        s.Rec = new VideoRecorder(new System.Windows.Int32Rect(x, y, w, h), (path, poster) =>
        {
            _videos.Remove(id);
            try { new ThumbnailWindow(path, poster: poster).Show(); ScheduleTrim(); } catch (Exception ex) { CrashLog.Write("video-present", ex); }
            var res = CommandResult.RecordingSaved(path, w, h, 0, 0);
            s.StopTcs?.TrySetResult(res);
            if (fixedLen) startTcs.TrySetResult(res);
        }, fmt, fps);

        _videos[id] = s; s.Rec.Start();

        if (fixedLen)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(dur, 1, 300)) };
            t.Tick += (_, _) => { t.Stop(); try { s.Rec.Stop(); } catch { } };
            t.Start();
            return startTcs.Task;
        }
        return Task.FromResult(CommandResult.RecordingStarted(id));
    }

    private Task<CommandResult> StartScroll(WsnapCommand cmd)
    {
        var (x, y, w, h) = ArgReader.Rect(cmd.Args);
        if (w < 2 || h < 2) return Task.FromResult(CommandResult.Fail("no_region", "scroll needs a region of at least 2x2"));
        var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        new ScrollCapture(new System.Windows.Int32Rect(x, y, w, h), path =>
        {
            try { new ThumbnailWindow(path).Show(); } catch (Exception ex) { CrashLog.Write("scroll-present", ex); }
            tcs.TrySetResult(CommandResult.FileSaved(path, w, h));
        }).Start();
        return tcs.Task;
    }

    // ---------------- stop recording (gif or video) ----------------

    private Task<CommandResult> StopRecording(string? id)
    {
        if (id != null && _gifs.TryGetValue(id, out var g)) return StopGif(g);
        if (id != null && _videos.TryGetValue(id, out var v)) return StopVideo(v);
        if (id == null)
        {
            if (_gifs.Count == 1) return StopGif(_gifs.Values.First());
            if (_gifs.Count == 0 && _videos.Count == 1) return StopVideo(_videos.Values.First());
        }
        return Task.FromResult(CommandResult.Fail("not_found", "no matching active recording"));
    }

    private Task<CommandResult> StopGif(GifSession s)
    {
        s.StopTcs ??= new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Rec.StopExternal();   // Finished fires → completes StopTcs
        return s.StopTcs.Task;
    }

    private Task<CommandResult> StopVideo(VideoSession s)
    {
        s.StopTcs ??= new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try { s.Rec.Stop(); } catch (Exception ex) { CrashLog.Write("video-stop", ex); s.StopTcs.TrySetResult(CommandResult.Fail("internal", ex.Message)); }
        return s.StopTcs.Task;
    }

    // ---------------- settings write (allow-listed, audited by the gate) ----------------

    private CommandResult ApplySetting(WsnapCommand cmd)
    {
        string? key = ArgReader.Str(cmd.Args, "key");
        string? val = ArgReader.Str(cmd.Args, "value");
        if (string.IsNullOrWhiteSpace(key)) return CommandResult.Fail("no_region", "key required");
        try
        {
            var s = Settings.Current;
            switch (key)
            {
                case "SaveFolder": s.SaveFolder = val ?? s.SaveFolder; break;
                case "OcrLanguage": s.OcrLanguage = val ?? s.OcrLanguage; break;
                case "AutoCopyOnCapture": s.AutoCopyOnCapture = ParseBool(val, s.AutoCopyOnCapture); break;
                case "KeepHistory": s.KeepHistory = ParseBool(val, s.KeepHistory); break;
                case "ClipboardWatch": s.ClipboardWatch = ParseBool(val, s.ClipboardWatch); break;
                default: return CommandResult.Fail("denied", $"setting '{key}' is not writable via control");
            }
            s.Save();
            ApplyRuntime();
            return CommandResult.Ack(ResultType.Settings);
        }
        catch (Exception ex) { CrashLog.Write("settings-set", ex); return CommandResult.Fail("internal", ex.Message); }
    }

    private static bool ParseBool(string? v, bool def) => bool.TryParse(v, out var b) ? b : def;
}
