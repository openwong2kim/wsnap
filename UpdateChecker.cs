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
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace Wsnap;

/// <summary>
/// Lightweight update checker. wsnap is currently unsigned, so this does NOT silently swap the
/// binary (auto-replacing an unsigned exe in-place is a security smell and fragile under the
/// single-file publish). Instead it compares the running version against the latest GitHub
/// Release and, when newer, surfaces a tray entry + toast linking to the verified release page
/// (and the signed installer asset once signing is live). Full silent self-update activates
/// after code signing — see ROADMAP.md.
///
/// Fetches only the public releases/latest endpoint; no telemetry, no auth. Honors
/// <see cref="Settings.UpdateCheck"/> (off disables the periodic background check; the manual
/// tray "check for updates" always runs).
/// </summary>
public sealed record UpdateInfo(Version Version, string ReleaseUrl, string? InstallerUrl);

public static class UpdateChecker
{
    private const string Repo = "openwong2kim/wsnap";
    private const string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>The running version, read from <c>AssemblyInformationalVersion</c>
    /// (populated by csproj &lt;Version&gt;). Falls back to 0.0.0 when unreadable.</summary>
    public static Version CurrentVersion
    {
        get
        {
            try
            {
                var asm = Assembly.GetEntryAssembly();
                if (asm != null)
                {
                    var attr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                    if (attr != null)
                    {
                        string raw = attr.InformationalVersion.Split('+')[0].Trim();
                        if (Version.TryParse(raw, out var v)) return v;
                    }
                    // Fall back to the assembly name version (csproj <Version> sets it too).
                    if (Version.TryParse(asm.GetName().Version?.ToString(), out var vn)) return vn;
                }
            }
            catch { }
            return new Version(0, 0, 0);
        }
    }

    /// <summary>Fetch the latest release; null on any failure (network, parse, non-200).</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            req.Headers.UserAgent.ParseAdd("wsnap/" + CurrentVersion);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
            string tag = (tagEl.GetString() ?? "").Trim();
            if (tag.StartsWith("v", StringComparison.OrdinalIgnoreCase)) tag = tag[1..];
            if (!Version.TryParse(tag, out var latest)) return null;

            string url = root.TryGetProperty("html_url", out var u) ? (u.GetString() ?? "") : "";

            // Prefer the installer; fall back to the portable zip.
            string? installer = FindAsset(root, "wsnap-setup-", ".exe")
                             ?? FindAsset(root, "", "-win-x64.zip");

            return new UpdateInfo(latest, url, string.IsNullOrEmpty(installer) ? null : installer);
        }
        catch { return null; }
    }

    private static string? FindAsset(JsonElement root, string prefix, string suffix)
    {
        if (!root.TryGetProperty("assets", out var assets)) return null;
        foreach (var a in assets.EnumerateArray())
        {
            string name = a.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
            string dl = a.TryGetProperty("browser_download_url", out var d) ? (d.GetString() ?? "") : "";
            bool match = (prefix.Length == 0 || name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                      && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
            if (match && !string.IsNullOrEmpty(dl)) return dl;
        }
        return null;
    }

    /// <summary>True when <paramref name="latest"/> is strictly newer than <paramref name="current"/>.</summary>
    public static bool IsNewer(Version latest, Version current) => latest > current;
}
