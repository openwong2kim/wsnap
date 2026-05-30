using System;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace Wsnap;

public partial class App : System.Windows.Application
{
    private static App? _instance;

    private HotkeyHook? _hook;
    private WinForms.NotifyIcon? _tray;
    private ClipboardWatcher? _clipboard;

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
    }

    private void SetupTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add($"캡처  ({Settings.Current.HotkeyText})", null, (_, _) => StartCapture());
        menu.Items.Add("텍스트 추출 (OCR 영역)", null, (_, _) => StartOcrCapture());
        menu.Items.Add("GIF 녹화 (영역)", null, (_, _) => StartGifCapture());
        menu.Items.Add("스크롤 캡처 (영역)", null, (_, _) => StartScrollCapture());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("열린 썸네일 전체 지우기", null, (_, _) => ThumbnailWindow.ClearAll());
        menu.Items.Add("설정…", null, (_, _) => SettingsWindow.ShowSingleton(ApplyRuntime));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Shutdown());

        _tray = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = $"wsnap — {Settings.Current.HotkeyText}로 캡처",
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => StartCapture();
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
        var overlay = new CaptureOverlay(CaptureMode.Capture);
        overlay.Closed += (_, _) =>
        {
            _overlayOpen = false;
            overlay.ResultBitmap?.Dispose();
            if (!string.IsNullOrEmpty(overlay.ResultPath))
                new ThumbnailWindow(overlay.ResultPath!).Show();
        };
        overlay.Show();
        overlay.Activate();
    }

    private void StartOcrCapture()
    {
        if (_overlayOpen) return;
        _overlayOpen = true;
        var overlay = new CaptureOverlay(CaptureMode.OcrText);
        overlay.Closed += async (_, _) =>
        {
            _overlayOpen = false;
            var bmp = overlay.ResultBitmap;
            if (bmp == null) return;
            try
            {
                Toast.Show("텍스트 인식 중…");
                string? text = await Ocr.RecognizeAsync(bmp);
                if (text == null) Toast.Show("OCR 사용 불가 (언어팩 설치 필요)", 2600);
                else if (text.Trim().Length == 0) Toast.Show("인식된 텍스트 없음");
                else { System.Windows.Clipboard.SetText(text); Toast.Show("텍스트 복사됨 ✓"); }
            }
            finally { bmp.Dispose(); }
        };
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
                new GifRecorder(r, path => new ThumbnailWindow(path).Show()).Start();
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

    protected override void OnExit(ExitEventArgs e)
    {
        _hook?.Dispose();
        _clipboard?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        base.OnExit(e);
    }
}
