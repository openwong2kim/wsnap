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
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Drawing = System.Drawing;
using AvColor = Avalonia.Media.Color;
using AvColors = Avalonia.Media.Colors;
using AvBrushes = Avalonia.Media.Brushes;
using AvRect = Avalonia.Rect;
using AvPoint = Avalonia.Point;

namespace Wsnap;

/// <summary>
/// Avalonia port (Phase 5) of the annotation editor: arrow, line, rect, ellipse, pen,
/// highlighter, text, numbered steps, mosaic, blur, crop. Keyboard-first, undo AND redo,
/// copy or save straight back into the flow. Coordinates are image pixels throughout
/// (a Viewbox scales the canvas) so the rendered PNG is pixel-exact.
///
/// Port decisions (do not regress):
///  • Arrow/line Paths are positioned via Canvas.Left/Top with geometry RELATIVE to their
///    own bounds origin (WPF used absolute frozen geometry + clone-transform moves, which
///    Avalonia's Geometry API doesn't support cleanly) — so Translate() is one code path.
///  • The crop dim is FOUR rectangles in a child canvas — even-odd geometry renders wrong
///    on Avalonia/Skia (spike (a)).
///  • RenderFinal uses Avalonia RenderTargetBitmap at 96 DPI 1:1 (this is the product
///    rasterizer, not screen verification) and crops via SkiaSharp (Phase 0 stack).
/// </summary>
public sealed class EditorWindow : Avalonia.Controls.Window
{
    private enum Tool { Select, Arrow, Line, Rect, Ellipse, Pen, Highlight, Text, Counter, Mosaic, Blur, Crop }

    private readonly string _srcPath;
    private readonly Drawing.Bitmap _srcBmp;       // for mosaic/blur sampling
    private readonly Canvas _canvas;               // image-pixel coordinate space
    private readonly Avalonia.Controls.Image _baseImage;
    private readonly int _pw, _ph;

    // undo/redo as a small op stack so crop, draw, mosaic, counters all compose.
    private abstract class Op { public abstract void Undo(); public abstract void Redo(); }
    private readonly List<Op> _undo = new();
    private readonly List<Op> _redo = new();

    private Tool _tool = Tool.Arrow;
    private AvColor _color = AvColors.Red;
    private double _thickness = 4;
    private int _counterNext = 1;

    private readonly Dictionary<Tool, ToggleButton> _toolButtons = new();
    private readonly List<Border> _swatches = new();

    // in-progress drawing state
    private AvPoint _start;
    private bool _drawing;
    private Shape? _live;
    private Polyline? _pen;
    private Avalonia.Controls.Shapes.Rectangle? _cropBox;
    private PixelRect? _cropRect;
    private Canvas? _cropDim;                       // 4-rect dim (spike (a) — no even-odd geometry)

    // selection / move state (Tool.Select)
    private Avalonia.Controls.Control? _selected;
    private Avalonia.Controls.Shapes.Rectangle? _selBox;
    private readonly List<Avalonia.Controls.Shapes.Rectangle> _handles = new();
    private bool _moving;
    private AvPoint _moveStart;
    private double _movedX, _movedY;

    /// <summary>Path of the saved edited PNG, or null if cancelled.</summary>
    public string? ResultPath { get; private set; }

    public EditorWindow(string srcPath)
    {
        _srcPath = srcPath;
        _srcBmp = new Drawing.Bitmap(srcPath);
        _thickness = Math.Clamp(Settings.Current.EditorThickness, 1, 40);
        _pw = _srcBmp.Width;
        _ph = _srcBmp.Height;

        Title = L.T("ed.title");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AppTheme.Apply(this);

        _canvas = new Canvas
        {
            Width = _pw, Height = _ph, ClipToBounds = true,
            Background = AvBrushes.Transparent
        };
        Bitmap baseBmp;
        using (var fs = File.OpenRead(srcPath)) baseBmp = new Bitmap(fs);
        _baseImage = new Avalonia.Controls.Image
        {
            Source = baseBmp, Width = _pw, Height = _ph,
            Stretch = Stretch.Fill, IsHitTestVisible = false
        };
        _canvas.Children.Add(_baseImage);
        _canvas.PointerPressed += OnDown;
        _canvas.PointerMoved += OnMove;
        _canvas.PointerReleased += OnUp;

        var canvasFrame = new Border
        {
            Background = AppTheme.Brush("Bg"),
            Child = new Viewbox { Stretch = Stretch.Uniform, Child = _canvas, Margin = new Thickness(14) }
        };

        var root = new DockPanel();
        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(canvasFrame);
        Content = root;

        // Fit to screen (logical work area).
        double waW = 1600, waH = 900;
        if (Screens.Primary is { } scr) { waW = scr.WorkingArea.Width / scr.Scaling; waH = scr.WorkingArea.Height / scr.Scaling; }
        Width = Math.Min(_pw + 60, waW * 0.92);
        Height = Math.Min(_ph + 150, waH * 0.92);

        SetTool(Tool.Arrow);
        SelectSwatch(0);
        KeyDown += OnKey;
        DragDrop.SetAllowDrop(this, true);          // accept images dropped anywhere on the window
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Closed += (_, _) => _srcBmp.Dispose();
    }

    // ---------------- toolbar ----------------

    private Border BuildToolbar()
    {
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 10, 8) };

        void ToolBtn(string label, Tool t, string tip)
        {
            var b = new ToggleButton
            {
                Content = label,
                Margin = new Thickness(1, 1, 1, 1),
                MinWidth = 38
            };
            b.Classes.Add("tool");
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => SetTool(t);
            _toolButtons[t] = b;
            bar.Children.Add(b);
        }

        ToolBtn(L.T("ed.toolSelect"), Tool.Select, L.T("ed.toolSelectTip"));
        bar.Children.Add(Sep());
        ToolBtn(L.T("ed.toolArrow"), Tool.Arrow, L.T("ed.toolArrowTip"));
        ToolBtn(L.T("ed.toolLine"), Tool.Line, L.T("ed.toolLineTip"));
        ToolBtn(L.T("ed.toolRect"), Tool.Rect, L.T("ed.toolRectTip"));
        ToolBtn(L.T("ed.toolEllipse"), Tool.Ellipse, L.T("ed.toolEllipseTip"));
        ToolBtn(L.T("ed.toolPen"), Tool.Pen, L.T("ed.toolPenTip"));
        ToolBtn(L.T("ed.toolHighlight"), Tool.Highlight, L.T("ed.toolHighlightTip"));
        ToolBtn(L.T("ed.toolText"), Tool.Text, L.T("ed.toolTextTip"));
        ToolBtn(L.T("ed.toolCounter"), Tool.Counter, L.T("ed.toolCounterTip"));
        ToolBtn(L.T("ed.toolMosaic"), Tool.Mosaic, L.T("ed.toolMosaicTip"));
        ToolBtn(L.T("ed.toolBlur"), Tool.Blur, L.T("ed.toolBlurTip"));
        ToolBtn(L.T("ed.toolCrop"), Tool.Crop, L.T("ed.toolCropTip"));

        bar.Children.Add(Sep());

        // thickness segmented
        AddThickness(bar, L.T("ed.thin"), 2);
        AddThickness(bar, L.T("ed.medium"), 5);
        AddThickness(bar, L.T("ed.thick"), 10);

        bar.Children.Add(Sep());

        // color swatches
        var colors = new[]
        {
            AvColors.Red, AvColors.Orange, AvColors.Gold, AvColors.LimeGreen,
            AvColors.DeepSkyBlue, AvColors.White, AvColors.Black
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
                BorderBrush = new SolidColorBrush(AppTheme.BorderStrong), BorderThickness = new Thickness(1),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            sw.PointerPressed += (_, _) => { _color = c; SelectSwatch(idx); };
            _swatches.Add(sw);
            bar.Children.Add(sw);
        }
        var custom = new Border
        {
            Width = 22, Height = 22, Margin = new Thickness(4, 2, 2, 2),
            CornerRadius = new CornerRadius(5),
            Background = AppTheme.Brush("Surface"),
            BorderBrush = new SolidColorBrush(AppTheme.BorderStrong), BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock { Text = "+", Foreground = AppTheme.Brush("Muted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 }
        };
        ToolTip.SetTip(custom, L.T("ed.customColor"));
        custom.PointerPressed += (_, _) => PickCustomColor();
        bar.Children.Add(custom);

        bar.Children.Add(Sep());

        bar.Children.Add(ActionBtn(L.T("ed.copy"), "primary", () => CopyToClipboard(), L.T("ed.copyTip")));
        bar.Children.Add(ActionBtn(L.T("ed.save"), "ghost", Save, L.T("ed.saveTip")));
        bar.Children.Add(ActionBtn(L.T("ed.cancel"), "ghost", () => { ResultPath = null; Close(); }, L.T("ed.cancelTip")));

        return new Border
        {
            Background = AppTheme.Brush("Panel"),
            BorderBrush = new SolidColorBrush(AppTheme.Border), BorderThickness = new Thickness(0, 0, 0, 1),
            Child = bar
        };
    }

    private static Avalonia.Controls.Control Sep() => new Border
    {
        Width = 1, Margin = new Thickness(7, 3, 7, 3),
        Background = new SolidColorBrush(AppTheme.BorderStrong)
    };

    private readonly Dictionary<double, ToggleButton> _thickButtons = new();

    private void AddThickness(WrapPanel bar, string label, double value)
    {
        var b = new ToggleButton { Content = label, Margin = new Thickness(1), MinWidth = 42 };
        b.Classes.Add("tool");
        ToolTip.SetTip(b, L.T("ed.strokeTip", value));
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

    private Button ActionBtn(string text, string cls, Action onClick, string tip)
    {
        var b = new Button { Content = text, Margin = new Thickness(3, 1, 0, 1), MinWidth = 60 };
        b.Classes.Add(cls);
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    private void SetTool(Tool t)
    {
        _tool = t;
        if (t != Tool.Select) ClearSelection();
        foreach (var kv in _toolButtons) kv.Value.IsChecked = kv.Key == t;
        foreach (var kv in _thickButtons) kv.Value.IsChecked = Math.Abs(kv.Key - _thickness) < 0.01;
    }

    private void SelectSwatch(int idx)
    {
        for (int i = 0; i < _swatches.Count; i++)
        {
            bool on = i == idx;
            _swatches[i].BorderBrush = on ? AppTheme.Brush("Accent") : new SolidColorBrush(AppTheme.BorderStrong);
            _swatches[i].BorderThickness = new Thickness(on ? 2.5 : 1);
        }
    }

    private void PickCustomColor()
    {
        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _color = AvColor.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
            SelectSwatch(-1);  // none of the presets active
        }
    }

    // ---------------- input ----------------

    private void OnKey(object? sender, KeyEventArgs e)
    {
        // While typing in a text annotation, let the TextBox own the keys.
        if (FocusManager?.GetFocusedElement() is TextBox focusedBox)
        {
            if (e.Key == Key.Escape) Focus();   // pull focus off the box (commits/drops on LostFocus)
            return;
        }

        bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;

        if (_tool == Tool.Select && _selected != null && (e.Key == Key.Delete || e.Key == Key.Back))
        { Commit(new DeleteOp(this, _canvas, _selected)); e.Handled = true; return; }

        if (e.Key == Key.Escape) { if (_selected != null) { ClearSelection(); return; } ResultPath = null; Close(); return; }
        if (e.Key == Key.Enter) { Save(); return; }
        if (ctrl && e.Key == Key.C) { CopyToClipboard(shift); return; }
        if (ctrl && e.Key == Key.V) { PasteFromClipboard(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.S) { Save(); return; }
        if (ctrl && (e.Key == Key.Y || (shift && e.Key == Key.Z))) { Redo(); return; }
        if (ctrl && e.Key == Key.Z) { Undo(); return; }

        if (e.Key == Key.ImeProcessed) return;   // IME-composition keys must not trigger tool shortcuts
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

    private void OnDown(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed) return;
        _start = e.GetPosition(_canvas);

        if (_tool == Tool.Select)
        {
            var hit = HitTopMost(_start);
            if (hit == null) { ClearSelection(); return; }
            if (hit != _selected) ShowSelection(hit);
            _moving = true; _moveStart = _start; _movedX = 0; _movedY = 0;
            e.Pointer.Capture(_canvas);
            e.Handled = true;
            return;
        }

        if (_tool == Tool.Text) { PlaceText(_start); e.Handled = true; return; }
        if (_tool == Tool.Counter) { PlaceCounter(_start); e.Handled = true; return; }

        _drawing = true;
        e.Pointer.Capture(_canvas);
        var brush = new SolidColorBrush(_color);

        switch (_tool)
        {
            case Tool.Rect:
            case Tool.Crop:
                _live = new Avalonia.Controls.Shapes.Rectangle
                {
                    Stroke = _tool == Tool.Crop ? AvBrushes.White : brush,
                    StrokeThickness = _tool == Tool.Crop ? 1.5 : _thickness,
                    StrokeDashArray = _tool == Tool.Crop ? new Avalonia.Collections.AvaloniaList<double> { 4, 3 } : null,
                    Fill = AvBrushes.Transparent
                };
                Canvas.SetLeft(_live, _start.X); Canvas.SetTop(_live, _start.Y);
                _canvas.Children.Add(_live);
                if (_tool == Tool.Crop) _cropBox = (Avalonia.Controls.Shapes.Rectangle)_live;
                break;

            case Tool.Ellipse:
                _live = new Ellipse
                {
                    Stroke = brush, StrokeThickness = _thickness,
                    Fill = AvBrushes.Transparent
                };
                Canvas.SetLeft(_live, _start.X); Canvas.SetTop(_live, _start.Y);
                _canvas.Children.Add(_live);
                break;

            case Tool.Mosaic:
            case Tool.Blur:
                _live = new Avalonia.Controls.Shapes.Rectangle
                {
                    Stroke = AvBrushes.White, StrokeThickness = 1,
                    Fill = new SolidColorBrush(AvColor.FromArgb(60, 255, 255, 255))
                };
                Canvas.SetLeft(_live, _start.X); Canvas.SetTop(_live, _start.Y);
                _canvas.Children.Add(_live);
                break;

            case Tool.Pen:
            case Tool.Highlight:
                bool hl = _tool == Tool.Highlight;
                var penBrush = hl
                    ? new SolidColorBrush(AvColor.FromArgb(0x66, _color.R, _color.G, _color.B))
                    : brush;
                _pen = new Polyline
                {
                    Stroke = penBrush,
                    StrokeThickness = hl ? Math.Max(10, _thickness * 3) : _thickness,
                    StrokeJoin = PenLineJoin.Round,
                    StrokeLineCap = hl ? PenLineCap.Flat : PenLineCap.Round,
                    Points = new Avalonia.Collections.AvaloniaList<AvPoint> { _start }
                };
                _canvas.Children.Add(_pen);
                break;
        }
    }

    private void OnMove(object? sender, PointerEventArgs e)
    {
        if (_tool == Tool.Select)
        {
            var sp = e.GetPosition(_canvas);
            if (!_moving || _selected == null)
            {
                _canvas.Cursor = HitTopMost(sp) != null
                    ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
                return;
            }
            double mdx = sp.X - _moveStart.X, mdy = sp.Y - _moveStart.Y;
            Translate(_selected, mdx, mdy);
            if (_selBox != null) { Canvas.SetLeft(_selBox, Canvas.GetLeft(_selBox) + mdx); Canvas.SetTop(_selBox, Canvas.GetTop(_selBox) + mdy); }
            foreach (var h in _handles) { Canvas.SetLeft(h, Canvas.GetLeft(h) + mdx); Canvas.SetTop(h, Canvas.GetTop(h) + mdy); }
            _movedX += mdx; _movedY += mdy;
            _moveStart = sp;
            return;
        }

        if (!_drawing) return;
        var p = e.GetPosition(_canvas);
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;

        if ((_tool == Tool.Pen || _tool == Tool.Highlight) && _pen != null)
        { _pen.Points.Add(p); return; }

        if (_live != null && (_live is Avalonia.Controls.Shapes.Rectangle || _live is Ellipse))
        {
            double w = Math.Abs(p.X - _start.X), h = Math.Abs(p.Y - _start.Y);
            if (shift && (_tool == Tool.Rect || _tool == Tool.Ellipse || _tool == Tool.Crop || _tool == Tool.Mosaic || _tool == Tool.Blur))
            { double s = Math.Min(w, h); w = h = s; }
            double x = p.X < _start.X ? _start.X - w : _start.X;
            double y = p.Y < _start.Y ? _start.Y - h : _start.Y;
            Canvas.SetLeft(_live, x); Canvas.SetTop(_live, y);
            _live.Width = w; _live.Height = h;
        }
    }

    private void OnUp(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;

        if (_tool == Tool.Select)
        {
            if (_moving && _selected != null)
            {
                _moving = false; e.Pointer.Capture(null);
                if (Math.Abs(_movedX) > 0.5 || Math.Abs(_movedY) > 0.5)
                { _undo.Add(new MoveOp(_selected, _movedX, _movedY, this)); _redo.Clear(); }
            }
            return;
        }

        if (!_drawing) return;
        _drawing = false;
        e.Pointer.Capture(null);
        var p = e.GetPosition(_canvas);
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;

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
                if (_live != null)
                {
                    // a click without a drag leaves Width/Height NaN (or ~0): drop the ghost
                    if (double.IsNaN(_live.Width) || double.IsNaN(_live.Height) || _live.Width < 2 || _live.Height < 2)
                        _canvas.Children.Remove(_live);
                    else Commit(new AddOp(_canvas, _live));
                }
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
                    var rect = new PixelRect(
                        (int)Math.Round(x), (int)Math.Round(y),
                        (int)Math.Round(double.IsNaN(_cropBox.Width) ? 0 : _cropBox.Width),
                        (int)Math.Round(double.IsNaN(_cropBox.Height) ? 0 : _cropBox.Height));
                    _canvas.Children.Remove(_cropBox);   // remove the dashed marquee (it would render otherwise)
                    _cropBox = null;
                    if (rect.Width > 1 && rect.Height > 1) Commit(new CropOp(this, rect));
                }
                break;
        }
        _live = null;
    }

    private static AvPoint ConstrainEnd(AvPoint s, AvPoint e, bool shift)
    {
        if (!shift) return e;
        double dx = e.X - s.X, dy = e.Y - s.Y;
        double ang = Math.Atan2(dy, dx);
        double snap = Math.Round(ang / (Math.PI / 4)) * (Math.PI / 4);
        double len = Math.Sqrt(dx * dx + dy * dy);
        return new AvPoint(s.X + len * Math.Cos(snap), s.Y + len * Math.Sin(snap));
    }

    // ---------------- tools ----------------

    private void DrawArrowOrLine(AvPoint s, AvPoint e, bool arrow)
    {
        var dx = e.X - s.X; var dy = e.Y - s.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 2) return;

        // Geometry is built RELATIVE to the min corner and the Path positioned via
        // Canvas.Left/Top — see the class doc (single Translate code path).
        var pts = new List<AvPoint> { s, e };
        AvPoint p1 = default, p2 = default;
        if (arrow)
        {
            double ux = dx / len, uy = dy / len;
            double head = Math.Max(10, _thickness * 4);
            double a = Math.PI / 7;
            p1 = new AvPoint(
                e.X - head * (ux * Math.Cos(a) - uy * Math.Sin(a)),
                e.Y - head * (uy * Math.Cos(a) + ux * Math.Sin(a)));
            p2 = new AvPoint(
                e.X - head * (ux * Math.Cos(-a) - uy * Math.Sin(-a)),
                e.Y - head * (uy * Math.Cos(-a) + ux * Math.Sin(-a)));
            pts.Add(p1); pts.Add(p2);
        }
        double ox = double.MaxValue, oy = double.MaxValue;
        foreach (var pt in pts) { ox = Math.Min(ox, pt.X); oy = Math.Min(oy, pt.Y); }
        AvPoint R(AvPoint pt) => new(pt.X - ox, pt.Y - oy);

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(R(s), false); ctx.LineTo(R(e)); ctx.EndFigure(false);
            if (arrow)
            {
                ctx.BeginFigure(R(p1), false); ctx.LineTo(R(e)); ctx.LineTo(R(p2)); ctx.EndFigure(false);
            }
        }
        var path = new Avalonia.Controls.Shapes.Path
        {
            Stroke = new SolidColorBrush(_color), StrokeThickness = _thickness,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round, Data = geo
        };
        Canvas.SetLeft(path, ox); Canvas.SetTop(path, oy);
        Commit(new AddOp(_canvas, path));
    }

    private void PlaceText(AvPoint at)
    {
        var fg = new SolidColorBrush(_color);
        var tb = new TextBox
        {
            Background = AvBrushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = fg,
            CaretBrush = fg,
            FontSize = Math.Max(16, _thickness * 5),
            FontWeight = FontWeight.SemiBold,
            MinWidth = 40, AcceptsReturn = true
        };
        Canvas.SetLeft(tb, at.X); Canvas.SetTop(tb, at.Y);

        // Add now so it lays out, but DON'T commit to undo yet: an empty text box left
        // behind would be an invisible-but-selectable ghost. Commit on losing focus if it
        // has text, otherwise quietly drop it.
        _canvas.Children.Add(tb);
        bool committed = false;
        tb.LostFocus += (_, _) =>
        {
            if (committed) return;
            if (string.IsNullOrEmpty(tb.Text)) _canvas.Children.Remove(tb);
            else { committed = true; Commit(new AddOp(_canvas, tb)); }
        };

        Dispatcher.UIThread.Post(() => tb.Focus(), DispatcherPriority.Input);
    }

    // ---------------- paste / drop a floating image ----------------

    /// <summary>Drop a floating image onto the canvas: selectable, movable, undoable, and
    /// baked into the final render like any other annotation. center==null → canvas centre.</summary>
    private void PlaceImage(Bitmap src, AvPoint? center = null)
    {
        if (src.PixelSize.Width < 1 || src.PixelSize.Height < 1) return;

        double w = src.PixelSize.Width, h = src.PixelSize.Height;
        double scale = Math.Min(1.0, Math.Min(_pw * 0.9 / w, _ph * 0.9 / h));   // fit within 90% of the canvas
        w *= scale; h *= scale;

        var img = new Avalonia.Controls.Image
        {
            Source = src, Width = w, Height = h,
            Stretch = Stretch.Fill, IsHitTestVisible = true     // selectable (unlike the mosaic overlay)
        };
        var c = center ?? new AvPoint(_pw / 2.0, _ph / 2.0);
        Canvas.SetLeft(img, Math.Clamp(c.X - w / 2, 0, Math.Max(0, _pw - w)));
        Canvas.SetTop(img, Math.Clamp(c.Y - h / 2, 0, Math.Max(0, _ph - h)));

        Commit(new AddOp(_canvas, img));
        // keep the crop dim (if any) on top, so a pasted image doesn't sit above it un-dimmed
        if (_cropDim != null) { _canvas.Children.Remove(_cropDim); _canvas.Children.Add(_cropDim); }

        SetTool(Tool.Select);
        ShowSelection(img);
    }

    private void PasteFromClipboard()
    {
        try
        {
            byte[]? bytes = ClipboardCore.TryReadImageBytes();
            if (bytes == null) { Toast.Show(L.T("ed.pasteEmpty")); return; }
            using var ms = new MemoryStream(bytes, writable: false);
            PlaceImage(new Bitmap(ms));
            CrashLog.Telemetry("edit-paste");
        }
        catch (Exception ex) { CrashLog.Write("editor-paste", ex); Toast.Show(L.T("ed.pasteFail")); }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasDroppableImage(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static bool HasDroppableImage(Avalonia.Input.IDataObject d)
    {
        if (d.Contains("PNG")) return true;
        if (d.Contains(DataFormats.Files))
        {
            var files = d.GetFiles();
            if (files != null)
                foreach (var f in files)
                    if (f.TryGetLocalPath() is string p && ClipboardCore.IsImagePath(p)) return true;
        }
        return false;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        try
        {
            var p = e.GetPosition(_canvas);   // auto-unwinds the Viewbox scale + margin, same as OnDown
            bool inside = p.X >= 0 && p.Y >= 0 && p.X <= _pw && p.Y <= _ph;
            var center = inside ? p : new AvPoint(_pw / 2.0, _ph / 2.0);

            // (1) image files — place each, cascading slightly when several are dropped
            if (e.Data.Contains(DataFormats.Files) && e.Data.GetFiles() is { } files)
            {
                int placed = 0;
                foreach (var f in files)
                {
                    if (f.TryGetLocalPath() is not string fp || !ClipboardCore.IsImagePath(fp) || !File.Exists(fp)) continue;
                    Bitmap src;
                    try { using var fs = File.OpenRead(fp); src = new Bitmap(fs); }
                    catch { continue; }
                    PlaceImage(src, new AvPoint(center.X + placed * 24, center.Y + placed * 24));
                    placed++;
                }
                if (placed == 0) Toast.Show(L.T("ed.dropNotImage"));
                e.Handled = true;
                return;
            }

            // (2) raw PNG bytes (e.g. an image dragged out of a browser)
            if (e.Data.Get("PNG") is byte[] png)
            {
                using var ms = new MemoryStream(png, writable: false);
                PlaceImage(new Bitmap(ms), center);
                e.Handled = true;
                return;
            }

            Toast.Show(L.T("ed.dropNotImage"));
        }
        catch (Exception ex) { CrashLog.Write("editor-drop", ex); Toast.Show(L.T("ed.dropFail")); }
    }

    private void PlaceCounter(AvPoint at)
    {
        double d = Math.Max(26, _thickness * 7);
        int n = _counterNext;
        bool darkText = IsBright(_color);
        var dot = new Grid { Width = d, Height = d };
        dot.Children.Add(new Ellipse
        {
            Fill = new SolidColorBrush(_color),
            Stroke = AvBrushes.White, StrokeThickness = 2
        });
        dot.Children.Add(new TextBlock
        {
            Text = n.ToString(),
            Foreground = darkText ? AvBrushes.Black : AvBrushes.White,
            FontWeight = FontWeight.Bold,
            FontSize = d * 0.5,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        Canvas.SetLeft(dot, at.X - d / 2); Canvas.SetTop(dot, at.Y - d / 2);
        _counterNext++;
        Commit(new AddOp(_canvas, dot, onUndo: () => _counterNext--, onRedo: () => _counterNext++));
    }

    private static bool IsBright(AvColor c)
        => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) > 160;

    private void ApplyPixelate(AvPoint a, AvPoint b, bool mosaic)
    {
        int x = (int)Math.Round(Math.Min(a.X, b.X));
        int y = (int)Math.Round(Math.Min(a.Y, b.Y));
        int w = (int)Math.Round(Math.Abs(a.X - b.X));
        int h = (int)Math.Round(Math.Abs(a.Y - b.Y));
        if (w < 2 || h < 2) return;
        x = Math.Clamp(x, 0, _pw - 1); y = Math.Clamp(y, 0, _ph - 1);
        w = Math.Clamp(w, 1, _pw - x); h = Math.Clamp(h, 1, _ph - y);

        // Block size (px) scales with the thickness control: 가늘게(2)≈16 · 보통(5)≈35 · 굵게(10)≈70.
        int block = Math.Clamp((int)Math.Round(_thickness * 7), 16, 128);
        int sw = Math.Max(1, w / block);
        int sh = Math.Max(1, h / block);

        // Clamp edge sampling (WrapMode.TileFlipXY) — see the WPF file: without it GDI+ blends
        // edge blocks with transparent out-of-image pixels and drops their alpha.
        using var ia = new Drawing.Imaging.ImageAttributes();
        ia.SetWrapMode(Drawing.Drawing2D.WrapMode.TileFlipXY);

        using var region = _srcBmp.Clone(new Drawing.Rectangle(x, y, w, h), Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var small = new Drawing.Bitmap(sw, sh, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(small))
        {
            g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(region, new Drawing.Rectangle(0, 0, sw, sh), 0, 0, region.Width, region.Height, Drawing.GraphicsUnit.Pixel, ia);
        }
        using var big = new Drawing.Bitmap(w, h, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(big))
        {
            g.InterpolationMode = mosaic
                ? Drawing.Drawing2D.InterpolationMode.NearestNeighbor
                : Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(small, new Drawing.Rectangle(0, 0, w, h), 0, 0, small.Width, small.Height, Drawing.GraphicsUnit.Pixel, ia);
        }

        var img = new Avalonia.Controls.Image
        {
            Source = AvImaging.ToAvaloniaBitmap(big), Width = w, Height = h,
            Stretch = Stretch.Fill, IsHitTestVisible = false
        };
        Canvas.SetLeft(img, x); Canvas.SetTop(img, y);
        Commit(new AddOp(_canvas, img));
    }

    // ---------------- selection / move (Tool.Select) ----------------

    private bool Selectable(Avalonia.Controls.Control el)
        => el != _baseImage && el != _cropDim && el != _selBox
           && (el is not Avalonia.Controls.Shapes.Rectangle r || !_handles.Contains(r));

    private Avalonia.Controls.Control? HitTopMost(AvPoint p)
    {
        for (int i = _canvas.Children.Count - 1; i >= 0; i--)
        {
            var el = _canvas.Children[i];
            if (!Selectable(el)) continue;
            if (HitBounds(el, p)) return el;
        }
        return null;
    }

    private static bool HitBounds(Avalonia.Controls.Control el, AvPoint p)
    {
        const double pad = 8;
        if (el is Polyline pl)
        {
            double t = Math.Max(pad, pl.StrokeThickness / 2 + 4);
            var pts = pl.Points;
            if (pts == null || pts.Count == 0) return false;
            for (int i = 1; i < pts.Count; i++)
                if (DistToSeg(p, pts[i - 1], pts[i]) <= t) return true;
            return Dist(p, pts[0]) <= t;
        }
        if (el is Avalonia.Controls.Shapes.Path path)
        {
            var b = PathBounds(path);
            if (b.Width <= 0 && b.Height <= 0) return false;
            double t = Math.Max(pad, path.StrokeThickness);
            return b.Inflate(t).Contains(p);
        }
        {
            double x = Canvas.GetLeft(el), y = Canvas.GetTop(el);
            if (double.IsNaN(x)) x = 0; if (double.IsNaN(y)) y = 0;
            double w = double.IsNaN(el.Width) ? el.Bounds.Width : el.Width;
            double h = double.IsNaN(el.Height) ? el.Bounds.Height : el.Height;
            return new AvRect(x - pad, y - pad, w + 2 * pad, h + 2 * pad).Contains(p);
        }
    }

    /// <summary>A Path's canvas-space bounds: geometry bounds (relative) offset by Canvas pos.</summary>
    private static AvRect PathBounds(Avalonia.Controls.Shapes.Path path)
    {
        var b = path.Data?.Bounds ?? default;
        double x = Canvas.GetLeft(path), y = Canvas.GetTop(path);
        if (double.IsNaN(x)) x = 0; if (double.IsNaN(y)) y = 0;
        return new AvRect(x + b.X, y + b.Y, b.Width, b.Height);
    }

    private static double Dist(AvPoint a, AvPoint b)
    { double dx = a.X - b.X, dy = a.Y - b.Y; return Math.Sqrt(dx * dx + dy * dy); }

    private static double DistToSeg(AvPoint p, AvPoint a, AvPoint b)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y, wx = p.X - a.X, wy = p.Y - a.Y;
        double c1 = vx * wx + vy * wy; if (c1 <= 0) return Dist(p, a);
        double c2 = vx * vx + vy * vy; if (c2 <= c1) return Dist(p, b);
        double t = c1 / c2; return Dist(p, new AvPoint(a.X + t * vx, a.Y + t * vy));
    }

    /// <summary>Translate any annotation type by (dx,dy). One function, reused by drag + MoveOp.
    /// Paths are canvas-positioned with relative geometry, so the generic branch covers them.</summary>
    private static void Translate(Avalonia.Controls.Control el, double dx, double dy)
    {
        if (el is Polyline pl && pl.Points != null)
        {
            var pts = pl.Points;
            for (int i = 0; i < pts.Count; i++) pts[i] = new AvPoint(pts[i].X + dx, pts[i].Y + dy);
            return;
        }
        double x = Canvas.GetLeft(el), y = Canvas.GetTop(el);
        if (double.IsNaN(x)) x = 0; if (double.IsNaN(y)) y = 0;
        Canvas.SetLeft(el, x + dx); Canvas.SetTop(el, y + dy);
    }

    private void ShowSelection(Avalonia.Controls.Control el)
    {
        ClearSelectionVisuals();
        _selected = el;
        var b = SelectionRect(el);
        if (b.Width <= 0 && b.Height <= 0) return;
        _selBox = new Avalonia.Controls.Shapes.Rectangle
        {
            Stroke = AppTheme.Brush("Accent"), StrokeThickness = 1.5,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 },
            Fill = AvBrushes.Transparent,
            IsHitTestVisible = false, Width = b.Width, Height = b.Height
        };
        Canvas.SetLeft(_selBox, b.X); Canvas.SetTop(_selBox, b.Y);
        _canvas.Children.Add(_selBox);
        foreach (var hp in HandlePoints(b))
        {
            var hsq = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = 8, Height = 8, Fill = AvBrushes.White,
                Stroke = AppTheme.Brush("Accent"), StrokeThickness = 1, IsHitTestVisible = false
            };
            Canvas.SetLeft(hsq, hp.X - 4); Canvas.SetTop(hsq, hp.Y - 4);
            _handles.Add(hsq); _canvas.Children.Add(hsq);
        }
    }

    private AvRect SelectionRect(Avalonia.Controls.Control el)
    {
        if (el is Polyline pl && pl.Points is { Count: > 0 } pts)
        {
            double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
            foreach (var pt in pts) { minx = Math.Min(minx, pt.X); miny = Math.Min(miny, pt.Y); maxx = Math.Max(maxx, pt.X); maxy = Math.Max(maxy, pt.Y); }
            double pad = pl.StrokeThickness / 2 + 2;
            return new AvRect(minx - pad, miny - pad, (maxx - minx) + 2 * pad, (maxy - miny) + 2 * pad);
        }
        if (el is Avalonia.Controls.Shapes.Path path && path.Data != null)
        {
            double pad = path.StrokeThickness / 2 + 2;
            return PathBounds(path).Inflate(pad);
        }
        {
            double x = Canvas.GetLeft(el), y = Canvas.GetTop(el);
            if (double.IsNaN(x)) x = 0; if (double.IsNaN(y)) y = 0;
            double w = double.IsNaN(el.Width) ? el.Bounds.Width : el.Width;
            double h = double.IsNaN(el.Height) ? el.Bounds.Height : el.Height;
            return new AvRect(x, y, w, h);
        }
    }

    private static IEnumerable<AvPoint> HandlePoints(AvRect b)
    {
        double mx = b.X + b.Width / 2, my = b.Y + b.Height / 2;
        return new[]
        {
            new AvPoint(b.X, b.Y), new AvPoint(mx, b.Y), new AvPoint(b.Right, b.Y),
            new AvPoint(b.X, my), new AvPoint(b.Right, my),
            new AvPoint(b.X, b.Bottom), new AvPoint(mx, b.Bottom), new AvPoint(b.Right, b.Bottom)
        };
    }

    private void ClearSelectionVisuals()
    {
        if (_selBox != null) { _canvas.Children.Remove(_selBox); _selBox = null; }
        foreach (var h in _handles) _canvas.Children.Remove(h);
        _handles.Clear();
    }

    private void ClearSelection() { ClearSelectionVisuals(); _selected = null; }

    private void RefreshSelectionIfSelected(Avalonia.Controls.Control el) { if (_selected == el) ShowSelection(el); }

    // ---------------- undo / redo ----------------

    private sealed class MoveOp : Op
    {
        private readonly Avalonia.Controls.Control _el; private readonly double _dx, _dy; private readonly EditorWindow _w;
        // The live drag ALREADY moved the element — do NOT translate again in the ctor.
        public MoveOp(Avalonia.Controls.Control el, double dx, double dy, EditorWindow w) { _el = el; _dx = dx; _dy = dy; _w = w; }
        public override void Undo() { Translate(_el, -_dx, -_dy); _w.RefreshSelectionIfSelected(_el); }
        public override void Redo() { Translate(_el, _dx, _dy); _w.RefreshSelectionIfSelected(_el); }
    }

    private sealed class DeleteOp : Op
    {
        private readonly Canvas _c; private readonly Avalonia.Controls.Control _el; private readonly int _index; private readonly EditorWindow _w;
        public DeleteOp(EditorWindow w, Canvas c, Avalonia.Controls.Control el) { _w = w; _c = c; _el = el; _index = c.Children.IndexOf(el); c.Children.Remove(el); w.ClearSelection(); }
        public override void Undo() { if (!_c.Children.Contains(_el)) { if (_index >= 0 && _index <= _c.Children.Count) _c.Children.Insert(_index, _el); else _c.Children.Add(_el); } }
        public override void Redo() { _c.Children.Remove(_el); _w.ClearSelection(); }
    }

    private sealed class AddOp : Op
    {
        private readonly Canvas _c; private readonly Avalonia.Controls.Control _el;
        private readonly Action? _onUndo, _onRedo;
        public AddOp(Canvas c, Avalonia.Controls.Control el, Action? onUndo = null, Action? onRedo = null)
        { _c = c; _el = el; _onUndo = onUndo; _onRedo = onRedo; if (!_c.Children.Contains(_el)) _c.Children.Add(_el); }
        public override void Undo() { _c.Children.Remove(_el); _onUndo?.Invoke(); }
        public override void Redo() { if (!_c.Children.Contains(_el)) _c.Children.Add(_el); _onRedo?.Invoke(); }
    }

    private sealed class CropOp : Op
    {
        private readonly EditorWindow _w; private readonly PixelRect _next; private readonly PixelRect? _prev;
        public CropOp(EditorWindow w, PixelRect next) { _w = w; _next = next; _prev = w._cropRect; w.ApplyCrop(next); }
        public override void Undo() => _w.ApplyCrop(_prev);
        public override void Redo() => _w.ApplyCrop(_next);
    }

    private void ApplyCrop(PixelRect? rect)
    {
        _cropRect = rect;
        if (_cropDim != null) { _canvas.Children.Remove(_cropDim); _cropDim = null; }
        if (rect is { } r)
        {
            // Four dim rectangles around the crop (spike (a): even-odd geometry renders wrong).
            var dimBrush = new SolidColorBrush(AvColor.FromArgb(0x88, 0, 0, 0));
            var dim = new Canvas { Width = _pw, Height = _ph, IsHitTestVisible = false };
            void Add(double x, double y, double w, double h)
            {
                if (w <= 0 || h <= 0) return;
                var rc = new Avalonia.Controls.Shapes.Rectangle { Fill = dimBrush, Width = w, Height = h };
                Canvas.SetLeft(rc, x); Canvas.SetTop(rc, y);
                dim.Children.Add(rc);
            }
            Add(0, 0, _pw, r.Y);                                     // top
            Add(0, r.Y + r.Height, _pw, _ph - r.Y - r.Height);        // bottom
            Add(0, r.Y, r.X, r.Height);                               // left
            Add(r.X + r.Width, r.Y, _pw - r.X - r.Width, r.Height);   // right
            Canvas.SetLeft(dim, 0); Canvas.SetTop(dim, 0);
            _cropDim = dim;
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

    /// <summary>Rasterize the canvas at image resolution and PNG-encode it, honouring the crop.
    /// This is the product rasterizer (not screen verification): Avalonia RenderTargetBitmap at
    /// 96 DPI renders 1:1 logical→pixel; the crop is applied with SkiaSharp (Phase 0 stack).</summary>
    private byte[] RenderFinalPng()
    {
        Focus();                                                       // commit any in-progress text box
        ClearSelectionVisuals();                                       // marquee + handles are guides, never baked in
        if (_cropDim != null) _cropDim.IsVisible = false;              // guide only — never baked in

        byte[] png;
        try
        {
            var rtb = new RenderTargetBitmap(new PixelSize(_pw, _ph), new Vector(96, 96));
            _canvas.Measure(new Avalonia.Size(_pw, _ph));
            _canvas.Arrange(new AvRect(0, 0, _pw, _ph));
            rtb.Render(_canvas);
            using var ms = new MemoryStream();
            rtb.Save(ms);
            png = ms.ToArray();
        }
        finally
        {
            if (_cropDim != null) _cropDim.IsVisible = true;           // restore the guide even if render throws
        }

        if (_cropRect is { } cr && cr.Width > 1 && cr.Height > 1)
        {
            int cx = Math.Clamp(cr.X, 0, _pw - 1);
            int cy = Math.Clamp(cr.Y, 0, _ph - 1);
            int cw = Math.Clamp(cr.Width, 1, _pw - cx);
            int ch = Math.Clamp(cr.Height, 1, _ph - cy);
            using var full = SkiaSharp.SKBitmap.Decode(png);
            if (full != null)
            {
                var sub = new SkiaSharp.SKBitmap();
                if (full.ExtractSubset(sub, new SkiaSharp.SKRectI(cx, cy, cx + cw, cy + ch)))
                {
                    using (sub)
                    using (var img = SkiaSharp.SKImage.FromBitmap(sub))
                    using (var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                        png = data.ToArray();
                }
            }
        }
        return png;
    }

    private void CopyToClipboard(bool andClose = false)
    {
        try
        {
            byte[] png = RenderFinalPng();
            ClipboardWatcher.SuppressNext();
            if (ClipboardCore.CopyImagePng(png, null)) Toast.Show(L.T("ed.copied"));
            else Toast.Show(L.T("ed.copyFail"));
            CrashLog.Telemetry("edit-copied");
            if (andClose) { ResultPath = null; Close(); }
        }
        catch (Exception ex) { CrashLog.Write("editor-copy", ex); Toast.Show(L.T("ed.copyFailEx", ex.Message)); }
    }

    private void Save()
    {
        try
        {
            byte[] png = RenderFinalPng();
            string outPath = CaptureStore.NewPath();
            File.WriteAllBytes(outPath, png);

            ResultPath = outPath;
            CaptureStore.PruneScratch();
            CrashLog.Telemetry("edit-saved");
            Close();
        }
        catch (Exception ex)
        {
            CrashLog.Write("editor-save", ex);
            Toast.Show(L.T("ed.saveFailEx", ex.Message));
        }
    }
}
