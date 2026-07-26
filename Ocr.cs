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
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RapidOcrNet;
using SkiaSharp;

namespace Wsnap;

/// <summary>
/// On-device OCR via PaddleOCR PP-OCRv5 models on ONNX Runtime (RapidOcrNet) — free, runs
/// locally. Replaces the old Windows.Media.Ocr engine, which mangled mixed KO/EN text
/// (O↔0, l↔I, dropped Hangul). Small language-agnostic det+cls models are embedded; every
/// recognition pack (Korean default included, since v1.8) downloads on first use and is
/// cached per-user, so ~13 MB of models stays out of the exe for users who never OCR.
///
/// Memory: the engine (ONNX sessions + models) is created lazily on the first OCR and
/// released after a short idle window, so the resident tray footprint stays lean.
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
    /// An OCR recognition language. det+cls models are shared (language-agnostic, embedded);
    /// only the recognition model (rec) + character dictionary vary. PP-OCRv5 is script-based,
    /// so one pack covers many languages (e.g. latin = 32 European languages incl. English).
    /// Every rec pack — Korean included since v1.8 — downloads on first use, keeping ~13 MB of
    /// models out of the exe for the majority who never OCR.
    /// </summary>
    public readonly record struct OcrLanguage(string Code, string Native, double SizeMb);

    /// <summary>Selectable OCR languages. Korean is the default (KO+EN); all are PP-OCRv5
    /// packs from HuggingFace monkt/paddleocr-onnx, fetched on demand and cached per-user.</summary>
    public static readonly OcrLanguage[] Languages =
    {
        new("korean",  "한국어 + English",        13.4),
        new("latin",   "Latin — EN/DE/FR/ES/IT… (32)", 7.9),
        new("chinese", "中文 + 日本語 + English", 84.5),
        new("english", "English",                 8.0),
        new("eslav",   "Кириллица / Cyrillic",   9.0),
        new("greek",   "Ελληνικά / Greek",        8.0),
        new("arabic",  "العربية / Arabic",        8.0),
        new("hindi",   "हिन्दी / Devanagari",      9.0),
        new("tamil",   "தமிழ் / Tamil",            8.0),
        new("telugu",  "తెలుగు / Telugu",          8.0),
        new("thai",    "ไทย / Thai",              8.0),
    };

    private const string ModelBaseUrl = "https://huggingface.co/monkt/paddleocr-onnx/resolve/main/languages";

    /// <summary>The configured OCR language, normalized to a supported one (else the embedded default).</summary>
    public static OcrLanguage CurrentLanguage => Resolve(Settings.Current.OcrLanguage);

    /// <summary>Map a stored code to a known language, falling back to the Korean default.</summary>
    public static OcrLanguage Resolve(string? code)
    {
        if (!string.IsNullOrWhiteSpace(code))
            foreach (var l in Languages) if (l.Code == code) return l;
        return Languages[0];   // korean (default)
    }

    /// <summary>Recognise text in a bitmap using the configured OCR language. Returns "" if
    /// nothing found, null if OCR is unavailable.</summary>
    public static Task<string?> RecognizeAsync(Bitmap bmp) => RecognizeAsync(bmp, null);

    /// <summary>Recognise text with an explicit language pack (null = the configured
    /// <see cref="CurrentLanguage"/>). Lets CLI/MCP callers pass a per-call --lang without
    /// mutating the persisted setting. Returns "" if nothing found, null if OCR is unavailable.</summary>
    public static async Task<string?> RecognizeAsync(Bitmap bmp, OcrLanguage? langOverride)
    {
        try
        {
            // Windows OCR was weak on small text; PP-OCR resizes internally, but nudging genuinely
            // tiny grabs up first still helps the detector lock onto glyphs. Only ever scales UP.
            SKBitmap sk;
            using (Bitmap prepared = UpscaleIfTiny(bmp))
                sk = SkiaImage.ToSKBitmap(prepared);   // direct pixel copy (Phase 0) — the old
                                                       // path PNG-encoded with GDI+ only to
                                                       // SKBitmap.Decode it right back

            OcrLanguage lang = langOverride ?? CurrentLanguage;   // per-call override or configured default

            // ONNX inference is synchronous and CPU-bound — keep it off the UI thread.
            return await Task.Run(() =>
            {
                using var _ = sk;                      // dispose on every path inside the worker
                var engine = GetEngine(lang);
                if (engine == null) return null;       // models missing/download failed → caller shows "사용 불가"

                Interlocked.Increment(ref _inFlight);
                try
                {
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
                // ONNX Runtime (onnxruntime.dll, ~14 MB native) is shipped OUT of the exe and
                // pulled on first OCR from the official Microsoft.ML.OnnxRuntime NuGet package,
                // then loaded via SetDllDirectory. Keeps every non-OCR user from carrying 14 MB
                // of inference runtime they never invoke. (Same lazy model the rec pack already uses.)
                if (!EnsureOnnxRuntime()) return null;

                // det + cls are language-agnostic and always embedded in the exe. Every
                // recognition pack (Korean included) downloads on first use and is cached.
                string det = ExtractModel("wsnap.ocr.det.onnx", "det.onnx");
                string cls = ExtractModel("wsnap.ocr.cls.onnx", "cls.onnx");

                var (_, rec, keys) = ModelPaths(lang);
                if (!EnsureInstalledCore(lang, null)) return null;   // download failed → caller shows "unavailable"

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

    /// <summary>True if the language is ready to use right now (already downloaded or migrated).</summary>
    public static bool IsInstalled(OcrLanguage lang)
    {
        MigrateLegacyKorean(lang);
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
        MigrateLegacyKorean(lang);
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
    /// first OCR is instant. No-op if already present. Reports download progress 0..1.</summary>
    public static Task<bool> EnsureInstalledAsync(OcrLanguage lang, IProgress<double>? progress = null)
        => Task.Run(() => EnsureInstalledCore(lang, progress));

    /// <summary>
    /// Up to v1.7 the Korean pack shipped inside the exe and was extracted loose as
    /// models\v5\korean_rec.onnx + korean_dict.txt. Those are byte-identical to the
    /// languages/korean download, so an upgrading user shouldn't re-download 13 MB —
    /// move the legacy pair into the per-language layout once, best-effort.
    /// </summary>
    private static void MigrateLegacyKorean(OcrLanguage lang)
    {
        if (lang.Code != "korean") return;
        try
        {
            var (dir, rec, keys) = ModelPaths(lang);
            if (File.Exists(rec) && File.Exists(keys)) return;   // already in place

            string legacyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "wsnap", "models", "v5");
            string legacyRec = Path.Combine(legacyDir, "korean_rec.onnx");
            string legacyKeys = Path.Combine(legacyDir, "korean_dict.txt");
            if (!File.Exists(legacyRec) || !File.Exists(legacyKeys)) return;
            if (new FileInfo(legacyRec).Length < 100_000) return;   // torn write — let the download path handle it

            Directory.CreateDirectory(dir);
            File.Move(legacyRec, rec, overwrite: true);
            File.Move(legacyKeys, keys, overwrite: true);
        }
        catch { /* migration is best-effort; the download path is the fallback */ }
    }

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

    // ---------- ONNX Runtime lazy bootstrap ----------

    /// <summary>The version of <c>Microsoft.ML.OnnxRuntime</c> the bundled <c>RapidOcrNet</c>
    /// was built against (see obj/project.assets.json). Pinned so the ABI the managed wrapper
    /// expects always matches the native DLL we fetch. Bump together with the RapidOcrNet
    /// PackageReference whenever that dependency moves.</summary>
    private const string OnnxRuntimeVersion = "1.24.3";

    private static int _runtimeState;   // 0 = unprobed, 1 = ready, -1 = failed this session
    private static readonly string RuntimeDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "wsnap", "runtime");

    /// <summary>Ensure <c>onnxruntime.dll</c> (+ providers_shared) live in <see cref="RuntimeDir"/>
    /// and that directory is on the DLL search path. Idempotent; memoises the outcome per session.</summary>
    private static bool EnsureOnnxRuntime()
    {
        int st = Volatile.Read(ref _runtimeState);
        if (st == 1) return true;
        if (st == -1) return false;

        string dll = Path.Combine(RuntimeDir, "onnxruntime.dll");
        if (File.Exists(dll) && new FileInfo(dll).Length > 1_000_000)
        {
            SetDllDirectory(RuntimeDir);
            Volatile.Write(ref _runtimeState, 1);
            return true;
        }

        try
        {
            Directory.CreateDirectory(RuntimeDir);
            Toast.Show(L.T("toast.runtimeDownloading"), 3000);

            string nupkg = Path.Combine(RuntimeDir, "onnxruntime.nupkg.tmp");
            string url = $"https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime/{OnnxRuntimeVersion}/microsoft.ml.onnxruntime.{OnnxRuntimeVersion}.nupkg";
            DownloadFile(url, nupkg, null);

            // A .nupkg is a zip. Pull only the win-x64 native binaries — the rest (managed asm,
            // other RIDs) is dead weight for this tool.
            using var zip = ZipFile.OpenRead(nupkg);
            foreach (var entry in zip.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (name == "runtimes/win-x64/native/onnxruntime.dll" ||
                    name == "runtimes/win-x64/native/onnxruntime_providers_shared.dll")
                {
                    entry.ExtractToFile(Path.Combine(RuntimeDir, Path.GetFileName(name)), overwrite: true);
                }
            }
            try { File.Delete(nupkg); } catch { }

            if (!File.Exists(dll) || new FileInfo(dll).Length < 1_000_000)
                throw new InvalidDataException("onnxruntime.dll missing or truncated after extract");

            SetDllDirectory(RuntimeDir);
            Volatile.Write(ref _runtimeState, 1);
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("onnx-runtime-bootstrap", ex);
            Toast.Show(L.T("toast.runtimeDownloadFail"), 3500);
            Volatile.Write(ref _runtimeState, -1);
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string lpPathName);

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
