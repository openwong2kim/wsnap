using System;
using System.Drawing;          // System.Drawing.Common (WinForms ref) for Bitmap/Graphics.CopyFromScreen
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Wsnap;

public enum CaptureMode
{
    /// <summary>Normal: save a PNG and pop a thumbnail.</summary>
    Capture,
    /// <summary>Select a region, OCR it, copy the text — no file kept.</summary>
    OcrText,
    /// <summary>Only report the selected rect (device px) — used by GIF / scroll capture.</summary>
    Region
}

/// <summary>
/// A borderless, topmost, transparent window covering the entire virtual desktop.
/// User drags a rectangle; on release we grab those pixels.
/// PerMonitorV2 DPI awareness (app.manifest) keeps coordinates correct across
/// mixed-DPI / fractional-scaling monitors — the exact case Flameshot mishandles.
/// </summary>
public sealed class CaptureOverlay : Window
{
    private readonly CaptureMode _mode;
    private System.Windows.Point _start;
    private bool _dragging;
    private readonly System.Windows.Shapes.Rectangle _selection;
    private readonly System.Windows.Controls.Canvas _canvas;

    /// <summary>Absolute path of the saved PNG (Capture mode), or null if cancelled / OCR mode.</summary>
    public string? ResultPath { get; private set; }

    /// <summary>The grabbed pixels (set on a successful selection in Capture/OcrText), caller owns disposal.</summary>
    public Bitmap? ResultBitmap { get; private set; }

    /// <summary>The selected region in device pixels (set on any successful selection).</summary>
    public System.Windows.Int32Rect? RegionPx { get; private set; }

    public CaptureOverlay(CaptureMode mode = CaptureMode.Capture)
    {
        _mode = mode;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 0, 0, 0));
        Topmost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        _canvas = new System.Windows.Controls.Canvas();
        _selection = new System.Windows.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(mode == CaptureMode.OcrText
                ? System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E)   // green for OCR
                : System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6)), // blue for capture
            StrokeThickness = 1.5,
            Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 0x3B, 0x82, 0xF6)),
            Visibility = Visibility.Collapsed
        };
        _canvas.Children.Add(_selection);
        Content = _canvas;

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { ResultPath = null; Close(); } };
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(this);
        _dragging = true;
        _selection.Visibility = Visibility.Visible;
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        double x = Math.Min(p.X, _start.X), y = Math.Min(p.Y, _start.Y);
        double w = Math.Abs(p.X - _start.X), h = Math.Abs(p.Y - _start.Y);
        System.Windows.Controls.Canvas.SetLeft(_selection, x);
        System.Windows.Controls.Canvas.SetTop(_selection, y);
        _selection.Width = w;
        _selection.Height = h;
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
        var p = e.GetPosition(this);

        double dipX = Math.Min(p.X, _start.X);
        double dipY = Math.Min(p.Y, _start.Y);
        double dipW = Math.Abs(p.X - _start.X);
        double dipH = Math.Abs(p.Y - _start.Y);

        if (dipW < 4 || dipH < 4) { ResultPath = null; Close(); return; }

        var src = PresentationSource.FromVisual(this);
        double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        int px = (int)Math.Round((Left + dipX) * sx);
        int py = (int)Math.Round((Top + dipY) * sy);
        int pw = (int)Math.Round(dipW * sx);
        int ph = (int)Math.Round(dipH * sy);

        RegionPx = new System.Windows.Int32Rect(px, py, pw, ph);

        // Region mode only needs the rectangle — no pixel grab.
        if (_mode == CaptureMode.Region) { Close(); return; }

        // Hide overlay so it isn't captured, then grab pixels.
        Hide();
        try
        {
            ResultBitmap = ScreenGrab.Grab(px, py, pw, ph);
            if (_mode == CaptureMode.Capture)
                ResultPath = CaptureStore.SaveBitmap(ResultBitmap);
        }
        catch (Exception ex)
        {
            CrashLog.Write("capture-grab", ex);
        }
        Close();
    }
}
