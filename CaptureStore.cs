using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Wsnap;

/// <summary>
/// Per-capture metadata for filename templating. Captured <see cref="App"/>-side BEFORE
/// the overlay grabs focus, otherwise {app}/{title} would read wsnap's own overlay.
/// All fields optional; missing tokens expand to empty (then collapsed by the sanitizer).
/// </summary>
public readonly struct NameContext
{
    public string? App { get; init; }      // foreground process name, no ".exe"
    public string? Title { get; init; }    // foreground window title
    public int Width { get; init; }        // captured pixel width  (0 = unknown)
    public int Height { get; init; }       // captured pixel height (0 = unknown)

    public static readonly NameContext Empty = new();
}

/// <summary>
/// Central place that decides WHERE a capture is written and saves the bytes.
/// Respects <see cref="Settings.SaveFolder"/> and <see cref="Settings.KeepHistory"/>
/// (date-foldered permanent archive vs. a flat scratch folder).
/// </summary>
public static class CaptureStore
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();
    private static int _seqCounter;   // process-lifetime monotonic counter for {seq}

    /// <summary>Allocate a fresh capture path (creating the folder), honouring the history setting.</summary>
    public static string NewPath(string ext = ".png") => NewPath(NameContext.Empty, ext);

    public static string NewPath(NameContext ctx, string ext = ".png")
    {
        var s = Settings.Current;
        string baseDir = string.IsNullOrWhiteSpace(s.SaveFolder)
            ? Path.Combine(Path.GetTempPath(), "wsnap")
            : s.SaveFolder;

        string dir = s.KeepHistory
            ? Path.Combine(baseDir, DateTime.Now.ToString("yyyy-MM-dd"))
            : baseDir;
        Directory.CreateDirectory(dir);

        if (!ext.StartsWith('.')) ext = "." + ext;
        return Unique(Path.Combine(dir, BuildName(s.FilenameTemplate, ctx) + ext));
    }

    public static string SaveBitmap(Bitmap bmp) => SaveBitmap(bmp, NameContext.Empty);

    public static string SaveBitmap(Bitmap bmp, NameContext ctx)
    {
        string path = NewPath(ctx);
        bmp.Save(path, ImageFormat.Png);
        CrashLog.Telemetry("capture-saved");
        PruneScratch();
        return path;
    }

    // ---------- capture history ----------

    public static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    private static string ResolvedBaseDir()
    {
        var s = Settings.Current;
        return string.IsNullOrWhiteSpace(s.SaveFolder) ? Path.Combine(Path.GetTempPath(), "wsnap") : s.SaveFolder;
    }

    private static bool IsImage(string f)
    {
        string e = Path.GetExtension(f);
        foreach (var x in ImageExts) if (string.Equals(e, x, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static DateTime SafeWriteTime(string f) { try { return File.GetLastWriteTime(f); } catch { return DateTime.MinValue; } }

    private static bool LooksLikeDateFolder(string n) =>
        DateTime.TryParseExact(n, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>All capture images newest-first: pinned dir + scratch dir + its yyyy-MM-dd subfolders (depth 1).</summary>
    public static List<(string Path, DateTime When, bool Pinned)> EnumerateHistory(int max = 600)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outp = new List<(string, DateTime, bool)>();

        void Scan(string dir, bool pinned, bool recurseDates)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                    if (IsImage(f) && seen.Add(Path.GetFullPath(f)))
                        outp.Add((f, SafeWriteTime(f), pinned));
                if (recurseDates)
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                        if (LooksLikeDateFolder(Path.GetFileName(sub))) Scan(sub, false, false);
            }
            catch (Exception ex) { CrashLog.Write("history-scan", ex); }
        }

        Scan(PinnedDir, pinned: true, recurseDates: false);   // pinned first → claims the dedupe slot
        Scan(ResolvedBaseDir(), pinned: false, recurseDates: true);

        outp.Sort((a, b) => b.Item2.CompareTo(a.Item2));      // newest first
        if (outp.Count > max) outp.RemoveRange(max, outp.Count - max);
        return outp;
    }

    /// <summary>Keep only the newest N images in the FLAT scratch dir (never date folders / pinned). 0 = unlimited.</summary>
    public static void PruneScratch()
    {
        int keep = Settings.Current.HistoryKeepRecent;
        if (keep <= 0) return;
        try
        {
            string baseDir = ResolvedBaseDir();
            if (!Directory.Exists(baseDir)) return;
            var files = new List<string>();
            foreach (var f in Directory.EnumerateFiles(baseDir)) if (IsImage(f)) files.Add(f);  // shallow only
            if (files.Count <= keep) return;
            files.Sort((a, b) => File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));
            for (int i = keep; i < files.Count; i++) { try { File.Delete(files[i]); } catch { } }
        }
        catch (Exception ex) { CrashLog.Write("prune-scratch", ex); }
    }

    // ---------- filename templating ----------

    /// <summary>Expand a template into a safe, non-empty base filename (no extension).</summary>
    public static string BuildName(string? template, NameContext ctx)
    {
        if (string.IsNullOrWhiteSpace(template)) template = Settings.DefaultFilenameTemplate;
        var now = DateTime.Now;
        string raw;
        try { raw = ExpandTemplate(template!, ctx, now); }
        catch (Exception ex) { CrashLog.Write("name-template", ex); raw = ExpandTemplate(Settings.DefaultFilenameTemplate, ctx, now); }

        string clean = SanitizeFileName(raw);
        if (string.IsNullOrWhiteSpace(clean))
            clean = "snap_" + now.ToString("yyyyMMdd_HHmmss_fff");
        return clean;
    }

    private static string ExpandTemplate(string template, NameContext ctx, DateTime now)
    {
        var sb = new StringBuilder(template.Length + 16);
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c == '{')
            {
                int end = template.IndexOf('}', i + 1);
                if (end < 0) { sb.Append(c); i++; continue; }   // unmatched '{' → literal
                sb.Append(ResolveToken(template.Substring(i + 1, end - i - 1), ctx, now));
                i = end + 1;
            }
            else { sb.Append(c); i++; }
        }
        return sb.ToString();
    }

    private static string ResolveToken(string tok, NameContext ctx, DateTime now)
    {
        string key = tok.Trim();
        switch (key.ToLowerInvariant())
        {
            case "app":   return ctx.App ?? "";
            case "title": return ctx.Title ?? "";
            case "date":  return now.ToString("yyyy-MM-dd");
            case "time":  return now.ToString("HH-mm-ss");
            case "seq":   return System.Threading.Interlocked.Increment(ref _seqCounter).ToString("D3");
            case "w":     return ctx.Width > 0 ? ctx.Width.ToString() : "";
            case "h":     return ctx.Height > 0 ? ctx.Height.ToString() : "";
            default:
                // Not a named token → interpret as a .NET date/time format (original case).
                try { return now.ToString(key); }
                catch (FormatException) { return ""; }
        }
    }

    /// <summary>Strip invalid chars, collapse separator runs, dodge reserved names, cap length.</summary>
    public static string SanitizeFileName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
            sb.Append(Array.IndexOf(InvalidChars, ch) >= 0 || ch < 0x20 ? '_' : ch);

        string r = sb.ToString().Trim();
        r = Regex.Replace(r, "[ _]{2,}", "_");
        r = r.Trim(' ', '.', '_', '-');
        if (r.Length > 120) r = r.Substring(0, 120).TrimEnd(' ', '.', '_', '-');
        if (Regex.IsMatch(r, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase))
            r = "_" + r;
        return r;
    }

    /// <summary>Non-temp folder for pinned captures so they survive %TEMP% cleanup.</summary>
    public static string PinnedDir => Path.Combine(Settings.ConfigDir, "pinned");

    /// <summary>
    /// Promote a (possibly temp-folder) capture into a durable location so pinning it
    /// can't be wiped by temp cleanup. No-op if it's already outside temp. Returns the
    /// path the file now lives at (the original on any failure).
    /// </summary>
    public static string PromoteToPinned(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return path;

            // Already kept somewhere durable (history on, custom non-temp folder) → leave it.
            string temp = Path.GetFullPath(Path.GetTempPath());
            if (!Path.GetFullPath(path).StartsWith(temp, StringComparison.OrdinalIgnoreCase))
                return path;

            Directory.CreateDirectory(PinnedDir);
            string dest = Path.Combine(PinnedDir, Path.GetFileName(path));
            dest = Unique(dest);
            try { File.Move(path, dest); }
            catch { File.Copy(path, dest, overwrite: false); }   // cross-volume / locked fallback
            return dest;
        }
        catch (Exception ex) { CrashLog.Write("promote-pinned", ex); return path; }
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
