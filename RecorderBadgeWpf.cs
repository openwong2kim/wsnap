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

namespace Wsnap;

/// <summary>WPF implementation of the recorders' floating "recording" pill (top-center,
/// click/Esc = stop) — the window the recorders built themselves before the Phase 4
/// framework split. Registered as <see cref="RecorderUi.BadgeFactory"/> in App.OnStartup.</summary>
public sealed class RecorderBadgeWpf : IRecorderBadge
{
    public event Action? Clicked;

    private readonly Window _win;
    private readonly TextBlock _text;

    public RecorderBadgeWpf(string initialText, uint argb)
    {
        _text = new TextBlock
        {
            Text = initialText,
            Foreground = Brushes.White,
            FontSize = 13, Margin = new Thickness(12, 8, 12, 8)
        };
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(
                (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)),
            Child = _text, Cursor = Cursors.Hand
        };
        _win = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent,
            Topmost = true, ShowInTaskbar = false, SizeToContent = SizeToContent.WidthAndHeight,
            Content = border
        };
        _win.MouseLeftButtonDown += (_, _) => Clicked?.Invoke();
        _win.KeyDown += (_, e) => { if (e.Key == Key.Escape) Clicked?.Invoke(); };
        _win.Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            _win.Left = wa.Left + (wa.Width - _win.ActualWidth) / 2;
            _win.Top = wa.Top + 12;   // top-center, away from most capture regions
        };
        _win.Show();
        _win.Activate();
    }

    public void SetText(string text) =>
        _win.Dispatcher.BeginInvoke(new Action(() => _text.Text = text));

    public void Close()
    {
        try
        {
            if (_win.Dispatcher.CheckAccess()) _win.Close();
            else _win.Dispatcher.Invoke(new Action(_win.Close));
        }
        catch { /* already closing */ }
    }
}
