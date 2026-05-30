using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Wsnap;

/// <summary>
/// v1.1 region GIF recorder. Grabs frames from a fixed screen rect on a timer until
/// the user stops, then encodes a looping animated GIF and hands back the path.
/// Deliberately simple (no separate trim editor) — fits the capture+DnD identity.
/// </summary>
public sealed class GifRecorder
{
    private const int Fps = 12;
    private const int MaxSeconds = 30;

    private readonly Int32Rect _region;
    private readonly Action<string> _onSaved;
    private readonly List<BitmapSource> _frames = new();
    private readonly DispatcherTimer _timer;
    private Window? _control;
    private TextBlock? _status;
    private bool _stopped;

    public GifRecorder(Int32Rect region, Action<string> onSaved)
    {
        _region = region;
        _onSaved = onSaved;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / Fps) };
        _timer.Tick += OnTick;
    }

    public void Start()
    {
        if (_region.Width < 2 || _region.Height < 2) return;
        ShowControl();
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        try
        {
            using var bmp = ScreenGrab.Grab(_region.X, _region.Y, _region.Width, _region.Height);
            _frames.Add(ScreenGrab.ToBitmapSource(bmp));
            if (_status != null) _status.Text = $"● 녹화 중 · {_frames.Count} 프레임 · 중지(클릭/Esc)";
            if (_frames.Count >= Fps * MaxSeconds) Stop();
        }
        catch (Exception ex) { CrashLog.Write("gif-tick", ex); Stop(); }
    }

    private void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _timer.Stop();
        _control?.Close();

        if (_frames.Count == 0) { Toast.Show("녹화 취소됨"); return; }

        Toast.Show("GIF 인코딩 중…");
        string path = CaptureStore.NewPath(".gif");
        try
        {
            GifWriter.Save(_frames, path, 1000 / Fps);
            CrashLog.Telemetry("gif-saved");
            _onSaved(path);
        }
        catch (Exception ex)
        {
            CrashLog.Write("gif-save", ex);
            Toast.Show("GIF 저장 실패");
        }
        _frames.Clear();
    }

    private void ShowControl()
    {
        _status = new TextBlock
        {
            Text = "● 녹화 중 · 0 프레임 · 중지(클릭/Esc)",
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 13, Margin = new Thickness(12, 8, 12, 8)
        };
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xF0, 0xC0, 0x2A, 0x2A)),
            Child = _status, Cursor = Cursors.Hand
        };
        _control = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
            Topmost = true, ShowInTaskbar = false, SizeToContent = SizeToContent.WidthAndHeight,
            Content = border
        };
        _control.MouseLeftButtonDown += (_, _) => Stop();
        _control.KeyDown += (_, e) => { if (e.Key == Key.Escape) Stop(); };
        _control.Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            _control.Left = wa.Left + (wa.Width - _control.ActualWidth) / 2;
            _control.Top = wa.Top + 12;   // top-center, away from most capture regions
        };
        _control.Show();
        _control.Activate();
    }
}
