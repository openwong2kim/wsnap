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
using System.Drawing;

namespace Wsnap;

/// <summary>Pixel grabbing off the live screen (device pixels in, Bitmap out). Framework-
/// agnostic and linked into Wsnap.Avalonia (Phase 2); the WPF-only BitmapSource conversion
/// lives in ScreenGrabWpf.cs (same partial class, WPF project only).</summary>
public static partial class ScreenGrab
{
    /// <summary>GDI grab (CopyFromScreen). The universal fallback, and the right choice for
    /// recorders re-grabbing many times per second (no per-call duplication setup).</summary>
    public static Bitmap Grab(int x, int y, int w, int h)
    {
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h), CopyPixelOperation.SourceCopy);
        return bmp;
    }

    /// <summary>One-shot grab for the latency-critical paths (overlay freeze, full-screen
    /// delivery): try the GPU desktop-duplication path first — several times faster than a
    /// full-desktop CPU BitBlt on large/high-DPI screens — and fall back to GDI whenever it
    /// can't serve (RDP, rotated outputs, driver quirks).</summary>
    public static Bitmap GrabFast(int x, int y, int w, int h)
        => DxgiGrab.TryGrab(x, y, w, h) ?? Grab(x, y, w, h);
}
