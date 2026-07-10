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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace Wsnap;

/// <summary>Avalonia implementation of the recorders' floating "recording" pill (top-center,
/// click/Esc = stop). Registered as <see cref="RecorderUi.BadgeFactory"/> by App. Marshals
/// SetText/Close through the UI dispatcher — recorders call from background threads.</summary>
public sealed class RecorderBadge : IRecorderBadge
{
    public event Action? Clicked;

    private readonly Window _win;
    private readonly TextBlock _text;

    public RecorderBadge(string initialText, uint argb)
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
            Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(
                (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)),
            Child = _text, Cursor = new Cursor(StandardCursorType.Hand)
        };
        _win = new Window
        {
            SystemDecorations = SystemDecorations.None, CanResize = false,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            Background = Brushes.Transparent,
            Topmost = true, ShowInTaskbar = false, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            FontFamily = AppTheme.Font,
            Content = border
        };
        _win.PointerPressed += (_, _) => Clicked?.Invoke();
        _win.KeyDown += (_, e) => { if (e.Key == Key.Escape) Clicked?.Invoke(); };
        _win.Opened += (_, _) =>
        {
            // Top-center of the cursor's monitor work area, in physical px (mixed-DPI safe).
            var (wa, s) = MonitorPlacement.CursorWorkArea();
            double wPx = _win.Bounds.Width * s;
            MonitorPlacement.MovePx(_win.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero,
                wa.Left + (wa.Width - wPx) / 2, wa.Top + 12 * s);
        };
        _win.Show();
        _win.Activate();
    }

    public void SetText(string text) =>
        Dispatcher.UIThread.Post(() => _text.Text = text);

    public void Close() =>
        Dispatcher.UIThread.Post(() => { try { _win.Close(); } catch { } });
}
