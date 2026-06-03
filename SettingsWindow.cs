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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    // working copy of the UI language
    private string _lang;
    private readonly List<ToggleButton> _langButtons = new();

    // working copy of the OCR language
    private string _ocrLang;
    private readonly List<ToggleButton> _ocrLangButtons = new();

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
        _lang = L.Normalize(s.Language);
        _ocrLang = Ocr.Resolve(s.OcrLanguage).Code;

        Title = L.T("set.title");
        Width = 500; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Theme.Apply(this);

        var root = new StackPanel { Margin = new Thickness(18) };

        // header
        root.Children.Add(new TextBlock
        {
            Text = L.T("set.header"), FontSize = 20, FontWeight = FontWeights.Bold,
            Foreground = Theme.Brush("Text"), Margin = new Thickness(2, 0, 0, 14)
        });

        // --- language ---
        root.Children.Add(Card(L.T("set.cardLanguage"),
            Row(L.T("set.language"), LanguageSegment(), null),
            Hint(L.T("set.languageHint"))));

        // --- OCR language (separate from the UI language above) ---
        root.Children.Add(Card(L.T("set.cardOcr"),
            new TextBlock { Text = L.T("set.ocrLanguage"), Foreground = Theme.Brush("Muted"), Margin = new Thickness(0, 0, 0, 8) },
            OcrLanguageSegment(),
            Hint(L.T("set.ocrLanguageHint"))));

        // --- storage ---
        _folderBox = Field(s.SaveFolder, readOnly: true);
        _template = Field(s.FilenameTemplate, readOnly: false);
        _history = Check(L.T("set.keepHistory"), s.KeepHistory);
        root.Children.Add(Card(L.T("set.cardStorage"),
            Row(L.T("set.saveFolder"), _folderBox, Btn(L.T("set.browse"), PickFolder, primary: false)),
            Row(L.T("set.filenameTemplate"), _template, null),
            Hint(L.T("set.templateHint")),
            _history));

        // --- capture ---
        _autocopy = Check(L.T("set.autoCopy"), s.AutoCopyOnCapture);
        _toolbar = Check(L.T("set.toolbar"), s.PostCaptureToolbar);
        root.Children.Add(Card(L.T("set.cardCapture"),
            _autocopy,
            _toolbar,
            Hint(L.T("set.toolbarHint"))));

        // --- thumbnails ---
        _fade = MakeSlider(0, 30, s.AutoDismissSeconds);
        _fadeVal = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Foreground = Theme.Brush("Muted"), MinWidth = 36 };
        _fade.ValueChanged += (_, _) => _fadeVal.Text = (int)_fade.Value == 0 ? L.T("set.off") : ((int)_fade.Value).ToString();
        _fadeVal.Text = (int)_fade.Value == 0 ? L.T("set.off") : ((int)_fade.Value).ToString();
        _max = MakeSlider(1, 10, s.MaxVisible);
        root.Children.Add(Card(L.T("set.cardThumbs"),
            SliderRow(L.T("set.autoDismiss"), _fade, _fadeVal),
            SliderRow(L.T("set.maxVisible"), _max, null)));

        // --- hotkey ---
        _hotkeyLabel = new TextBlock { Text = s.HotkeyText, FontWeight = FontWeights.Bold, Foreground = Theme.Brush("Text"), VerticalAlignment = VerticalAlignment.Center };
        _swallow = Check(L.T("set.swallowWinShiftS"), s.SwallowWinShiftS);
        root.Children.Add(Card(L.T("set.cardHotkey"),
            Row(L.T("set.captureHotkey"), _hotkeyLabel, Btn(L.T("set.change"), BeginCapture, primary: false)),
            _swallow));

        // --- resident ---
        _autostart = Check(L.T("set.startWithWindows"), AutoStart.IsEnabled());
        _clipboard = Check(L.T("set.clipboardWatch"), s.ClipboardWatch);
        _telemetry = Check(L.T("set.telemetry"), s.TelemetryOptIn);
        root.Children.Add(Card(L.T("set.cardResident"), _autostart, _clipboard, _telemetry));

        // --- upload ---
        _upload = Check(L.T("set.uploadEnabled"), s.UploadEnabled);
        _imgur = Field(s.ImgurClientId, readOnly: false);
        root.Children.Add(Card(L.T("set.cardUpload"), _upload, Row(L.T("set.imgurId"), _imgur, null)));

        // --- actions ---
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        actions.Children.Add(Btn(L.T("set.cancel"), Close, primary: false));
        actions.Children.Add(Btn(L.T("set.save"), ApplyAndClose, primary: true));
        root.Children.Add(actions);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height * 0.92 };

        PreviewKeyDown += OnPreviewKeyDown;
    }

    // ---- hotkey capture ----

    private void BeginCapture()
    {
        _capturing = true;
        _hotkeyLabel.Text = L.T("set.pressKeys");
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

        s.Language = _lang;
        L.Lang = _lang;          // applies immediately to any window opened after this
        s.OcrLanguage = _ocrLang; // next OCR rebuilds the engine for this language

        s.Save();
        _onApplied();
        Close();
    }

    /// <summary>Segmented language picker, one toggle per <see cref="L.Available"/> entry.</summary>
    private FrameworkElement LanguageSegment()
    {
        var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (code, name) in L.Available)
        {
            var tb = new ToggleButton
            {
                Style = Theme.Style("ToolToggle"),
                Content = name,
                Tag = code,
                IsChecked = code == _lang,
                Margin = new Thickness(0, 0, 6, 0)
            };
            tb.Click += (_, _) =>
            {
                _lang = code;
                foreach (var b in _langButtons) b.IsChecked = (string)b.Tag == _lang;
            };
            _langButtons.Add(tb);
            panel.Children.Add(tb);
        }
        return panel;
    }

    /// <summary>OCR-language picker as an inline wrap of toggle chips (one per pack, non-embedded
    /// ones annotated with download size). Same proven click model as the UI-language segment —
    /// no dropdown/ContextMenu, which wasn't opening reliably here.</summary>
    private FrameworkElement OcrLanguageSegment()
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        foreach (var l in Ocr.Languages)
        {
            var lang = l;                 // capture per-iteration
            var tb = new ToggleButton
            {
                Style = Theme.Style("ToolToggle"),
                Content = ChipText(lang),
                Tag = lang.Code,
                IsChecked = lang.Code == _ocrLang,
                Margin = new Thickness(0, 0, 6, 6)
            };
            tb.Click += async (_, _) =>
            {
                _ocrLang = lang.Code;
                foreach (var b in _ocrLangButtons) b.IsChecked = (string)b.Tag == _ocrLang;

                // Pre-install on pick so the first OCR is instant (the UX the user asked for).
                if (Ocr.IsInstalled(lang)) return;

                tb.IsEnabled = false;
                var progress = new Progress<double>(p => tb.Content = $"{lang.Native}  … {p * 100:0}%");
                try { await Ocr.EnsureInstalledAsync(lang, progress); }
                catch { /* EnsureInstalledAsync already toasts + logs failures */ }
                finally { tb.IsEnabled = true; tb.Content = ChipText(lang); }
            };
            _ocrLangButtons.Add(tb);
            wrap.Children.Add(tb);
        }
        return wrap;
    }

    /// <summary>Chip label: "✓" when ready to use (embedded or downloaded), else a download-size hint.</summary>
    private static string ChipText(Ocr.OcrLanguage l)
        => Ocr.IsInstalled(l) ? $"{l.Native}  ✓" : $"{l.Native}  ↓ ~{l.SizeMb:0.#} MB";

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
