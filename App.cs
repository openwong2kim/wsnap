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
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
    private DispatcherTimer? _idleTrim;

    [STAThread]
    public static void Main()
    {
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // One design system, merged once so every window inherits the dark identity.
        Resources.MergedDictionaries.Add(Theme.Dict);

        _hook = new HotkeyHook();
        _hook.CaptureRequested += StartCapture;
        _hook.Install();

        _clipboard = new ClipboardWatcher(path => new ThumbnailWindow(path).Show());
        _clipboard.SetEnabled(Settings.Current.ClipboardWatch);

        SetupTray();

        if (_hook.InstallFailed)
            Toast.Show("단축키 훅 설치 실패 — 보안 소프트웨어가 막았을 수 있어요. 트레이 메뉴로 캡처하세요.", 4000);
        else
            CrashLog.Telemetry("startup");

        StartMemoryTrimming();
    }

    /// <summary>
    /// Keep the resident (tray) footprint small. Once startup/JIT settles, do one compacting
    /// trim to release the warm-up allocations, then empty the working set on an idle timer so
    /// the process sits at tens of MB instead of holding onto everything it ever touched.
    /// </summary>
    private void StartMemoryTrimming()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            var warmup = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4) };
            warmup.Tick += (_, _) => { warmup.Stop(); MemoryTrim.TrimNow(); };
            warmup.Start();
        });

        _idleTrim = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(45) };
        _idleTrim.Tick += (_, _) => MemoryTrim.TrimWorkingSet();
        _idleTrim.Start();
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
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add($"영역 캡처  ({Settings.Current.HotkeyText})", null, (_, _) => StartCapture());
        menu.Items.Add("전체 화면 캡처", null, (_, _) => CaptureFullScreen());
        menu.Items.Add("현재 창 캡처", null, (_, _) => CaptureActiveWindow());
        menu.Items.Add("직전 영역 다시 캡처", null, (_, _) => RepeatLastRegion());

        var delay = new WinForms.ToolStripMenuItem("지연 캡처");
        delay.DropDownItems.Add("3초 후 영역 캡처", null, (_, _) => DelayedCapture(3));
        delay.DropDownItems.Add("5초 후 영역 캡처", null, (_, _) => DelayedCapture(5));
        menu.Items.Add(delay);

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("텍스트 추출 (OCR 영역)", null, (_, _) => StartOcrCapture());
        menu.Items.Add("색 추출 (스포이드)", null, (_, _) => StartColorPick());
        menu.Items.Add("GIF 녹화 (영역)", null, (_, _) => StartGifCapture());
        menu.Items.Add("스크롤 캡처 (영역)", null, (_, _) => StartScrollCapture());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("캡처 폴더 열기", null, (_, _) => OpenCaptureFolder());
        menu.Items.Add("캡처 히스토리…", null, (_, _) => HistoryWindow.ShowSingleton());
        menu.Items.Add("열린 썸네일 전체 지우기", null, (_, _) => ThumbnailWindow.ClearAll());
        menu.Items.Add("설정…", null, (_, _) => SettingsWindow.ShowSingleton(ApplyRuntime));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Shutdown());

        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Visible = true,
            Text = $"wsnap — {Settings.Current.HotkeyText}로 캡처",
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => StartCapture();
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
        if (_tray != null)
        {
            _tray.Text = $"wsnap — {Settings.Current.HotkeyText}로 캡처";
            if (_tray.ContextMenuStrip?.Items.Count > 0)
                _tray.ContextMenuStrip.Items[0].Text = $"캡처  ({Settings.Current.HotkeyText})";
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
                    if (path != null) { ImageClipboard.CopyImageFile(path); Toast.Show("이미지 복사됨 ✓"); }
                    break;

                case CaptureOverlay.PostAction.Edit:
                    if (path != null) OpenEditorThenThumbnail(path);
                    break;

                case CaptureOverlay.PostAction.Ocr:
                    if (bmp != null) { disposeBmp = false; RunOcr(bmp); }   // RunOcr owns disposal
                    break;

                case CaptureOverlay.PostAction.Gif:
                    if (region is { } r && r.Width > 1 && r.Height > 1)
                        new GifRecorder(r, p => { new ThumbnailWindow(p).Show(); ScheduleTrim(); }).Start();
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
        var ed = new EditorWindow(path);
        ed.Closed += (_, _) => { if (!string.IsNullOrEmpty(ed.ResultPath)) new ThumbnailWindow(ed.ResultPath!, edited: true).Show(); };
        ed.Show();
        ed.Activate();
    }

    private static async void RunOcr(System.Drawing.Bitmap bmp)
    {
        try
        {
            Toast.Show("텍스트 인식 중…");
            string? text = await Ocr.RecognizeAsync(bmp);
            if (text == null) Toast.Show("OCR 사용 불가 (언어팩 설치 필요)", 2600);
            else if (text.Trim().Length == 0) Toast.Show("인식된 텍스트 없음");
            else { ImageClipboard.CopyText(text); Toast.Show("텍스트 복사됨 ✓"); }
        }
        catch (Exception ex) { CrashLog.Write("ocr", ex); Toast.Show("OCR 실패"); }
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
                new GifRecorder(r, path => { new ThumbnailWindow(path).Show(); ScheduleTrim(); }).Start();
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
                new ScrollCapture(r, path => new ThumbnailWindow(path).Show()).Start();
        };
        overlay.Show();
        overlay.Activate();
    }

    // ---- one-shot capture modes (no overlay drag needed) ----

    /// <summary>Grab a device-px rect, save it, copy it (if enabled), pop a thumbnail.</summary>
    private void DeliverRegion(System.Windows.Int32Rect r)
    {
        if (r.Width < 1 || r.Height < 1) { Toast.Show("캡처할 영역이 없어요"); return; }
        try
        {
            var ctx = ForegroundContext(r.Width, r.Height);
            string path;
            using (var bmp = ScreenGrab.Grab(r.X, r.Y, r.Width, r.Height))
                path = CaptureStore.SaveBitmap(bmp, ctx);
            if (Settings.Current.AutoCopyOnCapture) ImageClipboard.CopyImageFile(path);
            new ThumbnailWindow(path).Show();
            ScheduleTrim();   // reclaim the full-screen grab bitmap once it's saved & shown
        }
        catch (Exception ex) { CrashLog.Write("deliver-region", ex); Toast.Show("캡처 실패"); }
    }

    private void CaptureFullScreen()
    {
        var b = WinForms.Screen.FromPoint(WinForms.Cursor.Position).Bounds;   // device px
        DeliverRegion(new System.Windows.Int32Rect(b.X, b.Y, b.Width, b.Height));
    }

    private void CaptureActiveWindow()
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) { Toast.Show("활성 창을 찾지 못했어요"); return; }
        // Extended frame bounds excludes the ~7px invisible resize border GetWindowRect includes.
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0)
        { if (!GetWindowRect(h, out r)) { Toast.Show("창 영역을 읽지 못했어요"); return; } }
        DeliverRegion(new System.Windows.Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
    }

    private void RepeatLastRegion()
    {
        if (CaptureOverlay.LastRegion is { } r) DeliverRegion(r);
        else { Toast.Show("아직 캡처한 영역이 없어요 — 먼저 영역을 잡아주세요"); StartCapture(); }
    }

    private void DelayedCapture(int seconds)
    {
        int remaining = seconds;
        Toast.Show($"{remaining}초 후 캡처…", 950);
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) =>
        {
            remaining--;
            if (remaining <= 0) { t.Stop(); StartCapture(); }
            else Toast.Show($"{remaining}초 후 캡처…", 950);
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
