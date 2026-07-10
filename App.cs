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
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace Wsnap;

public partial class App : System.Windows.Application
{
    private static App? _instance;

    private HotkeyHook? _hook;
    private WinForms.NotifyIcon? _tray;
    private ClipboardWatcher? _clipboard;
    private UpdateInfo? _update;

    // Control layer (v1.7): the tray instance hosts one shared bus + gate; the pipe server is
    // created only when the user opts into external control. See App.Control.cs for IResidentHost.
    private Wsnap.Control.ControlGate? _gate;
    private Wsnap.Control.CommandRouter? _router;
    private Wsnap.Control.PipeServer? _pipe;
    private FolderWatcher? _folderWatcher;

    [STAThread]
    public static void Main(string[] args)
    {
        // ---- client sub-commands run WITHOUT the WPF/tray app ----
        // `wsnap mcp` = stdio MCP server; `wsnap <verb>` = CLI. Both delegate to the running tray
        // instance over the control pipe when it's up, else run headless in-proc. This branch MUST
        // come before SingleInstance/Settings so a client invocation has no tray side effects.
        if (args.Length > 0)
        {
            if (string.Equals(args[0], "mcp", StringComparison.OrdinalIgnoreCase))
            {
                Settings.Load();
                Wsnap.Control.McpStdioServer.RunAsync(BuildClientRouter()).GetAwaiter().GetResult();
                return;
            }
            if (Wsnap.Control.CliRouter.IsKnownVerb(args[0]))
            {
                Settings.Load();
                Wsnap.Control.ConsoleBridge.Bind();
                int code = Wsnap.Control.CliRouter.Run(args, BuildClientRouter()).GetAwaiter().GetResult();
                Wsnap.Control.ConsoleBridge.Unbind();
                Environment.Exit(code);
                return;
            }
        }

        var app = new App();
        _instance = app;

        app.DispatcherUnhandledException += (_, e) =>
        {
            CrashLog.Write("dispatcher-unhandled", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) CrashLog.Write("domain-unhandled", ex);
        };

        // One instance only. A second launch tells the running one to capture, then exits.
        bool primary = SingleInstance.TryAcquire(() =>
            app.Dispatcher.BeginInvoke(() => _instance?.StartCapture()));
        if (!primary) return;

        Settings.Load();
        app.Run();
        SingleInstance.Release();
    }

    /// <summary>The router a CLI/MCP client process uses: delegate to the running tray instance over
    /// the control pipe when present; otherwise a headless in-proc router (interactive/recording
    /// commands then return resident_required since there's no tray host).</summary>
    private static Wsnap.Control.ICommandRouter BuildClientRouter()
    {
        if (Wsnap.Control.PipeClientRouter.IsResidentRunning())
            return new Wsnap.Control.PipeClientRouter();
        return new Wsnap.Control.CommandRouter(new Wsnap.Control.ControlGate(), host: null);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // One design system, merged once so every window inherits the dark identity.
        Resources.MergedDictionaries.Add(Theme.Dict);

        // Recorders are framework-agnostic since Phase 4 — give them the WPF badge.
        RecorderUi.BadgeFactory = (text, argb) => new RecorderBadgeWpf(text, argb);

        // Control layer: this tray instance is the resident host, so hotkey / tray / pipe / MCP all
        // share ONE CommandRouter + ONE ControlGate (single consent/rate-limit choke point).
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
        ScheduleUpdateCheck();
        PrewarmRenderPipeline();
    }

    /// <summary>
    /// Warm WPF's window/composition path once at startup so the FIRST hotkey press doesn't pay
    /// it. Creating the first real Window JIT-compiles layout/render code and spins up the D3D
    /// composition target — worth 100–300 ms on cold processes, which used to land on the first
    /// capture. A 1×1 borderless window far off-screen renders one frame and closes; the user
    /// never sees it.
    /// </summary>
    private void PrewarmRenderPipeline()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            try
            {
                var w = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Width = 1,
                    Height = 1,
                    Left = -32000,
                    Top = -32000,
                    Background = System.Windows.Media.Brushes.Black,
                    Content = new System.Windows.Controls.Canvas()
                };
                w.ContentRendered += (_, _) => w.Close();
                w.Show();
            }
            catch (Exception ex) { CrashLog.Write("prewarm", ex); }
        });
    }

    private void StartPipeServer()
    {
        try { _pipe = new Wsnap.Control.PipeServer(_router!); _pipe.Start(); }
        catch (Exception ex) { CrashLog.Write("pipe-start", ex); }
    }

    private DispatcherTimer? _badgeTimer;

    /// <summary>Badge the tray tooltip for a few seconds when an external caller (CLI/MCP/pipe)
    /// touches the screen — a lingering visibility signal on top of the one-shot toast. (GIF
    /// recording additionally shows its own red badge for the whole clip.)</summary>
    private void OnExternalScreenAccess(Wsnap.Control.WsnapCommand cmd)
    {
        Dispatcher.BeginInvoke(() =>
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
            if (res.Ok && !string.IsNullOrEmpty(res.Text)) { ImageClipboard.CopyText(res.Text!); Toast.Show(L.T("toast.textCopied")); }
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
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                RebuildTrayMenu();
                Toast.Show(L.T("toast.updateAvailable", info.Version), 3200);
            }));
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
    /// The honest footprint comes from compacting GCs (RetainVM=false returns freed memory)
    /// and from not bundling weight we don't use; the OS is better at paging than a timer.
    /// </summary>
    private void StartMemoryTrimming()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            var warmup = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4) };
            warmup.Tick += (_, _) => { warmup.Stop(); MemoryTrim.TrimNow(); };
            warmup.Start();
        });
    }

    /// <summary>After a capture's transient bitmaps are gone, reclaim + return the memory.</summary>
    private void ScheduleTrim()
    {
        var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        t.Tick += (_, _) => { t.Stop(); MemoryTrim.TrimNow(); };
        t.Start();
    }

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
        menu.Items.Add(L.T("tray.exit"), null, (_, _) => Shutdown());
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
                if (s != null) return new System.Drawing.Icon(s, System.Windows.Forms.SystemInformation.SmallIconSize);
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

    private bool _overlayOpen;

    private void StartCapture()
    {
        if (_overlayOpen) return;
        _overlayOpen = true;
        var ctx = ForegroundContext();   // BEFORE the overlay freezes/steals focus
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
                        if (Settings.Current.AutoCopyOnCapture) ImageClipboard.CopyImageFile(path);
                        new ThumbnailWindow(path).Show();
                    }
                    break;

                case CaptureOverlay.PostAction.Pin:
                    if (path != null)
                    {
                        if (Settings.Current.AutoCopyOnCapture) ImageClipboard.CopyImageFile(path);
                        var t = new ThumbnailWindow(path); t.Show(); t.PinNow();
                    }
                    break;

                case CaptureOverlay.PostAction.Copy:
                    if (path != null) { ImageClipboard.CopyImageFile(path); Toast.Show(L.T("toast.imageCopied")); }
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
            else { ImageClipboard.CopyText(text); Toast.Show(L.T("toast.textCopied")); }
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

    // ---- one-shot capture modes (no overlay drag needed) ----

    /// <summary>Grab a device-px rect, save it, copy it (if enabled), pop a thumbnail.</summary>
    private void DeliverRegion(System.Windows.Int32Rect r)
    {
        if (r.Width < 1 || r.Height < 1) { Toast.Show(L.T("toast.noRegion")); return; }
        try
        {
            var ctx = ForegroundContext(r.Width, r.Height);
            string path;
            using (var bmp = ScreenGrab.GrabFast(r.X, r.Y, r.Width, r.Height))
                path = CaptureStore.SaveBitmap(bmp, ctx);
            if (Settings.Current.AutoCopyOnCapture) ImageClipboard.CopyImageFile(path);
            new ThumbnailWindow(path).Show();
            ScheduleTrim();   // reclaim the full-screen grab bitmap once it's saved & shown
        }
        catch (Exception ex) { CrashLog.Write("deliver-region", ex); Toast.Show(L.T("toast.captureFailed")); }
    }

    private void CaptureFullScreen()
    {
        var b = WinForms.Screen.FromPoint(WinForms.Cursor.Position).Bounds;   // device px
        DeliverRegion(new System.Windows.Int32Rect(b.X, b.Y, b.Width, b.Height));
    }

    private void CaptureActiveWindow()
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) { Toast.Show(L.T("toast.noActiveWindow")); return; }
        // Extended frame bounds excludes the ~7px invisible resize border GetWindowRect includes.
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0)
        { if (!GetWindowRect(h, out r)) { Toast.Show(L.T("toast.windowReadFail")); return; } }
        DeliverRegion(new System.Windows.Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
    }

    private void RepeatLastRegion()
    {
        if (CaptureOverlay.LastRegion is { } r) DeliverRegion(r);
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

    protected override void OnExit(ExitEventArgs e)
    {
        _hook?.Dispose();
        _clipboard?.Dispose();
        _folderWatcher?.Dispose();
        _pipe?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        base.OnExit(e);
    }

    // ---- native ----
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder buf, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    /// <summary>Snapshot the foreground app/title NOW (before the overlay steals focus) for filename templates.</summary>
    private static NameContext ForegroundContext(int w = 0, int h = 0)
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            string title = "", app = "";
            if (fg != IntPtr.Zero)
            {
                int len = GetWindowTextLength(fg);
                if (len > 0) { var sb = new System.Text.StringBuilder(len + 1); GetWindowText(fg, sb, sb.Capacity); title = sb.ToString(); }
                GetWindowThreadProcessId(fg, out uint pid);
                if (pid != 0) { try { using var p = Process.GetProcessById((int)pid); app = p.ProcessName; } catch { } }
            }
            return new NameContext { App = app, Title = title, Width = w, Height = h };
        }
        catch (Exception ex) { CrashLog.Write("fg-context", ex); return NameContext.Empty; }
    }
}
