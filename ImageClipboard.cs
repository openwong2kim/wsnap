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
using System.Collections.Specialized;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Wsnap;

/// <summary>
/// One place to put an image on the clipboard so it pastes EVERYWHERE.
/// We set multiple formats on a single DataObject:
///   • CF_DIB via <see cref="DataObject.SetImage"/> — universal, but loses alpha.
///   • "PNG" stream — Chrome / Slack / Office / Figma honour this and keep alpha.
///   • FileDrop — Explorer, chat upload fields, and "paste a file" targets.
/// Committed with <c>SetDataObject(data, copy:true)</c> so it survives app exit, and
/// retried a few times because the clipboard can be transiently locked by other apps.
/// </summary>
public static class ImageClipboard
{
    /// <summary>Copy a saved image file (PNG path) in all formats. True on success.</summary>
    public static bool CopyImageFile(string path)
    {
        try
        {
            var src = LoadFrozen(path);
            byte[]? png = TryReadAllBytes(path);
            return Put(src, png, path);
        }
        catch (Exception ex) { CrashLog.Write("clip-copy-file", ex); return false; }
    }

    /// <summary>Copy an in-memory image (e.g. the editor's rendered result).</summary>
    public static bool CopyImageSource(BitmapSource src, string? fileForDrop = null)
    {
        try
        {
            byte[] png = EncodePng(src);
            return Put(src, png, fileForDrop != null && File.Exists(fileForDrop) ? fileForDrop : null);
        }
        catch (Exception ex) { CrashLog.Write("clip-copy-src", ex); return false; }
    }

    /// <summary>Plain text (used for "copy path" and OCR / hex results).</summary>
    public static bool CopyText(string text)
    {
        ClipboardWatcher.SuppressNext();
        return Retry(() => System.Windows.Clipboard.SetText(text), "clip-copy-text");
    }

    // ---- reading (editor paste / drag-drop) ----

    /// <summary>Read an image OFF the clipboard for the editor's paste. Prefers PNG
    /// (keeps alpha — symmetric with how we write it), then a standard bitmap, then an
    /// image file from a FileDrop. Null when there's no image. Result is frozen.</summary>
    public static BitmapSource? TryGetImage()
    {
        try
        {
            var img = FromDragData(System.Windows.Clipboard.GetDataObject());
            if (img != null) return img;
            // Final fallback: WPF's own clipboard image accessor (CF_DIB path).
            if (System.Windows.Clipboard.ContainsImage())
            {
                var bs = System.Windows.Clipboard.GetImage();
                if (bs != null) { if (bs.CanFreeze) bs.Freeze(); return bs; }
            }
            return null;
        }
        catch (Exception ex) { CrashLog.Write("clip-get-image", ex); return null; }
    }

    /// <summary>Pull a frozen image out of a clipboard- or drag-drop DataObject, trying
    /// PNG → bitmap → image FileDrop in turn. Null if none. Shared by paste and drop.</summary>
    public static BitmapSource? FromDragData(IDataObject? data)
    {
        if (data == null) return null;

        // (a) PNG stream — alpha-preserving, mirrors the "PNG" format Put() writes.
        if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream ms && ms.Length > 0)
        {
            ms.Position = 0;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }

        // (b) Standard bitmap (CF_DIB / CF_BITMAP) — universal, may drop alpha.
        if (data.GetDataPresent(System.Windows.DataFormats.Bitmap)
            && data.GetData(System.Windows.DataFormats.Bitmap) is BitmapSource bs)
        {
            if (bs.CanFreeze) bs.Freeze();
            return bs;
        }

        // (c) FileDrop — an image file copied in Explorer or dragged from disk.
        if (data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            && data.GetData(System.Windows.DataFormats.FileDrop) is string[] files)
        {
            foreach (var f in files)
                if (IsImagePath(f) && File.Exists(f))
                    return LoadFrozen(f);
        }
        return null;
    }

    /// <summary>True if the path ends with one of our known image extensions.</summary>
    public static bool IsImagePath(string f) =>
        Array.Exists(CaptureStore.ImageExts, e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Load an image file as a frozen BitmapSource (null on failure). Used by the editor's drop.</summary>
    public static BitmapSource? LoadImageFile(string path)
    {
        try { return LoadFrozen(path); }
        catch (Exception ex) { CrashLog.Write("clip-load-file", ex); return null; }
    }

    // ---- internals ----

    private static bool Put(BitmapSource src, byte[]? png, string? filePath)
    {
        var data = new DataObject();
        data.SetImage(src);                                   // CF_DIB
        if (png != null)
        {
            var ms = new MemoryStream(png);
            data.SetData("PNG", ms);                          // alpha-preserving
        }
        if (filePath != null)
            data.SetFileDropList(new StringCollection { filePath });

        // We're about to mutate the clipboard ourselves — don't let the watcher
        // bounce it back as a brand-new thumbnail.
        ClipboardWatcher.SuppressNext();
        return Retry(() => System.Windows.Clipboard.SetDataObject(data, true), "clip-set");
    }

    private static bool Retry(Action act, string tag)
    {
        for (int i = 0; i < 3; i++)
        {
            try { act(); return true; }
            catch (Exception ex)
            {
                if (i == 2) { CrashLog.Write(tag, ex); return false; }
                Thread.Sleep(80);
            }
        }
        return false;
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

    private static byte[] EncodePng(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static byte[]? TryReadAllBytes(string path)
    {
        try { return File.ReadAllBytes(path); } catch { return null; }
    }
}
