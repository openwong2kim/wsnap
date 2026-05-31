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
using System.IO;

namespace Wsnap;

/// <summary>
/// Dead-simple local crash/event log at %APPDATA%\wsnap\wsnap.log.
/// Doubles as the opt-in telemetry sink (local-only unless the user wires a remote one).
/// </summary>
public static class CrashLog
{
    private static readonly object Gate = new();

    private static string LogPath => Path.Combine(Settings.ConfigDir, "wsnap.log");

    public static void Write(string tag, Exception ex) => Write($"{tag}: {ex}");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.ConfigDir);
                // Date.Now is fine in app runtime (this isn't a workflow script).
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
    }

    /// <summary>Opt-in telemetry event. No-ops unless the user enabled it. Local log only.</summary>
    public static void Telemetry(string @event)
    {
        if (!Settings.Current.TelemetryOptIn) return;
        Write($"[telemetry] {@event}");
    }
}
