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
///  - <see cref="TrimNow"/> does a compacting GC (reclaims/​defragments the LOH where capture
///    bitmaps live) and then empties the working set — call it after a capture or edit closes.
///  - <see cref="TrimWorkingSet"/> only empties the working set (cheap, no GC pause) — call it
///    on an idle timer to keep the resident set low without churning the heap.
///
/// EmptyWorkingSet pages the working set out; pages fault back in on demand, so the only cost
/// is a tiny latency on the next interaction — fine for a background tray app.
/// </summary>
internal static class MemoryTrim
{
    /// <summary>Compacting GC + working-set trim. Use after a memory-heavy operation finishes.</summary>
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
        TrimWorkingSet();
    }

    /// <summary>Empty the process working set (no GC). Cheap enough for an idle timer.</summary>
    public static void TrimWorkingSet()
    {
        try { EmptyWorkingSet(GetCurrentProcess()); }
        catch { /* best-effort */ }
    }

    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr hProcess);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
}
