using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wsnap;

/// <summary>
/// Capture history gallery — browse, re-copy, re-edit, reveal, open, delete, and (the
/// crown-jewel) DRAG any past shot out as a real file. Reads the scratch folder + its
/// date subfolders + the pinned folder via <see cref="CaptureStore.EnumerateHistory"/>.
/// Singleton, themed, light (DecodePixelWidth thumbnails).
/// </summary>
public sealed class HistoryWindow : Window
{
    private sealed record HistoryItem(string Path, DateTime When, bool Pinned);

    private const double TileW = 200, TileGap = 14;

    private static HistoryWindow? _open;

    private readonly WrapPanel _grid;
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _count;
    private readonly Border _empty;
    private List<HistoryItem> _all = new();

    public static void ShowSingleton()
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new HistoryWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    private HistoryWindow()
    {
        Title = "wsnap — 캡처 히스토리";
        Width = 920; Height = 620; MinWidth = 560; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Theme.Apply(this);

        // header
        var title = new TextBlock { Text = "캡처 히스토리", FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Theme.Brush("Text"), VerticalAlignment = VerticalAlignment.Center };
        _count = new TextBlock { Foreground = Theme.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var right = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(_count);
        right.Children.Add(HeaderBtn("새로고침", Reload));
        right.Children.Add(HeaderBtn("폴더 열기", OpenFolder));
        var headerGrid = new Grid { Margin = new Thickness(18, 12, 18, 12) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 0); headerGrid.Children.Add(title);
        Grid.SetColumn(right, 1); headerGrid.Children.Add(right);
        var header = new Border
        {
            Background = Theme.Brush("Panel"),
            BorderBrush = Theme.Stroke(Theme.Border), BorderThickness = new Thickness(0, 0, 0, 1),
            Child = headerGrid
        };

        // gallery
        _grid = new WrapPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        _scroller = new ScrollViewer
        {
            Content = _grid,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(18), Focusable = false
        };

        _empty = BuildEmptyState();

        var center = new Grid();
        center.Children.Add(_scroller);
        center.Children.Add(_empty);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(center);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F5) Reload();
            else if (e.Key == Key.Escape) Close();
        };

        Reload();
    }

    private Button HeaderBtn(string text, Action onClick)
    {
        var b = new Button { Style = Theme.Style("GhostButton"), Content = text, Margin = new Thickness(6, 0, 0, 0), MinWidth = 84 };
        b.Click += (_, _) => onClick();
        return b;
    }

    private Border BuildEmptyState()
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new Border { Child = Icons.Make("folder", 44, Theme.Brush("Muted2")), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) });
        sp.Children.Add(new TextBlock { Text = "아직 저장된 캡처가 없어요", Foreground = Theme.Brush("Muted"), FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = "설정에서 '히스토리 보관'을 켜면 날짜별로 영구 저장됩니다", Foreground = Theme.Brush("Muted2"), FontSize = 12.5, Margin = new Thickness(0, 6, 0, 16), HorizontalAlignment = HorizontalAlignment.Center });
        var open = new Button { Style = Theme.Style("GhostButton"), Content = "설정 열기", HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 96 };
        open.Click += (_, _) => SettingsWindow.ShowSingleton(() => { });
        sp.Children.Add(open);
        return new Border { Child = sp, Visibility = Visibility.Collapsed };
    }

    private void OpenFolder()
    {
        try
        {
            string dir = Settings.Current.SaveFolder;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { CrashLog.Write("history-openfolder", ex); }
    }

    private void Reload()
    {
        _all = CaptureStore.EnumerateHistory()
            .Select(t => new HistoryItem(t.Path, t.When, t.Pinned))
            .ToList();

        _grid.Children.Clear();
        foreach (var it in _all) _grid.Children.Add(BuildTile(it));

        _count.Text = _all.Count == 0 ? "" : $"{_all.Count}장";
        _empty.Visibility = _all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _scroller.Visibility = _all.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Drag-out payload (extracted so it's testable without invoking modal DoDragDrop).</summary>
    public static System.Windows.DataObject BuildDropData(string path)
    {
        var d = new System.Windows.DataObject();
        d.SetFileDropList(new StringCollection { path });
        return d;
    }

    private Border BuildTile(HistoryItem it)
    {
        var img = new Image { Stretch = Stretch.Uniform, Height = 140, IsHitTestVisible = false };
        var src = LoadThumb(it.Path);
        UIElement picture;
        if (src != null) { img.Source = src; picture = img; }
        else picture = new Border { Height = 140, Child = Icons.Make("folder", 30, Theme.Brush("Muted2")) };

        var caption = new TextBlock
        {
            Text = (it.Pinned ? "📌 " : "") + Trunc(Path.GetFileName(it.Path), 22) + "   " + it.When.ToString("MM-dd HH:mm"),
            Foreground = Theme.Brush("Muted"), FontSize = 11, Margin = new Thickness(4, 4, 4, 2),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var bar = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) };
        bar.Children.Add(IconBtn("copy", "이미지 복사", () => CopyImage(it)));
        bar.Children.Add(IconBtn("edit", "편집", () => Edit(it)));
        bar.Children.Add(IconBtn("folder", "폴더에서 보기", () => Reveal(it)));
        bar.Children.Add(IconBtn("open", "열기", () => OpenFile(it)));
        bar.Children.Add(IconBtn("trash", "삭제", () => Delete(it), danger: true));
        var barWrap = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom, Height = 30, CornerRadius = new CornerRadius(0, 0, 6, 6),
            Background = Theme.Stroke(System.Windows.Media.Color.FromArgb(0xE6, 0x12, 0x13, 0x15)),
            Child = bar, Opacity = 0, IsHitTestVisible = false
        };

        var picGrid = new Grid();
        picGrid.Children.Add(picture);
        picGrid.Children.Add(barWrap);

        var stack = new StackPanel();
        stack.Children.Add(picGrid);
        stack.Children.Add(caption);

        var tile = new Border
        {
            Width = TileW, CornerRadius = new CornerRadius(10),
            Background = Theme.Stroke(System.Windows.Media.Color.FromRgb(0x12, 0x13, 0x15)),
            BorderBrush = it.Pinned ? Theme.Brush("Accent") : Theme.Stroke(Theme.Border),
            BorderThickness = new Thickness(1), Padding = new Thickness(4),
            Margin = new Thickness(0, 0, TileGap, TileGap), Cursor = Cursors.Hand,
            Child = stack
        };

        tile.MouseEnter += (_, _) => barWrap.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(120)) { });
        tile.MouseEnter += (_, _) => barWrap.IsHitTestVisible = true;
        tile.MouseLeave += (_, _) => { barWrap.IsHitTestVisible = false; barWrap.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(140)) { }); };

        // click = copy image, Ctrl+click = copy path, drag = FileDrop out
        bool maybeDrag = false; System.Windows.Point ds = default;
        tile.MouseLeftButtonDown += (_, e) => { ds = e.GetPosition(tile); maybeDrag = true; };
        tile.MouseMove += (_, e) =>
        {
            if (!maybeDrag || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(tile);
            if (Math.Abs(p.X - ds.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - ds.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            maybeDrag = false;
            if (File.Exists(it.Path)) DragDrop.DoDragDrop(tile, BuildDropData(it.Path), System.Windows.DragDropEffects.Copy);
        };
        tile.MouseLeftButtonUp += (_, _) =>
        {
            if (!maybeDrag) return;
            maybeDrag = false;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) { ImageClipboard.CopyText(it.Path); Toast.Show("경로 복사됨"); }
            else CopyImage(it);
        };

        // context menu mirrors the action bar
        var cm = new ContextMenu();
        cm.Items.Add(Menu("이미지 복사", () => CopyImage(it)));
        cm.Items.Add(Menu("편집", () => Edit(it)));
        cm.Items.Add(Menu("폴더에서 보기", () => Reveal(it)));
        cm.Items.Add(Menu("열기", () => OpenFile(it)));
        cm.Items.Add(new Separator());
        cm.Items.Add(Menu("삭제", () => Delete(it)));
        tile.ContextMenu = cm;

        return tile;
    }

    private static MenuItem Menu(string text, Action onClick)
    {
        var m = new MenuItem { Header = text };
        m.Click += (_, _) => onClick();
        return m;
    }

    private Button IconBtn(string icon, string tip, Action onClick, bool danger = false)
    {
        var b = new Button
        {
            Style = Theme.Style("SubtleButton"), Width = 26, Height = 26, Padding = new Thickness(0),
            Margin = new Thickness(1, 0, 1, 0), Content = Icons.Make(icon, 14, Theme.Brush("Muted")), ToolTip = tip
        };
        b.MouseEnter += (_, _) => b.Content = Icons.Make(icon, 14, danger ? Theme.Brush("Danger") : Theme.Brush("Text"));
        b.MouseLeave += (_, _) => b.Content = Icons.Make(icon, 14, Theme.Brush("Muted"));
        b.Click += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    private static ImageSource? LoadThumb(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 220;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

    // ---- tile actions ----

    private void CopyImage(HistoryItem it)
    {
        if (!File.Exists(it.Path)) { Toast.Show("파일을 찾을 수 없어요"); Reload(); return; }
        if (ImageClipboard.CopyImageFile(it.Path)) Toast.Show("이미지 복사됨 ✓"); else Toast.Show("복사 실패");
    }

    private void Edit(HistoryItem it)
    {
        if (!File.Exists(it.Path)) { Toast.Show("파일을 찾을 수 없어요"); Reload(); return; }
        var ed = new EditorWindow(it.Path);
        ed.Closed += (_, _) => { if (!string.IsNullOrEmpty(ed.ResultPath)) { new ThumbnailWindow(ed.ResultPath!, edited: true).Show(); Reload(); } };
        ed.Show(); ed.Activate();
    }

    private void Reveal(HistoryItem it)
    {
        try { if (File.Exists(it.Path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{it.Path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { CrashLog.Write("history-reveal", ex); }
    }

    private void OpenFile(HistoryItem it)
    {
        try { if (File.Exists(it.Path)) Process.Start(new ProcessStartInfo(it.Path) { UseShellExecute = true }); }
        catch (Exception ex) { CrashLog.Write("history-open", ex); }
    }

    private void Delete(HistoryItem it)
    {
        if (System.Windows.MessageBox.Show(this, "이 캡처를 삭제할까요?", "삭제", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        try
        {
            if (File.Exists(it.Path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    it.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch
        {
            try { File.Delete(it.Path); } catch (Exception ex) { CrashLog.Write("history-delete", ex); }
        }
        Reload();
    }
}
