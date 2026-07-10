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
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Wsnap;

/// <summary>Scaffold Avalonia Application, now carrying the Phase 1 foundations: FluentTheme +
/// Theme.axaml (wsnap design system) load via App.axaml, and <c>--showcase</c> opens the
/// DevShowcase window (theme + HotkeyHook verification). Real startup — tray icon (WinForms,
/// deliberately retained), capture windows — lands in Phase 2+; a bare launch still proves
/// startup works and exits clean.</summary>
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Settings.Load();
            string? overlayDemo = null, thumbDemo = null;
            if (desktop.Args != null)
                foreach (var a in desktop.Args)
                {
                    if (a.StartsWith("--overlay-demo=")) overlayDemo = a.Substring("--overlay-demo=".Length);
                    if (a.StartsWith("--thumb-demo=")) thumbDemo = a.Substring("--thumb-demo=".Length);
                }

            if (desktop.Args != null && System.Array.IndexOf(desktop.Args, "--showcase") >= 0)
            {
                desktop.MainWindow = new DevShowcase();
            }
            else if (overlayDemo != null)
            {
                // Phase 2 verification mode (dev-only): open the ported CaptureOverlay and dump
                // its outcome to a probe file for the external harness. All Settings mutations
                // are in-memory (never saved) so the user's real config is untouched.
                string demoDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wsnap_p2demo");
                System.IO.Directory.CreateDirectory(demoDir);
                Settings.Current.SaveFolder = demoDir;
                Settings.Current.HistoryKeepRecent = 0;   // don't prune the demo dir
                Settings.Current.PostCaptureToolbar = overlayDemo == "toolbar";
                var ov = new CaptureOverlay(overlayDemo == "region" ? CaptureMode.Region : CaptureMode.Capture);
                ov.Closed += (_, _) =>
                {
                    var r = ov.RegionPx;
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wsnap_p2_overlay_probe.txt"),
                        $"{ov.Action}|{(r == null ? "" : $"{r.Value.X},{r.Value.Y},{r.Value.Width},{r.Value.Height}")}|{ov.ResultPath}");
                };
                desktop.MainWindow = ov;
            }
            else if (thumbDemo != null)
            {
                // Phase 3 verification mode (dev-only): pop a real ThumbnailWindow for the given
                // image so the external harness can pixel-check placement and drive click/copy.
                Settings.Current.AutoDismissSeconds = 0;     // in-memory: stay until harness acts
                var tw = new ThumbnailWindow(thumbDemo);
                desktop.MainWindow = tw;
            }
            else
            {
                // Scaffold: nothing to show yet, exit clean after proving startup works. Deferred
                // via Post so the dispatcher's main loop is actually pumping before Shutdown()
                // runs — calling Shutdown() synchronously here (before Start() enters its loop)
                // throws.
                Dispatcher.UIThread.Post(() => desktop.Shutdown());
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
