using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace Wsnap;

/// <summary>
/// v0.2 settings form: storage folder, hotkey rebinding, fade time, stack size,
/// start-with-Windows, Win+Shift+S toggle, history, clipboard watch, upload, telemetry.
/// Edits are applied to <see cref="Settings.Current"/> and persisted on save; the
/// supplied callback lets the app re-apply runtime toggles (autostart, clipboard hook).
/// </summary>
public sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;   // single settings window

    private readonly Action _onApplied;

    // working copy of the hotkey
    private int _vk; private bool _shift, _ctrl, _alt, _win;
    private bool _capturing;

    private readonly TextBox _folderBox;
    private readonly TextBlock _hotkeyLabel;
    private readonly Slider _fade, _max;
    private readonly CheckBox _autostart, _swallow, _history, _clipboard, _telemetry, _upload;
    private readonly TextBox _imgur;

    public static void ShowSingleton(Action onApplied)
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new SettingsWindow(onApplied);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    private SettingsWindow(Action onApplied)
    {
        _onApplied = onApplied;
        var s = Settings.Current;
        _vk = s.HotkeyVk; _shift = s.HotkeyShift; _ctrl = s.HotkeyCtrl; _alt = s.HotkeyAlt; _win = s.HotkeyWin;

        Title = "wsnap 설정";
        Width = 460; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;

        var root = new StackPanel { Margin = new Thickness(16) };

        // --- storage ---
        root.Children.Add(Header("저장"));
        _folderBox = new TextBox { Text = s.SaveFolder, IsReadOnly = true, VerticalContentAlignment = VerticalAlignment.Center };
        var browse = Btn("찾아보기", PickFolder);
        root.Children.Add(Row("저장 폴더", _folderBox, browse));
        _history = Check("캡처를 날짜별 폴더에 영구 보관 (히스토리)", s.KeepHistory);
        root.Children.Add(_history);

        // --- thumbnails ---
        root.Children.Add(Header("썸네일"));
        _fade = MakeSlider(1, 30, s.AutoDismissSeconds);
        root.Children.Add(SliderRow("자동 사라짐(초)", _fade));
        _max = MakeSlider(1, 10, s.MaxVisible);
        root.Children.Add(SliderRow("최대 동시 표시 개수", _max));

        // --- hotkey ---
        root.Children.Add(Header("단축키"));
        _hotkeyLabel = new TextBlock { Text = s.HotkeyText, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        root.Children.Add(Row("캡처 단축키", _hotkeyLabel, Btn("변경", BeginCapture)));
        _swallow = Check("Win+Shift+S도 가로채기 (OS 스니핑툴 대체)", s.SwallowWinShiftS);
        root.Children.Add(_swallow);

        // --- resident ---
        root.Children.Add(Header("상주 동작"));
        _autostart = Check("Windows 시작 시 자동 실행", AutoStart.IsEnabled());
        root.Children.Add(_autostart);
        _clipboard = Check("클립보드 이미지 자동 썸네일화", s.ClipboardWatch);
        root.Children.Add(_clipboard);
        _telemetry = Check("익명 사용 로그 남기기(로컬 전용, 옵트인)", s.TelemetryOptIn);
        root.Children.Add(_telemetry);

        // --- upload ---
        root.Children.Add(Header("업로드 (선택)"));
        _upload = Check("Imgur 업로드 활성화", s.UploadEnabled);
        root.Children.Add(_upload);
        _imgur = new TextBox { Text = s.ImgurClientId, VerticalContentAlignment = VerticalAlignment.Center };
        root.Children.Add(Row("Imgur Client-ID", _imgur, null));

        // --- actions ---
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        actions.Children.Add(Btn("저장", ApplyAndClose));
        actions.Children.Add(Btn("취소", Close));
        root.Children.Add(actions);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height * 0.92 };

        PreviewKeyDown += OnPreviewKeyDown;
    }

    // ---- hotkey capture ----

    private void BeginCapture()
    {
        _capturing = true;
        _hotkeyLabel.Text = "키 조합을 누르세요…";
        _hotkeyLabel.Foreground = System.Windows.Media.Brushes.OrangeRed;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // ignore lone modifier presses; wait for a real key
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) { e.Handled = true; return; }

        if (key == Key.Escape) { _capturing = false; RefreshHotkeyLabel(); e.Handled = true; return; }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        var m = Keyboard.Modifiers;
        _vk = vk;
        _shift = (m & ModifierKeys.Shift) != 0;
        _ctrl = (m & ModifierKeys.Control) != 0;
        _alt = (m & ModifierKeys.Alt) != 0;
        _win = (m & ModifierKeys.Windows) != 0;
        _capturing = false;
        RefreshHotkeyLabel();
        e.Handled = true;
    }

    private void RefreshHotkeyLabel()
    {
        // build a preview using the same formatting as Settings.HotkeyText
        var tmp = new Settings { HotkeyVk = _vk, HotkeyShift = _shift, HotkeyCtrl = _ctrl, HotkeyAlt = _alt, HotkeyWin = _win };
        _hotkeyLabel.Text = tmp.HotkeyText;
        _hotkeyLabel.Foreground = System.Windows.Media.Brushes.Black;
    }

    private void PickFolder()
    {
        using var dlg = new WinForms.FolderBrowserDialog { SelectedPath = _folderBox.Text };
        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
            _folderBox.Text = dlg.SelectedPath;
    }

    // ---- apply ----

    private void ApplyAndClose()
    {
        var s = Settings.Current;
        s.SaveFolder = _folderBox.Text;
        s.KeepHistory = _history.IsChecked == true;
        s.AutoDismissSeconds = (int)_fade.Value;
        s.MaxVisible = (int)_max.Value;
        s.HotkeyVk = _vk; s.HotkeyShift = _shift; s.HotkeyCtrl = _ctrl; s.HotkeyAlt = _alt; s.HotkeyWin = _win;
        s.SwallowWinShiftS = _swallow.IsChecked == true;
        s.ClipboardWatch = _clipboard.IsChecked == true;
        s.TelemetryOptIn = _telemetry.IsChecked == true;
        s.UploadEnabled = _upload.IsChecked == true;
        s.ImgurClientId = _imgur.Text.Trim();

        AutoStart.Set(_autostart.IsChecked == true);
        s.StartWithWindows = _autostart.IsChecked == true;

        s.Save();
        _onApplied();
        Close();
    }

    // ---- tiny UI helpers ----

    private static TextBlock Header(string t) => new()
    {
        Text = t, FontWeight = FontWeights.Bold, FontSize = 13,
        Margin = new Thickness(0, 12, 0, 4), Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6))
    };

    private static CheckBox Check(string t, bool v) =>
        new() { Content = t, IsChecked = v, Margin = new Thickness(0, 3, 0, 3) };

    private System.Windows.Controls.Button Btn(string t, Action onClick)
    {
        var b = new System.Windows.Controls.Button { Content = t, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0), MinWidth = 70 };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static Grid Row(string label, FrameworkElement field, FrameworkElement? trailing)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(lbl, 0); g.Children.Add(lbl);
        Grid.SetColumn(field, 1); g.Children.Add(field);
        if (trailing != null) { Grid.SetColumn(trailing, 2); g.Children.Add(trailing); }
        return g;
    }

    private static Slider MakeSlider(int min, int max, int val) => new()
    {
        Minimum = min, Maximum = max, Value = Math.Clamp(val, min, max),
        TickFrequency = 1, IsSnapToTickEnabled = true, Width = 200
    };

    private static Grid SliderRow(string label, Slider sl)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

        var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(lbl, 0); g.Children.Add(lbl);
        Grid.SetColumn(sl, 1); g.Children.Add(sl);

        var val = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        val.Text = ((int)sl.Value).ToString();
        sl.ValueChanged += (_, _) => val.Text = ((int)sl.Value).ToString();
        Grid.SetColumn(val, 2); g.Children.Add(val);
        return g;
    }
}
