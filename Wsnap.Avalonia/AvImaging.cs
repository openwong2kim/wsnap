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
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GdiRect = System.Drawing.Rectangle;

namespace Wsnap;

/// <summary>
/// GDI Bitmap → Avalonia bitmap conversion (Phase 2) — the Avalonia counterpart of the WPF
/// build's ScreenGrab.ToBitmapSource. Screen grabs carry no meaningful alpha (CopyFromScreen
/// leaves it 0 on some stacks, DXGI often 0), so the copy stamps every pixel opaque; honouring
/// the channel could render the frozen desktop fully transparent.
/// </summary>
public static class AvImaging
{
    public static WriteableBitmap ToAvaloniaBitmap(System.Drawing.Bitmap bmp)
    {
        var rect = new GdiRect(0, 0, bmp.Width, bmp.Height);
        // Explicit 32bppArgb request normalizes any source format to 4-byte BGRA.
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var wb = new WriteableBitmap(new PixelSize(data.Width, data.Height),
                new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using var fb = wb.Lock();
            unsafe
            {
                for (int y = 0; y < data.Height; y++)
                {
                    uint* src = (uint*)((byte*)data.Scan0 + (long)y * data.Stride);
                    uint* dst = (uint*)((byte*)fb.Address + (long)y * fb.RowBytes);
                    for (int x = 0; x < data.Width; x++)
                        dst[x] = src[x] | 0xFF000000u;   // force opaque alpha
                }
            }
            return wb;
        }
        finally { bmp.UnlockBits(data); }
    }
}
