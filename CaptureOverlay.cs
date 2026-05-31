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
using System.Drawing;          // System.Drawing.Common (WinForms ref) for Bitmap/Graphics.CopyFromScreen
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Wsnap;

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
/// A borderless, topmost window covering the entire virtual desktop.
///
/// On open it FREEZES the desktop into one physical-pixel bitmap and shows that, so the
/// scene can't shift mid-selection. The backdrop dims everything EXCEPT the selection (an
/// even-odd "punch-through" hole) so the region reads bright/live, a live W×H badge tracks
/// the drag, and a magnifier loupe shows zoomed pixels + cursor coords + the hex colour
/// under the cursor (press C to copy it).
///
/// All grabbing/cropping uses PHYSICAL device pixels (GetCursorPos + the frozen bitmap),
/// the same space CopyFromScreen uses, so it's correct across mixed-DPI / fractional
/// scaling without per-window DIP guesswork.
/// </summary>
public sealed class CaptureOverlay : Window
{
    private readonly CaptureMode _mode;
    private System.Windows.Point _start;       // DIP, window-local (for visuals)
    private POINT _startPhys;                   // physical device px (for the grab)
    private bool _dragging;

    private readonly System.Windows.Controls.Canvas _canvas;
    private readonly RectangleGeometry _holeGeo;
    private readonly System.Windows.Shapes.Rectangle _selection;
    private readonly Border _badge;
    private readonly TextBlock _badgeText;
    private readonly Border _hint;
    private readonly System.Windows.Media.Color _accent;

    // frozen desktop (physical px) — sampled for the loupe and cropped for the result.
    private Bitmap? _freeze;
    private int _vx, _vy;                        // virtual-screen origin (physical px)

    // loupe
    private readonly Border _loupe;
    private readonly System.Windows.Controls.Image _loupeImg;
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
    private readonly System.Windows.Shapes.Rectangle _winHi;
    private readonly Border _winLabel;
    private readonly TextBlock _winLabelText;

    public string? ResultPath { get; private set; }
    public Bitmap? ResultBitmap { get; private set; }
    public System.Windows.Int32Rect? RegionPx { get; private set; }
    public static System.Windows.Int32Rect? LastRegion { get; private set; }

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
        _accent = mode == CaptureMode.OcrText ? Theme.Success
                : mode == CaptureMode.ColorPick ? Theme.Warn
                : Theme.Accent;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        _canvas = new System.Windows.Controls.Canvas();

        // freeze the desktop (best-effort; fall back to a live grab if it fails)
        TryFreeze();
        if (_freeze != null)
        {
            var frozen = new System.Windows.Controls.Image
            {
                Source = ScreenGrab.ToBitmapSource(_freeze),
                Width = Width, Height = Height, Stretch = Stretch.Fill, IsHitTestVisible = false
            };
            _canvas.Children.Add(frozen);
        }

        // punch-through dim
        var outer = new RectangleGeometry(new Rect(0, 0, Width, Height));
        _holeGeo = new RectangleGeometry(new Rect(0, 0, 0, 0));
        var grp = new GeometryGroup { FillRule = FillRule.EvenOdd };
        grp.Children.Add(outer);
        grp.Children.Add(_holeGeo);
        _canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = grp,
            Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x73, 0, 0, 0)),
            IsHitTestVisible = false
        });

        _selection = new System.Windows.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(_accent), StrokeThickness = 1.5,
            Fill = System.Windows.Media.Brushes.Transparent,
            Visibility = Visibility.Collapsed, IsHitTestVisible = false
        };
        _canvas.Children.Add(_selection);

        // window-hover highlight (dashed) + title label
        _winHi = new System.Windows.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(_accent), StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            Fill = System.Windows.Media.Brushes.Transparent,
            Visibility = Visibility.Collapsed, IsHitTestVisible = false
        };
        _canvas.Children.Add(_winHi);
        _winLabelText = new TextBlock { Foreground = System.Windows.Media.Brushes.White, FontFamily = Theme.Font, FontSize = 11 };
        _winLabel = new Border
        {
            CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 2, 6, 2),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xDC, 0x14, 0x16, 0x19)),
            Child = _winLabelText, Visibility = Visibility.Collapsed, IsHitTestVisible = false
        };
        _canvas.Children.Add(_winLabel);

        _badgeText = new TextBlock { Foreground = System.Windows.Media.Brushes.White, FontFamily = Theme.Font, FontSize = 12, FontWeight = FontWeights.SemiBold };
        _badge = new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 3, 7, 3),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xDC, 0x14, 0x16, 0x19)),
            Child = _badgeText, Visibility = Visibility.Collapsed, IsHitTestVisible = false
        };
        _canvas.Children.Add(_badge);

        _hint = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 9, 14, 9),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0x14, 0x16, 0x19)),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = mode == CaptureMode.OcrText ? "텍스트 영역 드래그 · 창 클릭 · Esc 취소"
                     : mode == CaptureMode.ColorPick ? "픽셀을 클릭해 색을 복사 · Esc 취소"
                     : "드래그=영역 · 창 클릭=창 캡처 · C=색 복사 · Esc 취소",
                Foreground = System.Windows.Media.Brushes.White, FontFamily = Theme.Font, FontSize = 13
            }
        };
        _canvas.Children.Add(_hint);

        // loupe (magnifier + hex/coords)
        _loupeImg = new System.Windows.Controls.Image { Width = 120, Height = 96, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_loupeImg, BitmapScalingMode.NearestNeighbor);
        var loupeGrid = new Grid { Width = 120, Height = 96 };
        loupeGrid.Children.Add(_loupeImg);
        loupeGrid.Children.Add(new System.Windows.Shapes.Rectangle { Width = 1.5, Height = 96, Fill = new SolidColorBrush(_accent), HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.85 });
        loupeGrid.Children.Add(new System.Windows.Shapes.Rectangle { Width = 120, Height = 1.5, Fill = new SolidColorBrush(_accent), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 });
        _loupeText = new TextBlock { Foreground = System.Windows.Media.Brushes.White, FontFamily = Theme.Font, FontSize = 11, TextAlignment = TextAlignment.Center, Padding = new Thickness(0, 3, 0, 3) };
        var loupeStack = new StackPanel();
        loupeStack.Children.Add(new Border { Child = loupeGrid, BorderBrush = new SolidColorBrush(_accent), BorderThickness = new Thickness(1, 1, 1, 0) });
        loupeStack.Children.Add(new Border { Child = _loupeText, Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xE6, 0x14, 0x16, 0x19)) });
        _loupe = new Border
        {
            Child = loupeStack, CornerRadius = new CornerRadius(7),
            Visibility = _freeze != null ? Visibility.Collapsed : Visibility.Collapsed,
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.5 }
        };
        _canvas.Children.Add(_loupe);

        Content = _canvas;

        Loaded += (_, _) =>
        {
            _hint.Measure(new System.Windows.Size(Width, Height));
            System.Windows.Controls.Canvas.SetLeft(_hint, (Width - _hint.DesiredSize.Width) / 2);
            System.Windows.Controls.Canvas.SetTop(_hint, Height * 0.62);
        };

        SourceInitialized += (_, _) =>
        {
            _selfHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (_mode != CaptureMode.ColorPick) EnumerateWindows();
        };
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        KeyDown += OnKey;
        Closed += (_, _) => { _freeze?.Dispose(); _freeze = null; };
    }

    private void TryFreeze()
    {
        try
        {
            _vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            _vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (w > 0 && h > 0) _freeze = ScreenGrab.Grab(_vx, _vy, w, h);
        }
        catch (Exception ex) { CrashLog.Write("overlay-freeze", ex); _freeze = null; }
    }

    private void OnKey(object sender, KeyEventArgs e)
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
        else if (e.Key == Key.C) { ImageClipboard.CopyText(_hex); Toast.Show($"{_hex} 복사됨 ✓"); }
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_committed) return;
        GetCursorPos(out _startPhys);
        if (_mode == CaptureMode.ColorPick)
        {
            ImageClipboard.CopyText(_hex);
            Toast.Show($"{_hex} 복사됨 ✓");
            Close();
            return;
        }
        _start = e.GetPosition(this);
        _dragging = true;
        // hide the hover highlight while dragging, but KEEP _hovered so a no-drag click can still grab the window
        _winHi.Visibility = Visibility.Collapsed; _winLabel.Visibility = Visibility.Collapsed;
        _selection.Visibility = Visibility.Visible;
        _hint.Visibility = Visibility.Collapsed;
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        GetCursorPos(out POINT cur);
        UpdateLoupe(p, cur);

        if (!_dragging)
        {
            if (!_committed && _mode != CaptureMode.ColorPick) UpdateWindowHover(cur);
            return;
        }
        double x = Math.Min(p.X, _start.X), y = Math.Min(p.Y, _start.Y);
        double w = Math.Abs(p.X - _start.X), h = Math.Abs(p.Y - _start.Y);
        System.Windows.Controls.Canvas.SetLeft(_selection, x);
        System.Windows.Controls.Canvas.SetTop(_selection, y);
        _selection.Width = w; _selection.Height = h;
        _holeGeo.Rect = new Rect(x, y, w, h);

        int pw = Math.Abs(cur.X - _startPhys.X);
        int ph = Math.Abs(cur.Y - _startPhys.Y);
        _badgeText.Text = $"{pw} × {ph}";
        _badge.Measure(new System.Windows.Size(Width, Height));
        double bx = Math.Min(x, Width - _badge.DesiredSize.Width - 2);
        double by = y - _badge.DesiredSize.Height - 6;
        if (by < 2) by = y + 6;
        System.Windows.Controls.Canvas.SetLeft(_badge, Math.Max(2, bx));
        System.Windows.Controls.Canvas.SetTop(_badge, by);
        _badge.Visibility = (pw > 1 || ph > 1) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateLoupe(System.Windows.Point dip, POINT phys)
    {
        if (_freeze == null) return;
        int bx = phys.X - _vx, by = phys.Y - _vy;
        if (bx < 0 || by < 0 || bx >= _freeze.Width || by >= _freeze.Height) { _loupe.Visibility = Visibility.Collapsed; return; }

        const int sample = 25;                  // odd → real center pixel
        int sx = Math.Clamp(bx - sample / 2, 0, Math.Max(0, _freeze.Width - sample));
        int sy = Math.Clamp(by - sample / 2, 0, Math.Max(0, _freeze.Height - sample));
        int sw = Math.Min(sample, _freeze.Width - sx);
        int sh = Math.Min(sample, _freeze.Height - sy);
        try
        {
            using var crop = _freeze.Clone(new System.Drawing.Rectangle(sx, sy, sw, sh), _freeze.PixelFormat);
            _loupeImg.Source = ScreenGrab.ToBitmapSource(crop);
            var c = _freeze.GetPixel(Math.Clamp(bx, 0, _freeze.Width - 1), Math.Clamp(by, 0, _freeze.Height - 1));
            _hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            _loupeText.Text = $"{_hex}   {phys.X}, {phys.Y}";
        }
        catch { return; }

        double lx = dip.X + 20, ly = dip.Y + 24;
        if (lx + 124 > Width) lx = dip.X - 144;
        if (ly + 128 > Height) ly = dip.Y - 132;
        System.Windows.Controls.Canvas.SetLeft(_loupe, Math.Max(2, lx));
        System.Windows.Controls.Canvas.SetTop(_loupe, Math.Max(2, ly));
        _loupe.Visibility = Visibility.Visible;
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

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

        RegionPx = new System.Windows.Int32Rect(px, py, pw, ph);
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
        _loupe.Visibility = Visibility.Collapsed;
        _badge.Visibility = Visibility.Collapsed;

        _toolbar = BuildToolbar();
        _canvas.Children.Add(_toolbar);
        _toolbar.Measure(new System.Windows.Size(Width, Height));
        var sz = _toolbar.DesiredSize;

        var sel = _holeGeo.Rect;
        double tx = sel.X + (sel.Width - sz.Width) / 2;
        double ty = sel.Bottom + 12;
        if (ty + sz.Height > Height - 4) ty = sel.Y - sz.Height - 12;   // flip above
        if (ty < 4) ty = Math.Max(4, sel.Y + 8);                         // tiny selection → inside-ish
        tx = Math.Clamp(tx, 4, Math.Max(4, Width - sz.Width - 4));
        System.Windows.Controls.Canvas.SetLeft(_toolbar, tx);
        System.Windows.Controls.Canvas.SetTop(_toolbar, ty);
    }

    private Border BuildToolbar()
    {
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        row.Children.Add(ToolbarBtn("copy", "복사 (C)", () => Choose(PostAction.Copy)));
        row.Children.Add(ToolbarBtn("save", "저장 (Enter)", () => Choose(PostAction.Save)));
        row.Children.Add(ToolbarBtn("edit", "편집 (E)", () => Choose(PostAction.Edit)));
        row.Children.Add(ToolbarBtn("text", "텍스트 추출 (T)", () => Choose(PostAction.Ocr)));
        row.Children.Add(ToolbarBtn("gif", "GIF 녹화 (G)", () => Choose(PostAction.Gif)));
        row.Children.Add(ToolbarBtn("pin", "고정 (P)", () => Choose(PostAction.Pin)));
        row.Children.Add(ToolbarBtn("close", "취소 (Esc)", () => { Action = PostAction.Cancel; Close(); }, danger: true));

        return new Border
        {
            Child = row, Padding = new Thickness(4),
            CornerRadius = new CornerRadius(10),
            Background = Theme.Brush("Panel"),
            BorderBrush = Theme.Stroke(Theme.BorderStrong), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.55, Color = Colors.Black }
        };
    }

    private System.Windows.Controls.Button ToolbarBtn(string icon, string tip, Action onClick, bool danger = false)
    {
        var b = new System.Windows.Controls.Button
        {
            Style = Theme.Style("SubtleButton"),
            Width = 34, Height = 34, Padding = new Thickness(0), Margin = new Thickness(1, 0, 1, 0),
            Content = Icons.Make(icon, 18, Theme.Brush("Muted")), ToolTip = tip
        };
        b.MouseEnter += (_, _) => b.Content = Icons.Make(icon, 18, danger ? Theme.Brush("Danger") : Theme.Brush("Text"));
        b.MouseLeave += (_, _) => b.Content = Icons.Make(icon, 18, Theme.Brush("Muted"));
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
        IntPtr hit = IntPtr.Zero; RECT hr = default; string title = "창";
        if (_windows != null)
            foreach (var win in _windows)   // forward = topmost-first (EnumWindows z-order)
                if (cur.X >= win.R.Left && cur.X < win.R.Right && cur.Y >= win.R.Top && cur.Y < win.R.Bottom)
                { hit = win.H; hr = win.R; if (!string.IsNullOrEmpty(win.Title)) title = win.Title; break; }

        if (hit == IntPtr.Zero)
        {
            _hovered = IntPtr.Zero;
            _winHi.Visibility = Visibility.Collapsed; _winLabel.Visibility = Visibility.Collapsed;
            _holeGeo.Rect = new Rect(0, 0, 0, 0);
            return;
        }

        _hovered = hit;
        var dip = PhysRectToDip(hr);
        System.Windows.Controls.Canvas.SetLeft(_winHi, dip.X); System.Windows.Controls.Canvas.SetTop(_winHi, dip.Y);
        _winHi.Width = dip.Width; _winHi.Height = dip.Height; _winHi.Visibility = Visibility.Visible;
        _holeGeo.Rect = dip;   // punch-through → hovered window reads bright

        _winLabelText.Text = title;
        _winLabel.Measure(new System.Windows.Size(Width, Height));
        double ly = dip.Y - _winLabel.DesiredSize.Height - 4; if (ly < 2) ly = dip.Y + 4;
        System.Windows.Controls.Canvas.SetLeft(_winLabel, Math.Max(2, dip.X));
        System.Windows.Controls.Canvas.SetTop(_winLabel, ly);
        _winLabel.Visibility = Visibility.Visible;
    }

    /// <summary>Map a physical-px window rect to overlay DIP, using the same scale the frozen image uses.</summary>
    private Rect PhysRectToDip(RECT r)
    {
        double sx = (_freeze != null && _freeze.Width > 0) ? Width / _freeze.Width : 1;
        double sy = (_freeze != null && _freeze.Height > 0) ? Height / _freeze.Height : 1;
        double x = (r.Left - _vx) * sx, y = (r.Top - _vy) * sy;
        double w = (r.Right - r.Left) * sx, h = (r.Bottom - r.Top) * sy;
        double x2 = Math.Min(Width, x + w), y2 = Math.Min(Height, y + h);
        x = Math.Max(0, x); y = Math.Max(0, y);
        return new Rect(x, y, Math.Max(0, x2 - x), Math.Max(0, y2 - y));
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

        RegionPx = new System.Windows.Int32Rect(px, py, pw, ph);
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

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern int GetWindowTextW(IntPtr hWnd, [System.Runtime.InteropServices.Out] System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(IntPtr hWnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
}
