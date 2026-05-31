using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Wsnap;

/// <summary>
/// The macOS-style floating thumbnail. Appears bottom-right after capture and
/// STACKS upward when several captures are live (newest at the bottom).
///  - LEFT-DRAG it out    -> delivers a real FileDrop. Stays put (drag it many places).
///  - CLICK it            -> copies the IMAGE to the clipboard (Ctrl+click = copy path).
///  - Hover action bar    -> 복사 / 저장 / 편집 / 텍스트(OCR) / 폴더 / 공유 / 핀 / 닫기.
///  - PIN it              -> never auto-dismisses; promoted out of %TEMP% so it survives.
///  - RIGHT-DRAG sideways -> flings it off the right edge to clear it.
///  - Ignore it           -> auto-dismisses after Settings.AutoDismissSeconds (0 = never).
/// </summary>
public sealed class ThumbnailWindow : Window
{
    private const double EdgeMargin = 24;
    private const double Gap = 12;
    private const double FlingThreshold = 40;

    private static readonly List<ThumbnailWindow> Stack = new();

    private string _filePath;
    private readonly Image _img;
    private readonly Border _actionBar;
    private readonly Border _root;
    private readonly Border? _badge;
    private ToggleButton _pinBtn = null!;
    private readonly DispatcherTimer _dismiss;
    private System.Windows.Point _dragStart, _flingStart;
    private bool _maybeDrag, _maybeFling, _closing, _pinned;

    public ThumbnailWindow(string filePath, bool edited = false)
    {
        _filePath = filePath;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        Width = 220;
        Height = 158;

        _img = new Image { Source = LoadFrozen(filePath), Stretch = Stretch.Uniform, IsHitTestVisible = false };

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
                Background = Theme.Stroke(Color.FromArgb(0xF0, Theme.Accent.R, Theme.Accent.G, Theme.Accent.B)),
                Child = new TextBlock
                {
                    Text = "수정됨",
                    Foreground = System.Windows.Media.Brushes.White,
                    FontSize = 10.5, FontWeight = FontWeights.SemiBold, FontFamily = Theme.Font
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
            Background = Theme.Stroke(Color.FromRgb(0x12, 0x13, 0x15)),
            BorderBrush = Theme.Stroke(Theme.BorderStrong),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Opacity = 0.55, Color = Colors.Black },
            Child = grid
        };
        // crisp clip so the image respects the rounded corners
        Content = _root;

        MouseLeftButtonDown += OnDown;
        MouseRightButtonDown += OnRightDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseEnter += (_, _) =>
        {
            _dismiss?.Stop();
            FadeBar(true);
            if (_badge != null) _badge.Visibility = Visibility.Hidden;
        };
        MouseLeave += (_, _) =>
        {
            FadeBar(false);
            if (_badge != null) _badge.Visibility = Visibility.Visible;
            StartDismissIfEnabled();
        };

        _dismiss = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(1, Settings.Current.AutoDismissSeconds)) };
        _dismiss.Tick += (_, _) => { _dismiss.Stop(); DismissSlide(); };

        Stack.Add(this);
        int max = Math.Max(1, Settings.Current.MaxVisible);
        while (Stack.Count > max) Stack[0].DismissNow();

        Reflow();
        // entrance pop
        Loaded += (_, _) => PlayPop();
        StartDismissIfEnabled();
    }

    // ---------- action bar ----------

    private Border BuildActionBar()
    {
        var bar = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        bar.Children.Add(IconBtn("copy", "이미지 복사 (Ctrl+클릭=경로)", CopyImage));
        bar.Children.Add(IconBtn("save", "다른 이름으로 저장", SaveAs));
        bar.Children.Add(IconBtn("edit", "편집", EditCurrent));
        bar.Children.Add(IconBtn("text", "텍스트 추출 (OCR)", OcrCurrent));
        bar.Children.Add(IconBtn("folder", "폴더에서 보기", Reveal));
        if (Uploader.Available)
            bar.Children.Add(IconBtn("share", "업로드 후 링크 복사", ShareCurrent));
        _pinBtn = PinToggle();
        bar.Children.Add(_pinBtn);
        bar.Children.Add(IconBtn("close", "닫기", () => DismissSlide(), danger: true));

        return new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Height = 34,
            CornerRadius = new CornerRadius(0, 0, 8, 8),
            Background = Theme.Stroke(Color.FromArgb(0xE6, 0x12, 0x13, 0x15)),
            Child = bar,
            Opacity = 0,
            IsHitTestVisible = false
        };
    }

    private Button IconBtn(string icon, string tip, Action onClick, bool danger = false)
    {
        var brush = danger ? Theme.Brush("Muted") : Theme.Brush("Muted");
        var b = new Button
        {
            Style = Theme.Style("SubtleButton"),
            Width = 26, Height = 26,
            Padding = new Thickness(0),
            Margin = new Thickness(1, 0, 1, 0),
            Content = Icons.Make(icon, 15, Theme.Brush("Muted")),
            ToolTip = tip
        };
        // brighten the glyph on hover
        b.MouseEnter += (_, _) => b.Content = Icons.Make(icon, 15, danger ? Theme.Brush("Danger") : Theme.Brush("Text"));
        b.MouseLeave += (_, _) => b.Content = Icons.Make(icon, 15, Theme.Brush("Muted"));
        b.Click += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    private ToggleButton PinToggle()
    {
        var t = new ToggleButton
        {
            Style = Theme.Style("ToolToggle"),
            Width = 26, Height = 26,
            Padding = new Thickness(0),
            Margin = new Thickness(1, 0, 1, 0),
            Content = Icons.Make("pin", 15, Theme.Brush("Muted")),
            ToolTip = "고정 (자동 사라짐 끄기)"
        };
        t.Checked += (_, e) => { e.Handled = true; SetPinned(true); };
        t.Unchecked += (_, e) => { e.Handled = true; SetPinned(false); };
        t.MouseEnter += (_, _) => { if (t.IsChecked != true) t.Content = Icons.Make("pin", 15, Theme.Brush("Text")); };
        t.MouseLeave += (_, _) => { if (t.IsChecked != true) t.Content = Icons.Make("pin", 15, Theme.Brush("Muted")); };
        return t;
    }

    private void FadeBar(bool show)
    {
        _actionBar.IsHitTestVisible = show;
        _actionBar.BeginAnimation(OpacityProperty,
            new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 130 : 160))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void PlayPop()
    {
        var st = new ScaleTransform(0.86, 0.86);
        _root.RenderTransformOrigin = new System.Windows.Point(0.5, 0.9);
        _root.RenderTransform = st;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var a = new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private static BitmapImage LoadFrozen(string path)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.UriSource = new Uri(path);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    private static void Reflow()
    {
        var wa = SystemParameters.WorkArea;
        double y = wa.Bottom - EdgeMargin;
        for (int i = Stack.Count - 1; i >= 0; i--)
        {
            var w = Stack[i];
            if (w._closing) continue;
            w.Left = wa.Right - w.Width - EdgeMargin;
            w.Top = y - w.Height;
            y -= w.Height + Gap;
        }
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
        if (IsMouseOver) return;
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
                _filePath = moved;
            _root.BorderBrush = Theme.Brush("Accent");
            _pinBtn.Content = Icons.Make("pin", 15, System.Windows.Media.Brushes.White);
            Toast.Show("고정됨 — 자동으로 사라지지 않아요");
        }
        else
        {
            _root.BorderBrush = Theme.Stroke(Theme.BorderStrong);
            _pinBtn.Content = Icons.Make("pin", 15, Theme.Brush("Muted"));
            StartDismissIfEnabled();
        }
    }

    // ---- input ----

    private void OnDown(object sender, MouseButtonEventArgs e) { _dragStart = e.GetPosition(this); _maybeDrag = true; }
    private void OnRightDown(object sender, MouseButtonEventArgs e) { _flingStart = e.GetPosition(this); _maybeFling = true; }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_maybeFling && e.RightButton == MouseButtonState.Pressed)
        {
            var rp = e.GetPosition(this);
            if (Math.Abs(rp.X - _flingStart.X) > FlingThreshold) { _maybeFling = false; DismissSlide(); }
            return;
        }

        if (!_maybeDrag || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _maybeDrag = false;
        _dismiss.Stop();

        var data = new System.Windows.DataObject();
        data.SetFileDropList(new StringCollection { _filePath });
        DragDrop.DoDragDrop(this, data, System.Windows.DragDropEffects.Copy);

        StartDismissIfEnabled();   // reusable; keep on screen
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_maybeDrag) return;
        _maybeDrag = false;
        // Ctrl+click = copy the path (power users / terminals); plain click = copy IMAGE.
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            ImageClipboard.CopyText(_filePath);
            Toast.Show("경로 복사됨");
        }
        else CopyImage();
    }

    // ---- actions ----

    private void CopyImage()
    {
        if (ImageClipboard.CopyImageFile(_filePath)) Toast.Show("이미지 복사됨 ✓");
        else Toast.Show("복사 실패 — 클립보드를 사용하는 다른 앱이 있을 수 있어요");
    }

    private void SaveAs()
    {
        _dismiss.Stop();
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG 이미지 (*.png)|*.png|모든 파일 (*.*)|*.*",
                FileName = Path.GetFileName(_filePath),
                Title = "캡처 저장"
            };
            if (dlg.ShowDialog() == true)
            {
                File.Copy(_filePath, dlg.FileName, overwrite: true);
                Toast.Show("저장됨 ✓");
            }
        }
        catch (Exception ex) { CrashLog.Write("save-as", ex); Toast.Show("저장 실패"); }
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

    private void EditCurrent()
    {
        _dismiss.Stop();
        var ed = new EditorWindow(_filePath);
        ed.Closed += (_, _) =>
        {
            // Edited result pops as its own fresh thumbnail bottom-right (draggable),
            // leaving the original in place — same flow as a new capture.
            if (!string.IsNullOrEmpty(ed.ResultPath))
                new ThumbnailWindow(ed.ResultPath!, edited: true).Show();
            StartDismissIfEnabled();
        };
        ed.Show();
        ed.Activate();
    }

    private async void OcrCurrent()
    {
        _dismiss.Stop();
        Toast.Show("텍스트 인식 중…");
        try
        {
            using var bmp = new System.Drawing.Bitmap(_filePath);
            string? text = await Ocr.RecognizeAsync(bmp);
            if (text == null)
                Toast.Show("OCR 사용 불가 (언어팩 설치 필요)", 2600);
            else if (text.Trim().Length == 0)
                Toast.Show("인식된 텍스트 없음");
            else
            {
                ImageClipboard.CopyText(text);
                Toast.Show("텍스트 복사됨 ✓");
            }
        }
        catch (Exception ex) { CrashLog.Write("ocr-thumb", ex); Toast.Show("OCR 실패"); }
        finally { StartDismissIfEnabled(); }
    }

    private async void ShareCurrent()
    {
        if (!Uploader.Available) { Toast.Show("업로드 비활성화됨 — 설정에서 Imgur 켜기", 2600); return; }
        _dismiss.Stop();
        Toast.Show("업로드 중…");
        try
        {
            string? url = await Uploader.UploadImgurAsync(_filePath);
            if (string.IsNullOrEmpty(url)) Toast.Show("업로드 실패");
            else { ImageClipboard.CopyText(url); Toast.Show("링크 복사됨 ✓", 2200); }
        }
        catch (Exception ex) { CrashLog.Write("share-thumb", ex); Toast.Show("업로드 실패"); }
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
        Reflow();
        var anim = new DoubleAnimation(Left, SystemParameters.WorkArea.Right + Width, TimeSpan.FromMilliseconds(190))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        anim.Completed += (_, _) => Close();
        BeginAnimation(LeftProperty, anim);
    }

    protected override void OnClosed(EventArgs e)
    {
        Stack.Remove(this);
        Reflow();
        base.OnClosed(e);
    }
}
