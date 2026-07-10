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
using System.Drawing;          // System.Drawing.Common (WinForms island) for Bitmap
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Collections;
using Avalonia.Media.Imaging;
using AvBrushes = Avalonia.Media.Brushes;
using Bitmap = System.Drawing.Bitmap;

namespace Wsnap;

/// <summary>Duplicate of the WPF CaptureOverlay's mode enum (that file is WPF-only and not
/// linked here); dies with the WPF build at the Phase 6 cutover.</summary>
public enum CaptureMode
{
    /// <summary>Normal: save a PNG and pop a thumbnail.</summary>
    Capture,
    /// <summary>Select a region, OCR it, copy the text — no file kept.</summary>
    OcrText,
    /// <summary>Only report the selected rect (device px) — used by GIF / scroll capture.</summary>
    Region,
    /// <summary>Click a pixel, copy its #RRGGBB — no file kept.</summary>
    ColorPick
}

/// <summary>
/// Avalonia port (Phase 2) of the WPF CaptureOverlay: a borderless, topmost window covering
/// the entire virtual desktop. On open it FREEZES the desktop into one physical-pixel bitmap
/// and shows that; the backdrop dims everything EXCEPT the selection; a live W×H badge tracks
/// the drag; a magnifier loupe shows zoomed pixels + coords + hex (C copies it).
///
/// Port notes (do not "simplify" these away):
///  • The dim is FOUR Rectangles around the selection, NOT an even-odd geometry hole — both
///    WPF-style GeometryGroup/EvenOdd and CombinedGeometry/Xor render WRONG on Avalonia's
///    Win32/Skia backend (spike (a), measured by external screenshot luminance).
///  • All grabbing/cropping uses PHYSICAL device pixels (GetCursorPos + the frozen bitmap),
///    the same space CopyFromScreen uses — logical units are for visuals only. The window is
///    placed via PixelPoint and force-sized in physical px, so mixed-DPI correctness of the
///    RESULT never depends on any DPI conversion.
///  • Pointer-move work is coalesced to one update per render frame via
///    RequestAnimationFrame (the CompositionTarget.Rendering analog).
/// </summary>
public sealed class CaptureOverlay : Avalonia.Controls.Window
{
    private readonly CaptureMode _mode;
    private Avalonia.Point _start;              // logical, window-local (for visuals)
    private POINT _startPhys;                   // physical device px (for the grab)
    private bool _dragging;

    // Mouse-move coalescing (see WPF file): stash the latest position, process once per frame.
    private Avalonia.Point _moveDip;
    private POINT _movePhys;
    private bool _moveDirty;
    private bool _frameRequested;
    private int _lastLoupeX = int.MinValue, _lastLoupeY = int.MinValue;

    private readonly Canvas _canvas;
    private readonly Avalonia.Controls.Shapes.Rectangle[] _dim = new Avalonia.Controls.Shapes.Rectangle[4];
    private Avalonia.Rect _hole;                       // logical; empty = no punch-through
    private readonly Avalonia.Controls.Shapes.Rectangle _selection;
    private readonly Ellipse[] _handles = new Ellipse[4];
    private const double HandleSize = 8;
    private readonly Border _badge;
    private readonly TextBlock _badgeText;
    private readonly Border _hint;
    private readonly Avalonia.Media.Color _accent;

    // frozen desktop (physical px) — sampled for the loupe and cropped for the result.
    private Bitmap? _freeze;
    private int _vx, _vy;                        // virtual-screen origin (physical px)
    private readonly double _lw, _lh;            // window logical size (physical / scale)

    // loupe
    private readonly Border _loupe;
    private readonly Avalonia.Controls.Image _loupeImg;
    private readonly TextBlock _loupeText;
    private string _hex = "#000000";

    // window auto-detection
    private readonly struct WinRect
    {
        public readonly IntPtr H; public readonly RECT R; public readonly string Title;
        public WinRect(IntPtr h, RECT r, string t) { H = h; R = r; Title = t; }
    }
    private System.Collections.Generic.List<WinRect>? _windows;
    private IntPtr _hovered = IntPtr.Zero, _selfHwnd = IntPtr.Zero;
    private EnumWindowsProc? _enumCb;
    private readonly Avalonia.Controls.Shapes.Rectangle _winHi;
    private readonly Border _winLabel;
    private readonly TextBlock _winLabelText;

    public string? ResultPath { get; private set; }
    public Bitmap? ResultBitmap { get; private set; }
    public PixelRect? RegionPx { get; private set; }
    public static PixelRect? LastRegion { get; private set; }

    /// <summary>What the user chose from the post-capture toolbar (Capture mode).</summary>
    public enum PostAction { Cancel, Save, Copy, Edit, Ocr, Gif, Pin }
    public PostAction Action { get; private set; } = PostAction.Cancel;

    /// <summary>Filename-template metadata captured by App BEFORE this overlay grabbed focus.</summary>
    public NameContext NameCtx { get; set; } = NameContext.Empty;

    private NameContext CtxWithSize() =>
        NameCtx with { Width = RegionPx?.Width ?? NameCtx.Width, Height = RegionPx?.Height ?? NameCtx.Height };

    private bool _committed;
    private Border? _toolbar;

    public CaptureOverlay(CaptureMode mode = CaptureMode.Capture)
    {
        _mode = mode;
        _accent = mode == CaptureMode.OcrText ? AppTheme.Success
                : mode == CaptureMode.ColorPick ? AppTheme.Warn
                : AppTheme.Accent;

        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        FontFamily = AppTheme.Font;

        // Virtual-screen bounds in PHYSICAL px (identical to the WPF file's TryFreeze inputs);
        // the window is positioned in physical px and its logical size derived from the scale
        // of the monitor at the virtual-screen origin. On mixed-DPI desktops that single scale
        // is approximate for OTHER monitors — visuals there may be a hair off, but every grab
        // stays exact because it never leaves physical space. Opened() below re-asserts the
        // physical bounds via SetWindowPos to kill any rounding drift.
        _vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        _vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        int vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        double scale = 1.0;
        IntPtr mon = MonitorFromPoint(new POINT { X = _vx, Y = _vy }, MONITOR_DEFAULTTONEAREST);
        if (GetDpiForMonitor(mon, 0, out uint dpiX, out _) == 0 && dpiX > 0) scale = dpiX / 96.0;
        _lw = vw / scale; _lh = vh / scale;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(_vx, _vy);
        Width = _lw; Height = _lh;

        _canvas = new Canvas();

        // Freeze the desktop FIRST (before the window ever shows) so the scene can't shift and
        // the overlay never captures itself. With a frozen backdrop the window stays opaque —
        // same rationale as WPF; only a failed freeze needs see-through.
        TryFreeze(vw, vh);
        Background = _freeze != null ? AvBrushes.Black : AvBrushes.Transparent;
        if (_freeze == null)
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        if (_freeze != null)
        {
            var frozen = new Avalonia.Controls.Image
            {
                Source = AvImaging.ToAvaloniaBitmap(_freeze),
                Width = _lw, Height = _lh, Stretch = Stretch.Fill, IsHitTestVisible = false
            };
            _canvas.Children.Add(frozen);
        }

        // Punch-through dim: four rectangles around the hole (spike (a) technique — see class doc).
        var dimBrush = new SolidColorBrush(Avalonia.Media.Color.FromArgb(0x73, 0, 0, 0));
        for (int i = 0; i < 4; i++)
        {
            _dim[i] = new Avalonia.Controls.Shapes.Rectangle { Fill = dimBrush, IsHitTestVisible = false };
            _canvas.Children.Add(_dim[i]);
        }
        SetHole(default);   // no selection yet → single full-cover rect

        _selection = new Avalonia.Controls.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(_accent), StrokeThickness = 1.5,
            Fill = AvBrushes.Transparent,
            IsVisible = false, IsHitTestVisible = false
        };
        _canvas.Children.Add(_selection);

        // macOS-style corner handles (affordance only; hidden after commit).
        for (int i = 0; i < _handles.Length; i++)
        {
            _handles[i] = new Ellipse
            {
                Width = HandleSize, Height = HandleSize,
                Fill = AvBrushes.White,
                Stroke = new SolidColorBrush(_accent), StrokeThickness = 1.25,
                IsVisible = false, IsHitTestVisible = false
            };
            _canvas.Children.Add(_handles[i]);
        }

        // window-hover highlight (dashed) + title label
        _winHi = new Avalonia.Controls.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(_accent), StrokeThickness = 1.5,
            StrokeDashArray = new AvaloniaList<double> { 4, 3 },
            Fill = AvBrushes.Transparent,
            IsVisible = false, IsHitTestVisible = false
        };
        _canvas.Children.Add(_winHi);
        _winLabelText = new TextBlock { Foreground = AvBrushes.White, FontSize = 11 };
        _winLabel = new Border
        {
            CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 2, 6, 2),
            Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(0xDC, 0x14, 0x16, 0x19)),
            Child = _winLabelText, IsVisible = false, IsHitTestVisible = false
        };
        _canvas.Children.Add(_winLabel);

        _badgeText = new TextBlock { Foreground = AvBrushes.White, FontSize = 12, FontWeight = FontWeight.SemiBold };
        _badge = new Border
        {
            CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 4, 10, 4),   // pill
            Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(0xF0, 0x16, 0x18, 0x1B)),
            BorderBrush = new SolidColorBrush(AppTheme.BorderStrong), BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 1 8 0 #66000000"),
            Child = _badgeText, IsVisible = false, IsHitTestVisible = false
        };
        _canvas.Children.Add(_badge);

        _hint = new Border
        {
            CornerRadius = new CornerRadius(999), Padding = new Thickness(18, 10, 18, 10),   // pill
            Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(0xE0, 0x16, 0x18, 0x1B)),
            BorderBrush = new SolidColorBrush(AppTheme.BorderStrong), BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 2 14 0 #73000000"),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = mode == CaptureMode.OcrText ? L.T("ov.hintOcr")
                     : mode == CaptureMode.ColorPick ? L.T("ov.hintColor")
                     : L.T("ov.hint"),
                Foreground = AvBrushes.White, FontSize = 13
            }
        };
        _canvas.Children.Add(_hint);

        // loupe (magnifier + hex/coords)
        _loupeImg = new Avalonia.Controls.Image { Width = 120, Height = 96, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapInterpolationMode(_loupeImg, BitmapInterpolationMode.None);
        var loupeGrid = new Grid { Width = 120, Height = 96 };
        loupeGrid.Children.Add(_loupeImg);
        loupeGrid.Children.Add(new Avalonia.Controls.Shapes.Rectangle { Width = 1.5, Height = 96, Fill = new SolidColorBrush(_accent), HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.85 });
        loupeGrid.Children.Add(new Avalonia.Controls.Shapes.Rectangle { Width = 120, Height = 1.5, Fill = new SolidColorBrush(_accent), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 });
        _loupeText = new TextBlock { Foreground = AvBrushes.White, FontSize = 11, TextAlignment = TextAlignment.Center, Padding = new Thickness(0, 3, 0, 3) };
        var loupeStack = new StackPanel();
        loupeStack.Children.Add(new Border { Child = loupeGrid, BorderBrush = new SolidColorBrush(_accent), BorderThickness = new Thickness(1, 1, 1, 0) });
        loupeStack.Children.Add(new Border { Child = _loupeText, Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(0xE6, 0x14, 0x16, 0x19)) });
        _loupe = new Border
        {
            Child = loupeStack, CornerRadius = new CornerRadius(7),
            BoxShadow = BoxShadows.Parse("0 2 12 0 #80000000"),
            IsVisible = false, IsHitTestVisible = false
        };
        _canvas.Children.Add(_loupe);

        Content = _canvas;

        Opened += (_, _) =>
        {
            // Center the hint, enumerate windows, and force EXACT physical bounds (kills any
            // logical-size rounding drift on fractional scales).
            _hint.Measure(Avalonia.Size.Infinity);
            Canvas.SetLeft(_hint, (_lw - _hint.DesiredSize.Width) / 2);
            Canvas.SetTop(_hint, _lh * 0.62);

            _selfHwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            MonitorPlacement.SetBoundsPx(_selfHwnd, _vx, _vy, vw, vh);
            if (_mode != CaptureMode.ColorPick) EnumerateWindows();
            Focus();
        };
        PointerPressed += OnDown;
        PointerMoved += OnMove;
        PointerReleased += OnUp;
        KeyDown += OnKey;
        Closed += (_, _) => { _freeze?.Dispose(); _freeze = null; };
    }

    private void TryFreeze(int vw, int vh)
    {
        try
        {
            if (vw > 0 && vh > 0) _freeze = ScreenGrab.GrabFast(_vx, _vy, vw, vh);
        }
        catch (Exception ex) { CrashLog.Write("overlay-freeze", ex); _freeze = null; }
    }

    /// <summary>Position the four dim rectangles around <paramref name="hole"/> (logical).
    /// An empty hole = the top rect covers the whole window and the rest collapse.</summary>
    private void SetHole(Avalonia.Rect hole)
    {
        _hole = hole;
        double W = _lw, H = _lh;
        double x = Math.Max(0, hole.X), y = Math.Max(0, hole.Y);
        double r = Math.Min(W, hole.Right), b = Math.Min(H, hole.Bottom);
        if (hole.Width <= 0 || hole.Height <= 0) { x = 0; y = 0; r = 0; b = 0; }

        Place(_dim[0], 0, 0, W, y);                 // top
        Place(_dim[1], 0, b, W, Math.Max(0, H - b)); // bottom
        Place(_dim[2], 0, y, x, Math.Max(0, b - y)); // left
        Place(_dim[3], r, y, Math.Max(0, W - r), Math.Max(0, b - y)); // right

        static void Place(Avalonia.Controls.Shapes.Rectangle rc, double px, double py, double pw, double ph)
        {
            Canvas.SetLeft(rc, px); Canvas.SetTop(rc, py);
            rc.Width = Math.Max(0, pw); rc.Height = Math.Max(0, ph);
        }
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (_committed)
        {
            switch (e.Key)
            {
                case Key.Escape: Action = PostAction.Cancel; Close(); break;
                case Key.Enter: case Key.S: Choose(PostAction.Save); break;
                case Key.C: Choose(PostAction.Copy); break;
                case Key.E: Choose(PostAction.Edit); break;
                case Key.T: Choose(PostAction.Ocr); break;
                case Key.G: Choose(PostAction.Gif); break;
                case Key.P: Choose(PostAction.Pin); break;
            }
            return;
        }
        if (e.Key == Key.Escape) { ResultPath = null; Close(); }
        else if (e.Key == Key.C) { CopyTextSuppressed(_hex); Toast.Show(L.T("ov.colorCopied", _hex)); }
    }

    private static void CopyTextSuppressed(string text)
    {
        ClipboardWatcher.SuppressNext();
        ClipboardCore.CopyText(text);
    }

    private void OnDown(object? sender, PointerPressedEventArgs e)
    {
        if (_committed) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        GetCursorPos(out _startPhys);
        if (_mode == CaptureMode.ColorPick)
        {
            CopyTextSuppressed(_hex);
            Toast.Show(L.T("ov.colorCopied", _hex));
            Close();
            return;
        }
        _start = e.GetPosition(this);
        _dragging = true;
        // hide the hover highlight while dragging, but KEEP _hovered so a no-drag click can still grab the window
        _winHi.IsVisible = false; _winLabel.IsVisible = false;
        _selection.IsVisible = true;
        foreach (var hnd in _handles) hnd.IsVisible = true;
        _hint.IsVisible = false;
        e.Pointer.Capture(this);
    }

    private void OnMove(object? sender, PointerEventArgs e)
    {
        // Cheap: stash the latest position; the heavy work runs on the next render frame.
        _moveDip = e.GetPosition(this);
        GetCursorPos(out _movePhys);
        _moveDirty = true;
        if (!_frameRequested)
        {
            _frameRequested = true;
            RequestAnimationFrame(_ =>
            {
                _frameRequested = false;
                if (!_moveDirty) return;
                _moveDirty = false;
                ProcessMove(_moveDip, _movePhys);
            });
        }
    }

    private void ProcessMove(Avalonia.Point p, POINT cur)
    {
        UpdateLoupe(p, cur);

        if (!_dragging)
        {
            if (!_committed && _mode != CaptureMode.ColorPick) UpdateWindowHover(cur);
            return;
        }
        double x = Math.Min(p.X, _start.X), y = Math.Min(p.Y, _start.Y);
        double w = Math.Abs(p.X - _start.X), h = Math.Abs(p.Y - _start.Y);
        Canvas.SetLeft(_selection, x);
        Canvas.SetTop(_selection, y);
        _selection.Width = w; _selection.Height = h;
        SetHole(new Avalonia.Rect(x, y, w, h));

        // corner handles track the selection (centered on each corner)
        double hh = HandleSize / 2;
        Canvas.SetLeft(_handles[0], x - hh);      Canvas.SetTop(_handles[0], y - hh);
        Canvas.SetLeft(_handles[1], x + w - hh);  Canvas.SetTop(_handles[1], y - hh);
        Canvas.SetLeft(_handles[2], x - hh);      Canvas.SetTop(_handles[2], y + h - hh);
        Canvas.SetLeft(_handles[3], x + w - hh);  Canvas.SetTop(_handles[3], y + h - hh);

        int pw = Math.Abs(cur.X - _startPhys.X);
        int ph = Math.Abs(cur.Y - _startPhys.Y);
        _badgeText.Text = $"{pw} × {ph}";
        _badge.Measure(Avalonia.Size.Infinity);
        double bx = Math.Min(x, _lw - _badge.DesiredSize.Width - 2);
        double by = y - _badge.DesiredSize.Height - 6;
        if (by < 2) by = y + 6;
        Canvas.SetLeft(_badge, Math.Max(2, bx));
        Canvas.SetTop(_badge, by);
        _badge.IsVisible = pw > 1 || ph > 1;
    }

    private void UpdateLoupe(Avalonia.Point dip, POINT phys)
    {
        if (_freeze == null) return;
        int bx = phys.X - _vx, by = phys.Y - _vy;
        if (bx < 0 || by < 0 || bx >= _freeze.Width || by >= _freeze.Height) { _loupe.IsVisible = false; return; }

        // Rebuild the zoomed bitmap + colour sample only when the cursor moved to a NEW source
        // pixel (the expensive part); placement still follows the cursor smoothly every tick.
        if (bx != _lastLoupeX || by != _lastLoupeY)
        {
            _lastLoupeX = bx; _lastLoupeY = by;
            const int sample = 25;                  // odd → real center pixel
            int sx = Math.Clamp(bx - sample / 2, 0, Math.Max(0, _freeze.Width - sample));
            int sy = Math.Clamp(by - sample / 2, 0, Math.Max(0, _freeze.Height - sample));
            int sw = Math.Min(sample, _freeze.Width - sx);
            int sh = Math.Min(sample, _freeze.Height - sy);
            try
            {
                using var crop = _freeze.Clone(new System.Drawing.Rectangle(sx, sy, sw, sh), _freeze.PixelFormat);
                _loupeImg.Source = AvImaging.ToAvaloniaBitmap(crop);
                var c = _freeze.GetPixel(Math.Clamp(bx, 0, _freeze.Width - 1), Math.Clamp(by, 0, _freeze.Height - 1));
                _hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                _loupeText.Text = $"{_hex}   {phys.X}, {phys.Y}";
            }
            catch { return; }
        }

        double lx = dip.X + 20, ly = dip.Y + 24;
        if (lx + 124 > _lw) lx = dip.X - 144;
        if (ly + 128 > _lh) ly = dip.Y - 132;
        Canvas.SetLeft(_loupe, Math.Max(2, lx));
        Canvas.SetTop(_loupe, Math.Max(2, ly));
        _loupe.IsVisible = true;
    }

    private void OnUp(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        _dragging = false;
        e.Pointer.Capture(null);

        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _start.X) < 4 && Math.Abs(p.Y - _start.Y) < 4)   // both tiny = a click, not a region drag
        {
            if (_hovered != IntPtr.Zero && _mode != CaptureMode.ColorPick && CaptureHoveredWindow()) return;
            ResultPath = null; Close(); return;
        }

        GetCursorPos(out POINT endPhys);
        int px = Math.Min(_startPhys.X, endPhys.X);
        int py = Math.Min(_startPhys.Y, endPhys.Y);
        int pw = Math.Abs(endPhys.X - _startPhys.X);
        int ph = Math.Abs(endPhys.Y - _startPhys.Y);
        if (pw < 1 || ph < 1) { ResultPath = null; Close(); return; }

        RegionPx = new PixelRect(px, py, pw, ph);
        LastRegion = RegionPx;

        if (_mode == CaptureMode.Region) { Close(); return; }

        try { ResultBitmap = CropFreezeOrLive(px, py, pw, ph); }
        catch (Exception ex) { CrashLog.Write("capture-grab", ex); }

        // OCR mode: App OCRs the bitmap; no file, no toolbar.
        if (_mode == CaptureMode.OcrText) { Close(); return; }

        // Capture mode: either show the post-capture toolbar, or commit straight to a file.
        if (Settings.Current.PostCaptureToolbar && ResultBitmap != null)
        {
            EnterCommitted();
            return;
        }
        if (ResultBitmap != null)
        {
            try { ResultPath = CaptureStore.SaveBitmap(ResultBitmap, CtxWithSize()); Action = PostAction.Save; }
            catch (Exception ex) { CrashLog.Write("capture-save", ex); }
        }
        Close();
    }

    // ---- post-capture floating toolbar ----

    private void EnterCommitted()
    {
        _committed = true;
        _loupe.IsVisible = false;
        _badge.IsVisible = false;
        foreach (var hnd in _handles) hnd.IsVisible = false;   // region is no longer resizable

        _toolbar = BuildToolbar();
        _canvas.Children.Add(_toolbar);
        _toolbar.Measure(Avalonia.Size.Infinity);
        var sz = _toolbar.DesiredSize;

        var sel = _hole;
        double tx = sel.X + (sel.Width - sz.Width) / 2;
        double ty = sel.Bottom + 12;
        if (ty + sz.Height > _lh - 4) ty = sel.Y - sz.Height - 12;   // flip above
        if (ty < 4) ty = Math.Max(4, sel.Y + 8);                     // tiny selection → inside-ish
        tx = Math.Clamp(tx, 4, Math.Max(4, _lw - sz.Width - 4));
        Canvas.SetLeft(_toolbar, tx);
        Canvas.SetTop(_toolbar, ty);
    }

    private Border BuildToolbar()
    {
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        row.Children.Add(ToolbarBtn("copy", L.T("ov.copy"), () => Choose(PostAction.Copy)));
        row.Children.Add(ToolbarBtn("save", L.T("ov.save"), () => Choose(PostAction.Save)));
        row.Children.Add(ToolbarBtn("edit", L.T("ov.edit"), () => Choose(PostAction.Edit)));
        row.Children.Add(ToolbarBtn("text", L.T("ov.ocr"), () => Choose(PostAction.Ocr)));
        row.Children.Add(ToolbarBtn("gif", L.T("ov.gif"), () => Choose(PostAction.Gif)));
        row.Children.Add(ToolbarBtn("pin", L.T("ov.pin"), () => Choose(PostAction.Pin)));
        row.Children.Add(ToolbarBtn("close", L.T("ov.cancel"), () => { Action = PostAction.Cancel; Close(); }, danger: true));

        return new Border
        {
            Child = row, Padding = new Thickness(4),
            CornerRadius = new CornerRadius(10),
            Background = AppTheme.Brush("Panel"),
            BorderBrush = new SolidColorBrush(AppTheme.BorderStrong), BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 3 18 0 #8C000000")
        };
    }

    private Button ToolbarBtn(string icon, string tip, Action onClick, bool danger = false)
    {
        var b = new Button
        {
            Width = 34, Height = 34, Padding = new Thickness(0), Margin = new Thickness(1, 0, 1, 0),
            Content = Icons.Make(icon, 18, AppTheme.Brush("Muted")),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        b.Classes.Add("subtle");
        ToolTip.SetTip(b, tip);
        b.PointerEntered += (_, _) => b.Content = Icons.Make(icon, 18, danger ? AppTheme.Brush("Danger") : AppTheme.Brush("Text"));
        b.PointerExited += (_, _) => b.Content = Icons.Make(icon, 18, AppTheme.Brush("Muted"));
        b.Click += (_, _) => onClick();
        return b;
    }

    private void Choose(PostAction a)
    {
        Action = a;
        try
        {
            // Actions that need a file on disk get one saved here; OCR/GIF use the bitmap/region.
            if ((a == PostAction.Save || a == PostAction.Copy || a == PostAction.Edit || a == PostAction.Pin)
                && ResultBitmap != null)
                ResultPath = CaptureStore.SaveBitmap(ResultBitmap, CtxWithSize());
        }
        catch (Exception ex) { CrashLog.Write("commit-save", ex); }
        Close();
    }

    /// <summary>Crop from the frozen bitmap (no flicker/race); fall back to a live grab.</summary>
    private Bitmap CropFreezeOrLive(int px, int py, int pw, int ph)
    {
        if (_freeze != null)
        {
            int bx = Math.Clamp(px - _vx, 0, Math.Max(0, _freeze.Width - 1));
            int by = Math.Clamp(py - _vy, 0, Math.Max(0, _freeze.Height - 1));
            int bw = Math.Clamp(pw, 1, _freeze.Width - bx);
            int bh = Math.Clamp(ph, 1, _freeze.Height - by);
            return _freeze.Clone(new System.Drawing.Rectangle(bx, by, bw, bh), _freeze.PixelFormat);
        }
        Hide();
        return ScreenGrab.Grab(px, py, pw, ph);
    }

    // ---- window auto-detection ----

    private void EnumerateWindows()
    {
        var list = new System.Collections.Generic.List<WinRect>();
        var self = _selfHwnd;
        _enumCb = (h, _) =>   // held in a field so the marshaled delegate isn't GC'd mid-call
        {
            if (h == self) return true;
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0)
                if (!GetWindowRect(h, out r)) return true;
            if (r.Right - r.Left < 8 || r.Bottom - r.Top < 8) return true;
            string title = "";
            int len = GetWindowTextLengthW(h);
            if (len > 0) { var sb = new System.Text.StringBuilder(len + 1); GetWindowTextW(h, sb, sb.Capacity); title = sb.ToString(); }
            list.Add(new WinRect(h, r, title));
            return true;
        };
        try { EnumWindows(_enumCb, IntPtr.Zero); _windows = list; }
        catch (Exception ex) { CrashLog.Write("enum-windows", ex); _windows = null; }
    }

    private void UpdateWindowHover(POINT cur)
    {
        IntPtr hit = IntPtr.Zero; RECT hr = default; string title = L.T("ov.window");
        if (_windows != null)
            foreach (var win in _windows)   // forward = topmost-first (EnumWindows z-order)
                if (cur.X >= win.R.Left && cur.X < win.R.Right && cur.Y >= win.R.Top && cur.Y < win.R.Bottom)
                { hit = win.H; hr = win.R; if (!string.IsNullOrEmpty(win.Title)) title = win.Title; break; }

        // Unchanged hover → nothing to redo (the window list is frozen while the overlay is
        // open); bailing avoids re-laying-out the dim rects every render tick.
        if (hit == _hovered) return;

        if (hit == IntPtr.Zero)
        {
            _hovered = IntPtr.Zero;
            _winHi.IsVisible = false; _winLabel.IsVisible = false;
            SetHole(default);
            return;
        }

        _hovered = hit;
        var dip = PhysRectToDip(hr);
        Canvas.SetLeft(_winHi, dip.X); Canvas.SetTop(_winHi, dip.Y);
        _winHi.Width = dip.Width; _winHi.Height = dip.Height; _winHi.IsVisible = true;
        SetHole(dip);   // punch-through → hovered window reads bright

        _winLabelText.Text = title;
        _winLabel.Measure(Avalonia.Size.Infinity);
        double ly = dip.Y - _winLabel.DesiredSize.Height - 4; if (ly < 2) ly = dip.Y + 4;
        Canvas.SetLeft(_winLabel, Math.Max(2, dip.X));
        Canvas.SetTop(_winLabel, ly);
        _winLabel.IsVisible = true;
    }

    /// <summary>Map a physical-px window rect to overlay logical units, using the same scale
    /// the frozen image uses (physical bitmap stretched over the logical window).</summary>
    private Avalonia.Rect PhysRectToDip(RECT r)
    {
        double sx = (_freeze != null && _freeze.Width > 0) ? _lw / _freeze.Width : 1;
        double sy = (_freeze != null && _freeze.Height > 0) ? _lh / _freeze.Height : 1;
        double x = (r.Left - _vx) * sx, y = (r.Top - _vy) * sy;
        double w = (r.Right - r.Left) * sx, h = (r.Bottom - r.Top) * sy;
        double x2 = Math.Min(_lw, x + w), y2 = Math.Min(_lh, y + h);
        x = Math.Max(0, x); y = Math.Max(0, y);
        return new Avalonia.Rect(x, y, Math.Max(0, x2 - x), Math.Max(0, y2 - y));
    }

    /// <summary>Capture the currently-hovered window (physical rect → freeze crop), same tail as a drag.</summary>
    private bool CaptureHoveredWindow()
    {
        if (_windows == null) return false;
        RECT? found = null;
        foreach (var win in _windows) if (win.H == _hovered) { found = win.R; break; }
        if (found is not RECT hr) return false;

        int px = hr.Left, py = hr.Top, pw = hr.Right - hr.Left, ph = hr.Bottom - hr.Top;
        if (pw < 1 || ph < 1) return false;

        RegionPx = new PixelRect(px, py, pw, ph);
        LastRegion = RegionPx;
        if (_mode == CaptureMode.Region) { Close(); return true; }

        try { ResultBitmap = CropFreezeOrLive(px, py, pw, ph); } catch (Exception ex) { CrashLog.Write("window-grab", ex); }
        if (_mode == CaptureMode.OcrText) { Close(); return true; }
        if (Settings.Current.PostCaptureToolbar && ResultBitmap != null) { EnterCommitted(); return true; }
        if (ResultBitmap != null)
        {
            try { ResultPath = CaptureStore.SaveBitmap(ResultBitmap, CtxWithSize()); Action = PostAction.Save; }
            catch (Exception ex) { CrashLog.Write("window-save", ex); }
        }
        Close();
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern int GetWindowTextW(IntPtr hWnd, [Out] System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(IntPtr hWnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
}
