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
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Wsnap;

/// <summary>Tiny transient notification near the tray (bottom-right), auto-fades.
/// Avalonia port (Phase 3) of the WPF Toast — replaces the Phase-1 ToastStub, so the linked
/// Ocr.cs progress messages now show for real. Animations use property Transitions (the
/// Avalonia idiom) instead of WPF BeginAnimation.</summary>
public sealed class Toast : Window
{
    public static void Show(string message, int ms = 1800)
    {
        // Must run on the UI thread.
        Dispatcher.UIThread.Post(() => new Toast(message, ms).ShowSelf());
    }

    private readonly int _ms;
    private readonly TranslateTransform _rise = new(0, 10);

    private Toast(string message, int ms)
    {
        _ms = ms;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = AppTheme.Font;

        // Design-system panel: theme tokens + hairline border + a small accent dot so the
        // toast reads as wsnap (not a generic OS balloon) at a glance.
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Ellipse
        {
            Width = 7, Height = 7,
            Fill = new SolidColorBrush(AppTheme.Accent),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 9, 0)
        });
        row.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = new SolidColorBrush(AppTheme.Text),
            FontSize = 13,
            MaxWidth = 360,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(0xF2, AppTheme.Panel.R, AppTheme.Panel.G, AppTheme.Panel.B)),
            BorderBrush = new SolidColorBrush(AppTheme.BorderStrong),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 16, 10),
            BoxShadow = BoxShadows.Parse("0 2 16 0 #80000000"),
            Child = row,
            RenderTransform = _rise
        };
        Content = border;

        // Fade + a short upward slide, driven by Transitions: setting the target value after
        // the transition is attached animates toward it.
        Opacity = 0;
        Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(160) }
        };
        _rise.Transitions = new Transitions
        {
            new DoubleTransition { Property = TranslateTransform.YProperty, Duration = TimeSpan.FromMilliseconds(180), Easing = new CubicEaseOut() }
        };
    }

    private void ShowSelf()
    {
        Show();
        PlaceSelf();
        // Re-assert bottom-right placement if a monitor DPI boundary re-scales us (the WPF
        // OnDpiChanged equivalent).
        ScalingChanged += (_, _) => Dispatcher.UIThread.Post(PlaceSelf, DispatcherPriority.Background);

        Opacity = 1;          // animates via the transition
        _rise.Y = 0;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Opacity = 0;      // fade out via the same transition
            DispatcherTimer.RunOnce(Close, TimeSpan.FromMilliseconds(220));
        };
        timer.Start();
    }

    /// <summary>Place bottom-right on the cursor's monitor in physical pixels (same
    /// MonitorPlacement machinery as WPF — logical work-area math misplaces on mixed-DPI).</summary>
    private void PlaceSelf()
    {
        var (wa, s) = MonitorPlacement.CursorWorkArea();
        double wPx = Bounds.Width * s;
        double hPx = Bounds.Height * s;
        MonitorPlacement.MovePx(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero,
            wa.Right - wPx - 24 * s, wa.Bottom - hPx - 24 * s);
    }
}
