using System;
using System.Windows;
using System.Windows.Controls;
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
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 24;
        Top = wa.Bottom - ActualHeight - 24;

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
