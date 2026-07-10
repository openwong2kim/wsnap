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
namespace Wsnap;

/// <summary>
/// TEMPORARY scaffold stub. Ocr.cs (a linked, otherwise-framework-agnostic file) calls
/// Toast.Show(...) to report OCR-model-download progress/failure — real Toast.cs is WPF-specific
/// and gets its Avalonia rewrite in Phase 3 (migration plan: plans/humming-meandering-aurora.md).
/// This stub exists ONLY so the Phase-1 scaffold compiles; delete it the moment Phase 3's real
/// Avalonia Toast lands.
/// </summary>
internal static class Toast
{
    public static void Show(string message, int ms = 1800) { /* no-op until Phase 3 */ }
}
