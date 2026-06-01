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
    private static Timer? _idleTimer;
    private static int _inFlight;                     // guards dispose against an in-flight Detect
    private static readonly TimeSpan IdleTtl = TimeSpan.FromSeconds(30);

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

            // ONNX inference is synchronous and CPU-bound — keep it off the UI thread.
            return await Task.Run(() =>
            {
                var engine = GetEngine();
                if (engine == null) return null;       // models missing → caller shows "사용 불가"

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

    private static RapidOcr? GetEngine()
    {
        lock (_gate)
        {
            if (_engine != null) return _engine;

            try
            {
                // Models are embedded in the exe (single-file distribution). Extract them once to a
                // stable per-user cache and feed RapidOcr absolute paths.
                string det = ExtractModel("wsnap.ocr.det.onnx", "det.onnx");
                string cls = ExtractModel("wsnap.ocr.cls.onnx", "cls.onnx");
                string rec = ExtractModel("wsnap.ocr.rec.onnx", "korean_rec.onnx");
                string keys = ExtractModel("wsnap.ocr.keys.txt", "korean_dict.txt");

                var engine = new RapidOcr();
                engine.InitModels(det, cls, rec, keys);
                _engine = engine;
                return _engine;
            }
            catch (Exception ex)
            {
                CrashLog.Write("ocr", ex);
                return null;
            }
        }
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
