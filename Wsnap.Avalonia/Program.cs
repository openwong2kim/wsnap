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
/// Scaffold-only entry point for the Avalonia UI migration (plans/humming-meandering-aurora.md).
/// Proves the 24 UI-framework-agnostic files (Ocr.cs, Settings.cs, CaptureStore.cs, Control\*,
/// etc. — linked, not copied, from ..\) compile and link correctly under Avalonia's SDK/package
/// set. Real windows (CaptureOverlay, EditorWindow, ...) land in later migration phases; this is
/// deliberately just enough to build and boot.
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

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
