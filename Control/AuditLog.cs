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
using Wsnap;

namespace Wsnap.Control;

/// <summary>
/// Append-only audit trail for external-initiated commands, at %APPDATA%\wsnap\audit.log.
/// <para>
/// Records <b>metadata only</b> — never the captured pixels, OCR text, window titles, or
/// colour values. Each line answers "who asked wsnap to do what, when, and was it visible /
/// did it return content", so a user can review external activity without the log itself
/// becoming a second copy of their screen contents.
/// </para>
/// <para>Writes are serialized and never throw: any failure is swallowed to <see cref="CrashLog"/>.</para>
/// </summary>
public static class AuditLog
{
    private static readonly object Gate = new();

    private static string LogPath => Path.Combine(Settings.ConfigDir, "audit.log");

    /// <summary>
    /// Append one pipe-separated line for <paramref name="cmd"/>:
    /// <c>time | source | clientId | kind | WxH | visible/silent | content/no-content | result</c>.
    /// Callers gate on <see cref="Settings.ExternalControlAudit"/>; this method assumes it should write.
    /// </summary>
    public static void Write(WsnapCommand cmd, CommandResult result, GateDecision decision)
    {
        try
        {
            // Size is logged only when the result actually carries pixel dimensions (captures and
            // saved recordings). OCR / colour / status results carry none, so they render as "-" —
            // exactly the "capture/OCR ? WxH : -" rule, without leaking the target rectangle.
            string size = result.Width > 0 && result.Height > 0
                ? $"{result.Width}x{result.Height}"
                : "-";

            string visibility = decision.RequireVisibleSignal ? "visible" : "silent";

            string content = decision.AllowReturnContent && CommandTraits.ReturnsContent(cmd.Kind)
                ? "content" : "no-content";

            string outcome =
                !decision.Allowed ? "deny:" + (decision.DenyCode ?? "unknown") :
                result.Ok         ? "ok" :
                                    "err:" + (result.ErrorCode ?? "unknown");

            string line = string.Join(" | ",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                cmd.Source.ToString(),
                CleanId(cmd.ClientId),
                cmd.Kind.ToString(),
                size,
                visibility,
                content,
                outcome);

            lock (Gate)
            {
                Directory.CreateDirectory(Settings.ConfigDir);
                // Default AppendAllText encoding is UTF-8 without BOM — matches CrashLog.
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("audit", ex);
        }
    }

    /// <summary>
    /// Make a caller-supplied client id safe for a single pipe-delimited line: strip the
    /// delimiter and newlines, trim, cap length, and fall back to "-" when absent. This is the
    /// only free-text field — every other column is an enum, a number, or a fixed token.
    /// </summary>
    private static string CleanId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "-";
        string s = id.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (s.Length == 0) return "-";
        return s.Length > 120 ? s.Substring(0, 120) : s;
    }
}
