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
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Wsnap;

/// <summary>Pixel grabbing off the live screen (device pixels in, Bitmap/BitmapSource out).</summary>
public static class ScreenGrab
{
    public static Bitmap Grab(int x, int y, int w, int h)
    {
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h), CopyPixelOperation.SourceCopy);
        return bmp;
    }

    /// <summary>
    /// Frozen WPF BitmapSource from a GDI Bitmap by copying its pixels ONCE via LockBits.
    /// The old GetHbitmap + CreateBitmapSourceFromHBitmap path made a SECOND full GDI copy of
    /// the source (a whole extra virtual-desktop grab — ~66 MB on dual-4K — on overlay open) and
    /// churned a GDI HBITMAP handle on every loupe/GIF tick; this reads the bytes straight into
    /// WPF's own buffer instead. LockBits is asked for Format32bppArgb so a source in any pixel
    /// format is normalized to 4-byte BGRA. We publish it as Bgr32 (alpha ignored) to match the
    /// old behaviour: screen grabs carry no meaningful alpha (CopyFromScreen leaves it 0), so
    /// honouring the alpha channel would render the frozen desktop fully transparent.
    /// </summary>
    public static BitmapSource ToBitmapSource(Bitmap bmp)
    {
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            // BitmapSource.Create copies the buffer synchronously here, so it's safe to unlock
            // the source pixels immediately afterwards in the finally.
            var src = BitmapSource.Create(
                data.Width, data.Height, 96, 96,
                System.Windows.Media.PixelFormats.Bgr32, null,
                data.Scan0, data.Stride * data.Height, data.Stride);
            src.Freeze();
            return src;
        }
        finally { bmp.UnlockBits(data); }
    }
}
