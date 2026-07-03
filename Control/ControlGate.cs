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
using System.Collections.Generic;
using Wsnap;

namespace Wsnap.Control;

/// <summary>
/// The single choke point where every command is vetted for consent, rate limits,
/// content-return policy, and visibility just before it can touch screen pixels.
/// <para>
/// Physical, user-initiated sources (Internal / Hotkey / Tray) pass through untouched so
/// the existing UX is never altered — no gate, no signal, no audit. Only external sources
/// (Cli / Mcp / Pipe) are policed against the user's <see cref="Settings"/> opt-ins.
/// </para>
/// <para>
/// Hold ONE shared instance: the per-source sliding-window rate-limit state lives on this
/// object, so a fresh instance per call would reset every limiter. <see cref="Evaluate"/>
/// and <see cref="Audit"/> are both thread-safe.
/// </para>
/// </summary>
public sealed class ControlGate : IControlGate
{
    /// <summary>Length of the rate-limit sliding window (per source, per bucket).</summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    // Sliding-window request log keyed by "{source}#{gen|rec}". Guarded by _rateLock;
    // a plain lock keeps the read-prune-count-append sequence atomic without ceremony.
    private readonly object _rateLock = new();
    private readonly Dictionary<string, Queue<DateTime>> _windows = new();

    /// <summary>Raised when an external, visible screen access succeeds. App subscribes to badge the
    /// tray (a lasting-ish signal beyond the one-shot toast). Never raised for physical/internal use.</summary>
    public event Action<WsnapCommand>? ScreenAccessSignalled;

    /// <inheritdoc/>
    public GateDecision Evaluate(WsnapCommand cmd)
    {
        // Physical, user-initiated sources are fully trusted: run exactly as before.
        if (!IsExternal(cmd.Source))
            return GateDecision.Allow(requireSignal: false, allowContent: true);

        // 1) Master switch — the external surface only exists when the user turned it on.
        if (!Settings.Current.ExternalControlEnabled)
            return GateDecision.Deny("disabled", "external control is off (enable it in wsnap settings)");

        // 2) Per-source rate limit; continuous recording is capped far more tightly.
        bool recording = CommandTraits.IsRecording(cmd.Kind);
        if (!TryConsume(cmd.Source, recording))
            return GateDecision.Deny("rate_limit", "too many requests");

        // 3) Content-return policy — the real exfiltration channel (pixels / OCR text / colour).
        //    Commands that hand content back are gagged unless the user opted in; this is not a
        //    denial — the command still runs, the caller in front just masks the payload.
        bool allowContent = !CommandTraits.ReturnsContent(cmd.Kind)
                            || Settings.Current.ExternalControlAllowReturnContent;

        // 4) Visibility — any external read of the live screen must announce itself. MCP is
        //    always visible; only CLI/pipe may go silent, and only if the user opted in.
        bool requireSignal = false;
        if (TouchesScreen(cmd.Kind))
            requireSignal = cmd.Source == CommandSource.Mcp
                            || !Settings.Current.ExternalControlAllowSilent;

        return GateDecision.Allow(requireSignal, allowContent);
    }

    /// <inheritdoc/>
    public void Audit(WsnapCommand cmd, CommandResult result, GateDecision decision)
    {
        // Physical use stays invisible and unlogged; only external commands are recorded.
        if (!IsExternal(cmd.Source)) return;

        if (Settings.Current.ExternalControlAudit)
            AuditLog.Write(cmd, result, decision);

        // Visible signal only for a successful screen access the gate flagged. The tray badge
        // is App's job; here we just raise a toast. Toast.Show marshals to the UI thread itself
        // (and no-ops when there is no WPF app), but stay exception-safe regardless.
        if (decision.RequireVisibleSignal && result.Ok)
        {
            try { Toast.Show(SignalMessage(cmd)); }
            catch (Exception ex) { CrashLog.Write("gate-signal", ex); }
            // Let App badge the tray too (the toast is transient; the badge lingers a few seconds).
            try { ScreenAccessSignalled?.Invoke(cmd); }
            catch (Exception ex) { CrashLog.Write("gate-badge", ex); }
        }
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>True for the three untrusted, non-physical entry points.</summary>
    private static bool IsExternal(CommandSource s) =>
        s is CommandSource.Cli or CommandSource.Mcp or CommandSource.Pipe;

    /// <summary>
    /// Commands that read live screen pixels / OCR / colour or record — the ones a user must
    /// be able to see happen. Reuses the contract's own traits so this never drifts from them.
    /// </summary>
    private static bool TouchesScreen(CommandKind k) =>
        CommandTraits.ReturnsContent(k) || CommandTraits.IsRecording(k);

    /// <summary>
    /// Sliding-window token check for one source and bucket (general vs recording). Returns
    /// false — and records nothing — when the caller is already at the cap for the last minute.
    /// </summary>
    private bool TryConsume(CommandSource source, bool recording)
    {
        int genLimit = Math.Max(1, Settings.Current.ExternalControlRateLimitPerMin);
        // Recording is stricter: at most a tenth of the general rate, floor of 3, and never
        // more permissive than the general limit itself (guards absurdly small configs).
        int limit = recording ? Math.Min(genLimit, Math.Max(3, genLimit / 10)) : genLimit;
        string key = source.ToString() + (recording ? "#rec" : "#gen");

        var now = DateTime.UtcNow;      // UTC for interval math — immune to clock/DST jumps.
        var cutoff = now - Window;
        lock (_rateLock)
        {
            if (!_windows.TryGetValue(key, out var hits))
            {
                hits = new Queue<DateTime>();
                _windows[key] = hits;
            }
            while (hits.Count > 0 && hits.Peek() < cutoff) hits.Dequeue();
            if (hits.Count >= limit) return false;
            hits.Enqueue(now);
            return true;
        }
    }

    /// <summary>Short, content-free toast text describing the kind of external access.</summary>
    private static string SignalMessage(WsnapCommand cmd)
    {
        string what =
            CommandTraits.IsRecording(cmd.Kind) ? "recording" :
            cmd.Kind is CommandKind.OcrRegion or CommandKind.OcrImage
                     or CommandKind.OcrLast or CommandKind.OcrInteractive ? "OCR" :
            cmd.Kind is CommandKind.ColorAt or CommandKind.ColorPick ? "color read" :
            "capture";
        return $"External {what} by {cmd.Source}";
    }
}
