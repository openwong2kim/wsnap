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
using System.Drawing.Imaging;
using System.IO;
using SkiaSharp;

namespace Wsnap;

/// <summary>
/// The single image-encoding stack (Phase 0 of the Avalonia migration): every PNG we produce
/// goes through SkiaSharp (already bundled for OCR), replacing the mix of GDI+
/// <c>Bitmap.Save</c> and WPF <c>PngBitmapEncoder</c>. UI-framework-agnostic on purpose —
/// System.Drawing is fine here (WinForms is a deliberately retained island), WPF types are not.
/// </summary>
public static class SkiaImage
{
    /// <summary>
    /// Encode a GDI+ bitmap to PNG. <paramref name="opaque"/> = true stamps the pixels as
    /// alpha-less: screen grabs carry no meaningful alpha (CopyFromScreen yields 255 here but
    /// 0 on some stacks — see ScreenGrab.ToBitmapSource), so honouring the channel could
    /// produce an all-transparent PNG on those machines. Pass false only for images whose
    /// alpha is real (e.g. transcoding an existing file).
    /// </summary>
    public static byte[] EncodePng(Bitmap bmp, bool opaque)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        // LockBits with an explicit 32bppArgb request normalizes any source pixel format
        // to 4-byte BGRA, which is exactly SkiaSharp's Bgra8888.
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var info = new SKImageInfo(data.Width, data.Height, SKColorType.Bgra8888,
                                       opaque ? SKAlphaType.Opaque : SKAlphaType.Unpremul);
            using var img = SKImage.FromPixels(info, data.Scan0, data.Stride);
            using var encoded = img.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>Encode straight to a file (the CaptureStore save path).</summary>
    public static void SavePng(Bitmap bmp, string path, bool opaque = true)
        => File.WriteAllBytes(path, EncodePng(bmp, opaque));

    /// <summary>Encode an SKBitmap to PNG bytes.</summary>
    public static byte[] EncodePng(SKBitmap bmp)
    {
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Copy a GDI+ bitmap into an owned SKBitmap (no lifetime ties to the source). Used by the
    /// GIF recorder's frame buffer and the OCR feed — replaces the old encode-to-PNG-then-decode
    /// round trip.
    /// </summary>
    public static SKBitmap ToSKBitmap(Bitmap bmp, bool opaque = true)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var info = new SKImageInfo(data.Width, data.Height, SKColorType.Bgra8888,
                                       opaque ? SKAlphaType.Opaque : SKAlphaType.Unpremul);
            var skb = new SKBitmap(info);
            unsafe
            {
                byte* src = (byte*)data.Scan0;
                byte* dst = (byte*)skb.GetPixels();
                int rowBytes = data.Width * 4;
                for (int y = 0; y < data.Height; y++)
                    Buffer.MemoryCopy(src + (long)y * data.Stride, dst + (long)y * skb.RowBytes,
                                      rowBytes, rowBytes);
            }
            return skb;
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>True if the bytes start with the 8-byte PNG signature.</summary>
    public static bool LooksLikePng(byte[] bytes) =>
        bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E &&
        bytes[3] == 0x47 && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;

    /// <summary>
    /// Re-encode arbitrary image bytes (JPEG/BMP/GIF/...) as PNG. Null when the bytes don't
    /// decode. Used when copying a non-PNG history file so the clipboard "PNG" format actually
    /// contains PNG (the old code shipped raw JPEG bytes under the "PNG" label).
    /// </summary>
    public static byte[]? TranscodeToPng(byte[] bytes)
    {
        // SkiaSharp 3.x throws ArgumentNullException (not null) when the bytes aren't a
        // decodable image, so the null check alone doesn't cover garbage input.
        try
        {
            using var skb = SKBitmap.Decode(bytes);
            return skb == null ? null : EncodePng(skb);
        }
        catch { return null; }
    }
}
