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
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace Wsnap;

/// <summary>
/// WPF-facing clipboard API (Phase 0 shape): every window keeps calling this, but the actual
/// clipboard I/O lives in the framework-agnostic <see cref="ClipboardCore"/> (WinForms OLE
/// clipboard — the path spike (b) verified against real Chrome) and all PNG encoding goes
/// through <see cref="SkiaImage"/>. What remains here is the WPF glue: BitmapSource
/// conversions, drag-event DataObjects, and watcher suppression.
/// </summary>
public static class ImageClipboard
{
    /// <summary>Copy a saved image file in all formats. True on success. Delegates to the
    /// framework-agnostic core (shared with the Avalonia windows since Phase 3).</summary>
    public static bool CopyImageFile(string path) => ClipboardCore.CopyImageFile(path);

    /// <summary>Copy an in-memory image (e.g. the editor's rendered result).</summary>
    public static bool CopyImageSource(BitmapSource src, string? fileForDrop = null)
    {
        try
        {
            byte[] png = EncodePng(src);
            ClipboardWatcher.SuppressNext();
            return ClipboardCore.CopyImagePng(png,
                fileForDrop != null && File.Exists(fileForDrop) ? fileForDrop : null);
        }
        catch (Exception ex) { CrashLog.Write("clip-copy-src", ex); return false; }
    }

    /// <summary>Plain text (used for "copy path" and OCR / hex results).</summary>
    public static bool CopyText(string text)
    {
        ClipboardWatcher.SuppressNext();
        return ClipboardCore.CopyText(text);
    }

    // ---- reading (editor paste / drag-drop) ----

    /// <summary>Read an image OFF the clipboard for the editor's paste. Format preference
    /// (PNG stream → DIB → FileDrop) lives in <see cref="ClipboardCore"/>; this just decodes
    /// the returned bytes into a frozen BitmapSource. Null when there's no image.</summary>
    public static BitmapSource? TryGetImage()
    {
        try
        {
            byte[]? bytes = ClipboardCore.TryReadImageBytes();
            return bytes == null ? null : FromBytes(bytes);
        }
        catch (Exception ex) { CrashLog.Write("clip-get-image", ex); return null; }
    }

    /// <summary>Pull a frozen image out of a WPF drag-drop DataObject, trying
    /// PNG → bitmap → image FileDrop in turn. Null if none. Used by the editor's drop.</summary>
    public static BitmapSource? FromDragData(IDataObject? data)
    {
        if (data == null) return null;

        // (a) PNG stream — alpha-preserving, mirrors the "PNG" format ClipboardCore writes.
        if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream ms && ms.Length > 0)
            return FromBytes(ms.ToArray());

        // (b) Standard bitmap (CF_DIB / CF_BITMAP) — universal, may drop alpha.
        if (data.GetDataPresent(DataFormats.Bitmap)
            && data.GetData(DataFormats.Bitmap) is BitmapSource bs)
        {
            if (bs.CanFreeze) bs.Freeze();
            return bs;
        }

        // (c) FileDrop — an image file copied in Explorer or dragged from disk.
        if (data.GetDataPresent(DataFormats.FileDrop)
            && data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var f in files)
                if (IsImagePath(f) && File.Exists(f))
                    return LoadFrozen(f);
        }
        return null;
    }

    /// <summary>True if the path ends with one of our known image extensions.</summary>
    public static bool IsImagePath(string f) => ClipboardCore.IsImagePath(f);

    /// <summary>Load an image file as a frozen BitmapSource (null on failure). Used by the editor's drop.</summary>
    public static BitmapSource? LoadImageFile(string path)
    {
        try { return LoadFrozen(path); }
        catch (Exception ex) { CrashLog.Write("clip-load-file", ex); return null; }
    }

    // ---- internals ----

    private static BitmapImage FromBytes(byte[] bytes)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = new MemoryStream(bytes, writable: false);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    private static BitmapImage LoadFrozen(string path)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.UriSource = new Uri(path);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    /// <summary>PNG-encode a BitmapSource through SkiaSharp (replaces PngBitmapEncoder).
    /// Bgra32 is WPF's straight (non-premultiplied) BGRA — SkiaSharp's Bgra8888/Unpremul —
    /// so the conversion normalizes Pbgra32 render targets and every other format too.</summary>
    private static byte[] EncodePng(BitmapSource src)
    {
        BitmapSource bgra = src.Format == System.Windows.Media.PixelFormats.Bgra32
            ? src
            : new FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Bgra32, null, 0);

        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var pixels = new byte[(long)stride * h];
        bgra.CopyPixels(pixels, stride, 0);

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var img = SKImage.FromPixels(info, handle.AddrOfPinnedObject(), stride);
            using var encoded = img.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }
        finally { handle.Free(); }
    }
}
