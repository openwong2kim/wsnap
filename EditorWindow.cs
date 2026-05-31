using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Drawing = System.Drawing;

namespace Wsnap;

/// <summary>
/// Real-but-focused annotation editor: arrow, line, rect, ellipse, pen, highlighter,
/// text, numbered steps, mosaic, blur, crop. Keyboard-first, undo AND redo, copy or
/// save straight back into the drag-and-drop flow. Coordinates are image pixels
/// throughout (a Viewbox scales the canvas to the window) so the rendered PNG is
/// pixel-exact. Dark, themed chrome to match the rest of wsnap.
/// </summary>
public sealed class EditorWindow : Window
{
    private enum Tool { Select, Arrow, Line, Rect, Ellipse, Pen, Highlight, Text, Counter, Mosaic, Blur, Crop }

    private readonly string _srcPath;
    private readonly Drawing.Bitmap _srcBmp;       // for mosaic/blur sampling
    private readonly Canvas _canvas;               // image-pixel coordinate space
    private readonly Image _baseImage;             // the screenshot itself (never selectable)
    private readonly int _pw, _ph;

    // undo/redo as a small op stack so crop, draw, mosaic, counters all compose.
    private abstract class Op { public abstract void Undo(); public abstract void Redo(); }
    private readonly List<Op> _undo = new();
    private readonly List<Op> _redo = new();

    private Tool _tool = Tool.Arrow;
    private System.Windows.Media.Color _color = System.Windows.Media.Colors.Red;
    private double _thickness = 4;
    private int _counterNext = 1;

    private readonly Dictionary<Tool, ToggleButton> _toolButtons = new();
    private readonly List<Border> _swatches = new();

    // in-progress drawing state
    private System.Windows.Point _start;
    private bool _drawing;
    private Shape? _live;
    private Polyline? _pen;
    private System.Windows.Shapes.Rectangle? _cropBox;
    private Int32Rect? _cropRect;
    private System.Windows.Shapes.Path? _cropDim;

    // selection / move state (Tool.Select)
    private UIElement? _selected;
    private System.Windows.Shapes.Rectangle? _selBox;
    private readonly List<System.Windows.Shapes.Rectangle> _handles = new();
    private bool _moving;
    private System.Windows.Point _moveStart, _movedTotal;

    /// <summary>Path of the saved edited PNG, or null if cancelled.</summary>
    public string? ResultPath { get; private set; }

    public EditorWindow(string srcPath)
    {
        _srcPath = srcPath;
        _srcBmp = new Drawing.Bitmap(srcPath);
        _thickness = Math.Clamp(Settings.Current.EditorThickness, 1, 40);

        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.UriSource = new Uri(srcPath);
        bi.EndInit();
        bi.Freeze();
        _pw = bi.PixelWidth;
        _ph = bi.PixelHeight;

        Title = "wsnap — 편집";
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Theme.Apply(this);

        _canvas = new Canvas
        {
            Width = _pw, Height = _ph, ClipToBounds = true,
            Background = System.Windows.Media.Brushes.Transparent
        };
        _baseImage = new Image
        {
            Source = bi, Width = _pw, Height = _ph,
            Stretch = Stretch.Fill, IsHitTestVisible = false
        };
        _canvas.Children.Add(_baseImage);
        _canvas.MouseLeftButtonDown += OnDown;
        _canvas.MouseMove += OnMove;
        _canvas.MouseLeftButtonUp += OnUp;

        var canvasFrame = new Border
        {
            Background = Theme.Brush("Bg"),
            Child = new Viewbox { Stretch = Stretch.Uniform, Child = _canvas, Margin = new Thickness(14) }
        };

        var root = new DockPanel();
        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(canvasFrame);
        Content = root;

        // Fit to screen.
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(_pw + 60, wa.Width * 0.92);
        Height = Math.Min(_ph + 150, wa.Height * 0.92);

        SetTool(Tool.Arrow);
        SelectSwatch(0);
        KeyDown += OnKey;
        Closed += (_, _) => _srcBmp.Dispose();
    }

    // ---------------- toolbar ----------------

    private Border BuildToolbar()
    {
        var bar = new WrapPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(10, 8, 10, 8) };

        void ToolBtn(string label, Tool t, string tip)
        {
            var b = new ToggleButton
            {
                Style = Theme.Style("ToolToggle"),
                Content = label,
                Margin = new Thickness(1, 1, 1, 1),
                MinWidth = 38,
                ToolTip = tip
            };
            b.Click += (_, _) => SetTool(t);
            _toolButtons[t] = b;
            bar.Children.Add(b);
        }

        ToolBtn("선택", Tool.Select, "선택·이동 (V) · Del 삭제");
        bar.Children.Add(Sep());
        ToolBtn("화살표", Tool.Arrow, "화살표 (A)");
        ToolBtn("직선", Tool.Line, "직선 (L) · Shift=45°");
        ToolBtn("사각", Tool.Rect, "사각형 (R) · Shift=정사각");
        ToolBtn("원", Tool.Ellipse, "타원 (O) · Shift=정원");
        ToolBtn("펜", Tool.Pen, "펜 (P)");
        ToolBtn("형광", Tool.Highlight, "형광펜 (H)");
        ToolBtn("텍스트", Tool.Text, "텍스트 (T)");
        ToolBtn("번호", Tool.Counter, "번호 단계 (N)");
        ToolBtn("모자이크", Tool.Mosaic, "모자이크 (M)");
        ToolBtn("흐림", Tool.Blur, "흐림 (B)");
        ToolBtn("자르기", Tool.Crop, "자르기 (C) · Shift=정사각");

        bar.Children.Add(Sep());

        // thickness segmented
        AddThickness(bar, "가늘게", 2);
        AddThickness(bar, "보통", 5);
        AddThickness(bar, "굵게", 10);

        bar.Children.Add(Sep());

        // color swatches
        var colors = new[]
        {
            System.Windows.Media.Colors.Red, System.Windows.Media.Colors.Orange,
            System.Windows.Media.Colors.Gold, System.Windows.Media.Colors.LimeGreen,
            System.Windows.Media.Colors.DeepSkyBlue, System.Windows.Media.Colors.White,
            System.Windows.Media.Colors.Black
        };
        for (int i = 0; i < colors.Length; i++)
        {
            int idx = i;
            var c = colors[i];
            var sw = new Border
            {
                Width = 22, Height = 22, Margin = new Thickness(2, 2, 2, 2),
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(c),
                BorderBrush = Theme.Stroke(Theme.BorderStrong), BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            sw.MouseLeftButtonDown += (_, _) => { _color = c; SelectSwatch(idx); };
            _swatches.Add(sw);
            bar.Children.Add(sw);
        }
        var custom = new Border
        {
            Width = 22, Height = 22, Margin = new Thickness(4, 2, 2, 2),
            CornerRadius = new CornerRadius(5),
            Background = Theme.Brush("Surface"),
            BorderBrush = Theme.Stroke(Theme.BorderStrong), BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "사용자 지정 색",
            Child = new TextBlock { Text = "+", Foreground = Theme.Brush("Muted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 }
        };
        custom.MouseLeftButtonDown += (_, _) => PickCustomColor();
        bar.Children.Add(custom);

        bar.Children.Add(Sep());

        bar.Children.Add(ActionBtn("복사", "PrimaryButton", () => CopyToClipboard(), "복사 (Ctrl+C)"));
        bar.Children.Add(ActionBtn("저장", "GhostButton", Save, "저장 (Enter)"));
        bar.Children.Add(ActionBtn("취소", "GhostButton", () => { ResultPath = null; Close(); }, "취소 (Esc)"));

        return new Border
        {
            Background = Theme.Brush("Panel"),
            BorderBrush = Theme.Stroke(Theme.Border), BorderThickness = new Thickness(0, 0, 0, 1),
            Child = bar
        };
    }

    private static UIElement Sep() => new Border
    {
        Width = 1, Margin = new Thickness(7, 3, 7, 3),
        Background = Theme.Stroke(Theme.BorderStrong)
    };

    private readonly Dictionary<double, ToggleButton> _thickButtons = new();

    private void AddThickness(WrapPanel bar, string label, double value)
    {
        var b = new ToggleButton
        {
            Style = Theme.Style("ToolToggle"),
            Content = label, Margin = new Thickness(1), MinWidth = 42,
            ToolTip = $"선 두께 {value}px"
        };
        b.Click += (_, _) => SetThickness(value);
        _thickButtons[value] = b;
        bar.Children.Add(b);
    }

    private void SetThickness(double v)
    {
        _thickness = v;
        Settings.Current.EditorThickness = (int)v;
        foreach (var kv in _thickButtons) kv.Value.IsChecked = Math.Abs(kv.Key - v) < 0.01;
    }

    private Button ActionBtn(string text, string styleKey, Action onClick, string tip)
    {
        var b = new Button
        {
            Style = Theme.Style(styleKey), Content = text,
            Margin = new Thickness(3, 1, 0, 1), MinWidth = 60, ToolTip = tip
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void SetTool(Tool t)
    {
        _tool = t;
        if (t != Tool.Select) ClearSelection();
        foreach (var kv in _toolButtons) kv.Value.IsChecked = kv.Key == t;
        // make sure the closest thickness pill reflects current value
        foreach (var kv in _thickButtons) kv.Value.IsChecked = Math.Abs(kv.Key - _thickness) < 0.01;
    }

    private void SelectSwatch(int idx)
    {
        for (int i = 0; i < _swatches.Count; i++)
        {
            bool on = i == idx;
            _swatches[i].BorderBrush = on ? Theme.Brush("Accent") : Theme.Stroke(Theme.BorderStrong);
            _swatches[i].BorderThickness = new Thickness(on ? 2.5 : 1);
        }
    }

    private void PickCustomColor()
    {
        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _color = System.Windows.Media.Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
            SelectSwatch(-1);  // none of the presets active
        }
    }

    // ---------------- input ----------------

    private void OnKey(object sender, KeyEventArgs e)
    {
        // While typing in a text annotation, let the TextBox own the keys.
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox)
        {
            if (e.Key == Key.Escape) Keyboard.ClearFocus();
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (_tool == Tool.Select && _selected != null && (e.Key == Key.Delete || e.Key == Key.Back))
        { Commit(new DeleteOp(this, _canvas, _selected)); e.Handled = true; return; }

        if (e.Key == Key.Escape) { if (_selected != null) { ClearSelection(); return; } ResultPath = null; Close(); return; }
        if (e.Key == Key.Enter) { Save(); return; }
        if (ctrl && e.Key == Key.C) { CopyToClipboard(shift); return; }
        if (ctrl && e.Key == Key.S) { Save(); return; }
        if (ctrl && (e.Key == Key.Y || (shift && e.Key == Key.Z))) { Redo(); return; }
        if (ctrl && e.Key == Key.Z) { Undo(); return; }

        var t = e.Key switch
        {
            Key.V => Tool.Select,
            Key.A => Tool.Arrow,
            Key.L => Tool.Line,
            Key.R => Tool.Rect,
            Key.O => Tool.Ellipse,
            Key.P => Tool.Pen,
            Key.H => Tool.Highlight,
            Key.T => Tool.Text,
            Key.N => Tool.Counter,
            Key.M => Tool.Mosaic,
            Key.B => Tool.Blur,
            Key.C => Tool.Crop,
            _ => (Tool?)null
        };
        if (t is { } tool) SetTool(tool);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(_canvas);

        if (_tool == Tool.Select)
        {
            var hit = HitTopMost(_start);
            if (hit == null) { ClearSelection(); return; }
            if (hit != _selected) ShowSelection(hit);
            _moving = true; _moveStart = _start; _movedTotal = new System.Windows.Point(0, 0);
            _canvas.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_tool == Tool.Text) { PlaceText(_start); return; }
        if (_tool == Tool.Counter) { PlaceCounter(_start); return; }

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
                    Fill = System.Windows.Media.Brushes.Transparent
                };
                Canvas.SetLeft(_live, _start.X); Canvas.SetTop(_live, _start.Y);
                _canvas.Children.Add(_live);
                if (_tool == Tool.Crop) _cropBox = (System.Windows.Shapes.Rectangle)_live;
                break;

            case Tool.Ellipse:
                _live = new System.Windows.Shapes.Ellipse
                {
                    Stroke = brush, StrokeThickness = _thickness,
                    Fill = System.Windows.Media.Brushes.Transparent
                };
                Canvas.SetLeft(_live, _start.X); Canvas.SetTop(_live, _start.Y);
                _canvas.Children.Add(_live);
                break;

            case Tool.Mosaic:
            case Tool.Blur:
                _live = new System.Windows.Shapes.Rectangle
                {
                    Stroke = System.Windows.Media.Brushes.White, StrokeThickness = 1,
                    Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(60, 255, 255, 255))
                };
                Canvas.SetLeft(_live, _start.X); Canvas.SetTop(_live, _start.Y);
                _canvas.Children.Add(_live);
                break;

            case Tool.Pen:
            case Tool.Highlight:
                bool hl = _tool == Tool.Highlight;
                var penBrush = hl
                    ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x66, _color.R, _color.G, _color.B))
                    : brush;
                _pen = new Polyline
                {
                    Stroke = penBrush,
                    StrokeThickness = hl ? Math.Max(10, _thickness * 3) : _thickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = hl ? PenLineCap.Flat : PenLineCap.Round,
                    StrokeEndLineCap = hl ? PenLineCap.Flat : PenLineCap.Round
                };
                _pen.Points.Add(_start);
                _canvas.Children.Add(_pen);
                break;
        }
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_tool == Tool.Select)
        {
            var sp = e.GetPosition(_canvas);
            if (!_moving || _selected == null)
            {
                _canvas.Cursor = HitTopMost(sp) != null ? Cursors.SizeAll : Cursors.Arrow;
                return;
            }
            double mdx = sp.X - _moveStart.X, mdy = sp.Y - _moveStart.Y;
            Translate(_selected, mdx, mdy);
            if (_selBox != null) { Canvas.SetLeft(_selBox, Canvas.GetLeft(_selBox) + mdx); Canvas.SetTop(_selBox, Canvas.GetTop(_selBox) + mdy); }
            foreach (var h in _handles) { Canvas.SetLeft(h, Canvas.GetLeft(h) + mdx); Canvas.SetTop(h, Canvas.GetTop(h) + mdy); }
            _movedTotal.X += mdx; _movedTotal.Y += mdy;
            _moveStart = sp;
            return;
        }

        if (!_drawing) return;
        var p = e.GetPosition(_canvas);
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if ((_tool == Tool.Pen || _tool == Tool.Highlight) && _pen != null) { _pen.Points.Add(p); return; }

        if (_live is FrameworkElement fe && (_live is System.Windows.Shapes.Rectangle || _live is System.Windows.Shapes.Ellipse))
        {
            double w = Math.Abs(p.X - _start.X), h = Math.Abs(p.Y - _start.Y);
            if (shift && (_tool == Tool.Rect || _tool == Tool.Ellipse || _tool == Tool.Crop || _tool == Tool.Mosaic || _tool == Tool.Blur))
            { double s = Math.Min(w, h); w = h = s; }
            double x = p.X < _start.X ? _start.X - w : _start.X;
            double y = p.Y < _start.Y ? _start.Y - h : _start.Y;
            Canvas.SetLeft(fe, x); Canvas.SetTop(fe, y);
            fe.Width = w; fe.Height = h;
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_tool == Tool.Select)
        {
            if (_moving && _selected != null)
            {
                _moving = false; _canvas.ReleaseMouseCapture();
                if (Math.Abs(_movedTotal.X) > 0.5 || Math.Abs(_movedTotal.Y) > 0.5)
                { _undo.Add(new MoveOp(_selected, _movedTotal.X, _movedTotal.Y, this)); _redo.Clear(); }
            }
            return;
        }

        if (!_drawing) return;
        _drawing = false;
        _canvas.ReleaseMouseCapture();
        var p = e.GetPosition(_canvas);
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        switch (_tool)
        {
            case Tool.Arrow:
            case Tool.Line:
                DrawArrowOrLine(_start, ConstrainEnd(_start, p, shift), _tool == Tool.Arrow);
                break;

            case Tool.Pen:
            case Tool.Highlight:
                if (_pen != null) Commit(new AddOp(_canvas, _pen));
                _pen = null;
                break;

            case Tool.Rect:
            case Tool.Ellipse:
                if (_live != null) Commit(new AddOp(_canvas, _live));
                break;

            case Tool.Mosaic:
                if (_live != null) { _canvas.Children.Remove(_live); ApplyPixelate(_start, p, mosaic: true); }
                break;

            case Tool.Blur:
                if (_live != null) { _canvas.Children.Remove(_live); ApplyPixelate(_start, p, mosaic: false); }
                break;

            case Tool.Crop:
                if (_cropBox != null)
                {
                    double x = Canvas.GetLeft(_cropBox), y = Canvas.GetTop(_cropBox);
                    var rect = new Int32Rect(
                        (int)Math.Round(x), (int)Math.Round(y),
                        (int)Math.Round(_cropBox.Width), (int)Math.Round(_cropBox.Height));
                    _canvas.Children.Remove(_cropBox);   // remove the dashed marquee (it would render otherwise)
                    _cropBox = null;
                    if (rect.Width > 1 && rect.Height > 1) Commit(new CropOp(this, rect));
                }
                break;
        }
        _live = null;
    }

    private static System.Windows.Point ConstrainEnd(System.Windows.Point s, System.Windows.Point e, bool shift)
    {
        if (!shift) return e;
        double dx = e.X - s.X, dy = e.Y - s.Y;
        double ang = Math.Atan2(dy, dx);
        double snap = Math.Round(ang / (Math.PI / 4)) * (Math.PI / 4);
        double len = Math.Sqrt(dx * dx + dy * dy);
        return new System.Windows.Point(s.X + len * Math.Cos(snap), s.Y + len * Math.Sin(snap));
    }

    // ---------------- tools ----------------

    private void DrawArrowOrLine(System.Windows.Point s, System.Windows.Point e, bool arrow)
    {
        var dx = e.X - s.X; var dy = e.Y - s.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 2) return;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(s, false, false); ctx.LineTo(e, true, true);
            if (arrow)
            {
                double ux = dx / len, uy = dy / len;
                double head = Math.Max(10, _thickness * 4);
                double a = Math.PI / 7;
                var p1 = new System.Windows.Point(
                    e.X - head * (ux * Math.Cos(a) - uy * Math.Sin(a)),
                    e.Y - head * (uy * Math.Cos(a) + ux * Math.Sin(a)));
                var p2 = new System.Windows.Point(
                    e.X - head * (ux * Math.Cos(-a) - uy * Math.Sin(-a)),
                    e.Y - head * (uy * Math.Cos(-a) + ux * Math.Sin(-a)));
                ctx.BeginFigure(p1, false, false); ctx.LineTo(e, true, true); ctx.LineTo(p2, true, true);
            }
        }
        geo.Freeze();
        var path = new System.Windows.Shapes.Path
        {
            Stroke = new SolidColorBrush(_color), StrokeThickness = _thickness,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, Data = geo
        };
        Commit(new AddOp(_canvas, path));
    }

    private void PlaceText(System.Windows.Point at)
    {
        var tb = new TextBox
        {
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(_color),
            CaretBrush = new SolidColorBrush(_color),
            FontSize = Math.Max(16, _thickness * 5),
            FontWeight = FontWeights.SemiBold,
            FontFamily = Theme.Font,
            MinWidth = 40, AcceptsReturn = true
        };
        Canvas.SetLeft(tb, at.X); Canvas.SetTop(tb, at.Y);
        Commit(new AddOp(_canvas, tb));
        tb.Loaded += (_, _) => tb.Focus();
    }

    private void PlaceCounter(System.Windows.Point at)
    {
        double d = Math.Max(26, _thickness * 7);
        int n = _counterNext;
        bool darkText = IsBright(_color);
        var dot = new Grid { Width = d, Height = d };
        dot.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Fill = new SolidColorBrush(_color),
            Stroke = System.Windows.Media.Brushes.White, StrokeThickness = 2
        });
        dot.Children.Add(new TextBlock
        {
            Text = n.ToString(),
            Foreground = darkText ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White,
            FontWeight = FontWeights.Bold, FontFamily = Theme.Font,
            FontSize = d * 0.5,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        Canvas.SetLeft(dot, at.X - d / 2); Canvas.SetTop(dot, at.Y - d / 2);
        _counterNext++;
        Commit(new AddOp(_canvas, dot, onUndo: () => _counterNext--, onRedo: () => _counterNext++));
    }

    private static bool IsBright(System.Windows.Media.Color c)
        => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) > 160;

    private void ApplyPixelate(System.Windows.Point a, System.Windows.Point b, bool mosaic)
    {
        int x = (int)Math.Round(Math.Min(a.X, b.X));
        int y = (int)Math.Round(Math.Min(a.Y, b.Y));
        int w = (int)Math.Round(Math.Abs(a.X - b.X));
        int h = (int)Math.Round(Math.Abs(a.Y - b.Y));
        if (w < 2 || h < 2) return;
        x = Math.Clamp(x, 0, _pw - 1); y = Math.Clamp(y, 0, _ph - 1);
        w = Math.Clamp(w, 1, _pw - x); h = Math.Clamp(h, 1, _ph - y);

        int divisor = mosaic ? 12 : 5;
        int sw = Math.Max(1, w / (mosaic ? divisor : divisor * 3));
        int sh = Math.Max(1, h / (mosaic ? divisor : divisor * 3));

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
            g.InterpolationMode = mosaic
                ? Drawing.Drawing2D.InterpolationMode.NearestNeighbor
                : Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(small, 0, 0, w, h);
        }

        var img = new Image { Source = ToBitmapSource(big), Width = w, Height = h, IsHitTestVisible = false };
        Canvas.SetLeft(img, x); Canvas.SetTop(img, y);
        Commit(new AddOp(_canvas, img));
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

    // ---------------- selection / move (Tool.Select) ----------------

    private bool Selectable(UIElement el)
        => el != _baseImage && el != _cropDim && el != _selBox
           && (el is not System.Windows.Shapes.Rectangle r || !_handles.Contains(r));

    private UIElement? HitTopMost(System.Windows.Point p)
    {
        for (int i = _canvas.Children.Count - 1; i >= 0; i--)
        {
            var el = _canvas.Children[i];
            if (!Selectable(el)) continue;
            if (HitBounds(el, p)) return el;
        }
        return null;
    }

    private static bool HitBounds(UIElement el, System.Windows.Point p)
    {
        const double pad = 8;
        if (el is Polyline pl)
        {
            double t = Math.Max(pad, pl.StrokeThickness / 2 + 4);
            for (int i = 1; i < pl.Points.Count; i++)
                if (DistToSeg(p, pl.Points[i - 1], pl.Points[i]) <= t) return true;
            return pl.Points.Count > 0 && (p - pl.Points[0]).Length <= t;
        }
        if (el is System.Windows.Shapes.Path path)
        {
            var b = path.Data?.Bounds ?? Rect.Empty;
            if (b.IsEmpty) return false;
            b.Inflate(Math.Max(pad, path.StrokeThickness), Math.Max(pad, path.StrokeThickness));
            return b.Contains(p);
        }
        if (el is FrameworkElement fe)
        {
            double x = Canvas.GetLeft(fe), y = Canvas.GetTop(fe);
            if (double.IsNaN(x)) x = 0; if (double.IsNaN(y)) y = 0;
            double w = double.IsNaN(fe.Width) ? fe.ActualWidth : fe.Width;
            double h = double.IsNaN(fe.Height) ? fe.ActualHeight : fe.Height;
            return new Rect(x - pad, y - pad, w + 2 * pad, h + 2 * pad).Contains(p);
        }
        return false;
    }

    private static double DistToSeg(System.Windows.Point p, System.Windows.Point a, System.Windows.Point b)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y, wx = p.X - a.X, wy = p.Y - a.Y;
        double c1 = vx * wx + vy * wy; if (c1 <= 0) return (p - a).Length;
        double c2 = vx * vx + vy * vy; if (c2 <= c1) return (p - b).Length;
        double t = c1 / c2; return (p - new System.Windows.Point(a.X + t * vx, a.Y + t * vy)).Length;
    }

    /// <summary>Translate any annotation type by (dx,dy). One function, reused by drag + MoveOp.</summary>
    private static void Translate(UIElement el, double dx, double dy)
    {
        if (el is Polyline pl)
        {
            var pts = pl.Points;
            for (int i = 0; i < pts.Count; i++) pts[i] = new System.Windows.Point(pts[i].X + dx, pts[i].Y + dy);
            return;
        }
        if (el is System.Windows.Shapes.Path path && path.Data is Geometry g)
        {
            // arrow/line geometry is in absolute canvas coords with no Canvas.Left/Top;
            // it's frozen → clone, compose a TranslateTransform, re-freeze, reassign.
            var clone = g.Clone();
            var tg = new TransformGroup();
            if (clone.Transform != null && clone.Transform != Transform.Identity) tg.Children.Add(clone.Transform);
            tg.Children.Add(new TranslateTransform(dx, dy));
            clone.Transform = tg;
            clone.Freeze();
            path.Data = clone;
            return;
        }
        double x = Canvas.GetLeft(el), y = Canvas.GetTop(el);
        if (double.IsNaN(x)) x = 0; if (double.IsNaN(y)) y = 0;
        Canvas.SetLeft(el, x + dx); Canvas.SetTop(el, y + dy);
    }

    private void ShowSelection(UIElement el)
    {
        ClearSelectionVisuals();
        _selected = el;
        var b = SelectionRect(el);
        if (b.IsEmpty) return;
        _selBox = new System.Windows.Shapes.Rectangle
        {
            Stroke = Theme.Brush("Accent"), StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            Fill = System.Windows.Media.Brushes.Transparent,
            IsHitTestVisible = false, Width = b.Width, Height = b.Height
        };
        Canvas.SetLeft(_selBox, b.X); Canvas.SetTop(_selBox, b.Y);
        _canvas.Children.Add(_selBox);
        foreach (var hp in HandlePoints(b))
        {
            var hsq = new System.Windows.Shapes.Rectangle
            {
                Width = 8, Height = 8, Fill = System.Windows.Media.Brushes.White,
                Stroke = Theme.Brush("Accent"), StrokeThickness = 1, IsHitTestVisible = false
            };
            Canvas.SetLeft(hsq, hp.X - 4); Canvas.SetTop(hsq, hp.Y - 4);
            _handles.Add(hsq); _canvas.Children.Add(hsq);
        }
    }

    private Rect SelectionRect(UIElement el)
    {
        if (el is Polyline pl && pl.Points.Count > 0)
        {
            double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
            foreach (var pt in pl.Points) { minx = Math.Min(minx, pt.X); miny = Math.Min(miny, pt.Y); maxx = Math.Max(maxx, pt.X); maxy = Math.Max(maxy, pt.Y); }
            double pad = pl.StrokeThickness / 2 + 2;
            return new Rect(minx - pad, miny - pad, (maxx - minx) + 2 * pad, (maxy - miny) + 2 * pad);
        }
        if (el is System.Windows.Shapes.Path path && path.Data != null)
        {
            var bb = path.Data.Bounds; double pad = path.StrokeThickness / 2 + 2;
            bb.Inflate(pad, pad); return bb;
        }
        if (el is FrameworkElement fe)
        {
            double x = Canvas.GetLeft(fe), y = Canvas.GetTop(fe);
            if (double.IsNaN(x)) x = 0; if (double.IsNaN(y)) y = 0;
            double w = double.IsNaN(fe.Width) ? fe.ActualWidth : fe.Width;
            double h = double.IsNaN(fe.Height) ? fe.ActualHeight : fe.Height;
            return new Rect(x, y, w, h);
        }
        return Rect.Empty;
    }

    private static IEnumerable<System.Windows.Point> HandlePoints(Rect b)
    {
        double mx = b.X + b.Width / 2, my = b.Y + b.Height / 2;
        return new[]
        {
            new System.Windows.Point(b.X, b.Y), new System.Windows.Point(mx, b.Y), new System.Windows.Point(b.Right, b.Y),
            new System.Windows.Point(b.X, my), new System.Windows.Point(b.Right, my),
            new System.Windows.Point(b.X, b.Bottom), new System.Windows.Point(mx, b.Bottom), new System.Windows.Point(b.Right, b.Bottom)
        };
    }

    private void ClearSelectionVisuals()
    {
        if (_selBox != null) { _canvas.Children.Remove(_selBox); _selBox = null; }
        foreach (var h in _handles) _canvas.Children.Remove(h);
        _handles.Clear();
    }

    private void ClearSelection() { ClearSelectionVisuals(); _selected = null; }

    private void RefreshSelectionIfSelected(UIElement el) { if (_selected == el) ShowSelection(el); }

    // ---------------- undo / redo ----------------

    private sealed class MoveOp : Op
    {
        private readonly UIElement _el; private readonly double _dx, _dy; private readonly EditorWindow _w;
        // The live drag ALREADY moved the element — do NOT translate again in the ctor.
        public MoveOp(UIElement el, double dx, double dy, EditorWindow w) { _el = el; _dx = dx; _dy = dy; _w = w; }
        public override void Undo() { Translate(_el, -_dx, -_dy); _w.RefreshSelectionIfSelected(_el); }
        public override void Redo() { Translate(_el, _dx, _dy); _w.RefreshSelectionIfSelected(_el); }
    }

    private sealed class DeleteOp : Op
    {
        private readonly Canvas _c; private readonly UIElement _el; private readonly int _index; private readonly EditorWindow _w;
        public DeleteOp(EditorWindow w, Canvas c, UIElement el) { _w = w; _c = c; _el = el; _index = c.Children.IndexOf(el); c.Children.Remove(el); w.ClearSelection(); }
        public override void Undo() { if (!_c.Children.Contains(_el)) { if (_index >= 0 && _index <= _c.Children.Count) _c.Children.Insert(_index, _el); else _c.Children.Add(_el); } }
        public override void Redo() { _c.Children.Remove(_el); _w.ClearSelection(); }
    }

    private sealed class AddOp : Op
    {
        private readonly Canvas _c; private readonly UIElement _el;
        private readonly Action? _onUndo, _onRedo;
        public AddOp(Canvas c, UIElement el, Action? onUndo = null, Action? onRedo = null)
        { _c = c; _el = el; _onUndo = onUndo; _onRedo = onRedo; if (!_c.Children.Contains(_el)) _c.Children.Add(_el); }
        public override void Undo() { _c.Children.Remove(_el); _onUndo?.Invoke(); }
        public override void Redo() { if (!_c.Children.Contains(_el)) _c.Children.Add(_el); _onRedo?.Invoke(); }
    }

    private sealed class CropOp : Op
    {
        private readonly EditorWindow _w; private readonly Int32Rect _next; private readonly Int32Rect? _prev;
        public CropOp(EditorWindow w, Int32Rect next) { _w = w; _next = next; _prev = w._cropRect; w.ApplyCrop(next); }
        public override void Undo() => _w.ApplyCrop(_prev);
        public override void Redo() => _w.ApplyCrop(_next);
    }

    private void ApplyCrop(Int32Rect? rect)
    {
        _cropRect = rect;
        if (_cropDim != null) { _canvas.Children.Remove(_cropDim); _cropDim = null; }
        if (rect is { } r)
        {
            var grp = new GeometryGroup { FillRule = FillRule.EvenOdd };
            grp.Children.Add(new RectangleGeometry(new Rect(0, 0, _pw, _ph)));
            grp.Children.Add(new RectangleGeometry(new Rect(r.X, r.Y, r.Width, r.Height)));
            _cropDim = new System.Windows.Shapes.Path
            {
                Data = grp,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x88, 0, 0, 0)),
                IsHitTestVisible = false
            };
            _canvas.Children.Add(_cropDim);
        }
    }

    private void Commit(Op op)
    {
        _undo.Add(op);
        _redo.Clear();
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        var op = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        op.Undo();
        _redo.Add(op);
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        var op = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        op.Redo();
        _undo.Add(op);
    }

    // ---------------- render / save / copy ----------------

    private BitmapSource RenderFinal()
    {
        Keyboard.ClearFocus();
        ClearSelectionVisuals();                                          // marquee + handles are guides, never baked in
        if (_cropDim != null) _cropDim.Visibility = Visibility.Collapsed;  // guide only — never baked in
        _canvas.UpdateLayout();

        var rtb = new RenderTargetBitmap(_pw, _ph, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(_canvas);

        if (_cropDim != null) _cropDim.Visibility = Visibility.Visible;

        BitmapSource final = rtb;
        if (_cropRect is { } cr && cr.Width > 1 && cr.Height > 1)
        {
            cr.X = Math.Clamp(cr.X, 0, _pw - 1);
            cr.Y = Math.Clamp(cr.Y, 0, _ph - 1);
            cr.Width = Math.Clamp(cr.Width, 1, _pw - cr.X);
            cr.Height = Math.Clamp(cr.Height, 1, _ph - cr.Y);
            final = new CroppedBitmap(rtb, cr);
        }
        final.Freeze();
        return final;
    }

    private void CopyToClipboard(bool andClose = false)
    {
        try
        {
            var img = RenderFinal();
            if (ImageClipboard.CopyImageSource(img)) Toast.Show("이미지 복사됨 ✓");
            else Toast.Show("복사 실패");
            CrashLog.Telemetry("edit-copied");
            if (andClose) { ResultPath = null; Close(); }
        }
        catch (Exception ex) { CrashLog.Write("editor-copy", ex); Toast.Show("복사 실패: " + ex.Message); }
    }

    private void Save()
    {
        try
        {
            var final = RenderFinal();
            string outPath = CaptureStore.NewPath();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(final));
            using (var fs = File.Create(outPath)) enc.Save(fs);

            ResultPath = outPath;
            CaptureStore.PruneScratch();
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
