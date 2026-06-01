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
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Wsnap;

/// <summary>Tiny transient notification near the tray (bottom-right), auto-fades.</summary>
public sealed class Toast : Window
{
    public static void Show(string message, int ms = 1800)
    {
        // Must run on the UI thread.
        var app = System.Windows.Application.Current;
        if (app == null) return;
        app.Dispatcher.BeginInvoke(() => new Toast(message, ms).ShowSelf());
    }

    private readonly int _ms;

    private Toast(string message, int ms)
    {
        _ms = ms;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xF0, 0x1E, 0x1E, 0x1E)),
            Padding = new Thickness(14, 10, 14, 10),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.5 },
            Child = new TextBlock
            {
                Text = message,
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 13,
                MaxWidth = 360,
                TextWrapping = TextWrapping.Wrap
            }
        };
        Content = border;
    }

    private void ShowSelf()
    {
        Show();
        // Place in physical pixels on the cursor's monitor — SystemParameters.WorkArea is
        // primary-monitor-only and misplaces the toast (taskbar clash) on multi-monitor /
        // mixed-DPI desktops. SizeToContent leaves DIU sizes, so scale them to device px.
        var (wa, s) = MonitorPlacement.CursorWorkArea();
        double wPx = ActualWidth * s;
        double hPx = ActualHeight * s;
        MonitorPlacement.MovePx(new WindowInteropHelper(this).Handle,
            wa.Right - wPx - 24 * s, wa.Bottom - hPx - 24 * s);

        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
            fade.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fade);
        };
        timer.Start();
    }
}
