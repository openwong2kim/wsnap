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
///
/// All edits live in working-copy fields (not read straight off the controls) so the
/// whole window can be torn down and rebuilt without losing in-progress input. That
/// rebuild is what powers the <b>live language preview</b>: picking a language in the
/// dropdown flips <see cref="L.Lang"/> and re-renders every label instantly — no save,
/// no reopen. Cancelling (or closing without saving) restores the original language.
/// </summary>
public sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;   // single settings window

    private readonly Action _onApplied;

    // ---- working copies (survive a language-preview rebuild) ----
    private string _lang, _ocrLang, _saveFolder, _filenameTemplate, _imgurId;
    private int _vk; private bool _shift, _ctrl, _alt, _win;
    private bool _keepHistory, _autoCopy, _postToolbar, _swallowWinS, _clipboardWatch, _telemetry, _upload, _autostart;
    private int _autoDismiss, _maxVisible;

    private readonly string _origLang;   // language to restore if the user cancels the preview
    private bool _applied;                // true once Save committed — suppresses the restore
    private bool _building;               // true while BuildContent() runs — suppresses change handlers
    private bool _capturing;              // hotkey-capture in progress

    // ---- live controls (recreated on every BuildContent) ----
    private TextBox? _folderBox, _template, _imgur;
    private CheckBox? _historyChk, _autocopyChk, _toolbarChk, _swallowChk, _autostartChk, _clipboardChk, _telemetryChk, _uploadChk;
    private Slider? _fade, _max;
    private TextBlock? _fadeVal, _hotkeyLabel;
    private ComboBox? _langCombo;   // current language combo (rebuild swaps it); also a smoke-test seam
    private readonly List<ToggleButton> _ocrLangButtons = new();
    private readonly HashSet<string> _ocrInstalling = new();   // OCR codes downloading now — survives a rebuild

    /// <summary>Display row for the language dropdown; <c>ToString</c> drives both the list and the closed box.</summary>
    private sealed class LangItem
    {
        public string Code = "";
        public string Name = "";
        public override string ToString() => Name;
    }

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

        // snapshot every setting into a working copy so a rebuild never loses input
        _origLang = L.Lang;
        _lang = L.Normalize(s.Language);
        _ocrLang = Ocr.Resolve(s.OcrLanguage).Code;
        _vk = s.HotkeyVk; _shift = s.HotkeyShift; _ctrl = s.HotkeyCtrl; _alt = s.HotkeyAlt; _win = s.HotkeyWin;
        _saveFolder = s.SaveFolder;
        _filenameTemplate = s.FilenameTemplate;
        _imgurId = s.ImgurClientId;
        _keepHistory = s.KeepHistory;
        _autoCopy = s.AutoCopyOnCapture;
        _postToolbar = s.PostCaptureToolbar;
        _swallowWinS = s.SwallowWinShiftS;
        _clipboardWatch = s.ClipboardWatch;
        _telemetry = s.TelemetryOptIn;
        _upload = s.UploadEnabled;
        _autostart = AutoStart.IsEnabled();
        _autoDismiss = s.AutoDismissSeconds;
        _maxVisible = s.MaxVisible;

        Width = 500; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Theme.Apply(this);

        BuildContent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Build (or rebuild) the whole window in the current UI language. Re-run on a
    /// language-preview switch so every label re-localizes; all values come from working copies.</summary>
    private void BuildContent()
    {
        _building = true;
        _ocrLangButtons.Clear();
        _capturing = false;

        Title = L.T("set.title");

        var root = new StackPanel { Margin = new Thickness(18) };

        // header
        root.Children.Add(new TextBlock
        {
            Text = L.T("set.header"), FontSize = 20, FontWeight = FontWeights.Bold,
            Foreground = Theme.Brush("Text"), Margin = new Thickness(2, 0, 0, 14)
        });

        // --- language (live preview) ---
        root.Children.Add(Card(L.T("set.cardLanguage"),
            Row(L.T("set.language"), LanguagePicker(), null),
            Hint(L.T("set.languageHint"))));

        // --- OCR language (separate from the UI language above) ---
        root.Children.Add(Card(L.T("set.cardOcr"),
            new TextBlock { Text = L.T("set.ocrLanguage"), Foreground = Theme.Brush("Muted"), Margin = new Thickness(0, 0, 0, 8) },
            OcrLanguageSegment(),
            Hint(L.T("set.ocrLanguageHint"))));

        // --- storage ---
        _folderBox = Field(_saveFolder, readOnly: true);
        _template = Field(_filenameTemplate, readOnly: false);
        _historyChk = Check(L.T("set.keepHistory"), _keepHistory);
        root.Children.Add(Card(L.T("set.cardStorage"),
            Row(L.T("set.saveFolder"), _folderBox, Btn(L.T("set.browse"), PickFolder, primary: false)),
            Row(L.T("set.filenameTemplate"), _template, null),
            Hint(L.T("set.templateHint")),
            _historyChk));

        // --- capture ---
        _autocopyChk = Check(L.T("set.autoCopy"), _autoCopy);
        _toolbarChk = Check(L.T("set.toolbar"), _postToolbar);
        root.Children.Add(Card(L.T("set.cardCapture"),
            _autocopyChk,
            _toolbarChk,
            Hint(L.T("set.toolbarHint"))));

        // --- thumbnails ---
        _fade = MakeSlider(0, 30, _autoDismiss);
        _fadeVal = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Foreground = Theme.Brush("Muted"), MinWidth = 36 };
        _fade.ValueChanged += (_, _) => _fadeVal.Text = (int)_fade.Value == 0 ? L.T("set.off") : ((int)_fade.Value).ToString();
        _fadeVal.Text = (int)_fade.Value == 0 ? L.T("set.off") : ((int)_fade.Value).ToString();
        _max = MakeSlider(1, 10, _maxVisible);
        root.Children.Add(Card(L.T("set.cardThumbs"),
            SliderRow(L.T("set.autoDismiss"), _fade, _fadeVal),
            SliderRow(L.T("set.maxVisible"), _max, null)));

        // --- hotkey ---
        _hotkeyLabel = new TextBlock { Text = HotkeyText(), FontWeight = FontWeights.Bold, Foreground = Theme.Brush("Text"), VerticalAlignment = VerticalAlignment.Center };
        _swallowChk = Check(L.T("set.swallowWinShiftS"), _swallowWinS);
        root.Children.Add(Card(L.T("set.cardHotkey"),
            Row(L.T("set.captureHotkey"), _hotkeyLabel, Btn(L.T("set.change"), BeginCapture, primary: false)),
            _swallowChk));

        // --- resident ---
        _autostartChk = Check(L.T("set.startWithWindows"), _autostart);
        _clipboardChk = Check(L.T("set.clipboardWatch"), _clipboardWatch);
        _telemetryChk = Check(L.T("set.telemetry"), _telemetry);
        root.Children.Add(Card(L.T("set.cardResident"), _autostartChk, _clipboardChk, _telemetryChk));

        // --- upload ---
        _uploadChk = Check(L.T("set.uploadEnabled"), _upload);
        _imgur = Field(_imgurId, readOnly: false);
        root.Children.Add(Card(L.T("set.cardUpload"), _uploadChk, Row(L.T("set.imgurId"), _imgur, null)));

        // --- actions ---
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        actions.Children.Add(Btn(L.T("set.cancel"), Close, primary: false));
        actions.Children.Add(Btn(L.T("set.save"), ApplyAndClose, primary: true));
        root.Children.Add(actions);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height * 0.92 };

        _building = false;
    }

    /// <summary>Pull the live control values back into the working copies (called before a
    /// rebuild or save). Hotkey / language / OCR-language are already kept live by their handlers.</summary>
    private void SyncFromUi()
    {
        if (_folderBox != null) _saveFolder = _folderBox.Text;
        if (_template != null) _filenameTemplate = _template.Text;
        if (_imgur != null) _imgurId = _imgur.Text;
        if (_historyChk != null) _keepHistory = _historyChk.IsChecked == true;
        if (_autocopyChk != null) _autoCopy = _autocopyChk.IsChecked == true;
        if (_toolbarChk != null) _postToolbar = _toolbarChk.IsChecked == true;
        if (_swallowChk != null) _swallowWinS = _swallowChk.IsChecked == true;
        if (_autostartChk != null) _autostart = _autostartChk.IsChecked == true;
        if (_clipboardChk != null) _clipboardWatch = _clipboardChk.IsChecked == true;
        if (_telemetryChk != null) _telemetry = _telemetryChk.IsChecked == true;
        if (_uploadChk != null) _upload = _uploadChk.IsChecked == true;
        if (_fade != null) _autoDismiss = (int)_fade.Value;
        if (_max != null) _maxVisible = (int)_max.Value;
    }

    /// <summary>Restore the original UI language if the user closes without saving the preview.</summary>
    protected override void OnClosed(EventArgs e)
    {
        if (!_applied) L.Lang = _origLang;
        base.OnClosed(e);
    }

    // ---- language picker (dropdown + live preview) ----

    /// <summary>UI-language dropdown over every <see cref="L.Available"/> entry. Picking a language
    /// preserves all other in-progress edits, flips <see cref="L.Lang"/>, and rebuilds the window
    /// so the change previews instantly.</summary>
    private FrameworkElement LanguagePicker()
    {
        var combo = new ComboBox { Style = Theme.Style("Combo"), Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (code, name) in L.Available)
        {
            var item = new LangItem { Code = code, Name = name };
            combo.Items.Add(item);
            if (code == _lang) combo.SelectedItem = item;
        }
        if (combo.SelectedItem == null && combo.Items.Count > 0) combo.SelectedIndex = 0;

        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is LangItem li && li.Code != _lang)
            {
                SyncFromUi();        // keep every other edit
                _lang = li.Code;
                L.Lang = _lang;      // preview now
                BuildContent();      // re-render every label in the new language
            }
        };
        _langCombo = combo;
        return combo;
    }

    // ---- hotkey capture ----

    private string HotkeyText() =>
        new Settings { HotkeyVk = _vk, HotkeyShift = _shift, HotkeyCtrl = _ctrl, HotkeyAlt = _alt, HotkeyWin = _win }.HotkeyText;

    private void BeginCapture()
    {
        _capturing = true;
        if (_hotkeyLabel != null)
        {
            _hotkeyLabel.Text = L.T("set.pressKeys");
            _hotkeyLabel.Foreground = Theme.Brush("Warn");
        }
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
        if (_hotkeyLabel == null) return;
        _hotkeyLabel.Text = HotkeyText();
        _hotkeyLabel.Foreground = Theme.Brush("Text");
    }

    private void PickFolder()
    {
        if (_folderBox == null) return;
        using var dlg = new WinForms.FolderBrowserDialog { SelectedPath = _folderBox.Text };
        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
        {
            _saveFolder = dlg.SelectedPath;
            _folderBox.Text = _saveFolder;
        }
    }

    // ---- apply ----

    private void ApplyAndClose()
    {
        SyncFromUi();
        var s = Settings.Current;
        s.SaveFolder = _saveFolder;
        s.FilenameTemplate = string.IsNullOrWhiteSpace(_filenameTemplate) ? Settings.DefaultFilenameTemplate : _filenameTemplate.Trim();
        s.KeepHistory = _keepHistory;
        s.AutoDismissSeconds = _autoDismiss;
        s.MaxVisible = _maxVisible;
        s.AutoCopyOnCapture = _autoCopy;
        s.PostCaptureToolbar = _postToolbar;
        s.HotkeyVk = _vk; s.HotkeyShift = _shift; s.HotkeyCtrl = _ctrl; s.HotkeyAlt = _alt; s.HotkeyWin = _win;
        s.SwallowWinShiftS = _swallowWinS;
        s.ClipboardWatch = _clipboardWatch;
        s.TelemetryOptIn = _telemetry;
        s.UploadEnabled = _upload;
        s.ImgurClientId = _imgurId.Trim();

        AutoStart.Set(_autostart);
        s.StartWithWindows = _autostart;

        s.Language = _lang;
        L.Lang = _lang;          // applies immediately to any window opened after this
        s.OcrLanguage = _ocrLang; // next OCR rebuilds the engine for this language

        _applied = true;         // keep the chosen language; don't restore on close
        s.Save();
        _onApplied();
        Close();
    }

    /// <summary>OCR-language picker as an inline wrap of toggle chips (one per pack, non-embedded
    /// ones annotated with download size). Same proven click model as the editor tool toggles.</summary>
    private FrameworkElement OcrLanguageSegment()
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        foreach (var l in Ocr.Languages)
        {
            var lang = l;                 // capture per-iteration
            bool installing = _ocrInstalling.Contains(lang.Code);
            var tb = new ToggleButton
            {
                Style = Theme.Style("ToolToggle"),
                Content = installing ? $"{lang.Native}  …" : ChipText(lang),
                Tag = lang.Code,
                IsChecked = lang.Code == _ocrLang,
                IsEnabled = !installing,   // a rebuild mid-download lands on a disabled chip, not a clickable one
                Margin = new Thickness(0, 0, 6, 6)
            };
            tb.Click += async (_, _) =>
            {
                _ocrLang = lang.Code;
                foreach (var b in _ocrLangButtons) b.IsChecked = (string)b.Tag == _ocrLang;

                // Pre-install on pick so the first OCR is instant (the UX the user asked for).
                if (Ocr.IsInstalled(lang) || _ocrInstalling.Contains(lang.Code)) return;

                // Route progress/enable to whatever chip is live now — a language-preview rebuild
                // swaps the button out from under us, so capturing `tb` would update an orphan.
                _ocrInstalling.Add(lang.Code);
                SetChipEnabled(lang.Code, false);
                var progress = new Progress<double>(p => SetChipContent(lang.Code, $"{lang.Native}  … {p * 100:0}%"));
                try { await Ocr.EnsureInstalledAsync(lang, progress); }
                catch { /* EnsureInstalledAsync already toasts + logs failures */ }
                finally
                {
                    _ocrInstalling.Remove(lang.Code);
                    SetChipEnabled(lang.Code, true);
                    SetChipContent(lang.Code, ChipText(lang));
                }
            };
            _ocrLangButtons.Add(tb);
            wrap.Children.Add(tb);
        }
        return wrap;
    }

    /// <summary>Find the live OCR chip for a language code (the list is rebuilt on a language preview).</summary>
    private ToggleButton? OcrChip(string code)
    {
        foreach (var b in _ocrLangButtons) if ((string)b.Tag == code) return b;
        return null;
    }

    private void SetChipEnabled(string code, bool enabled) { var b = OcrChip(code); if (b != null) b.IsEnabled = enabled; }
    private void SetChipContent(string code, string text) { var b = OcrChip(code); if (b != null) b.Content = text; }

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
