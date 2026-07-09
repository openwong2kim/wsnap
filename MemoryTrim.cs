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

namespace Wsnap;

/// <summary>
/// Keeps the resident (tray) footprint small. wsnap spends most of its life idle in the
/// tray, but a capture briefly allocates large bitmaps (often on the GC Large Object Heap)
/// and the CLR holds onto that committed memory long after. <see cref="TrimNow"/> returns it:
/// a compacting GC reclaims/defragments the LOH where capture bitmaps live, and with
/// System.GC.RetainVM=false (runtimeconfig) the freed segments go back to the OS.
///
/// There is deliberately no EmptyWorkingSet helper any more. Purging the working set paged
/// the WHOLE process out — JIT-compiled code and WPF internals included — so the next hotkey
/// press paid a hard page-fault storm before the overlay could appear. That traded a smaller
/// Task-Manager number for first-interaction latency, which is the opposite of what a capture
/// tool should optimize for. The real footprint fixes live in packaging (nothing bundled that
/// isn't used) and in this compacting trim after memory-heavy operations.
/// </summary>
internal static class MemoryTrim
{
    /// <summary>Compacting GC (no working-set purge). Use after a memory-heavy operation finishes.</summary>
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
}
