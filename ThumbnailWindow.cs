using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Wsnap;

/// <summary>
/// The macOS-style floating thumbnail. Appears bottom-right after capture and
/// STACKS upward when several captures are live (newest at the bottom).
///  - LEFT-DRAG it out   -> delivers a real FileDrop. Stays put (drag it many places).
///  - CLICK it           -> copies the file path to the clipboard.
///  - Hover buttons      -> 편집(editor) / 텍스트(OCR) / ✕(close).
///  - RIGHT-DRAG sideways -> flings it off the right edge to clear it.
///  - Ignore it          -> auto-dismisses after Settings.AutoDismissSeconds.
/// </summary>
public sealed class ThumbnailWindow : Window
{
    private const double EdgeMargin = 24;
    private const double Gap = 12;
    private const double FlingThreshold = 40;

    private static readonly List<ThumbnailWindow> Stack = new();

    private string _filePath;
    private readonly Image _img;
    private readonly StackPanel _buttons;
    private readonly DispatcherTimer _dismiss;
    private System.Windows.Point _dragStart, _flingStart;
    private bool _maybeDrag, _maybeFling, _closing;

    public ThumbnailWindow(string filePath)
    {
        _filePath = filePath;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        Width = 200;
        Height = 150;

        _img = new Image { Source = LoadFrozen(filePath), Stretch = Stretch.Uniform, IsHitTestVisible = false };

        _buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 6, 0),
            Visibility = Visibility.Hidden
        };
        _buttons.Children.Add(MiniButton("편집", EditCurrent));
        _buttons.Children.Add(MiniButton("텍스트", OcrCurrent));
        _buttons.Children.Add(MiniButton("✕", () => DismissSlide()));

        var grid = new Grid();
        grid.Children.Add(_img);
        grid.Children.Add(_buttons);

        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x1E, 0x1E)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6)),
            BorderThickness = new Thickness(2),
            Padding = new Thickness(6),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.5 },
            Child = grid
        };
        Content = border;

        MouseLeftButtonDown += OnDown;
        MouseRightButtonDown += OnRightDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseEnter += (_, _) => { _dismiss?.Stop(); _buttons.Visibility = Visibility.Visible; };
        MouseLeave += (_, _) => { _buttons.Visibility = Visibility.Hidden; if (!_closing) _dismiss?.Start(); };

        _dismiss = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(1, Settings.Current.AutoDismissSeconds)) };
        _dismiss.Tick += (_, _) => { _dismiss.Stop(); DismissSlide(); };

        Stack.Add(this);
        int max = Math.Max(1, Settings.Current.MaxVisible);
        while (Stack.Count > max) Stack[0].DismissNow();

        Reflow();
        _dismiss.Start();
    }

    private static System.Windows.Controls.Button MiniButton(string text, Action onClick)
    {
        var b = new System.Windows.Controls.Button
        {
            Content = text,
            FontSize = 11,
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(2, 0, 0, 0),
            Cursor = Cursors.Hand,
            Foreground = System.Windows.Media.Brushes.White,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(0)
        };
        b.Click += (_, e) => { e.Handled = true; onClick(); };
        return b;
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

        if (!IsMouseOver) _dismiss.Start();   // reusable; keep on screen
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_maybeDrag) return;
        _maybeDrag = false;
        System.Windows.Clipboard.SetText(_filePath);
        Toast.Show("경로 복사됨");
    }

    // ---- actions ----

    private void EditCurrent()
    {
        _dismiss.Stop();
        var ed = new EditorWindow(_filePath);
        ed.Closed += (_, _) =>
        {
            // Edited result pops as its own fresh thumbnail bottom-right (draggable),
            // leaving the original in place — same flow as a new capture.
            if (!string.IsNullOrEmpty(ed.ResultPath))
                new ThumbnailWindow(ed.ResultPath!).Show();
            if (!_closing && !IsMouseOver) _dismiss.Start();
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
                System.Windows.Clipboard.SetText(text);
                Toast.Show("텍스트 복사됨 ✓");
            }
        }
        catch (Exception ex) { CrashLog.Write("ocr-thumb", ex); Toast.Show("OCR 실패"); }
        finally { if (!_closing && !IsMouseOver) _dismiss.Start(); }
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
        var anim = new DoubleAnimation(Left, SystemParameters.WorkArea.Right + Width, TimeSpan.FromMilliseconds(180))
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
