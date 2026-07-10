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
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wsnap;

namespace Wsnap.Control;

/// <summary>
/// Model Context Protocol server over stdio, letting an MCP client (e.g. Claude) drive wsnap's
/// capture / OCR / color / history / GIF surface. The transport is newline-delimited JSON-RPC 2.0
/// (one line = one message) on stdin/stdout; <b>nothing else may ever touch stdout</b> or the
/// protocol stream is corrupted, so all diagnostics go to <see cref="CrashLog"/> (a file) instead.
///
/// This server owns none of the capture logic: every <c>tools/call</c> is mapped to a
/// <see cref="WsnapCommand"/> and handed to the injected <see cref="ICommandRouter"/>, which applies
/// the security gate and — for interactive/recording work — delegates to the resident tray instance.
/// A <see cref="CommandResult"/> comes back and is projected onto the MCP result shape here.
/// </summary>
public static class McpStdioServer
{
    private const string ProtocolVersion = "2024-11-05";
    private const string ServerName      = "wsnap";
    private const string ServerVersion   = "1.7.0";

    /// <summary>Cap the long edge of an embedded image. Claude vision downscales anything larger to
    /// ~1568 px anyway, so shipping bigger just burns tokens for no fidelity gain.</summary>
    private const int MaxImageEdge = 1568;

    /// <summary>
    /// Run the stdio read/dispatch/write loop until stdin reaches EOF or <paramref name="ct"/> fires.
    /// Wire this from the <c>mcp</c> entry-point branch: build a router (delegating to the resident over
    /// the pipe when available, local <see cref="CaptureCore"/> otherwise) and
    /// <c>McpStdioServer.RunAsync(router).GetAwaiter().GetResult()</c> before any WPF/UI starts.
    /// </summary>
    public static async Task RunAsync(ICommandRouter router, CancellationToken ct = default)
    {
        // UTF-8, no BOM (a BOM on the first line would poison the very first JSON message).
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var reader = new StreamReader(Console.OpenStandardInput(), utf8);
        using var writer = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = false, NewLine = "\n" };

        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;                 // EOF — client closed stdin.
                if (line.Length == 0) continue;          // keep-alive blank line; ignore.

                string? response = await HandleLineAsync(router, line, ct).ConfigureAwait(false);
                if (response is null) continue;          // notification / no-reply.

                await writer.WriteLineAsync(response.AsMemory(), ct).ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }
        catch (Exception ex) { CrashLog.Write("mcp-server", ex); }
    }

    // ---------------- dispatch ----------------

    /// <summary>Parse one line and route it. Returns the response text, or null for notifications.</summary>
    private static async Task<string?> HandleLineAsync(ICommandRouter router, string line, CancellationToken ct)
    {
        if (!JsonRpc.TryParse(line, out var req))
            return JsonRpc.Error(null, JsonRpc.ParseError, "invalid JSON");

        if (string.IsNullOrEmpty(req.Method))
            return JsonRpc.Error(req.Id, JsonRpc.InvalidRequest, "missing method");

        try
        {
            switch (req.Method)
            {
                case "initialize":
                    return Reply(req, InitializeResult());

                case "ping":
                    return Reply(req, new Dictionary<string, object?>());

                case "tools/list":
                    return Reply(req, ToolsListResult());

                case "tools/call":
                    return Reply(req, await ToolsCallAsync(router, req.Params, ct).ConfigureAwait(false));

                // Client → server notifications carry no id and expect no response.
                case "notifications/initialized":
                case "notifications/cancelled":
                    return null;

                default:
                    return req.IsNotification
                        ? null
                        : JsonRpc.Error(req.Id, JsonRpc.MethodNotFound, $"unknown method: {req.Method}");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (McpProtocolException mpe)
        {
            return req.IsNotification ? null : JsonRpc.Error(req.Id, mpe.Code, mpe.Message);
        }
        catch (Exception ex)
        {
            CrashLog.Write("mcp-handle", ex);
            return req.IsNotification ? null : JsonRpc.Error(req.Id, JsonRpc.InternalError, ex.Message);
        }
    }

    /// <summary>Wrap a result as a success envelope, unless the message was a notification.</summary>
    private static string? Reply(JsonRpcRequest req, object? result) =>
        req.IsNotification ? null : JsonRpc.Success(req.Id, result);

    // ---------------- initialize / tools/list ----------------

    private static object InitializeResult() => new Dictionary<string, object?>
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"]    = new Dictionary<string, object?> { ["tools"] = new Dictionary<string, object?>() },
        ["serverInfo"]      = new Dictionary<string, object?> { ["name"] = ServerName, ["version"] = ServerVersion },
    };

    private static object ToolsListResult() => new Dictionary<string, object?> { ["tools"] = ToolDefinitions() };

    // ---------------- tools/call ----------------

    private static async Task<Dictionary<string, object?>> ToolsCallAsync(
        ICommandRouter router, JsonElement? prms, CancellationToken ct)
    {
        string? name = ArgReader.Str(prms, "name");
        if (string.IsNullOrWhiteSpace(name))
            throw new McpProtocolException(JsonRpc.InvalidParams, "tools/call: missing tool name");

        // MCP passes tool inputs under "arguments"; keep only a real object, cloned so it stays valid
        // for the (possibly async / cross-thread) command execution.
        JsonElement? arguments =
            ArgReader.TryProp(prms, "arguments", out var a) && a.ValueKind == JsonValueKind.Object
                ? a.Clone()
                : null;

        if (!TryMapTool(name, arguments, out var kind))
            throw new McpProtocolException(JsonRpc.InvalidParams, $"unknown tool: {name}");

        var cmd = new WsnapCommand(kind, arguments, CommandSource.Mcp);
        var result = await router.ExecuteAsync(cmd, ct).ConfigureAwait(false);
        return ToMcpResult(arguments, result);
    }

    /// <summary>
    /// Map an MCP tool name to a <see cref="CommandKind"/>. Self-contained by design so this file
    /// compiles without CommandCatalog (see task brief). TODO(integration): if/when
    /// <c>CommandCatalog.TryParseMcpTool(name)</c> lands (owned by another agent), route through it
    /// first for a single wire-id source of truth and keep this switch as the fallback.
    /// </summary>
    private static bool TryMapTool(string tool, JsonElement? args, out CommandKind kind)
    {
        switch (tool)
        {
            case "capture_region":      kind = CommandKind.CaptureRegion;      return true;
            case "capture_fullscreen":  kind = CommandKind.CaptureFullScreen;  return true;
            case "capture_window":      kind = CommandKind.CaptureWindow;      return true;
            case "capture_interactive": kind = CommandKind.CaptureInteractive; return true; // mode passed via args
            case "ocr_region":          kind = CommandKind.OcrRegion;          return true;
            case "ocr_image":           kind = CommandKind.OcrImage;           return true;
            case "ocr_last_capture":    kind = CommandKind.OcrLast;            return true;
            case "list_history":        kind = CommandKind.HistoryList;        return true;
            case "get_capture":         kind = CommandKind.HistoryGet;         return true;
            case "record_gif":          kind = CommandKind.Gif;                return true;
            case "stop_recording":      kind = CommandKind.GifStop;            return true;
            case "pick_color":
                // Explicit coordinates → headless one-shot read; no coordinates → interactive eyedropper.
                kind = ArgReader.HasProp(args, "x") && ArgReader.HasProp(args, "y")
                    ? CommandKind.ColorAt
                    : CommandKind.ColorPick;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    // ---------------- CommandResult → MCP result ----------------

    /// <summary>Project a <see cref="CommandResult"/> onto the MCP <c>{content,isError}</c> shape.</summary>
    private static Dictionary<string, object?> ToMcpResult(JsonElement? args, CommandResult r)
    {
        if (!r.Ok)
            return ErrorResult(ErrorText(r));

        // The compact JSON summary is always present (path-only by default to save tokens).
        var summary = SummaryFor(r);

        // Content gating: when ControlGate denied external content return, the Router has already
        // blanked Text/Hex/RGB (Path/Width/Height survive — a saved path is not "content"). Surface
        // that plainly so the model doesn't read the empty fields as a real result.
        if (r.ContentRedacted)
        {
            summary["content_redacted"] = true;
            summary["notice"] = "외부 콘텐츠 반환이 설정에서 비활성화됨 — wsnap 설정에서 " +
                                "'ExternalControlAllowReturnContent'를 켜세요.";
        }

        var content = new List<object?> { TextContent(JsonRpc.Serialize(summary)) };

        // Opt-in image (return = "image" | "both") — suppressed entirely when content is redacted.
        if (!r.ContentRedacted && WantsImage(args) && r.Path is { Length: > 0 } path && IsPng(path) && File.Exists(path))
        {
            var image = TryImageContent(path);
            if (image is not null) content.Add(image);
        }

        return new Dictionary<string, object?> { ["content"] = content, ["isError"] = false };
    }

    /// <summary>Build the per-type JSON summary the model reads (serialized into a text content block).</summary>
    private static Dictionary<string, object?> SummaryFor(CommandResult r)
    {
        switch (r.Type)
        {
            case ResultType.File:
            {
                var m = new Dictionary<string, object?> { ["path"] = r.Path };
                if (r.Width  > 0) m["width"]  = r.Width;
                if (r.Height > 0) m["height"] = r.Height;
                m["copied"] = r.Copied;
                if (r.Bytes > 0)                     m["bytes"] = r.Bytes;
                if (!string.IsNullOrEmpty(r.App))    m["app"]   = r.App;
                if (!string.IsNullOrEmpty(r.Title))  m["title"] = r.Title;
                return m;
            }

            case ResultType.Text:
            {
                var m = new Dictionary<string, object?>
                {
                    ["text"]  = r.Text ?? "",
                    ["lang"]  = r.Lang,
                    ["empty"] = r.Empty,
                };
                if (!string.IsNullOrEmpty(r.Path)) m["source_path"] = r.Path; // ocr_last_capture annotates its source
                return m;
            }

            case ResultType.Color:
                return new Dictionary<string, object?>
                {
                    ["hex"] = r.Hex,
                    ["rgb"] = new Dictionary<string, object?> { ["r"] = r.R, ["g"] = r.G, ["b"] = r.B },
                    ["x"]   = r.X,
                    ["y"]   = r.Y,
                };

            case ResultType.History:
            {
                var items = new List<object?>();
                if (r.History is not null)
                    foreach (var h in r.History)
                        items.Add(new Dictionary<string, object?>
                        {
                            ["path"]   = h.Path,
                            ["when"]   = h.When.ToString("o", CultureInfo.InvariantCulture), // ISO 8601
                            ["pinned"] = h.Pinned,
                        });
                return new Dictionary<string, object?> { ["count"] = items.Count, ["items"] = items };
            }

            case ResultType.Recording:
                // record_gif(until_stop) returns just a started id; a finished/saved recording has a path.
                if (!string.IsNullOrEmpty(r.RecordingId) && string.IsNullOrEmpty(r.Path))
                    return new Dictionary<string, object?> { ["recording_id"] = r.RecordingId };
                var rec = new Dictionary<string, object?>
                {
                    ["path"]    = r.Path,
                    ["frames"]  = r.Frames,
                    ["seconds"] = r.Seconds,
                };
                if (r.Width  > 0) rec["width"]  = r.Width;
                if (r.Height > 0) rec["height"] = r.Height;
                return rec;

            default:
                return new Dictionary<string, object?> { ["ok"] = true };
        }
    }

    /// <summary>Serialize the failure into the JSON text the model sees, with resident-only guidance.</summary>
    private static string ErrorText(CommandResult r)
    {
        string code = r.ErrorCode ?? "internal";
        string message = r.Error ?? "unknown error";
        if (string.Equals(code, "resident_required", StringComparison.OrdinalIgnoreCase))
            message = "wsnap 트레이 앱이 실행 중이어야 합니다 — interactive/recording 명령은 상주 인스턴스가 필요합니다. " +
                      "(The wsnap tray app must be running for interactive/recording commands.) " + message;
        return JsonRpc.Serialize(new Dictionary<string, object?> { ["error"] = code, ["message"] = message });
    }

    // ---------------- content helpers ----------------

    private static bool WantsImage(JsonElement? args)
    {
        string? ret = ArgReader.Str(args, "return");
        return string.Equals(ret, "image", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ret, "both",  StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPng(string path) => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, object?> TextContent(string text) =>
        new() { ["type"] = "text", ["text"] = text };

    private static Dictionary<string, object?>? TryImageContent(string path)
    {
        try
        {
            byte[] png = EncodeDownscaledPng(path);
            return new Dictionary<string, object?>
            {
                ["type"]     = "image",
                ["data"]     = Convert.ToBase64String(png),
                ["mimeType"] = "image/png",
            };
        }
        // On any failure (decode, GDI, disk) omit the image rather than corrupt the response — the
        // text summary already carries the path.
        catch (Exception ex) { CrashLog.Write("mcp-image", ex); return null; }
    }

    /// <summary>
    /// Re-encode a PNG so its long edge is ≤ <see cref="MaxImageEdge"/> px (high-quality bicubic),
    /// returning PNG bytes. Images already within the bound are returned verbatim (no resample), so
    /// small captures pay nothing. Larger ones shrink to the size Claude's vision would enforce
    /// anyway, cutting base64/token cost without losing usable detail.
    /// </summary>
    private static byte[] EncodeDownscaledPng(string path)
    {
        // Read the bytes up front and decode from memory so we never hold a GDI+ file lock on the
        // capture (a Bitmap opened over a path keeps the file locked for its lifetime, which would
        // race the writer that just saved it). The stream must outlive the source Bitmap.
        byte[] original = File.ReadAllBytes(path);
        using var inMs = new MemoryStream(original, writable: false);
        using var src = new Bitmap(inMs);

        int longEdge = Math.Max(src.Width, src.Height);
        if (longEdge <= MaxImageEdge)
            return original; // already small enough — ship the original bytes verbatim.

        double scale = (double)MaxImageEdge / longEdge;
        int w = Math.Max(1, (int)Math.Round(src.Width  * scale));
        int h = Math.Max(1, (int)Math.Round(src.Height * scale));

        using var dst = new Bitmap(w, h);
        using (var g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
            g.SmoothingMode     = SmoothingMode.HighQuality;
            g.DrawImage(src, 0, 0, w, h);
        }

        // SkiaSharp encode (Phase 0). Not forced opaque: this transcodes an existing image
        // verbatim, so whatever alpha the source PNG carried is preserved.
        return SkiaImage.EncodePng(dst, opaque: false);
    }

    private static Dictionary<string, object?> ErrorResult(string text) =>
        new() { ["content"] = new List<object?> { TextContent(text) }, ["isError"] = true };

    // ---------------- tool schemas ----------------

    /// <summary>The 12 advertised tools, each with a JSON-Schema <c>inputSchema</c>.</summary>
    private static List<object?> ToolDefinitions()
    {
        // Coordinates are device pixels, origin at the top-left of the virtual desktop (multi-monitor
        // aware; a monitor left of primary has negative x).
        const string px = " Device pixels; origin is the top-left of the virtual desktop.";

        var ret   = Enum("How the capture is returned. 'path' (default) returns only the saved file " +
                         "path to save tokens; 'image' and 'both' also embed the PNG as base64.",
                         "path", "image", "both");
        var lang  = Str("OCR language hint such as 'ko' or 'en'. Defaults to the app's current OCR language.");

        return new List<object?>
        {
            Tool("capture_region",
                 "Capture a rectangular screen region and save it as PNG.",
                 Props(
                     ("x",      Int("Left edge." + px)),
                     ("y",      Int("Top edge." + px)),
                     ("width",  Int("Width in pixels.")),
                     ("height", Int("Height in pixels.")),
                     ("copy",   Bool("Also copy the capture to the clipboard.")),
                     ("return", ret)),
                 "x", "y", "width", "height"),

            Tool("capture_fullscreen",
                 "Capture a whole monitor and save it as PNG.",
                 Props(
                     ("monitor", Str("Which monitor: 'cursor' (default), 'primary', or a 0-based index like '0'.")),
                     ("return",  ret))),

            Tool("capture_window",
                 "Capture the current foreground window and save it as PNG.",
                 Props(("return", ret))),

            Tool("capture_interactive",
                 "Let the user drag-select a region on screen, then act on it. Requires the wsnap tray " +
                 "app to be running.",
                 Props(
                     ("mode",       Enum("What the selection does: 'region' saves an image (default), " +
                                         "'ocr' recognizes text, 'color' picks a pixel color.",
                                         "region", "ocr", "color")),
                     ("timeout_ms", Int("Auto-cancel the overlay after this many milliseconds of no input.")),
                     ("return",     ret))),

            Tool("ocr_region",
                 "Recognize text inside a screen region (no file saved).",
                 Props(
                     ("x",      Int("Left edge." + px)),
                     ("y",      Int("Top edge." + px)),
                     ("width",  Int("Width in pixels.")),
                     ("height", Int("Height in pixels.")),
                     ("lang",   lang)),
                 "x", "y", "width", "height"),

            Tool("ocr_image",
                 "Recognize text in an existing image file on disk.",
                 Props(
                     ("path", Str("Absolute path to an image file (PNG/JPG/…).")),
                     ("lang", lang)),
                 "path"),

            Tool("ocr_last_capture",
                 "Recognize text in the most recent capture in history.",
                 Props(("lang", lang))),

            Tool("pick_color",
                 "Read a pixel color. With x and y it reads that pixel directly; without them it opens " +
                 "the interactive eyedropper (which requires the wsnap tray app to be running).",
                 Props(
                     ("x", Int("Pixel x." + px)),
                     ("y", Int("Pixel y." + px)))),

            Tool("list_history",
                 "List recent captures (newest first).",
                 Props(
                     ("limit",       Int("Maximum number of entries to return (default 30).")),
                     ("pinned_only", Bool("Return only pinned captures.")))),

            Tool("get_capture",
                 "Resolve a stored capture by history index, filename, or absolute path.",
                 Props(
                     ("id",     Str("0-based history index (0 = newest) or a capture filename.")),
                     ("path",   Str("Absolute path to an existing capture file.")),
                     ("return", ret))),

            Tool("record_gif",
                 "Record a screen region to an animated GIF. Requires the wsnap tray app to be running. " +
                 "Use mode 'fixed' with duration_s for a timed clip, or 'until_stop' and later call " +
                 "stop_recording.",
                 Props(
                     ("x",          Int("Left edge." + px)),
                     ("y",          Int("Top edge." + px)),
                     ("width",      Int("Width in pixels.")),
                     ("height",     Int("Height in pixels.")),
                     ("duration_s", Num("Clip length in seconds (used when mode = 'fixed').")),
                     ("fps",        Int("Frames per second, e.g. 10–15.")),
                     ("mode",       Enum("'fixed' records for duration_s then saves; 'until_stop' records " +
                                         "until stop_recording is called.", "fixed", "until_stop")),
                     ("return",     ret)),
                 "x", "y", "width", "height"),

            Tool("stop_recording",
                 "Stop an in-progress GIF recording and save it.",
                 Props(
                     ("recording_id", Str("The id returned by record_gif in 'until_stop' mode. Omit to " +
                                          "stop the most recent recording.")))),
        };
    }

    // ---- tiny JSON-Schema builders ----

    private static Dictionary<string, object?> Tool(
        string name, string description, Dictionary<string, object?> properties, params string[] required)
    {
        var schema = new Dictionary<string, object?> { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0) schema["required"] = required;
        return new Dictionary<string, object?>
        {
            ["name"]        = name,
            ["description"] = description,
            ["inputSchema"] = schema,
        };
    }

    private static Dictionary<string, object?> Props(params (string Name, Dictionary<string, object?> Schema)[] properties)
    {
        var map = new Dictionary<string, object?>(properties.Length);
        foreach (var (n, s) in properties) map[n] = s;
        return map;
    }

    private static Dictionary<string, object?> Int(string desc)  => new() { ["type"] = "integer", ["description"] = desc };
    private static Dictionary<string, object?> Num(string desc)  => new() { ["type"] = "number",  ["description"] = desc };
    private static Dictionary<string, object?> Bool(string desc) => new() { ["type"] = "boolean", ["description"] = desc };
    private static Dictionary<string, object?> Str(string desc)  => new() { ["type"] = "string",  ["description"] = desc };
    private static Dictionary<string, object?> Enum(string desc, params string[] values) =>
        new() { ["type"] = "string", ["description"] = desc, ["enum"] = values };

    // ---------------- protocol error signal ----------------

    /// <summary>A protocol-level failure (bad params, unknown tool) that maps to a JSON-RPC error.</summary>
    private sealed class McpProtocolException : Exception
    {
        public int Code { get; }
        public McpProtocolException(int code, string message) : base(message) => Code = code;
    }
}
