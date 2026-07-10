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
using System.Drawing;
using System.IO;
using SWF = System.Windows.Forms;

namespace Wsnap;

/// <summary>
/// UI-framework-agnostic clipboard I/O over the WinForms clipboard (the deliberately retained
/// island — same OLE machinery WPF wraps, and the exact path the Avalonia migration's spike (b)
/// validated end-to-end against real Chrome; Avalonia's own IClipboard image support is broken
/// on Windows, AvaloniaUI/Avalonia#20183/#20644, so it is never used). Writes commit with
/// copy:true so the data survives app exit, and retry a few times because the clipboard can be
/// transiently locked by other apps. The WPF-facing wrapper (<see cref="ImageClipboard"/>) owns
/// watcher suppression and BitmapSource conversions; this layer is pure data.
/// </summary>
public static class ClipboardCore
{
    /// <summary>
    /// Put an image on the clipboard so it pastes EVERYWHERE, in wsnap's three formats:
    ///   • CF_BITMAP/CF_DIB via <see cref="SWF.DataObject.SetImage(Image)"/> — universal, loses alpha.
    ///   • "PNG" stream — Chrome / Slack / Office / Figma honour this and keep alpha.
    ///   • FileDrop — Explorer, chat upload fields, and "paste a file" targets.
    /// <paramref name="png"/> must be actual PNG bytes (see SkiaImage.TranscodeToPng for other inputs).
    /// </summary>
    public static bool CopyImagePng(byte[] png, string? filePath)
    {
        try
        {
            // Bitmap over a MemoryStream defers decoding to the stream, so clone into a
            // stream-independent copy before the using scope closes it.
            using var ms = new MemoryStream(png, writable: false);
            using var decoded = new Bitmap(ms);
            using var bitmap = new Bitmap(decoded);

            var data = new SWF.DataObject();
            data.SetImage(bitmap);                                // CF_BITMAP (OLE synthesizes CF_DIB/V5)
            data.SetData("PNG", false, new MemoryStream(png));    // alpha-preserving
            if (filePath != null)
                data.SetFileDropList(new StringCollection { filePath });

            // copy:true = OleFlushClipboard — every format is rendered into HGLOBALs before
            // this returns, so disposing the bitmap afterwards is safe and the data outlives us.
            SWF.Clipboard.SetDataObject(data, copy: true, retryTimes: 3, retryDelay: 80);
            return true;
        }
        catch (Exception ex) { CrashLog.Write("clip-set", ex); return false; }
    }

    /// <summary>Plain text (used for "copy path" and OCR / hex results).</summary>
    public static bool CopyText(string text)
    {
        try
        {
            var data = new SWF.DataObject(SWF.DataFormats.UnicodeText, text);
            SWF.Clipboard.SetDataObject(data, copy: true, retryTimes: 3, retryDelay: 80);
            return true;
        }
        catch (Exception ex) { CrashLog.Write("clip-copy-text", ex); return false; }
    }

    /// <summary>
    /// Read an image OFF the clipboard as encoded bytes (PNG unless a non-PNG file was on a
    /// FileDrop). Order mirrors what we write: "PNG" stream (keeps alpha) → standard
    /// CF_DIB/CF_BITMAP (re-encoded to PNG, alpha-less by definition) → an image file from a
    /// FileDrop. Null when there's no image. <paramref name="includeFileDrop"/> = false skips
    /// the FileDrop fallback — ClipboardWatcher uses that so a plain file copy in Explorer
    /// doesn't count as an "image copy".
    /// </summary>
    public static byte[]? TryReadImageBytes(bool includeFileDrop = true)
    {
        try
        {
            var data = SWF.Clipboard.GetDataObject();
            if (data != null && data.GetDataPresent("PNG")
                && data.GetData("PNG") is MemoryStream ms && ms.Length > 0)
                return ms.ToArray();

            if (SWF.Clipboard.ContainsImage() && SWF.Clipboard.GetImage() is Bitmap dib)
                using (dib) return SkiaImage.EncodePng(dib, opaque: true);

            if (includeFileDrop)
                foreach (string? f in SWF.Clipboard.GetFileDropList())
                    if (f != null && IsImagePath(f) && File.Exists(f))
                        return File.ReadAllBytes(f);

            return null;
        }
        catch (Exception ex) { CrashLog.Write("clip-get-image", ex); return null; }
    }

    /// <summary>True if the path ends with one of our known image extensions.</summary>
    public static bool IsImagePath(string f) =>
        Array.Exists(CaptureStore.ImageExts, e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase));
}
