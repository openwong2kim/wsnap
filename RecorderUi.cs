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

namespace Wsnap;

/// <summary>The floating "recording" pill the recorders show (top-center, click/Esc = stop).
/// Implementations marshal to their UI thread themselves — recorders call from any thread.</summary>
public interface IRecorderBadge
{
    /// <summary>Fired when the user clicks the badge (or presses Esc on it) to stop.</summary>
    event Action? Clicked;
    void SetText(string text);
    void Close();
}

/// <summary>
/// Host-provided badge factory (Phase 4 of the Avalonia migration). The recorders
/// (GifRecorder / VideoRecorder / ScrollCapture) are pure capture+encode logic and must not
/// depend on a UI framework; each host app (WPF App, Avalonia App) registers its own badge
/// implementation at startup. Headless hosts (CLI, tests) leave the factory null — recorders
/// then simply run without a badge, which is also the old showControl:false behaviour.
/// </summary>
public static class RecorderUi
{
    /// <summary>(initialText, argbBackground) → badge. Assigned once by the host app's startup.</summary>
    public static Func<string, uint, IRecorderBadge?>? BadgeFactory;

    public static IRecorderBadge? TryShow(string text, uint argbBackground)
    {
        try { return BadgeFactory?.Invoke(text, argbBackground); }
        catch (Exception ex) { CrashLog.Write("recorder-badge", ex); return null; }
    }
}
