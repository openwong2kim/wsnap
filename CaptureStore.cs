using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace Wsnap;

/// <summary>
/// Central place that decides WHERE a capture is written and saves the bytes.
/// Respects <see cref="Settings.SaveFolder"/> and <see cref="Settings.KeepHistory"/>
/// (date-foldered permanent archive vs. a flat scratch folder).
/// </summary>
public static class CaptureStore
{
    /// <summary>Allocate a fresh capture path (creating the folder), honouring the history setting.</summary>
    public static string NewPath(string ext = ".png")
    {
        var s = Settings.Current;
        string baseDir = string.IsNullOrWhiteSpace(s.SaveFolder)
            ? Path.Combine(Path.GetTempPath(), "wsnap")
            : s.SaveFolder;

        string dir = s.KeepHistory
            ? Path.Combine(baseDir, DateTime.Now.ToString("yyyy-MM-dd"))
            : baseDir;

        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"snap_{DateTime.Now:yyyyMMdd_HHmmss_fff}{ext}");
    }

    public static string SaveBitmap(Bitmap bmp)
    {
        string path = NewPath();
        bmp.Save(path, ImageFormat.Png);
        CrashLog.Telemetry("capture-saved");
        return path;
    }
}
