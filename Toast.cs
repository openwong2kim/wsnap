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

        // Design-system panel: theme tokens + hairline border + a small accent dot so the
        // toast reads as wsnap (not a generic OS balloon) at a glance.
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 7, Height = 7,
            Fill = new SolidColorBrush(Theme.Accent),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 9, 0)
        });
        row.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = new SolidColorBrush(Theme.Text),
            FontFamily = Theme.Font,
            FontSize = 13,
            MaxWidth = 360,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xF2, Theme.Panel.R, Theme.Panel.G, Theme.Panel.B)),
            BorderBrush = Theme.Stroke(Theme.BorderStrong),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 16, 10),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.5 },
            Child = row
        };
        Content = border;
    }

    private void ShowSelf()
    {
        Show();
        PlaceSelf();

        // Fade + a short upward slide (transform-only, so the HWND placement is untouched).
        Opacity = 0;
        var rise = new TranslateTransform(0, 10);
        if (Content is Border b) { b.RenderTransform = rise; }
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        rise.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

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

    /// <summary>
    /// Place the toast bottom-right on the cursor's monitor in physical pixels — SystemParameters
    /// .WorkArea is primary-monitor-only and misplaces the toast (taskbar clash) on multi-monitor /
    /// mixed-DPI desktops. SizeToContent leaves DIU sizes, so scale them to device px.
    /// </summary>
    private void PlaceSelf()
    {
        var (wa, s) = MonitorPlacement.CursorWorkArea();
        double wPx = ActualWidth * s;
        double hPx = ActualHeight * s;
        MonitorPlacement.MovePx(new WindowInteropHelper(this).Handle,
            wa.Right - wPx - 24 * s, wa.Bottom - hPx - 24 * s);
    }

    /// <summary>
    /// Re-assert the bottom-right placement after a DPI change. MovePx positions the HWND in
    /// device pixels; crossing a monitor DPI boundary raises WM_DPICHANGED, whose WPF DEFAULT
    /// handler re-applies the OS-suggested rect and overrides our placement. ActualWidth/Height
    /// stay in DIP across the change, so recomputing with the fresh scale lands it correctly.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(PlaceSelf));
    }
}
