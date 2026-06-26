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
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Wsnap;

/// <summary>
/// Resolves an ffmpeg executable for video recording (H.264/MP4). wsnap does NOT embed ffmpeg
/// (it is ~80–100 MB); instead we resolve it in this order, mirroring the OCR language-pack
/// pattern so the single-file exe stays lean:
///   1. <see cref="Settings.FFmpegPath"/> — an explicit user override in Settings.
///   2. App-local on-demand download at <c>%LOCALAPPDATA%\wsnap\ffmpeg\ffmpeg.exe</c>.
///   3. Whatever <c>ffmpeg.exe</c> is already on the system PATH.
/// This deliberately avoids the Media Foundation SinkWriter path, which requires <c>mfplat.dll</c>
/// and is absent on Windows N editions / stripped images (the original prototype returned
/// <c>E_NOINTERFACE</c> for exactly this reason). ffmpeg is environment-agnostic and far more
/// capable (better H.264 quality, trivial audio later, works everywhere).
/// </summary>
public static class FFmpegProvider
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>gyan.dev release-essentials build. The zip contains
    /// <c>ffmpeg-*-essentials_build/bin/ffmpeg.exe</c>; we extract only ffmpeg.exe.</summary>
    private const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    /// <summary>Per-user app-local ffmpeg directory (next to OCR models under %LOCALAPPDATA%\wsnap).</summary>
    public static string AppDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wsnap", "ffmpeg");

    /// <summary>The ffmpeg.exe we manage, if downloaded.</summary>
    public static string LocalExePath => Path.Combine(AppDir, "ffmpeg.exe");

    /// <summary>
    /// Resolve a usable ffmpeg.exe path, or <c>null</c> when none is available. Does NOT trigger
    /// a download — call <see cref="EnsureDownloadedAsync"/> for that.
    /// </summary>
    public static string? TryResolve()
    {
        // 1. explicit user override
        var overridePath = Settings.Current.FFmpegPath;
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        // 2. app-local on-demand download
        if (File.Exists(LocalExePath) && IsUsable(LocalExePath))
            return LocalExePath;

        // 3. system PATH
        string? onPath = ProbePath();
        if (onPath != null && IsUsable(onPath)) return onPath;

        return null;
    }

    /// <summary>True when ffmpeg.exe at <paramref name="path"/> actually responds to -version.</summary>
    public static bool IsUsable(string path)
    {
        try
        {
            var psi = new ProcessStartInfo(path, "-version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p.WaitForExit(3000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Look up ffmpeg.exe on the PATH via <c>where</c> (Windows equivalent of which).</summary>
    private static string? ProbePath()
    {
        try
        {
            var psi = new ProcessStartInfo("where.exe", "ffmpeg.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            string? line = p.StandardOutput.ReadLine();
            p.WaitForExit(2000);
            line = line?.Trim();
            return !string.IsNullOrWhiteSpace(line) && File.Exists(line) ? line : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Download the essentials build and extract ffmpeg.exe to <see cref="LocalExePath"/>.
    /// Reports percentage (0–100). Returns the exe path on success, or <c>null</c> on failure.
    /// No hash pinning: gyan.dev rotates builds, so we trust the TLS-fetched release artifact
    /// and verify only that the extracted exe is usable (<see cref="IsUsable"/>).
    /// </summary>
    public static async Task<string?> EnsureDownloadedAsync(
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            string zipPath = Path.Combine(AppDir, "ffmpeg.zip");

            // Stream-download with progress to the zip file.
            using (var resp = await Http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zipPath);
                var buf = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    read += n;
                    if (total.HasValue && total.Value > 0)
                        progress?.Report((int)(read * 100 / total.Value));
                }
            }

            // Extract only ffmpeg.exe from the nested bin/ folder.
            string? extracted = null;
            using (var za = ZipFile.OpenRead(zipPath))
            {
                foreach (var e in za.Entries)
                {
                    if (!e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    extracted = LocalExePath;
                    e.ExtractToFile(LocalExePath, overwrite: true);
                    break;
                }
            }

            try { File.Delete(zipPath); } catch { }

            return (extracted != null && IsUsable(LocalExePath)) ? LocalExePath : null;
        }
        catch (Exception ex)
        {
            CrashLog.Write("ffmpeg-download", ex);
            return null;
        }
    }
}
