using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Drawing = System.Drawing;

namespace Wsnap;

/// <summary>
/// Minimal-but-real annotation editor: crop, arrow, rectangle, pen, text, mosaic.
/// Deliberately minimal — just the few tools people actually use,
/// keyboard-first, then straight back into the drag-and-drop flow.
/// Coordinates are image pixels throughout (a Viewbox scales the canvas to the window),
/// so the rendered PNG is pixel-exact.
/// </summary>
public sealed class EditorWindow : Window
{
    private enum Tool { Arrow, Rect, Pen, Text, Mosaic, Crop }

    private readonly string _srcPath;
    private readonly Drawing.Bitmap _srcBmp;       // for mosaic sampling
    private readonly Canvas _canvas;               // image-pixel coordinate space
    private readonly int _pw, _ph;

    private readonly List<UIElement> _undo = new();
    private Tool _tool = Tool.Arrow;
    private System.Windows.Media.Color _color = System.Windows.Media.Colors.Red;
    private double _thickness = 3;

    // in-progress drawing state
    private System.Windows.Point _start;
    private bool _drawing;
    private Shape? _live;
    private Polyline? _pen;
    private System.Windows.Shapes.Rectangle? _cropBox;
    private Int32Rect? _cropRect;

    /// <summary>Path of the saved edited PNG, or null if cancelled.</summary>
    public string? ResultPath { get; private set; }

    public EditorWindow(string srcPath)
    {
        _srcPath = srcPath;
        _srcBmp = new Drawing.Bitmap(srcPath);

        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.UriSource = new Uri(srcPath);
        bi.EndInit();
        bi.Freeze();
        _pw = bi.PixelWidth;
        _ph = bi.PixelHeight;

        Title = "wsnap — 편집  (A화살표 R사각 P펜 T텍스트 M모자이크 C크롭 · Ctrl+Z 실행취소 · Enter 저장 · Esc 취소)";
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x25, 0x25, 0x25));

        // Transparent (not null) background so the Canvas is hit-testable across its
        // whole area — without this, clicks fall through to nothing and no tool draws.
        _canvas = new Canvas
        {
            Width = _pw, Height = _ph, ClipToBounds = true,
            Background = System.Windows.Media.Brushes.Transparent
        };
        _canvas.Children.Add(new Image
        {
            Source = bi, Width = _pw, Height = _ph,
            Stretch = Stretch.Fill, IsHitTestVisible = false
        });
        _canvas.MouseLeftButtonDown += OnDown;
        _canvas.MouseMove += OnMove;
        _canvas.MouseLeftButtonUp += OnUp;

        var view = new Viewbox { Stretch = Stretch.Uniform, Child = _canvas, Margin = new Thickness(8) };

        var root = new DockPanel();
        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(view);
        Content = root;

        // Fit to screen.
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(_pw + 40, wa.Width * 0.9);
        Height = Math.Min(_ph + 100, wa.Height * 0.9);

        KeyDown += OnKey;
        Closed += (_, _) => _srcBmp.Dispose();
    }

    // ---------------- toolbar ----------------

    private Border BuildToolbar()
    {
        var bar = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 6) };

        void ToolBtn(string label, Tool t) => bar.Children.Add(MakeButton(label, () => _tool = t));
        ToolBtn("화살표", Tool.Arrow);
        ToolBtn("사각형", Tool.Rect);
        ToolBtn("펜", Tool.Pen);
        ToolBtn("텍스트", Tool.Text);
        ToolBtn("모자이크", Tool.Mosaic);
        ToolBtn("크롭", Tool.Crop);

        bar.Children.Add(new Separator { Width = 12, Opacity = 0 });
        foreach (var c in new[] { System.Windows.Media.Colors.Red, System.Windows.Media.Colors.Yellow,
                                  System.Windows.Media.Colors.LimeGreen, System.Windows.Media.Colors.DeepSkyBlue,
                                  System.Windows.Media.Colors.White, System.Windows.Media.Colors.Black })
        {
            var sw = new System.Windows.Shapes.Rectangle
            {
                Width = 18, Height = 18, Margin = new Thickness(2),
                Fill = new SolidColorBrush(c), Stroke = System.Windows.Media.Brushes.Gray, StrokeThickness = 1,
                Cursor = Cursors.Hand
            };
            sw.MouseLeftButtonDown += (_, _) => _color = c;
            bar.Children.Add(sw);
        }

        bar.Children.Add(new Separator { Width = 12, Opacity = 0 });
        bar.Children.Add(MakeButton("저장", Save));
        bar.Children.Add(MakeButton("취소", () => { ResultPath = null; Close(); }));

        return new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x1A)),
            Child = bar
        };
    }

    private static System.Windows.Controls.Button MakeButton(string text, Action onClick)
    {
        var b = new System.Windows.Controls.Button
        {
            Content = text, Margin = new Thickness(2), Padding = new Thickness(8, 3, 8, 3),
            Cursor = Cursors.Hand
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    // ---------------- input ----------------

    private void OnKey(object sender, KeyEventArgs e)
    {
        // While typing in a text annotation, let the TextBox own the keys
        // (otherwise letters switch tools and Enter would save mid-typing).
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox)
        {
            if (e.Key == Key.Escape) Keyboard.ClearFocus();
            return;
        }

        if (e.Key == Key.Escape) { ResultPath = null; Close(); return; }
        if (e.Key == Key.Enter) { Save(); return; }
        if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { Undo(); return; }
        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { Save(); return; }

        _tool = e.Key switch
        {
            Key.A => Tool.Arrow,
            Key.R => Tool.Rect,
            Key.P => Tool.Pen,
            Key.T => Tool.Text,
            Key.M => Tool.Mosaic,
            Key.C => Tool.Crop,
            _ => _tool
        };
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(_canvas);

        if (_tool == Tool.Text) { PlaceText(_start); return; }

        _drawing = true;
        _canvas.CaptureMouse();
        var brush = new SolidColorBrush(_color);

        switch (_tool)
        {
            case Tool.Rect:
            case Tool.Crop:
                _live = new System.Windows.Shapes.Rectangle
                {
                    Stroke = _tool == Tool.Crop ? System.Windows.Media.Brushes.White : brush,
                    StrokeThickness = _tool == Tool.Crop ? 1.5 : _thickness,
                    StrokeDashArray = _tool == Tool.Crop ? new DoubleCollection { 4, 3 } : null,
                    Fill = _tool == Tool.Crop
                        ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 0, 0, 0))
                        : System.Windows.Media.Brushes.Transparent
                };
                Canvas.SetLeft(_live, _start.X); Canvas.SetTop(_live, _start.Y);
                _canvas.Children.Add(_live);
                if (_tool == Tool.Crop) _cropBox = (System.Windows.Shapes.Rectangle)_live;
                break;

            case Tool.Mosaic:
                _live = new System.Windows.Shapes.Rectangle
                {
                    Stroke = System.Windows.Media.Brushes.White, StrokeThickness = 1,
                    Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(60, 255, 255, 255))
                };
                Canvas.SetLeft(_live, _start.X); Canvas.SetTop(_live, _start.Y);
                _canvas.Children.Add(_live);
                break;

            case Tool.Pen:
                _pen = new Polyline { Stroke = brush, StrokeThickness = _thickness,
                    StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round };
                _pen.Points.Add(_start);
                _canvas.Children.Add(_pen);
                break;
        }
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_drawing) return;
        var p = e.GetPosition(_canvas);

        if (_tool == Tool.Pen && _pen != null) { _pen.Points.Add(p); return; }

        if (_live is System.Windows.Shapes.Rectangle r)
        {
            double x = Math.Min(p.X, _start.X), y = Math.Min(p.Y, _start.Y);
            Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
            r.Width = Math.Abs(p.X - _start.X);
            r.Height = Math.Abs(p.Y - _start.Y);
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawing) return;
        _drawing = false;
        _canvas.ReleaseMouseCapture();
        var p = e.GetPosition(_canvas);

        switch (_tool)
        {
            case Tool.Arrow:
                DrawArrow(_start, p);
                break;

            case Tool.Pen:
                if (_pen != null) _undo.Add(_pen);
                _pen = null;
                break;

            case Tool.Rect:
                if (_live != null) _undo.Add(_live);
                break;

            case Tool.Mosaic:
                if (_live != null)
                {
                    _canvas.Children.Remove(_live);   // remove the marquee
                    ApplyMosaic(_start, p);
                }
                break;

            case Tool.Crop:
                if (_cropBox != null)
                {
                    double x = Canvas.GetLeft(_cropBox), y = Canvas.GetTop(_cropBox);
                    _cropRect = new Int32Rect(
                        (int)Math.Round(x), (int)Math.Round(y),
                        (int)Math.Round(_cropBox.Width), (int)Math.Round(_cropBox.Height));
                }
                break;
        }
        _live = null;
    }

    // ---------------- tools ----------------

    private void DrawArrow(System.Windows.Point s, System.Windows.Point e)
    {
        var dx = e.X - s.X; var dy = e.Y - s.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 2) return;
        double ux = dx / len, uy = dy / len;
        double head = Math.Max(10, _thickness * 4);
        double ang = Math.PI / 7;
        var p1 = new System.Windows.Point(
            e.X - head * (ux * Math.Cos(ang) - uy * Math.Sin(ang)),
            e.Y - head * (uy * Math.Cos(ang) + ux * Math.Sin(ang)));
        var p2 = new System.Windows.Point(
            e.X - head * (ux * Math.Cos(-ang) - uy * Math.Sin(-ang)),
            e.Y - head * (uy * Math.Cos(-ang) + ux * Math.Sin(-ang)));

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(s, false, false); ctx.LineTo(e, true, true);
            ctx.BeginFigure(p1, false, false); ctx.LineTo(e, true, true); ctx.LineTo(p2, true, true);
        }
        geo.Freeze();
        var path = new System.Windows.Shapes.Path
        {
            Stroke = new SolidColorBrush(_color), StrokeThickness = _thickness,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, Data = geo
        };
        _canvas.Children.Add(path);
        _undo.Add(path);
    }

    private void PlaceText(System.Windows.Point at)
    {
        var tb = new TextBox
        {
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(_color),
            FontSize = Math.Max(14, _thickness * 6),
            FontWeight = FontWeights.SemiBold,
            MinWidth = 40, AcceptsReturn = true
        };
        Canvas.SetLeft(tb, at.X); Canvas.SetTop(tb, at.Y);
        _canvas.Children.Add(tb);
        _undo.Add(tb);
        tb.Loaded += (_, _) => tb.Focus();
    }

    private void ApplyMosaic(System.Windows.Point a, System.Windows.Point b)
    {
        int x = (int)Math.Round(Math.Min(a.X, b.X));
        int y = (int)Math.Round(Math.Min(a.Y, b.Y));
        int w = (int)Math.Round(Math.Abs(a.X - b.X));
        int h = (int)Math.Round(Math.Abs(a.Y - b.Y));
        if (w < 2 || h < 2) return;
        x = Math.Clamp(x, 0, _pw - 1); y = Math.Clamp(y, 0, _ph - 1);
        w = Math.Clamp(w, 1, _pw - x); h = Math.Clamp(h, 1, _ph - y);

        const int block = 12;
        int sw = Math.Max(1, w / block), sh = Math.Max(1, h / block);

        using var region = _srcBmp.Clone(new Drawing.Rectangle(x, y, w, h), _srcBmp.PixelFormat);
        using var small = new Drawing.Bitmap(sw, sh);
        using (var g = Drawing.Graphics.FromImage(small))
        {
            g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(region, 0, 0, sw, sh);
        }
        using var big = new Drawing.Bitmap(w, h);
        using (var g = Drawing.Graphics.FromImage(big))
        {
            g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(small, 0, 0, w, h);
        }

        var img = new Image { Source = ToBitmapSource(big), Width = w, Height = h, IsHitTestVisible = false };
        Canvas.SetLeft(img, x); Canvas.SetTop(img, y);
        _canvas.Children.Add(img);
        _undo.Add(img);
    }

    private static BitmapSource ToBitmapSource(Drawing.Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        var last = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _canvas.Children.Remove(last);
    }

    // ---------------- save ----------------

    private void Save()
    {
        try
        {
            // Drop focus so a half-typed text box commits.
            Keyboard.ClearFocus();
            _canvas.UpdateLayout();

            var rtb = new RenderTargetBitmap(_pw, _ph, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(_canvas);

            BitmapSource final = rtb;
            if (_cropRect is { } cr && cr.Width > 1 && cr.Height > 1)
            {
                cr.X = Math.Clamp(cr.X, 0, _pw - 1);
                cr.Y = Math.Clamp(cr.Y, 0, _ph - 1);
                cr.Width = Math.Clamp(cr.Width, 1, _pw - cr.X);
                cr.Height = Math.Clamp(cr.Height, 1, _ph - cr.Y);
                final = new CroppedBitmap(rtb, cr);
            }

            string outPath = CaptureStore.NewPath();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(final));
            using (var fs = File.Create(outPath)) enc.Save(fs);

            ResultPath = outPath;
            CrashLog.Telemetry("edit-saved");
            Close();
        }
        catch (Exception ex)
        {
            CrashLog.Write("editor-save", ex);
            System.Windows.MessageBox.Show("저장 실패: " + ex.Message);
        }
    }
}
