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
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RapidOcrNet;
using SkiaSharp;

namespace Wsnap;

/// <summary>
/// On-device OCR via PaddleOCR PP-OCRv5 models on ONNX Runtime (RapidOcrNet) — free, offline,
/// no network. Replaces the old Windows.Media.Ocr engine, which mangled mixed KO/EN text
/// (O↔0, l↔I, dropped Hangul). Detection + angle-classification models ship with RapidOcrNet;
/// recognition uses the Korean PP-OCRv5 model (covers KO + EN + digits + symbols).
///
/// Memory: the engine (ONNX sessions + ~18 MB of models) is created lazily on the first OCR and
/// released after a short idle window, so the resident tray footprint stays where v1.2.4 put it.
/// </summary>
public static class Ocr
{
    private static readonly object _gate = new();
    private static RapidOcr? _engine;                 // lazily created; dropped after idle
    private static string? _engineLang;               // language the live engine was built for
    private static Timer? _idleTimer;
    private static int _inFlight;                     // guards dispose against an in-flight Detect
    private static readonly TimeSpan IdleTtl = TimeSpan.FromSeconds(30);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>
    /// An OCR recognition language. det+cls models are shared (language-agnostic); only the
    /// recognition model (rec) + character dictionary vary. PP-OCRv5 is script-based, so one
    /// pack covers many languages (e.g. latin = 32 European languages incl. English).
    /// <paramref name="Embedded"/> packs ship inside the exe; the rest download on first use.
    /// </summary>
    public readonly record struct OcrLanguage(string Code, string Native, double SizeMb, bool Embedded);

    /// <summary>Selectable OCR languages. Korean is the embedded default (KO+EN); the rest
    /// are PP-OCRv5 packs from HuggingFace monkt/paddleocr-onnx, fetched on demand.</summary>
    public static readonly OcrLanguage[] Languages =
    {
        new("korean",  "한국어 + English",        12.8, true),
        new("latin",   "English / Latin (32)",   7.9,  false),
        new("chinese", "中文 + 日本語 + English", 84.5, false),
        new("english", "English",                 8.0,  false),
        new("eslav",   "Кириллица / Cyrillic",   9.0,  false),
        new("greek",   "Ελληνικά / Greek",        8.0,  false),
        new("arabic",  "العربية / Arabic",        8.0,  false),
        new("hindi",   "हिन्दी / Devanagari",      9.0,  false),
        new("tamil",   "தமிழ் / Tamil",            8.0,  false),
        new("telugu",  "తెలుగు / Telugu",          8.0,  false),
        new("thai",    "ไทย / Thai",              8.0,  false),
    };

    private const string ModelBaseUrl = "https://huggingface.co/monkt/paddleocr-onnx/resolve/main/languages";

    /// <summary>The configured OCR language, normalized to a supported one (else the embedded default).</summary>
    public static OcrLanguage CurrentLanguage => Resolve(Settings.Current.OcrLanguage);

    /// <summary>Map a stored code to a known language, falling back to the embedded Korean default.</summary>
    public static OcrLanguage Resolve(string? code)
    {
        if (!string.IsNullOrWhiteSpace(code))
            foreach (var l in Languages) if (l.Code == code) return l;
        return Languages[0];   // korean (embedded)
    }

    /// <summary>Recognise text in a bitmap. Returns "" if nothing found, null if OCR is unavailable.</summary>
    public static async Task<string?> RecognizeAsync(Bitmap bmp)
    {
        try
        {
            // Windows OCR was weak on small text; PP-OCR resizes internally, but nudging genuinely
            // tiny grabs up first still helps the detector lock onto glyphs. Only ever scales UP.
            using Bitmap prepared = UpscaleIfTiny(bmp);

            byte[] png;
            using (var ms = new MemoryStream())
            {
                prepared.Save(ms, ImageFormat.Png);
                png = ms.ToArray();
            }

            OcrLanguage lang = CurrentLanguage;        // read on the UI thread, used below

            // ONNX inference is synchronous and CPU-bound — keep it off the UI thread.
            return await Task.Run(() =>
            {
                var engine = GetEngine(lang);
                if (engine == null) return null;       // models missing/download failed → caller shows "사용 불가"

                Interlocked.Increment(ref _inFlight);
                try
                {
                    using SKBitmap? sk = SKBitmap.Decode(png);
                    if (sk == null) return "";

                    // Screenshots are upright, so skip 180° angle classification (faster, no false flips).
                    var options = RapidOcrOptions.Default with { DoAngle = false };
                    OcrResult result = engine.Detect(sk, options);

                    string text = (result?.StrRes ?? string.Empty).Trim();
                    // The Korean dict includes decomposed jamo; compose to precomposed syllables
                    // so consumers (clipboard, search) get normal Hangul.
                    return text.Length == 0 ? string.Empty : text.Normalize(NormalizationForm.FormC);
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                    ScheduleIdleDispose();
                }
            });
        }
        catch (Exception ex)
        {
            CrashLog.Write("ocr", ex);
            return null;
        }
    }

    private static RapidOcr? GetEngine(OcrLanguage lang)
    {
        lock (_gate)
        {
            // Reuse the live engine only if it's for the same language; otherwise rebuild.
            if (_engine != null && _engineLang == lang.Code) return _engine;
            if (_engine != null) { try { _engine.Dispose(); } catch { } _engine = null; _engineLang = null; }

            try
            {
                // det + cls are language-agnostic and always embedded in the exe. The recognition
                // model + dictionary vary per language: Korean ships embedded; others download.
                string det = ExtractModel("wsnap.ocr.det.onnx", "det.onnx");
                string cls = ExtractModel("wsnap.ocr.cls.onnx", "cls.onnx");

                string rec, keys;
                if (lang.Embedded)   // korean — bundled in the single-file exe
                {
                    rec = ExtractModel("wsnap.ocr.rec.onnx", "korean_rec.onnx");
                    keys = ExtractModel("wsnap.ocr.keys.txt", "korean_dict.txt");
                }
                else
                {
                    var (_, r, k) = ModelPaths(lang);
                    if (!EnsureInstalledCore(lang, null)) return null;   // download failed → caller shows "unavailable"
                    rec = r; keys = k;
                }

                var engine = new RapidOcr();
                engine.InitModels(det, cls, rec, keys);
                _engine = engine;
                _engineLang = lang.Code;
                return _engine;
            }
            catch (Exception ex)
            {
                CrashLog.Write("ocr", ex);
                return null;
            }
        }
    }

    /// <summary>Per-user cache paths for a language's downloaded models.</summary>
    private static (string dir, string rec, string keys) ModelPaths(OcrLanguage lang)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "wsnap", "models", "v5", lang.Code);
        return (dir, Path.Combine(dir, "rec.onnx"), Path.Combine(dir, "dict.txt"));
    }

    /// <summary>True if the language is ready to use right now (embedded, or already downloaded).</summary>
    public static bool IsInstalled(OcrLanguage lang)
    {
        if (lang.Embedded) return true;
        var (_, rec, keys) = ModelPaths(lang);
        return File.Exists(rec) && new FileInfo(rec).Length > 100_000 && File.Exists(keys);
    }

    /// <summary>
    /// Ensure the language's models are present, downloading them from HuggingFace if needed.
    /// Returns false if the download failed. <paramref name="progress"/> reports 0..1 of the
    /// recognition model download (the large file). Runs synchronously — call via
    /// <see cref="EnsureInstalledAsync"/> from UI code, or directly from the OCR worker thread.
    /// </summary>
    private static bool EnsureInstalledCore(OcrLanguage lang, IProgress<double>? progress)
    {
        if (lang.Embedded) return true;
        var (dir, rec, keys) = ModelPaths(lang);
        if (File.Exists(rec) && new FileInfo(rec).Length > 100_000 && File.Exists(keys))
            return true;

        try
        {
            Directory.CreateDirectory(dir);
            Toast.Show(L.T("toast.ocrDownloading", lang.Native, $"~{lang.SizeMb:0.#} MB"), 3000);
            DownloadFile($"{ModelBaseUrl}/{lang.Code}/dict.txt", keys, null);
            DownloadFile($"{ModelBaseUrl}/{lang.Code}/rec.onnx", rec, progress);
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("ocr-download", ex);
            // Don't leave a half-written model that would fail to load next time.
            try { if (File.Exists(rec)) File.Delete(rec); } catch { }
            Toast.Show(L.T("toast.ocrDownloadFail", lang.Native), 3500);
            return false;
        }
    }

    /// <summary>Pre-install a language's models (e.g. when the user picks it in settings) so the
    /// first OCR is instant. No-op if embedded or already present. Reports download progress 0..1.</summary>
    public static Task<bool> EnsureInstalledAsync(OcrLanguage lang, IProgress<double>? progress = null)
        => Task.Run(() => EnsureInstalledCore(lang, progress));

    /// <summary>Download to a temp file then atomically swap, so a torn write never wins.
    /// Streams so progress can be reported against Content-Length (the rec model is several MB).</summary>
    private static void DownloadFile(string url, string destPath, IProgress<double>? progress)
    {
        string tmp = destPath + ".tmp";
        using (var resp = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1;
            using var src = resp.Content.ReadAsStream();
            using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);

            byte[] buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                fs.Write(buf, 0, n);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        File.Move(tmp, destPath, overwrite: true);
    }

    /// <summary>
    /// Extract an embedded model resource to %LOCALAPPDATA%\wsnap\models\v5 on first use and return
    /// its absolute path. Re-extracts only if the file is missing or the size differs (cheap version
    /// check that survives app upgrades shipping new models). Absolute paths matter because the tray /
    /// autostart process can launch with an unrelated working directory.
    /// </summary>
    private static string ExtractModel(string resourceName, string fileName)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "wsnap", "models", "v5");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, fileName);

        var asm = typeof(Ocr).Assembly;
        using Stream? res = asm.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded OCR model '{resourceName}' not found.");

        if (File.Exists(path) && new FileInfo(path).Length == res.Length)
            return path;   // already extracted and intact

        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            res.CopyTo(fs);
        File.Move(tmp, path, overwrite: true);   // atomic-ish swap so a torn write never wins
        return path;
    }

    /// <summary>Arm (or re-arm) the idle timer that releases the engine after the last OCR.</summary>
    private static void ScheduleIdleDispose()
    {
        lock (_gate)
        {
            _idleTimer?.Dispose();
            _idleTimer = new Timer(_ => DisposeEngine(), null, IdleTtl, Timeout.InfiniteTimeSpan);
        }
    }

    private static void DisposeEngine()
    {
        RapidOcr? toDispose;
        lock (_gate)
        {
            // An OCR is still running — don't pull the native sessions out from under it
            // (the user's no-AccessViolation rule). Re-arm and try again later.
            if (Volatile.Read(ref _inFlight) > 0)
            {
                _idleTimer?.Dispose();
                _idleTimer = new Timer(_ => DisposeEngine(), null, IdleTtl, Timeout.InfiniteTimeSpan);
                return;
            }

            toDispose = _engine;
            _engine = null;
            _idleTimer?.Dispose();
            _idleTimer = null;
        }

        try { toDispose?.Dispose(); } catch { /* best effort */ }
        // Return the model memory (LOH + ORT arenas) to the OS so the idle tray stays lean.
        try { MemoryTrim.TrimNow(); } catch { }
    }

    /// <summary>
    /// Upscale only genuinely small captures so the detector has enough glyph height to work with.
    /// Never downscales (PP-OCR caps the long side itself). Returns a disposable bitmap the caller owns.
    /// </summary>
    private static Bitmap UpscaleIfTiny(Bitmap src)
    {
        double longSide = Math.Max(src.Width, src.Height);
        const double target = 960;
        double scale = longSide < target ? Math.Min(3.0, target / longSide) : 1.0;
        if (scale <= 1.01) return (Bitmap)src.Clone();

        int nw = (int)Math.Round(src.Width * scale);
        int nh = (int)Math.Round(src.Height * scale);
        var dst = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        g.DrawImage(src, 0, 0, nw, nh);
        return dst;
    }
}
