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
