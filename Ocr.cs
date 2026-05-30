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

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
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
