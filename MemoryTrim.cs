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
using System.Runtime;
using System.Runtime.InteropServices;

namespace Wsnap;

/// <summary>
/// Keeps the resident (tray) footprint small. wsnap spends most of its life idle in the
/// tray, but a capture briefly allocates large bitmaps (often on the GC Large Object Heap)
/// and the CLR holds onto that committed memory long after. These helpers return it to the
/// OS so Task Manager shows tens of MB at idle instead of hundreds.
///
///  - <see cref="TrimNow"/> does a compacting GC ONLY — it reclaims/​defragments the LOH where
///    capture bitmaps live, and a compacting collection already returns the freed LOH segments
///    to the OS. Call it after a capture or edit closes. It deliberately does NOT purge the
///    working set (see below).
///  - <see cref="TrimWorkingSet"/> empties the working set (EmptyWorkingSet), which pages the
///    WHOLE process out — JIT-compiled code and WPF internals included. Those pages fault back
///    in on demand, so the NEXT interaction (e.g. the capture hotkey after idling) pays a hard
///    page-fault storm before the overlay can even appear. That trades a smaller idle-RAM
///    number for first-interaction latency, so call it sparingly: once after a long idle, never
///    on a short repeating timer.
/// </summary>
internal static class MemoryTrim
{
    /// <summary>Compacting GC only (no working-set purge). Use after a memory-heavy operation finishes.</summary>
    public static void TrimNow()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        catch { /* GC tuning is best-effort */ }
    }

    /// <summary>Empty the process working set (no GC). Costs a page-fault storm on the next
    /// interaction, so reserve it for a one-shot long-idle trim — never a short repeating timer.</summary>
    public static void TrimWorkingSet()
    {
        try { EmptyWorkingSet(GetCurrentProcess()); }
        catch { /* best-effort */ }
    }

    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr hProcess);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
}
