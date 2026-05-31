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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace Wsnap;

/// <summary>
/// Settings: storage folder, hotkey rebinding, fade time, stack size, auto-copy,
/// start-with-Windows, Win+Shift+S toggle, history, clipboard watch, upload, telemetry.
/// Themed to match the rest of wsnap (dark cards, styled inputs); edits apply to
/// <see cref="Settings.Current"/> and persist on save, then the callback re-applies
/// runtime toggles (autostart, clipboard hook).
/// </summary>
public sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;   // single settings window

    private readonly Action _onApplied;

    // working copy of the hotkey
    private int _vk; private bool _shift, _ctrl, _alt, _win;
    private bool _capturing;

    private readonly TextBox _folderBox;
    private readonly TextBox _template;
    private readonly TextBlock _hotkeyLabel;
    private readonly Slider _fade, _max;
    private readonly TextBlock _fadeVal;
    private readonly CheckBox _autostart, _swallow, _history, _clipboard, _telemetry, _upload, _autocopy, _toolbar;
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
        Width = 500; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Theme.Apply(this);

        var root = new StackPanel { Margin = new Thickness(18) };

        // header
        root.Children.Add(new TextBlock
        {
            Text = "설정", FontSize = 20, FontWeight = FontWeights.Bold,
            Foreground = Theme.Brush("Text"), Margin = new Thickness(2, 0, 0, 14)
        });

        // --- storage ---
        _folderBox = Field(s.SaveFolder, readOnly: true);
        _template = Field(s.FilenameTemplate, readOnly: false);
        _history = Check("캡처를 날짜별 폴더에 영구 보관 (히스토리)", s.KeepHistory);
        root.Children.Add(Card("저장",
            Row("저장 폴더", _folderBox, Btn("찾아보기", PickFolder, primary: false)),
            Row("파일 이름 형식", _template, null),
            Hint("토큰: {app} {title} {date} {time} {seq} {w} {h} · 또는 {yyyy-MM-dd_HHmmss} 같은 날짜 형식"),
            _history));

        // --- capture ---
        _autocopy = Check("캡처하면 자동으로 클립보드에 복사 (Ctrl+V 바로 붙여넣기)", s.AutoCopyOnCapture);
        _toolbar = Check("영역 선택 후 액션 툴바 표시 (끄면: 드래그하면 즉시 우하단 썸네일)", s.PostCaptureToolbar);
        root.Children.Add(Card("캡처",
            _autocopy,
            _toolbar,
            Hint("기본값: 끔 — 드래그하면 캡처가 바로 우하단 썸네일로 떠오릅니다. 켜면 선택 영역에 복사·저장·편집·OCR·GIF·고정 툴바가 표시됩니다.")));

        // --- thumbnails ---
        _fade = MakeSlider(0, 30, s.AutoDismissSeconds);
        _fadeVal = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Foreground = Theme.Brush("Muted"), MinWidth = 36 };
        _fade.ValueChanged += (_, _) => _fadeVal.Text = (int)_fade.Value == 0 ? "끄기" : ((int)_fade.Value).ToString();
        _fadeVal.Text = (int)_fade.Value == 0 ? "끄기" : ((int)_fade.Value).ToString();
        _max = MakeSlider(1, 10, s.MaxVisible);
        root.Children.Add(Card("썸네일",
            SliderRow("자동 사라짐(초) · 0=끄기", _fade, _fadeVal),
            SliderRow("최대 동시 표시 개수", _max, null)));

        // --- hotkey ---
        _hotkeyLabel = new TextBlock { Text = s.HotkeyText, FontWeight = FontWeights.Bold, Foreground = Theme.Brush("Text"), VerticalAlignment = VerticalAlignment.Center };
        _swallow = Check("Win+Shift+S도 가로채기 (OS 스니핑툴 대체)", s.SwallowWinShiftS);
        root.Children.Add(Card("단축키",
            Row("캡처 단축키", _hotkeyLabel, Btn("변경", BeginCapture, primary: false)),
            _swallow));

        // --- resident ---
        _autostart = Check("Windows 시작 시 자동 실행", AutoStart.IsEnabled());
        _clipboard = Check("클립보드 이미지 자동 썸네일화", s.ClipboardWatch);
        _telemetry = Check("익명 사용 로그 남기기(로컬 전용, 옵트인)", s.TelemetryOptIn);
        root.Children.Add(Card("상주 동작", _autostart, _clipboard, _telemetry));

        // --- upload ---
        _upload = Check("Imgur 업로드 활성화", s.UploadEnabled);
        _imgur = Field(s.ImgurClientId, readOnly: false);
        root.Children.Add(Card("업로드 (선택)", _upload, Row("Imgur Client-ID", _imgur, null)));

        // --- actions ---
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        actions.Children.Add(Btn("취소", Close, primary: false));
        actions.Children.Add(Btn("저장", ApplyAndClose, primary: true));
        root.Children.Add(actions);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height * 0.92 };

        PreviewKeyDown += OnPreviewKeyDown;
    }

    // ---- hotkey capture ----

    private void BeginCapture()
    {
        _capturing = true;
        _hotkeyLabel.Text = "키 조합을 누르세요…";
        _hotkeyLabel.Foreground = Theme.Brush("Warn");
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

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
        var tmp = new Settings { HotkeyVk = _vk, HotkeyShift = _shift, HotkeyCtrl = _ctrl, HotkeyAlt = _alt, HotkeyWin = _win };
        _hotkeyLabel.Text = tmp.HotkeyText;
        _hotkeyLabel.Foreground = Theme.Brush("Text");
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
        s.FilenameTemplate = string.IsNullOrWhiteSpace(_template.Text) ? Settings.DefaultFilenameTemplate : _template.Text.Trim();
        s.KeepHistory = _history.IsChecked == true;
        s.AutoDismissSeconds = (int)_fade.Value;
        s.MaxVisible = (int)_max.Value;
        s.AutoCopyOnCapture = _autocopy.IsChecked == true;
        s.PostCaptureToolbar = _toolbar.IsChecked == true;
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

    // ---- themed UI helpers ----

    private static Border Card(string title, params UIElement[] rows)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12.5,
            Foreground = Theme.Brush("Accent"), Margin = new Thickness(0, 0, 0, 8)
        });
        foreach (var r in rows) panel.Children.Add(r);
        return new Border
        {
            Background = Theme.Brush("Panel"),
            BorderBrush = Theme.Stroke(Theme.Border), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(15, 13, 15, 14),
            Margin = new Thickness(0, 0, 0, 12),
            Child = panel
        };
    }

    private CheckBox Check(string t, bool v) =>
        new() { Style = Theme.Style("Toggle"), Content = t, IsChecked = v, Margin = new Thickness(0, 4, 0, 4) };

    private static TextBlock Hint(string t) =>
        new() { Text = t, Foreground = Theme.Brush("Muted"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };

    private TextBox Field(string text, bool readOnly) =>
        new() { Style = Theme.Style("Field"), Text = text, IsReadOnly = readOnly };

    private Button Btn(string t, Action onClick, bool primary)
    {
        var b = new Button
        {
            Style = Theme.Style(primary ? "PrimaryButton" : "GhostButton"),
            Content = t, Margin = new Thickness(6, 0, 0, 0), MinWidth = 76
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static Grid Row(string label, FrameworkElement field, FrameworkElement? trailing)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lbl = new TextBlock { Text = label, Foreground = Theme.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(lbl, 0); g.Children.Add(lbl);
        Grid.SetColumn(field, 1); g.Children.Add(field);
        if (trailing != null) { Grid.SetColumn(trailing, 2); g.Children.Add(trailing); }
        return g;
    }

    private static Slider MakeSlider(int min, int max, int val) => new()
    {
        Style = Theme.Style("Track"),
        Minimum = min, Maximum = max, Value = Math.Clamp(val, min, max),
        TickFrequency = 1, IsSnapToTickEnabled = true, Width = 240, VerticalAlignment = VerticalAlignment.Center
    };

    private static Grid SliderRow(string label, Slider sl, TextBlock? valueBlock)
    {
        var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(175) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

        var lbl = new TextBlock { Text = label, Foreground = Theme.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(lbl, 0); g.Children.Add(lbl);
        Grid.SetColumn(sl, 1); g.Children.Add(sl);

        var val = valueBlock ?? new TextBlock { Foreground = Theme.Brush("Muted"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        if (valueBlock == null)
        {
            val.Text = ((int)sl.Value).ToString();
            sl.ValueChanged += (_, _) => val.Text = ((int)sl.Value).ToString();
        }
        Grid.SetColumn(val, 2); g.Children.Add(val);
        return g;
    }
}
