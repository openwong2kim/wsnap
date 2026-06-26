// wsnap — macOS-style screen capture for Windows.
// Copyright (C) 2026 openwong2kim and wsnap contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License version 3, as published by
// by the Free Software Foundation.
//
// This program is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
// or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License
// for more details.
//
// You should have received a copy of the GNU General Public License along
// with this program. If not, see <https://www.gnu.org/licenses/>.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Wsnap;

/// <summary>
/// Enumerates audio capture sources for video recording, via the same ffmpeg we already use for
/// encoding. Two families:
///   • <b>dshow</b> — microphone devices (and "Stereo Mix", when the user has enabled it). Broadly
///     available; this is the validated path.
///   • <b>wasapi</b> — system-audio loopback (what you hear). Only present in ffmpeg builds that
///     compile the wasapi input device (gyan.dev <i>essentials</i> does not). Probed at runtime;
///     when absent, system audio is simply unavailable and the option degrades silently.
/// </summary>
public static class AudioDevices
{
    /// <summary>List dshow audio-input device friendly names (microphones, stereo mix, …).</summary>
    public static List<string> ListDshowAudio()
    {
        var result = new List<string>();
        var ff = FFmpegProvider.TryResolve();
        if (ff == null) return result;
        try
        {
            string err = RunCapture(ff, "-list_devices", "true", "-f", "dshow", "-i", "dummy");
            foreach (var line in err.Split('\n'))
            {
                if (!line.Contains("audio", StringComparison.OrdinalIgnoreCase)) continue;
                // Lines look like:  [dshow @ 0x…] "마이크 배열(Realtek Audio)" (audio)
                int q1 = line.IndexOf('"');
                int q2 = q1 >= 0 ? line.IndexOf('"', q1 + 1) : -1;
                if (q1 >= 0 && q2 > q1)
                {
                    string name = line.Substring(q1 + 1, q2 - q1 - 1);
                    if (!result.Contains(name)) result.Add(name);
                }
            }
        }
        catch { }
        return result;
    }

    /// <summary>True when this ffmpeg build exposes the wasapi input device (system loopback).</summary>
    public static bool WasapiAvailable
    {
        get
        {
            var ff = FFmpegProvider.TryResolve();
            if (ff == null) return false;
            try { return RunCapture(ff, "-hide_banner", "-devices").Contains("wasapi"); }
            catch { return false; }
        }
    }

    /// <summary>Pick a usable microphone device name: the user's override, else the first dshow
    /// audio device, else null.</summary>
    public static string? ResolveMic()
    {
        var dev = Settings.Current.VideoMicDevice;
        if (!string.IsNullOrWhiteSpace(dev)) return dev;
        var list = ListDshowAudio();
        return list.Count > 0 ? list[0] : null;
    }

    /// <summary>Best-effort system-audio (loopback) input spec for ffmpeg, or null when unavailable.
    /// Prefers wasapi loopback; falls back to a "Stereo Mix"-style dshow device if one exists.</summary>
    public static (string fmt, string arg)? ResolveSystem()
    {
        if (WasapiAvailable)
        {
            // wasapi loopback endpoint: render devices listed by ffmpeg; the loopback variant is
            // named "<endpoint> (Loopback)". We can't reliably enumerate render endpoints across
            // builds, so use the default-render loopback which ffmpeg accepts verbatim.
            return ("wasapi", "audio=Default (Loopback)");
        }
        // Some machines expose system audio through a "Stereo Mix" / "Wave Out" dshow device.
        foreach (var name in ListDshowAudio())
        {
            if (name.Contains("Stereo Mix", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Wave Out", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("스테레오 믹스", StringComparison.OrdinalIgnoreCase))
                return ("dshow", $"audio={name}");
        }
        return null;
    }

    /// <summary>Run ffmpeg with the given args, returning merged stdout+stderr (it writes device
    /// lists / device tables to stderr). Times out so a hung probe can never block recording.</summary>
    private static string RunCapture(string ff, params string[] args)
    {
        var psi = new ProcessStartInfo(ff)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg start failed");
        // ffmpeg -list_devices / -devices exits non-zero (it's an "error" probe) — that's expected.
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(4000);
        return stdout + "\n" + stderr;
    }
}
