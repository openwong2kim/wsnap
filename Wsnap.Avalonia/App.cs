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

/// <summary>The Avalonia Application. Since Phase 6 a bare launch starts the RESIDENT tray app
/// (App.Resident.cs / App.Control.cs — hotkeys, tray icon, watchers, opt-in control pipe);
/// the <c>--*-demo</c>/<c>--showcase</c> flags remain as dev-only external-verification modes
/// from earlier phases.</summary>
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Settings.Load();
            // Recorders are framework-agnostic since Phase 4 — give them the Avalonia badge.
            RecorderUi.BadgeFactory = (text, argb) => new RecorderBadge(text, argb);
            string? overlayDemo = null, thumbDemo = null, gifDemo = null;
            if (desktop.Args != null)
                foreach (var a in desktop.Args)
                {
                    if (a.StartsWith("--overlay-demo=")) overlayDemo = a.Substring("--overlay-demo=".Length);
                    if (a.StartsWith("--thumb-demo=")) thumbDemo = a.Substring("--thumb-demo=".Length);
                    if (a.StartsWith("--gif-demo=")) gifDemo = a.Substring("--gif-demo=".Length);
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
            else if (desktop.Args != null && System.Array.IndexOf(desktop.Args, "--settings-demo") >= 0)
            {
                // Phase 3b verification: real SettingsWindow, no-op apply callback. The harness
                // never clicks Save, so the user's settings.json is untouched.
                desktop.MainWindow = new SettingsWindow(() => { });
            }
            else if (desktop.Args != null && System.Array.IndexOf(desktop.Args, "--history-demo") >= 0)
            {
                // Phase 3b verification: real HistoryWindow over an in-memory temp SaveFolder.
                string demoDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wsnap_p3b_hist");
                System.IO.Directory.CreateDirectory(demoDir);
                Settings.Current.SaveFolder = demoDir;
                Settings.Current.HistoryKeepRecent = 0;
                desktop.MainWindow = new HistoryWindow();
            }
            else if (gifDemo != null)
            {
                // Phase 4 verification: record x,y,w,h for <sec> seconds, write the saved GIF
                // path to a probe file, exit. Exercises the framework-agnostic GifRecorder with
                // the Avalonia badge end-to-end. In-memory settings only.
                var parts = gifDemo.Split(',');
                var rect = new System.Drawing.Rectangle(
                    int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
                int sec = int.Parse(parts[4]);
                string demoDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wsnap_p4demo");
                System.IO.Directory.CreateDirectory(demoDir);
                Settings.Current.SaveFolder = demoDir;
                Settings.Current.HistoryKeepRecent = 0;
                desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
                string probe = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wsnap_p4_gif_probe.txt");
                try { System.IO.File.Delete(probe); } catch { }
                var rec = new GifRecorder(rect, p => System.IO.File.WriteAllText(probe, p),
                                          maxSeconds: sec, showControl: true, fps: 10);
                rec.Finished += () => Dispatcher.UIThread.Post(() => desktop.Shutdown());
                rec.Start();
            }
            else if (desktop.Args != null && System.Array.Find(desktop.Args, a => a.StartsWith("--editor-demo=")) is string edArg)
            {
                // Phase 5 verification: real EditorWindow on the given image; the probe records
                // the saved result path (or empty on cancel). In-memory settings only.
                string demoDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wsnap_p5demo");
                System.IO.Directory.CreateDirectory(demoDir);
                Settings.Current.SaveFolder = demoDir;
                Settings.Current.HistoryKeepRecent = 0;
                var ed = new EditorWindow(edArg.Substring("--editor-demo=".Length));
                ed.Closed += (_, _) => System.IO.File.WriteAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wsnap_p5_editor_probe.txt"),
                    ed.ResultPath ?? "");
                desktop.MainWindow = ed;
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
                // Phase 6: a bare launch is the resident tray app. --resident-demo[=pipe] is the
                // sandboxed variant for the external harness: all Settings mutations are
                // IN-MEMORY ONLY (never saved), captures land in a temp dir, the hotkey is a
                // fixed test chord, and nothing touches the network.
                string? demo = null;
                if (desktop.Args != null)
                    foreach (var a in desktop.Args)
                        if (a.StartsWith("--resident-demo")) demo = a;
                if (demo != null) ApplyResidentDemoSandbox(demo);
                StartResident(desktop, demo != null);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Sandbox the resident run for the Phase 6 verification harness. In-memory only —
    /// Settings.Save is never called on this instance by the harness paths, so the user's real
    /// settings.json is untouched.</summary>
    private static void ApplyResidentDemoSandbox(string arg)
    {
        string demoDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wsnap_p6demo");
        System.IO.Directory.CreateDirectory(demoDir);
        var s = Settings.Current;
        s.SaveFolder = demoDir;
        s.HistoryKeepRecent = 0;          // don't prune the demo dir
        s.ClipboardWatch = false;
        s.WatchFolderOcr = false;
        s.ClipboardAutoOcr = false;
        s.TelemetryOptIn = false;
        s.UpdateCheck = false;
        s.AutoCopyOnCapture = false;      // keep the harness run off the user's clipboard
        // Fixed test chord Ctrl+Alt+F9 → interactive capture (same chord Phase 1 verified).
        s.HotkeyVk = 0x78; s.HotkeyCtrl = true; s.HotkeyAlt = true; s.HotkeyShift = false; s.HotkeyWin = false;
        s.Hotkeys = new System.Collections.Generic.List<HotkeyBinding>
        {
            new() { Vk = 0x78, Ctrl = true, Alt = true, Command = "capture.interactive", Swallow = true }
        };
        // --resident-demo=pipe: opt in to external control so the pipe path can be re-verified.
        s.ExternalControlEnabled = arg.EndsWith("=pipe", System.StringComparison.Ordinal);
    }
}
