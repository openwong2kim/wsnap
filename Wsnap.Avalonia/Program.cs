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

namespace Wsnap;

/// <summary>
/// Entry point for the Avalonia app (plans/humming-meandering-aurora.md). Same contract as the
/// WPF exe's Main: <c>mcp</c>/CLI verbs run headless without the tray app; anything else boots
/// the Avalonia lifetime — since Phase 6 a bare launch is the RESIDENT tray app, guarded by
/// the same SingleInstance mutex the WPF exe uses (re-running the exe = "take a shot", and the
/// WPF and Avalonia residents can never run side by side fighting over hotkeys).
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // ---- client sub-commands run WITHOUT the Avalonia app (Phase 4) ----
        // Same contract as the WPF exe's Main: `mcp` = stdio MCP server; a known CLI verb runs
        // headless (or delegates to a running tray instance over the control pipe). This branch
        // MUST come before any Avalonia/Settings side effects.
        if (args.Length > 0)
        {
            if (string.Equals(args[0], "mcp", StringComparison.OrdinalIgnoreCase))
            {
                Settings.Load();
                Wsnap.Control.McpStdioServer.RunAsync(BuildClientRouter()).GetAwaiter().GetResult();
                return;
            }
            if (Wsnap.Control.CliRouter.IsKnownVerb(args[0]))
            {
                Settings.Load();
                Wsnap.Control.ConsoleBridge.Bind();
                int code = Wsnap.Control.CliRouter.Run(args, BuildClientRouter()).GetAwaiter().GetResult();
                Wsnap.Control.ConsoleBridge.Unbind();
                Environment.Exit(code);
                return;
            }
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) CrashLog.Write("domain-unhandled", ex);
        };

        // One resident instance only (shared mutex with the WPF exe, on purpose). A second
        // launch tells the running one to capture, then exits. Dev-only harness modes
        // (--showcase / the --*-demo flags other than --resident-demo) skip the mutex so they
        // can run beside a live resident.
        bool resident = !Array.Exists(args, a =>
            a == "--showcase" || (a.Contains("-demo") && !a.StartsWith("--resident-demo", StringComparison.Ordinal)));
        if (resident)
        {
            bool primary = SingleInstance.TryAcquire(() => App.Instance?.OnSecondLaunch());
            if (!primary) return;
        }

        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { if (resident) SingleInstance.Release(); }
    }

    /// <summary>The router a CLI/MCP client process uses: delegate to the running tray instance
    /// over the control pipe when present; otherwise a headless in-proc router (interactive /
    /// recording commands then return resident_required). Mirrors the WPF App.BuildClientRouter.</summary>
    private static Wsnap.Control.ICommandRouter BuildClientRouter()
    {
        if (Wsnap.Control.PipeClientRouter.IsResidentRunning())
            return new Wsnap.Control.PipeClientRouter();
        return new Wsnap.Control.CommandRouter(new Wsnap.Control.ControlGate(), host: null);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
