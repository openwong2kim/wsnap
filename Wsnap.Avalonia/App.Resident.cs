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
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using WinForms = System.Windows.Forms;

namespace Wsnap;

/// <summary>
/// Phase 6: the resident (tray) side of the Avalonia app — the port of the WPF App.cs. A bare
/// launch installs the hotkey hook, clipboard/folder watchers, the WinForms tray icon
/// (deliberately retained: NotifyIcon + ContextMenuStrip + TrayMenuTheme's owner-draw dark
/// theme have no Avalonia NativeMenu equivalent) and, when the user opted in, the control
/// pipe server. All of it shares ONE CommandRouter + ONE ControlGate with CLI/MCP/hotkeys.
/// </summary>
public partial class App
{
    internal static App? Instance => Avalonia.Application.Current as App;

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private HotkeyHook? _hook;
    private WinForms.NotifyIcon? _tray;
    private ClipboardWatcher? _clipboard;
    private FolderWatcher? _folderWatcher;
    private UpdateInfo? _update;

    // Control layer (v1.7): the tray instance hosts one shared bus + gate; the pipe server is
    // created only when the user opts into external control. See App.Control.cs for IResidentHost.
    private Wsnap.Control.ControlGate? _gate;
    private Wsnap.Control.CommandRouter? _router;
    private Wsnap.Control.PipeServer? _pipe;

    // --resident-demo: sandboxed resident run for the external verification harness — skip
    // anything that talks to the network (update check; telemetry is off via the sandboxed
    // Settings) and write a probe file once startup finished.
    private bool _residentDemo;

    /// <summary>SingleInstance signalled a second launch: re-running the exe = "take a shot".</summary>
    internal void OnSecondLaunch() => Dispatcher.UIThread.Post(StartCapture);

    /// <summary>
    /// Wire up the resident app. Runs DEFERRED (posted) so the dispatcher's loop is pumping and
    /// Avalonia's SynchronizationContext is installed first — HotkeyHook / FolderWatcher capture
    /// SynchronizationContext.Current for marshalling, and ClipboardWatcher's message-only
    /// window needs the pumping UI thread.
    /// </summary>
    private void StartResident(IClassicDesktopStyleApplicationLifetime desktop, bool demo)
    {
        _desktop = desktop;
        _residentDemo = demo;
        desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
        desktop.Exit += (_, _) => TearDownResident();

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                // Control layer: this tray instance is the resident host, so hotkey / tray / pipe /
                // MCP all share ONE CommandRouter + ONE ControlGate (single consent/rate-limit choke point).
                _gate = new Wsnap.Control.ControlGate();
                _router = new Wsnap.Control.CommandRouter(_gate, this);
                _gate.ScreenAccessSignalled += OnExternalScreenAccess;

                _hook = new HotkeyHook();
                _hook.Triggered += OnHotkey;
                _hook.Install();

                _clipboard = new ClipboardWatcher(OnClipboardImage);
                _clipboard.SetEnabled(Settings.Current.ClipboardWatch);

                _folderWatcher = new FolderWatcher();
                _folderWatcher.SetEnabled(Settings.Current.WatchFolderOcr);

                // The control pipe listener exists ONLY when the user opted in — off = zero attack surface.
                if (Settings.Current.ExternalControlEnabled) StartPipeServer();

                SetupTray();

                if (_hook.InstallFailed)
                    Toast.Show(L.T("toast.hookFailed"), 4000);
                else
                    CrashLog.Telemetry("startup");

                StartMemoryTrimming();
                if (!_residentDemo) ScheduleUpdateCheck();
                PrewarmRenderPipeline();

                if (_residentDemo)
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "wsnap_p6_resident_probe.txt"),
                        $"started|{Environment.ProcessId}|hook={!_hook.InstallFailed}|tray={_tray != null}|pipe={_pipe != null}");

                    // --tray-menu-probe (with --resident-demo): pop the WinForms ContextMenuStrip at a
                    // fixed point after 2s so the external harness can pixel-check that the owner-draw
                    // dark theme actually renders under Avalonia's message loop — the one genuinely
                    // new risk of the retained WinForms tray island.
                    if (_desktop?.Args != null && Array.IndexOf(_desktop.Args, "--tray-menu-probe") >= 0)
                    {
                        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                        t.Tick += (_, _) => { t.Stop(); _tray?.ContextMenuStrip?.Show(new System.Drawing.Point(200, 200)); };
                        t.Start();
                    }
                }
            }
            catch (Exception ex) { CrashLog.Write("resident-start", ex); }
        });
    }

    private void TearDownResident()
    {
        _hook?.Dispose();
        _clipboard?.Dispose();
        _folderWatcher?.Dispose();
        _pipe?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
    }

    private void StartPipeServer()
    {
        try { _pipe = new Wsnap.Control.PipeServer(_router!); _pipe.Start(); }
        catch (Exception ex) { CrashLog.Write("pipe-start", ex); }
    }

    /// <summary>
    /// Warm the window/composition path once at startup so the FIRST hotkey press doesn't pay
    /// it (JIT of layout/render code + the Skia/D3D composition target spin-up). A 1×1
    /// borderless window far off-screen renders once and closes; the user never sees it.
    /// </summary>
    private void PrewarmRenderPipeline()
    {
        try
        {
            var w = new Avalonia.Controls.Window
            {
                SystemDecorations = Avalonia.Controls.SystemDecorations.None,
                CanResize = false,
                ShowInTaskbar = false,
                ShowActivated = false,
                Width = 1,
                Height = 1,
                Position = new Avalonia.PixelPoint(-32000, -32000),
                Background = Avalonia.Media.Brushes.Black,
                Content = new Avalonia.Controls.Canvas()
            };
            w.Opened += (_, _) => Dispatcher.UIThread.Post(w.Close, DispatcherPriority.Background);
            w.Show();
        }
        catch (Exception ex) { CrashLog.Write("prewarm", ex); }
    }

    private DispatcherTimer? _badgeTimer;

    /// <summary>Badge the tray tooltip for a few seconds when an external caller (CLI/MCP/pipe)
    /// touches the screen — a lingering visibility signal on top of the one-shot toast. (GIF
    /// recording additionally shows its own red badge for the whole clip.)</summary>
    private void OnExternalScreenAccess(Wsnap.Control.WsnapCommand cmd)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_tray == null) return;
            string s = $"wsnap — external control active ({cmd.Source})";
            _tray.Text = s.Length <= 63 ? s : s.Substring(0, 60) + "...";   // NotifyIcon.Text caps at 63
            _badgeTimer?.Stop();
            _badgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _badgeTimer.Tick += (_, _) => { _badgeTimer!.Stop(); if (_tray != null) _tray.Text = L.T("tray.tip", Settings.Current.HotkeyText); };
            _badgeTimer.Start();
        });
    }

    /// <summary>A hotkey binding fired: dispatch its command through the bus, same path as CLI/MCP.
    /// Unknown command id falls back to the classic region capture so a broken binding still shoots.</summary>
    private void OnHotkey(HotkeyBinding b)
    {
        try
        {
            if (!Wsnap.Control.CommandCatalog.TryParseId(b.Command, out var kind)) { StartCapture(); return; }
            System.Text.Json.JsonElement? args = null;
            if (b.Args is { Count: > 0 })
            {
                var dict = new Dictionary<string, object?>(b.Args.Count);
                foreach (var kv in b.Args) dict[kv.Key] = kv.Value;
                args = Wsnap.Control.ArgReader.Obj(dict);
            }
            _ = _router!.ExecuteAsync(new Wsnap.Control.WsnapCommand(kind, args, Wsnap.Control.CommandSource.Hotkey));
        }
        catch (Exception ex) { CrashLog.Write("hotkey-dispatch", ex); StartCapture(); }
    }

    /// <summary>Clipboard image detected: pop a thumbnail, and (opt-in) auto-OCR it to the clipboard.</summary>
    private void OnClipboardImage(string path)
    {
        new ThumbnailWindow(path).Show();
        if (Settings.Current.ClipboardAutoOcr) _ = AutoOcrToClipboard(path);
    }

    private async Task AutoOcrToClipboard(string path)
    {
        try
        {
            var res = await _router!.ExecuteAsync(new Wsnap.Control.WsnapCommand(
                Wsnap.Control.CommandKind.OcrImage,
                Wsnap.Control.ArgReader.Obj(new Dictionary<string, object?> { ["path"] = path }),
                Wsnap.Control.CommandSource.Internal));
            if (res.Ok && !string.IsNullOrEmpty(res.Text)) { ClipboardCore.CopyTextSuppressed(res.Text!); Toast.Show(L.T("toast.textCopied")); }
        }
        catch (Exception ex) { CrashLog.Write("clip-auto-ocr", ex); }
    }

    /// <summary>Background update check ~20s after startup (deferred so it never adds to launch
    /// latency or the initial memory spike), if the user hasn't disabled it. The manual tray
    /// "check for updates" runs regardless of this setting.</summary>
    private void ScheduleUpdateCheck()
    {
        var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(20) };
        t.Tick += (_, _) => { t.Stop(); _ = CheckForUpdateAsync(manual: false); };
        t.Start();
    }

    private async Task CheckForUpdateAsync(bool manual)
    {
        try
        {
            var info = await UpdateChecker.CheckAsync();
            if (info == null) { if (manual) Toast.Show(L.T("toast.updateCheckFailed"), 2600); return; }
            if (!UpdateChecker.IsNewer(info.Version, UpdateChecker.CurrentVersion))
            {
                if (manual) Toast.Show(L.T("toast.upToDate", UpdateChecker.CurrentVersion), 2600);
                return;
            }
            _update = info;
            Dispatcher.UIThread.Post(() =>
            {
                RebuildTrayMenu();
                Toast.Show(L.T("toast.updateAvailable", info.Version), 3200);
            });
        }
        catch (Exception ex) { CrashLog.Write("update-check", ex); if (manual) Toast.Show(L.T("toast.updateCheckFailed"), 2600); }
    }

    /// <summary>Open the latest release page (or installer asset) in the default browser.</summary>
    private void OpenUpdate()
    {
        var url = _update?.InstallerUrl;
        if (string.IsNullOrEmpty(url)) url = _update?.ReleaseUrl;
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { CrashLog.Write("update-open", ex); }
    }

    /// <summary>
    /// Keep the resident (tray) footprint small. Once startup/JIT settles, do one compacting
    /// trim to release the warm-up allocations. There is deliberately NO EmptyWorkingSet here:
    /// paging the process out made the Task-Manager number pretty at idle but hit the NEXT
    /// hotkey press with a hard page-fault storm — the exact "capture feels laggy" complaint.
    /// </summary>
    private void StartMemoryTrimming()
    {
        var warmup = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4) };
        warmup.Tick += (_, _) => { warmup.Stop(); MemoryTrim.TrimNow(); };
        warmup.Start();
    }

    /// <summary>After a capture's transient bitmaps are gone, reclaim + return the memory.</summary>
    private void ScheduleTrim()
    {
        var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        t.Tick += (_, _) => { t.Stop(); MemoryTrim.TrimNow(); };
        t.Start();
    }

    // ---------------- tray (WinForms island, deliberately retained) ----------------

    private void SetupTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Visible = true,
            Text = L.T("tray.tip", Settings.Current.HotkeyText),
            ContextMenuStrip = BuildTrayMenu()
        };
        _tray.DoubleClick += (_, _) => StartCapture();
    }

    /// <summary>Build the tray context menu in the current UI language. Rebuilt on language change.</summary>
    private WinForms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(L.T("tray.captureRegion", Settings.Current.HotkeyText), null, (_, _) => StartCapture());
        menu.Items.Add(L.T("tray.captureFull"), null, (_, _) => CaptureFullScreen());
        menu.Items.Add(L.T("tray.captureWindow"), null, (_, _) => CaptureActiveWindow());
        menu.Items.Add(L.T("tray.repeatRegion"), null, (_, _) => RepeatLastRegion());

        var delay = new WinForms.ToolStripMenuItem(L.T("tray.delay"));
        delay.DropDownItems.Add(L.T("tray.delay3"), null, (_, _) => DelayedCapture(3));
        delay.DropDownItems.Add(L.T("tray.delay5"), null, (_, _) => DelayedCapture(5));
        menu.Items.Add(delay);

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(L.T("tray.ocr"), null, (_, _) => StartOcrCapture());
        menu.Items.Add(L.T("tray.colorPick"), null, (_, _) => StartColorPick());
        menu.Items.Add(L.T("tray.gif"), null, (_, _) => StartGifCapture());
        var video = new WinForms.ToolStripMenuItem(L.T("tray.video"));
        video.DropDownItems.Add(L.T("tray.videoMp4"), null, (_, _) => StartVideoCapture(VideoFormat.Mp4));
        video.DropDownItems.Add(L.T("tray.videoApng"), null, (_, _) => StartVideoCapture(VideoFormat.Apng));
        menu.Items.Add(video);
        menu.Items.Add(L.T("tray.scroll"), null, (_, _) => StartScrollCapture());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(L.T("tray.openFolder"), null, (_, _) => OpenCaptureFolder());
        menu.Items.Add(L.T("tray.history"), null, (_, _) => HistoryWindow.ShowSingleton());
        menu.Items.Add(L.T("tray.clearThumbs"), null, (_, _) => ThumbnailWindow.ClearAll());
        menu.Items.Add(L.T("tray.settings"), null, (_, _) => SettingsWindow.ShowSingleton(ApplyRuntime));
        if (_update != null)
            menu.Items.Add(L.T("tray.updateAvailable", _update.Version), null, (_, _) => OpenUpdate());
        else
            menu.Items.Add(L.T("tray.checkUpdate"), null, (_, _) => _ = CheckForUpdateAsync(manual: true));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(L.T("tray.exit"), null, (_, _) => _desktop?.Shutdown());
        TrayMenuTheme.Apply(menu);   // last: submenus exist now, so the dark theme reaches them
        return menu;
    }

    /// <summary>Rebuild the tray menu in place (used after a language change and when an update
    /// becomes available, so the "new version" entry appears without a restart).</summary>
    private void RebuildTrayMenu()
    {
        if (_tray == null) return;
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = BuildTrayMenu();
        old?.Dispose();
    }

    /// <summary>Load the bundled app icon (embedded so it works inside the single-file exe).</summary>
    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var name = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("wsnap.ico", StringComparison.OrdinalIgnoreCase));
            if (name != null)
            {
                using var s = asm.GetManifestResourceStream(name);
                if (s != null) return new System.Drawing.Icon(s, WinForms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex) { CrashLog.Write("tray-icon", ex); }
        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>Re-apply runtime toggles after the settings window saves.</summary>
    private void ApplyRuntime()
    {
        _clipboard?.SetEnabled(Settings.Current.ClipboardWatch);
        _folderWatcher?.SetEnabled(Settings.Current.WatchFolderOcr);

        // External-control toggled in settings: bring the pipe listener into line at runtime.
        if (Settings.Current.ExternalControlEnabled && _pipe == null) StartPipeServer();
        else if (!Settings.Current.ExternalControlEnabled && _pipe != null) { try { _pipe.Dispose(); } catch { } _pipe = null; }

        if (_tray != null)
        {
            // Rebuild the whole menu so a language change re-localizes every item (and the
            // hotkey label refreshes either way). The old menu is replaced and disposed.
            var old = _tray.ContextMenuStrip;
            _tray.ContextMenuStrip = BuildTrayMenu();
            old?.Dispose();
            _tray.Text = L.T("tray.tip", Settings.Current.HotkeyText);
        }
    }

    // ---------------- capture entry points (tray / hotkey / second launch) ----------------

    private bool _overlayOpen;

    private void StartCapture()
    {
        if (_overlayOpen) return;
        _overlayOpen = true;
        var ctx = Wsnap.Control.CaptureCore.ForegroundContext();   // BEFORE the overlay freezes/steals focus
        var overlay = new CaptureOverlay(CaptureMode.Capture) { NameCtx = ctx };
        overlay.Closed += (_, _) => { _overlayOpen = false; RouteCapture(overlay); };
        overlay.Show();
        overlay.Activate();
    }

    /// <summary>Route a finished Capture overlay to the action the user picked (toolbar or default).</summary>
    private void RouteCapture(CaptureOverlay overlay)
    {
        var act = overlay.Action;
        string? path = overlay.ResultPath;
        var bmp = overlay.ResultBitmap;
        var region = overlay.RegionPx;
        bool disposeBmp = true;
        try
        {
            switch (act)
            {
                case CaptureOverlay.PostAction.Save:
                    if (path != null)
                    {
                        if (Settings.Current.AutoCopyOnCapture) ClipboardCore.CopyImageFile(path);
                        new ThumbnailWindow(path).Show();
                    }
                    break;

                case CaptureOverlay.PostAction.Pin:
                    if (path != null)
                    {
                        if (Settings.Current.AutoCopyOnCapture) ClipboardCore.CopyImageFile(path);
                        var t = new ThumbnailWindow(path); t.Show(); t.PinNow();
                    }
                    break;

                case CaptureOverlay.PostAction.Copy:
                    if (path != null) { ClipboardCore.CopyImageFile(path); Toast.Show(L.T("toast.imageCopied")); }
                    break;

                case CaptureOverlay.PostAction.Edit:
                    if (path != null) OpenEditorThenThumbnail(path);
                    break;

                case CaptureOverlay.PostAction.Ocr:
                    if (bmp != null) { disposeBmp = false; RunOcr(bmp); }   // RunOcr owns disposal
                    break;

                case CaptureOverlay.PostAction.Gif:
                    if (region is { } r && r.Width > 1 && r.Height > 1)
                        new GifRecorder(new System.Drawing.Rectangle(r.X, r.Y, r.Width, r.Height), p => { new ThumbnailWindow(p).Show(); ScheduleTrim(); }).Start();
                    break;
            }
        }
        catch (Exception ex) { CrashLog.Write("route-capture", ex); }
        finally { if (disposeBmp) bmp?.Dispose(); }
        // The primary region-capture path lands here; the overlay's large frozen-screen
        // bitmap and result bitmap are released above, so reclaim the memory now. OCR keeps
        // its bitmap (trims in RunOcr's finally) and GIF is mid-recording — its blocking
        // compacting trim would drop frames — so it trims from its completion callback.
        if (act != CaptureOverlay.PostAction.Ocr && act != CaptureOverlay.PostAction.Gif) ScheduleTrim();
    }

    private void OpenEditorThenThumbnail(string path)
    {
        EditorWindow ed;
        try { ed = new EditorWindow(path); }
        catch (Exception ex) { CrashLog.Write("open-editor", ex); Toast.Show(L.T("ed.openFail")); return; }
        ed.Closed += (_, _) => { if (!string.IsNullOrEmpty(ed.ResultPath)) new ThumbnailWindow(ed.ResultPath!, edited: true).Show(); };
        ed.Show();
        ed.Activate();
    }

    private static async void RunOcr(System.Drawing.Bitmap bmp)
    {
        try
        {
            Toast.Show(L.T("toast.ocrBusy"));
            string? text = await Ocr.RecognizeAsync(bmp);
            if (text == null) Toast.Show(L.T("toast.ocrUnavailable"), 2600);
            else if (text.Trim().Length == 0) Toast.Show(L.T("toast.ocrNoText"));
            else { ClipboardCore.CopyTextSuppressed(text); Toast.Show(L.T("toast.textCopied")); }
        }
        catch (Exception ex) { CrashLog.Write("ocr", ex); Toast.Show(L.T("toast.ocrFailed")); }
        finally { bmp.Dispose(); MemoryTrim.TrimNow(); }   // OCR's bitmap is gone now
    }

    private void StartOcrCapture()
    {
        if (_overlayOpen) return;
        _overlayOpen = true;
        var overlay = new CaptureOverlay(CaptureMode.OcrText);
        overlay.Closed += (_, _) =>
        {
            _overlayOpen = false;
            var bmp = overlay.ResultBitmap;
            if (bmp != null) RunOcr(bmp);
        };
        overlay.Show();
        overlay.Activate();
    }

    private void StartColorPick()
    {
        if (_overlayOpen) return;
        _overlayOpen = true;
        var overlay = new CaptureOverlay(CaptureMode.ColorPick);
        overlay.Closed += (_, _) => _overlayOpen = false;
        overlay.Show();
        overlay.Activate();
    }

    private void StartGifCapture()
    {
        if (_overlayOpen) return;
        _overlayOpen = true;
        var overlay = new CaptureOverlay(CaptureMode.Region);
        overlay.Closed += (_, _) =>
        {
            _overlayOpen = false;
            if (overlay.RegionPx is { } r && r.Width > 1 && r.Height > 1)
                new GifRecorder(new System.Drawing.Rectangle(r.X, r.Y, r.Width, r.Height), path => { new ThumbnailWindow(path).Show(); ScheduleTrim(); }).Start();
        };
        overlay.Show();
        overlay.Activate();
    }

    /// <summary>Region video recording. <see cref="VideoFormat.Mp4"/> = H.264 (small, video),
    /// <see cref="VideoFormat.Apng"/> = lossless animated PNG ("PNG video"). Falls back to GIF
    /// when ffmpeg can't be resolved, so the user always gets a result from the same action.</summary>
    private void StartVideoCapture(VideoFormat format)
    {
        if (_overlayOpen) return;
        _overlayOpen = true;
        var overlay = new CaptureOverlay(CaptureMode.Region);
        overlay.Closed += (_, _) =>
        {
            _overlayOpen = false;
            if (overlay.RegionPx is not { } r || r.Width <= 1 || r.Height <= 1) return;

            if (VideoRecorder.IsAvailable)
            {
                new VideoRecorder(new System.Drawing.Rectangle(r.X, r.Y, r.Width, r.Height), (path, poster) => { new ThumbnailWindow(path, poster: poster).Show(); ScheduleTrim(); }, format).Start();
            }
            else
            {
                // ffmpeg missing (and no download triggered yet): degrade to GIF so the capture isn't lost.
                Toast.Show(L.T("vid.ffmpegFallback"));
                new GifRecorder(new System.Drawing.Rectangle(r.X, r.Y, r.Width, r.Height), path => { new ThumbnailWindow(path).Show(); ScheduleTrim(); }).Start();
            }
        };
        overlay.Show();
        overlay.Activate();
    }

    private void StartScrollCapture()
    {
        if (_overlayOpen) return;
        _overlayOpen = true;
        var overlay = new CaptureOverlay(CaptureMode.Region);
        overlay.Closed += (_, _) =>
        {
            _overlayOpen = false;
            if (overlay.RegionPx is { } r && r.Width > 1 && r.Height > 1)
                new ScrollCapture(new System.Drawing.Rectangle(r.X, r.Y, r.Width, r.Height), path => new ThumbnailWindow(path).Show()).Start();
        };
        overlay.Show();
        overlay.Activate();
    }

    // ---- one-shot capture modes (no overlay drag needed) — via the shared headless core, with
    // the normal capture UX (auto-copy + thumbnail + trim) attached through PresentCapture. ----

    private void CaptureFullScreen()
    {
        var res = Wsnap.Control.CaptureCore.CaptureFullScreen(null);   // monitor under the cursor
        if (res.Ok && res.Path != null) PresentCapture(res.Path);
        else Toast.Show(L.T("toast.captureFailed"));
    }

    private void CaptureActiveWindow()
    {
        var res = Wsnap.Control.CaptureCore.CaptureWindow();
        if (res.Ok && res.Path != null) PresentCapture(res.Path);
        else Toast.Show(res.ErrorCode == "no_window" ? L.T("toast.noActiveWindow") : L.T("toast.captureFailed"));
    }

    private void RepeatLastRegion()
    {
        if (CaptureOverlay.LastRegion is { } r)
        {
            var res = Wsnap.Control.CaptureCore.CaptureRegion(r.X, r.Y, r.Width, r.Height);
            if (res.Ok && res.Path != null) PresentCapture(res.Path);
            else Toast.Show(L.T("toast.captureFailed"));
        }
        else { Toast.Show(L.T("toast.noLastRegion")); StartCapture(); }
    }

    private void DelayedCapture(int seconds)
    {
        int remaining = seconds;
        Toast.Show(L.T("toast.countdown", remaining), 950);
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) =>
        {
            remaining--;
            if (remaining <= 0) { t.Stop(); StartCapture(); }
            else Toast.Show(L.T("toast.countdown", remaining), 950);
        };
        t.Start();
    }

    private void OpenCaptureFolder()
    {
        try
        {
            string dir = Settings.Current.SaveFolder;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { CrashLog.Write("open-folder", ex); }
    }
}
