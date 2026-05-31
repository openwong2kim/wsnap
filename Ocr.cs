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
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Wsnap;

/// <summary>
/// On-device OCR via Windows.Media.Ocr (free, no network, no extra deps).
/// Recognises whatever language packs the user has installed; prefers KO/EN.
/// </summary>
public static class Ocr
{
    /// <summary>Recognise text in a bitmap. Returns "" if nothing found, null if OCR is unavailable.</summary>
    public static async Task<string?> RecognizeAsync(Bitmap bmp)
    {
        try
        {
            var engine = CreateEngine();
            if (engine == null) return null;

            // Windows OCR is weak on small text — upscale small captures before recognition.
            using Bitmap prepared = Preprocess(bmp);

            using var ms = new MemoryStream();
            prepared.Save(ms, ImageFormat.Png);
            byte[] bytes = ms.ToArray();

            var ras = new InMemoryRandomAccessStream();
            using (var dw = new DataWriter(ras))
            {
                dw.WriteBytes(bytes);
                await dw.StoreAsync();
                await dw.FlushAsync();
                dw.DetachStream();
            }
            ras.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(ras);
            using var soft = await decoder.GetSoftwareBitmapAsync();

            var result = await engine.RecognizeAsync(soft);
            return result?.Text ?? "";
        }
        catch (Exception ex)
        {
            CrashLog.Write("ocr", ex);
            return null;
        }
    }

    /// <summary>
    /// Upscale small captures so text is big enough for the OCR engine (it struggles below
    /// a certain glyph height). Only ever scales UP, high-quality bicubic, capped at 3× and
    /// at the engine's max image dimension. Returns a disposable bitmap the caller owns.
    /// </summary>
    private static Bitmap Preprocess(Bitmap src)
    {
        double longSide = Math.Max(src.Width, src.Height);
        const double target = 1800;                 // aim for ~1800px on the long side
        double scale = longSide < target ? Math.Min(3.0, target / longSide) : 1.0;

        double maxDim = OcrEngine.MaxImageDimension; // don't exceed what the engine accepts (static)
        if (maxDim > 0 && longSide * scale > maxDim)
            scale = Math.Max(1.0, maxDim / longSide);

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

    private static OcrEngine? CreateEngine()
    {
        // Best: whatever languages the user reads.
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine != null) return engine;

        // Fallbacks if the profile yields nothing usable.
        foreach (var tag in new[] { "ko", "en-US", "en" })
        {
            try
            {
                var lang = new Language(tag);
                if (OcrEngine.IsLanguageSupported(lang))
                {
                    var e = OcrEngine.TryCreateFromLanguage(lang);
                    if (e != null) return e;
                }
            }
            catch { }
        }
        return null;
    }
}
