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
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Wsnap;

/// <summary>
/// The macOS-style floating thumbnail — Avalonia port (Phase 3) of the WPF ThumbnailWindow.
/// Appears bottom-right after capture and STACKS upward when several captures are live.
///  - LEFT-DRAG it out    -> delivers a real FileDrop (spike (c) path: DataFormats.Files with
///                           a PRE-RESOLVED IStorageFile — resolving inside the press handler
///                           is too late; see the handoff doc pitfalls).
///  - CLICK it            -> copies the IMAGE to the clipboard (Ctrl+click = copy path).
///  - Hover action bar    -> 복사 / 저장 / 텍스트(OCR) / 폴더 / 공유 / 핀 / 닫기.
///    (편집 버튼은 EditorWindow가 Phase 5에서 이식될 때 추가 — WPF 파일과의 의도적 차이.)
///  - PIN it              -> never auto-dismisses; promoted out of %TEMP% so it survives.
///  - RIGHT-DRAG sideways -> flings it off the right edge to clear it.
///  - Ignore it           -> auto-dismisses after Settings.AutoDismissSeconds (0 = never).
/// All placement is physical-pixel via MonitorPlacement (mixed-DPI safe), same as WPF.
/// </summary>
public sealed class ThumbnailWindow : Window
{
    private const double EdgeMargin = 24;
    private const double Gap = 12;
    private const double FlingThreshold = 40;
    private const double DragThreshold = 4;

    private static readonly List<ThumbnailWindow> Stack = new();

    // Target monitor for the stack, in PHYSICAL device pixels (see WPF file).
    private static System.Drawing.Rectangle _targetWorkPx;
    private static double _targetScale = 1.0;
    private static bool _targetSet;

    private IntPtr _hwnd;

    private string _filePath;
    private IStorageFile? _dragFile;             // pre-resolved for drag-out (spike (c) pitfall ③)
    private readonly bool _isVideo;
    private readonly Image _img;
    private readonly Border _actionBar;
    private readonly Border _root;
    private readonly Border? _badge;
    private ToggleButton _pinBtn = null!;
    private readonly DispatcherTimer _dismiss;
    private readonly TranslateTransform _slide = new();
    private Avalonia.Point _dragStart, _flingStart;
    private bool _maybeDrag, _maybeFling, _closing, _pinned;

    public ThumbnailWindow(string filePath, bool edited = false, string? poster = null)
    {
        _filePath = filePath;
        _isVideo = poster != null;

        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = 220;
        Height = 158;
        FontFamily = AppTheme.Font;

        _img = new Image { Source = LoadThumb(poster ?? filePath), Stretch = Stretch.Uniform, IsHitTestVisible = false };

        _actionBar = BuildActionBar();

        if (edited)
        {
            _badge = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8, 8, 0, 0),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 2, 7, 2),
                Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(0xF0, AppTheme.Accent.R, AppTheme.Accent.G, AppTheme.Accent.B)),
                Child = new TextBlock
                {
                    Text = L.T("thumb.edited"),
                    Foreground = Brushes.White,
                    FontSize = 10.5, FontWeight = FontWeight.SemiBold
                }
            };
        }

        var grid = new Grid();
        grid.Children.Add(_img);
        if (_badge != null) grid.Children.Add(_badge);
        grid.Children.Add(_actionBar);

        _root = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Avalonia.Media.Color.FromRgb(0x12, 0x13, 0x15)),
            BorderBrush = new SolidColorBrush(AppTheme.BorderStrong),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5),
            BoxShadow = BoxShadows.Parse("0 4 22 0 #8C000000"),
            Child = grid,
            RenderTransform = _slide,
            RenderTransformOrigin = RelativePoint.Parse("50%,90%")
        };
        Content = _root;

        // Fades/slides ride Transitions (Avalonia idiom; WPF used BeginAnimation).
        _actionBar.Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(150), Easing = new CubicEaseOut() }
        };
        Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(185), Easing = new CubicEaseIn() }
        };
        _slide.Transitions = new Transitions
        {
            new DoubleTransition { Property = TranslateTransform.XProperty, Duration = TimeSpan.FromMilliseconds(190), Easing = new CubicEaseIn() }
        };

        PointerPressed += OnDown;
        PointerMoved += OnMove;
        PointerReleased += OnUp;
        PointerEntered += (_, _) =>
        {
            _dismiss?.Stop();
            FadeBar(true);
            if (_badge != null) _badge.IsVisible = false;
        };
        PointerExited += (_, _) =>
        {
            FadeBar(false);
            if (_badge != null) _badge.IsVisible = true;
            StartDismissIfEnabled();
        };

        _dismiss = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(1, Settings.Current.AutoDismissSeconds)) };
        _dismiss.Tick += (_, _) => { _dismiss.Stop(); DismissSlide(); };

        UpdateTargetMonitor();

        Stack.Add(this);
        int max = Math.Max(1, Settings.Current.MaxVisible);
        while (Stack.Count > max) Stack[0].DismissNow();

        Opened += (_, _) =>
        {
            _hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            Reflow();
            PlayPop();
            ResolveDragFile();
        };
        ScalingChanged += (_, _) => Dispatcher.UIThread.Post(Reflow, DispatcherPriority.Background);
        StartDismissIfEnabled();
    }

    /// <summary>Resolve the drag-out IStorageFile ahead of time — doing it inside the pointer
    /// press means the press is already over by the time the await completes (spike (c)).</summary>
    private async void ResolveDragFile()
    {
        try { _dragFile = await StorageProvider.TryGetFileFromPathAsync(_filePath); }
        catch { _dragFile = null; }
    }

    // ---------- action bar ----------

    private Border BuildActionBar()
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        bar.Children.Add(IconBtn("copy", L.T("thumb.copy"), CopyImage));
        bar.Children.Add(IconBtn("save", L.T("thumb.saveAs"), SaveAs));
        // OCR is image-only. (Edit arrives with the Phase 5 EditorWindow port.)
        if (!_isVideo)
            bar.Children.Add(IconBtn("text", L.T("thumb.ocr"), OcrCurrent));
        bar.Children.Add(IconBtn("folder", L.T("thumb.reveal"), Reveal));
        if (Uploader.Available)
            bar.Children.Add(IconBtn("share", L.T("thumb.share"), ShareCurrent));
        _pinBtn = PinToggle();
        bar.Children.Add(_pinBtn);
        bar.Children.Add(IconBtn("close", L.T("thumb.close"), () => DismissSlide(), danger: true));

        return new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Height = 34,
            CornerRadius = new CornerRadius(0, 0, 8, 8),
            Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(0xE6, 0x12, 0x13, 0x15)),
            Child = bar,
            Opacity = 0,
            IsHitTestVisible = false
        };
    }

    private Button IconBtn(string icon, string tip, Action onClick, bool danger = false)
    {
        var b = new Button
        {
            Width = 26, Height = 26,
            Padding = new Thickness(0),
            Margin = new Thickness(1, 0, 1, 0),
            Content = Icons.Make(icon, 15, AppTheme.Brush("Muted")),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        b.Classes.Add("subtle");
        ToolTip.SetTip(b, tip);
        b.PointerEntered += (_, _) => b.Content = Icons.Make(icon, 15, danger ? AppTheme.Brush("Danger") : AppTheme.Brush("Text"));
        b.PointerExited += (_, _) => b.Content = Icons.Make(icon, 15, AppTheme.Brush("Muted"));
        b.Click += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    private ToggleButton PinToggle()
    {
        var t = new ToggleButton
        {
            Width = 26, Height = 26,
            Padding = new Thickness(0),
            Margin = new Thickness(1, 0, 1, 0),
            Content = Icons.Make("pin", 15, AppTheme.Brush("Muted"))
        };
        t.Classes.Add("tool");
        ToolTip.SetTip(t, L.T("thumb.pin"));
        t.IsCheckedChanged += (_, e) => { e.Handled = true; SetPinned(t.IsChecked == true); };
        t.PointerEntered += (_, _) => { if (t.IsChecked != true) t.Content = Icons.Make("pin", 15, AppTheme.Brush("Text")); };
        t.PointerExited += (_, _) => { if (t.IsChecked != true) t.Content = Icons.Make("pin", 15, AppTheme.Brush("Muted")); };
        return t;
    }

    private void FadeBar(bool show)
    {
        _actionBar.IsHitTestVisible = show;
        _actionBar.Opacity = show ? 1 : 0;   // animates via the transition
    }

    private void PlayPop()
    {
        var st = new ScaleTransform(0.86, 0.86)
        {
            Transitions = new Transitions
            {
                new DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = TimeSpan.FromMilliseconds(180), Easing = new CubicEaseOut() },
                new DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = TimeSpan.FromMilliseconds(180), Easing = new CubicEaseOut() },
            }
        };
        _root.RenderTransform = st;
        Dispatcher.UIThread.Post(() => { st.ScaleX = 1; st.ScaleY = 1; });
        // restore the slide transform for dismiss once the pop settles
        DispatcherTimer.RunOnce(() => { if (!_closing) _root.RenderTransform = _slide; }, TimeSpan.FromMilliseconds(220));
    }

    /// <summary>Decode at ~2× the on-screen box, height- or width-limited by aspect (same
    /// memory rationale as WPF: pinned thumbnails stay resident). SkiaSharp reads the header
    /// for dimensions; Avalonia's DecodeToWidth/Height does the scaled decode.</summary>
    private static Bitmap LoadThumb(string path)
    {
        const int boxW = 440, boxH = 316;
        int ow = 0, oh = 0;
        try
        {
            using var codec = SkiaSharp.SKCodec.Create(path);
            if (codec != null) { ow = codec.Info.Width; oh = codec.Info.Height; }
        }
        catch { /* fall back to a full decode below */ }

        using var fs = File.OpenRead(path);
        if (ow > 0 && oh > 0)
        {
            bool widthLimited = (long)ow * boxH >= (long)oh * boxW;   // wider than the box aspect
            if (widthLimited && ow > boxW) return Bitmap.DecodeToWidth(fs, boxW);
            if (!widthLimited && oh > boxH) return Bitmap.DecodeToHeight(fs, boxH);
        }
        return new Bitmap(fs);
    }

    /// <summary>Re-stack every live thumbnail bottom-right of the TARGET monitor, entirely in
    /// physical device pixels via SetWindowPos (mixed-DPI safe — see the WPF file).</summary>
    private static void Reflow()
    {
        if (!_targetSet) UpdateTargetMonitor();
        var wa = _targetWorkPx;
        double s = _targetScale;
        double margin = EdgeMargin * s;
        double gap = Gap * s;

        double y = wa.Bottom - margin;           // physical px, bottom edge of work area
        for (int i = Stack.Count - 1; i >= 0; i--)
        {
            var w = Stack[i];
            if (w._closing) continue;
            double wPx = w.Width * s;
            double hPx = w.Height * s;
            double xPx = wa.Right - wPx - margin;
            MonitorPlacement.SetBoundsPx(w._hwnd, xPx, y - hPx, wPx, hPx);
            y -= hPx + gap;
        }
    }

    private static void UpdateTargetMonitor()
    {
        (_targetWorkPx, _targetScale) = MonitorPlacement.CursorWorkArea();
        _targetSet = true;
    }

    /// <summary>Tray-menu "전체 지우기".</summary>
    public static void ClearAll()
    {
        foreach (var w in Stack.ToArray()) w.DismissNow();
    }

    /// <summary>Programmatically pin (used by the capture toolbar's Pin action).</summary>
    public void PinNow() { _pinBtn.IsChecked = true; }

    // ---- dismiss policy ----

    private void StartDismissIfEnabled()
    {
        if (_closing || _pinned) return;
        if (Settings.Current.AutoDismissSeconds <= 0) return;   // 0 = never
        if (IsPointerOver) return;
        _dismiss.Interval = TimeSpan.FromSeconds(Settings.Current.AutoDismissSeconds);
        _dismiss.Start();
    }

    private void SetPinned(bool on)
    {
        _pinned = on;
        if (on)
        {
            _dismiss.Stop();
            string moved = CaptureStore.PromoteToPinned(_filePath);
            if (!string.Equals(moved, _filePath, StringComparison.OrdinalIgnoreCase))
            {
                _filePath = moved;
                ResolveDragFile();   // the drag-out payload must follow the promoted path
            }
            _root.BorderBrush = AppTheme.Brush("Accent");
            _pinBtn.Content = Icons.Make("pin", 15, Brushes.White);
            Toast.Show(L.T("thumb.pinned"));
        }
        else
        {
            _root.BorderBrush = new SolidColorBrush(AppTheme.BorderStrong);
            _pinBtn.Content = Icons.Make("pin", 15, AppTheme.Brush("Muted"));
            StartDismissIfEnabled();
        }
    }

    // ---- input ----

    private void OnDown(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed) { _dragStart = e.GetPosition(this); _maybeDrag = true; }
        else if (props.IsRightButtonPressed) { _flingStart = e.GetPosition(this); _maybeFling = true; }
    }

    private async void OnMove(object? sender, PointerEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (_maybeFling && props.IsRightButtonPressed)
        {
            var rp = e.GetPosition(this);
            if (Math.Abs(rp.X - _flingStart.X) > FlingThreshold) { _maybeFling = false; DismissSlide(); }
            return;
        }

        if (!_maybeDrag || !props.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.X) < DragThreshold && Math.Abs(p.Y - _dragStart.Y) < DragThreshold)
            return;

        _maybeDrag = false;
        _dismiss.Stop();

        if (_dragFile != null)
        {
            var data = new DataObject();
            data.Set(DataFormats.Files, new[] { _dragFile });
            // 11.2: DoDragDrop returns Task<DragDropEffects> (no DoDragDropAsync name yet).
            // wsnap stays alive after the drop, so Explorer's async data extraction is safe
            // (spike (c) pitfall ②).
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
        }

        StartDismissIfEnabled();   // reusable; keep on screen
    }

    private void OnUp(object? sender, PointerReleasedEventArgs e)
    {
        if (!_maybeDrag || e.InitialPressMouseButton != MouseButton.Left) { _maybeFling = false; return; }
        _maybeDrag = false;
        // Ctrl+click = copy the path (power users / terminals); plain click = copy IMAGE.
        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            ClipboardCore.CopyTextSuppressed(_filePath);
            Toast.Show(L.T("thumb.pathCopied"));
        }
        else CopyImage();
    }

    // ---- actions ----

    private void CopyImage()
    {
        // A video has no image to put on the clipboard; plain click copies the mp4 path instead.
        if (_isVideo) { ClipboardCore.CopyTextSuppressed(_filePath); Toast.Show(L.T("thumb.pathCopied")); return; }
        if (ClipboardCore.CopyImageFile(_filePath)) Toast.Show(L.T("toast.imageCopied"));
        else Toast.Show(L.T("thumb.copyFail"));
    }

    private async void SaveAs()
    {
        _dismiss.Stop();
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L.T("thumb.saveTitle"),
                SuggestedFileName = Path.GetFileName(_filePath),
                DefaultExtension = Path.GetExtension(_filePath).TrimStart('.'),
            });
            if (file?.TryGetLocalPath() is string dest)
            {
                File.Copy(_filePath, dest, overwrite: true);
                Toast.Show(L.T("thumb.saved"));
            }
        }
        catch (Exception ex) { CrashLog.Write("save-as", ex); Toast.Show(L.T("thumb.saveFail")); }
        finally { StartDismissIfEnabled(); }
    }

    private void Reveal()
    {
        try
        {
            if (File.Exists(_filePath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_filePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { CrashLog.Write("reveal", ex); }
    }

    private async void OcrCurrent()
    {
        _dismiss.Stop();
        Toast.Show(L.T("toast.ocrBusy"));
        try
        {
            using var bmp = new System.Drawing.Bitmap(_filePath);
            string? text = await Ocr.RecognizeAsync(bmp);
            if (text == null)
                Toast.Show(L.T("toast.ocrUnavailable"), 2600);
            else if (text.Trim().Length == 0)
                Toast.Show(L.T("toast.ocrNoText"));
            else
            {
                ClipboardCore.CopyTextSuppressed(text);
                Toast.Show(L.T("toast.textCopied"));
            }
        }
        catch (Exception ex) { CrashLog.Write("ocr-thumb", ex); Toast.Show(L.T("toast.ocrFailed")); }
        finally { StartDismissIfEnabled(); }
    }

    private async void ShareCurrent()
    {
        if (!Uploader.Available) { Toast.Show(L.T("thumb.uploadDisabled"), 2600); return; }
        _dismiss.Stop();
        Toast.Show(L.T("thumb.uploading"));
        try
        {
            string? url = await Uploader.UploadImgurAsync(_filePath);
            if (string.IsNullOrEmpty(url)) Toast.Show(L.T("thumb.uploadFail"));
            else { ClipboardCore.CopyTextSuppressed(url); Toast.Show(L.T("thumb.linkCopied"), 2200); }
        }
        catch (Exception ex) { CrashLog.Write("share-thumb", ex); Toast.Show(L.T("thumb.uploadFail")); }
        finally { StartDismissIfEnabled(); }
    }

    // ---- dismissal ----

    private void DismissNow()
    {
        if (_closing) return;
        _closing = true;
        _dismiss.Stop();
        Close();
    }

    private void DismissSlide()
    {
        if (_closing) return;
        _closing = true;
        _dismiss.Stop();
        Reflow();   // lift the survivors immediately

        // Slide off to the right + fade via the pre-attached transitions. Uses the inner
        // RenderTransform (window-local units), not window position — the HWND is placed in
        // physical pixels via SetWindowPos and must not be re-positioned logically.
        _root.RenderTransform = _slide;
        _slide.X = Width;
        Opacity = 0;
        DispatcherTimer.RunOnce(Close, TimeSpan.FromMilliseconds(210));
    }

    protected override void OnClosed(EventArgs e)
    {
        Stack.Remove(this);
        Reflow();
        base.OnClosed(e);
    }
}
