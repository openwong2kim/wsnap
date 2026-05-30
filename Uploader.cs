using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Wsnap;

/// <summary>
/// Optional, opt-in image host upload (default OFF — wsnap's identity is local + DnD).
/// Imgur anonymous upload using a user-supplied Client-ID. Returns the public URL.
/// </summary>
public static class Uploader
{
    private static readonly HttpClient Http = new();

    public static bool Available =>
        Settings.Current.UploadEnabled && !string.IsNullOrWhiteSpace(Settings.Current.ImgurClientId);

    public static async Task<string?> UploadImgurAsync(string filePath)
    {
        if (!Available) return null;
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(filePath);
            string b64 = Convert.ToBase64String(bytes);

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.imgur.com/3/image");
            req.Headers.Add("Authorization", "Client-ID " + Settings.Current.ImgurClientId.Trim());
            req.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("image", b64),
                new KeyValuePair<string, string>("type", "base64"),
            });

            using var resp = await Http.SendAsync(req);
            string json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                CrashLog.Write($"upload-imgur: HTTP {(int)resp.StatusCode} {json}");
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("link", out var link))
            {
                CrashLog.Telemetry("upload-imgur");
                return link.GetString();
            }
            return null;
        }
        catch (Exception ex) { CrashLog.Write("upload-imgur", ex); return null; }
    }
}
