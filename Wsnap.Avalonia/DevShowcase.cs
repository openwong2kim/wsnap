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
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Wsnap;

/// <summary>
/// Phase 1 verification window (dev-only, launched with <c>--showcase</c>; never reachable from
/// normal startup). Two jobs, both automated by an external harness (see the migration handoff
/// doc — self-render screenshots are not trusted, so verification samples the real screen):
///   1. Theme: a deterministic Canvas of token swatches and class-styled controls at fixed
///      logical coordinates, in a borderless window at a fixed position, so an external GDI
///      screenshot can pixel-check that Theme.axaml actually resolved and applied.
///   2. HotkeyHook: installs the (now framework-agnostic) hook with an in-memory test binding
///      (Ctrl+Alt+F9 → "test.probe", never saved to disk) and writes a probe file when it
///      fires, proving Triggered marshals onto Avalonia's UI thread.
/// </summary>
public sealed class DevShowcase : Window
{
    public static readonly string ProbeFile =
        Path.Combine(Path.GetTempPath(), "wsnap_p1_hotkey_probe.txt");

    private readonly HotkeyHook _hook = new();
    private readonly TextBlock _hotkeyStatus;

    public DevShowcase()
    {
        Title = "wsnap dev showcase (Phase 1)";
        SystemDecorations = SystemDecorations.None;   // client origin == window origin for the harness
        CanResize = false;
        Topmost = true;
        Width = 560; Height = 400;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(60, 60);
        AppTheme.Apply(this);

        var canvas = new Canvas();

        // Row 0 (y=0): 40x40 token swatches, x = 40*i — the harness pixel-checks their centers.
        var tokens = new[] { "Accent", "AccentDeep", "Surface", "SurfaceHi", "Panel2", "Text", "Danger", "Success" };
        for (int i = 0; i < tokens.Length; i++)
        {
            var sw = new Border { Width = 40, Height = 40, Background = AppTheme.Brush(tokens[i]) };
            Canvas.SetLeft(sw, 40 * i); Canvas.SetTop(sw, 0);
            canvas.Children.Add(sw);
        }

        // Row 1 (y=80): class-styled buttons at fixed rects (pixel-checked: primary bg = Accent,
        // checked tool toggle bg = Accent, ghost stays transparent over window Bg).
        var primary = new Button { Content = "Primary", Width = 160, Height = 40 };
        primary.Classes.Add("primary");
        Canvas.SetLeft(primary, 0); Canvas.SetTop(primary, 80);
        canvas.Children.Add(primary);

        var tool = new ToggleButton { Content = "Tool", Width = 100, Height = 40, IsChecked = true };
        tool.Classes.Add("tool");
        Canvas.SetLeft(tool, 200); Canvas.SetTop(tool, 80);
        canvas.Children.Add(tool);

        var ghost = new Button { Content = "Ghost", Width = 120, Height = 40 };
        ghost.Classes.Add("ghost");
        Canvas.SetLeft(ghost, 340); Canvas.SetTop(ghost, 80);
        canvas.Children.Add(ghost);

        // Row 2+ — stock controls riding the Fluent token overrides (visual record, not pixel-checked).
        var form = new StackPanel { Orientation = Orientation.Vertical, Spacing = 10, Width = 520 };
        Canvas.SetLeft(form, 0); Canvas.SetTop(form, 150);
        var field = new TextBox { Text = "field text", Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
        field.Classes.Add("field");
        form.Children.Add(field);
        var check = new CheckBox { Content = "toggle option", IsChecked = true };
        check.Classes.Add("toggle");
        form.Children.Add(check);
        var combo = new ComboBox { Width = 240, HorizontalAlignment = HorizontalAlignment.Left, SelectedIndex = 0 };
        combo.Classes.Add("combo");
        combo.ItemsSource = new[] { "first item", "second item" };
        form.Children.Add(combo);
        form.Children.Add(new Slider { Width = 240, HorizontalAlignment = HorizontalAlignment.Left, Value = 60, Maximum = 100 });
        _hotkeyStatus = new TextBlock { Text = "hotkey: waiting (Ctrl+Alt+F9)", FontSize = 13 };
        _hotkeyStatus.Classes.Add("muted");
        form.Children.Add(_hotkeyStatus);
        canvas.Children.Add(form);

        Content = canvas;
        WireHotkeyProbe();
        Closed += (_, _) => _hook.Dispose();
    }

    private void WireHotkeyProbe()
    {
        try { File.Delete(ProbeFile); } catch { }

        // In-memory only — the showcase never calls Settings.Save(), so the user's real
        // bindings on disk are untouched.
        Settings.Current.Hotkeys = new List<HotkeyBinding>
        {
            new() { Vk = 0x78 /* F9 */, Ctrl = true, Alt = true, Command = "test.probe", Swallow = true },
        };

        _hook.Triggered += b =>
        {
            // Runs via the SynchronizationContext captured at Install() — assert that really is
            // Avalonia's UI thread, then touch UI to prove it (would throw off-thread).
            bool onUi = Avalonia.Threading.Dispatcher.UIThread.CheckAccess();
            _hotkeyStatus.Text = $"hotkey: fired {b.Command}";
            File.WriteAllText(ProbeFile, $"{b.Command}|ui={onUi}");
        };
        _hook.Install();
        if (_hook.InstallFailed) _hotkeyStatus.Text = "hotkey: hook install FAILED";
    }
}
