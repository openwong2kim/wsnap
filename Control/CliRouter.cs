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
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Wsnap;
using WinForms = System.Windows.Forms;

namespace Wsnap.Control;

/// <summary>
/// <c>wsnap &lt;verb&gt; [flags]</c> 진입점. 첫 토큰을 verb로, 나머지를 플래그로 파싱해
/// <see cref="WsnapCommand"/>(<see cref="CommandSource.Cli"/>)로 정규화하고 주입된
/// <see cref="ICommandRouter"/>로 실행한다. CaptureCore를 직접 부르지 않는다 — 라우터가
/// 헤드리스/상주위임/게이트를 결정한다.
///
/// 출력 규약: 기본은 사람용 한 줄 요약을 stdout에, <c>--json</c>은 단일 JSON 오브젝트를 stdout에,
/// 로그·진단은 stderr에. <c>--out -</c>는 원시 PNG 바이트를 stdout에 흘리고 요약은 stderr로 보낸다.
///
/// 종료 코드: 0 성공 · 1 일반실패 · 2 사용법오류 · 3 상주필요 · 4 결과없음/취소 · 5 OCR불가.
///
/// verb→CommandKind 매핑은 이 파일 안에 자족적 스위치로 둔다(CommandCatalog에 하드 의존하지 않아
/// 병렬 개발/컴파일이 서로를 막지 않는다).
/// </summary>
public static class CliRouter
{
    // ---------------------------------------------------------------------------------
    // 진입 표면
    // ---------------------------------------------------------------------------------

    /// <summary>첫 토큰이 wsnap 하위 명령(또는 전역 --help/--version, 별도 처리되는 mcp)인가.
    /// 통합자가 "이건 CLI 실행이다 vs GUI를 띄운다"를 가르는 데 쓴다.</summary>
    public static bool IsKnownVerb(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return false;
        switch (arg.ToLowerInvariant())
        {
            case "capture":
            case "ocr":
            case "color":
            case "gif":
            case "video":
            case "scroll":
            case "history":
            case "repeat":
            case "settings":
            case "open":
            case "status":
            case "mcp":              // 인식엔 포함(Run에서는 다루지 않음 — 통합자가 McpStdioServer로 분기)
            case "--help":
            case "-h":
            case "help":
            case "--version":
            case "-v":
            case "version":
                return true;
            default:
                return false;
        }
    }

    /// <summary>CLI 한 번 실행. 종료 코드를 반환한다(호출자가 Environment.Exit).</summary>
    public static async Task<int> Run(string[] args, ICommandRouter router)
    {
        if (args is null || args.Length == 0) { PrintUsage(toStdout: false); return 2; }

        string verb = args[0].ToLowerInvariant();
        if (verb is "--help" or "-h" or "help" or "/?") { PrintUsage(toStdout: true); return 0; }
        if (verb is "--version" or "-v" or "version") { ConsoleBridge.OutLine("wsnap " + VersionString()); return 0; }

        string[] rest = args.Length > 1 ? args[1..] : Array.Empty<string>();
        if (Contains(rest, "--help", "-h")) { PrintVerbHelp(verb); return 0; }

        try
        {
            return verb switch
            {
                "capture"  => await RunCapture(rest, router),
                "ocr"      => await RunOcr(rest, router),
                "color"    => await RunColor(rest, router),
                "gif"      => await RunGif(rest, router),
                "video"    => await RunVideo(rest, router),
                "scroll"   => await RunScroll(rest, router),
                "history"  => await RunHistory(rest, router),
                "repeat"   => await RunRepeat(rest, router),
                "settings" => await RunSettings(rest, router),
                "open"     => await RunOpen(rest, router),
                "status"   => await RunStatus(rest, router),
                _          => Usage($"unknown command: {verb}"),
            };
        }
        catch (UsageException ux)
        {
            return Usage(ux.Message);
        }
        catch (Exception ex)
        {
            CrashLog.Write("cli", ex);
            ConsoleBridge.ErrLine("wsnap: " + ex.Message);
            return 1;
        }
    }

    // ---------------------------------------------------------------------------------
    // verb 핸들러
    // ---------------------------------------------------------------------------------

    private static async Task<int> RunCapture(string[] rest, ICommandRouter router)
    {
        var cli = new Cli(rest, "region", "out", "display");
        CommandKind kind;
        JsonElement? args;
        if (cli.Has("region"))
        {
            var (x, y, w, h) = ParseRect(cli.Get("region"));
            kind = CommandKind.CaptureRegion;
            args = Args(("x", x), ("y", y), ("width", w), ("height", h));
        }
        else if (cli.Has("full"))
        {
            kind = CommandKind.CaptureFullScreen;
            // --display N (또는 primary) → monitor; 없으면 커서 아래 모니터(CaptureCore 기본).
            args = cli.Has("display") ? Args(("monitor", cli.Get("display"))) : (JsonElement?)null;
        }
        else if (cli.Has("window"))
        {
            kind = CommandKind.CaptureWindow;
            args = null;
        }
        else
        {
            // 모드 미지정(또는 --interactive) → 드래그 오버레이. 상주 필요(없으면 resident_required).
            kind = CommandKind.CaptureInteractive;
            args = null;
        }

        // --copy / --out clipboard 힌트를 args로도 전달(상주 host가 복사 수행; result.Copied가 진실).
        bool wantCopy = cli.Has("copy") || string.Equals(cli.Get("out"), "clipboard", StringComparison.OrdinalIgnoreCase);
        if (wantCopy && args is { } a)
            args = Merge(a, ("copy", true));
        else if (wantCopy)
            args = Args(("copy", true));

        return await RunCaptureLike(new WsnapCommand(kind, args, CommandSource.Cli), router, cli);
    }

    private static async Task<int> RunRepeat(string[] rest, ICommandRouter router)
    {
        var cli = new Cli(rest, "out");
        return await RunCaptureLike(new WsnapCommand(CommandKind.CaptureRepeat, null, CommandSource.Cli), router, cli);
    }

    private static async Task<int> RunOcr(string[] rest, ICommandRouter router)
    {
        var cli = new Cli(rest, "region", "file", "lang");
        bool json = cli.Has("json");
        string? lang = cli.Get("lang");
        string? tempFile = null;
        try
        {
            WsnapCommand cmd;
            if (cli.Has("region"))
            {
                var (x, y, w, h) = ParseRect(cli.Get("region"));
                cmd = new WsnapCommand(CommandKind.OcrRegion,
                    Args(("x", x), ("y", y), ("width", w), ("height", h), ("lang", lang)), CommandSource.Cli);
            }
            else if (cli.Has("file"))
            {
                string? f = cli.Get("file");
                if (f == "-") { tempFile = ReadStdinToTemp(); f = tempFile; }
                if (string.IsNullOrEmpty(f)) return Usage("ocr --file requires a path or '-'");
                cmd = new WsnapCommand(CommandKind.OcrImage, Args(("path", f), ("lang", lang)), CommandSource.Cli);
            }
            else
            {
                // --last(기본): 가장 최근 캡처를 읽는다.
                cmd = new WsnapCommand(CommandKind.OcrLast, Args(("lang", lang)), CommandSource.Cli);
            }

            var r = await router.ExecuteAsync(cmd);
            if (!r.Ok) return Fail(r, json);

            if (json)
                WriteJsonLine(w =>
                {
                    w.WriteBoolean("ok", true);
                    w.WriteString("text", r.Text ?? "");
                    w.WriteString("lang", r.Lang ?? lang ?? "");
                    w.WriteBoolean("empty", r.Empty);
                    if (!string.IsNullOrEmpty(r.Path)) w.WriteString("source", r.Path);
                });
            else if (!string.IsNullOrEmpty(r.Text))
                ConsoleBridge.OutLine(r.Text);   // 텍스트 없음("")은 빈 출력 + exit 0
            return 0;
        }
        finally
        {
            if (tempFile != null) TryDelete(tempFile);
        }
    }

    private static async Task<int> RunColor(string[] rest, ICommandRouter router)
    {
        var cli = new Cli(rest, "at", "format");
        bool json = cli.Has("json");
        int x, y;
        if (cli.Has("at")) { (x, y) = ParsePoint(cli.Get("at")); }
        else { var p = WinForms.Cursor.Position; x = p.X; y = p.Y; }   // --cursor 또는 기본: 현재 커서 위치

        var r = await router.ExecuteAsync(new WsnapCommand(CommandKind.ColorAt, Args(("x", x), ("y", y)), CommandSource.Cli));
        if (!r.Ok) return Fail(r, json);

        if (json)
            WriteJsonLine(w =>
            {
                w.WriteBoolean("ok", true);
                w.WriteString("hex", r.Hex ?? Hex(r));
                w.WriteNumber("r", r.R); w.WriteNumber("g", r.G); w.WriteNumber("b", r.B);
                w.WriteNumber("x", r.X); w.WriteNumber("y", r.Y);
            });
        else
        {
            string fmt = (cli.Get("format") ?? "hex").ToLowerInvariant();
            ConsoleBridge.OutLine(fmt == "rgb" ? $"rgb({r.R}, {r.G}, {r.B})" : (r.Hex ?? Hex(r)));
        }
        return 0;
    }

    private static async Task<int> RunGif(string[] rest, ICommandRouter router)
    {
        var cli = new Cli(rest, "region", "duration", "fps");
        bool json = cli.Has("json");

        // gif stop [<recording-id>]
        if (cli.Positional.Count > 0 && string.Equals(cli.Positional[0], "stop", StringComparison.OrdinalIgnoreCase))
        {
            string? rid = cli.Positional.Count > 1 ? cli.Positional[1] : null;
            var stop = await router.ExecuteAsync(new WsnapCommand(CommandKind.GifStop,
                rid != null ? Args(("recording_id", rid)) : (JsonElement?)null, CommandSource.Cli));
            return EmitRecording(stop, json);
        }

        var kv = new List<(string, object?)>();
        if (cli.Has("region"))
        {
            var (x, y, w, h) = ParseRect(cli.Get("region"));
            kv.Add(("x", x)); kv.Add(("y", y)); kv.Add(("width", w)); kv.Add(("height", h));
        }
        if (cli.Has("duration")) kv.Add(("duration_s", ParseDoubleStrict(cli.Get("duration"))));
        if (cli.Has("fps")) kv.Add(("fps", ParseIntStrict(cli.Get("fps"))));
        kv.Add(("mode", cli.Has("duration") ? "fixed" : "until_stop"));

        var res = await router.ExecuteAsync(new WsnapCommand(CommandKind.Gif, Args(kv.ToArray()), CommandSource.Cli));
        return EmitRecording(res, json);
    }

    private static async Task<int> RunVideo(string[] rest, ICommandRouter router)
    {
        var cli = new Cli(rest, "region", "format", "duration");
        bool json = cli.Has("json");

        var kv = new List<(string, object?)>();
        if (cli.Has("region"))
        {
            var (x, y, w, h) = ParseRect(cli.Get("region"));
            kv.Add(("x", x)); kv.Add(("y", y)); kv.Add(("width", w)); kv.Add(("height", h));
        }
        if (cli.Has("format")) kv.Add(("format", cli.Get("format")));
        if (cli.Has("duration")) kv.Add(("duration_s", ParseDoubleStrict(cli.Get("duration"))));

        JsonElement? args = kv.Count > 0 ? Args(kv.ToArray()) : (JsonElement?)null;
        var res = await router.ExecuteAsync(new WsnapCommand(CommandKind.Video, args, CommandSource.Cli));
        return EmitRecording(res, json);
    }

    private static async Task<int> RunScroll(string[] rest, ICommandRouter router)
    {
        var cli = new Cli(rest, "region");
        bool json = cli.Has("json");

        JsonElement? args = null;
        if (cli.Has("region"))
        {
            var (x, y, w, h) = ParseRect(cli.Get("region"));
            args = Args(("x", x), ("y", y), ("width", w), ("height", h));
        }
        var res = await router.ExecuteAsync(new WsnapCommand(CommandKind.Scroll, args, CommandSource.Cli));
        return EmitRecording(res, json);
    }

    private static async Task<int> RunHistory(string[] rest, ICommandRouter router)
    {
        var cli = new Cli(rest, "limit", "out");
        bool json = cli.Has("json");
        string sub = cli.Positional.Count > 0 ? cli.Positional[0].ToLowerInvariant() : "list";

        switch (sub)
        {
            case "list":
            {
                int limit = cli.Has("limit") ? ParseIntStrict(cli.Get("limit")) : 30;
                bool pinned = cli.Has("pinned");
                var r = await router.ExecuteAsync(new WsnapCommand(CommandKind.HistoryList,
                    Args(("limit", limit), ("pinnedOnly", pinned)), CommandSource.Cli));
                if (!r.Ok) return Fail(r, json);

                var items = r.History ?? Array.Empty<HistoryItem>();
                if (json)
                    WriteJsonLine(w =>
                    {
                        w.WriteBoolean("ok", true);
                        w.WriteStartArray("items");
                        for (int i = 0; i < items.Count; i++)
                        {
                            var it = items[i];
                            w.WriteStartObject();
                            w.WriteNumber("index", i);
                            w.WriteString("path", it.Path);
                            w.WriteString("when", it.When.ToString("o", CultureInfo.InvariantCulture));
                            w.WriteBoolean("pinned", it.Pinned);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                    });
                else
                    for (int i = 0; i < items.Count; i++)
                    {
                        var it = items[i];
                        ConsoleBridge.OutLine(string.Format(CultureInfo.InvariantCulture, "{0}\t{1:yyyy-MM-dd HH:mm}\t{2}\t{3}",
                            i, it.When, it.Pinned ? "pin" : "   ", it.Path));
                    }
                return 0;
            }

            case "get":
            {
                if (cli.Positional.Count < 2) return Usage("history get <id>");
                string id = cli.Positional[1];
                // id는 인덱스·파일명·경로 어느 것이든 CaptureCore.HistoryGet이 해석한다.
                // 라우터가 "id"/"path" 중 무엇을 읽든 같은 문자열이 넘어가도록 둘 다 채운다.
                var r = await router.ExecuteAsync(new WsnapCommand(CommandKind.HistoryGet,
                    Args(("id", id), ("path", id)), CommandSource.Cli));
                if (!r.Ok) return Fail(r, json);

                string? outv = cli.Get("out");
                if (outv == "-")
                {
                    WritePngToStdout(r.Path);
                    ConsoleBridge.ErrLine(r.Path ?? "");
                    return 0;
                }
                if (!string.IsNullOrEmpty(outv))
                {
                    try { string dst = CopyTo(r.Path!, outv!); ConsoleBridge.OutLine(dst); }
                    catch (Exception ex) { ConsoleBridge.ErrLine("wsnap: " + ex.Message); return 1; }
                    return 0;
                }
                if (json) WriteJsonLine(w => { w.WriteBoolean("ok", true); w.WriteString("path", r.Path); });
                else ConsoleBridge.OutLine(r.Path ?? "");
                return 0;
            }

            case "open":
            {
                var r = await router.ExecuteAsync(new WsnapCommand(CommandKind.ShowHistory, null, CommandSource.Cli));
                if (!r.Ok) return Fail(r, json);
                if (json) WriteJsonLine(w => w.WriteBoolean("ok", true));
                else ConsoleBridge.ErrLine("Opened history window.");
                return 0;
            }

            default:
                return Usage($"unknown history subcommand: {sub}");
        }
    }

    private static async Task<int> RunSettings(string[] rest, ICommandRouter router)
    {
        var cli = new Cli(rest);
        bool json = cli.Has("json");
        string sub = cli.Positional.Count > 0 ? cli.Positional[0].ToLowerInvariant() : "get";

        switch (sub)
        {
            case "path":
            {
                // 전용 CommandKind가 없다 — 설정 파일 위치는 순수 로컬 정보라 여기서 바로 출력한다.
                string p = Path.Combine(Settings.ConfigDir, "settings.json");
                if (json) WriteJsonLine(w => { w.WriteBoolean("ok", true); w.WriteString("path", p); });
                else ConsoleBridge.OutLine(p);
                return 0;
            }

            case "get":
            {
                string? key = cli.Positional.Count > 1 ? cli.Positional[1] : null;
                JsonElement? args = key != null ? Args(("keys", new[] { key })) : (JsonElement?)null;
                var r = await router.ExecuteAsync(new WsnapCommand(CommandKind.SettingsGet, args, CommandSource.Cli));
                if (!r.Ok) return Fail(r, json);

                if (json)
                    WriteJsonLine(w =>
                    {
                        w.WriteBoolean("ok", true);
                        w.WritePropertyName("settings");
                        JsonSerializer.Serialize(w, r.Payload, JsonCompact);
                    });
                else if (r.Payload is string s) ConsoleBridge.OutLine(s);
                else if (r.Payload != null) ConsoleBridge.OutLine(JsonSerializer.Serialize(r.Payload, JsonPretty));
                return 0;
            }

            case "set":
            {
                if (cli.Positional.Count < 3) return Usage("settings set <key> <value>");
                string key = cli.Positional[1], val = cli.Positional[2];
                var r = await router.ExecuteAsync(new WsnapCommand(CommandKind.SettingsSet,
                    Args(("key", key), ("value", val)), CommandSource.Cli));
                if (!r.Ok) return Fail(r, json);
                if (json) WriteJsonLine(w => { w.WriteBoolean("ok", true); w.WriteString("key", key); w.WriteString("value", val); });
                else ConsoleBridge.ErrLine($"set {key} = {val}");
                return 0;
            }

            default:
                return Usage($"unknown settings subcommand: {sub}");
        }
    }

    private static async Task<int> RunOpen(string[] rest, ICommandRouter router)
    {
        var cli = new Cli(rest);
        bool json = cli.Has("json");
        var r = await router.ExecuteAsync(new WsnapCommand(CommandKind.OpenFolder, null, CommandSource.Cli));
        if (!r.Ok) return Fail(r, json);
        if (json) WriteJsonLine(w => w.WriteBoolean("ok", true));
        else ConsoleBridge.ErrLine("Opened save folder.");
        return 0;
    }

    private static async Task<int> RunStatus(string[] rest, ICommandRouter router)
    {
        var cli = new Cli(rest);
        bool json = cli.Has("json");
        var r = await router.ExecuteAsync(new WsnapCommand(CommandKind.Status, null, CommandSource.Cli));
        if (!r.Ok) return Fail(r, json);

        if (json)
            WriteJsonLine(w =>
            {
                w.WriteBoolean("ok", true);
                if (r.Payload != null) { w.WritePropertyName("status"); JsonSerializer.Serialize(w, r.Payload, JsonCompact); }
            });
        else if (r.Payload != null) ConsoleBridge.OutLine(JsonSerializer.Serialize(r.Payload, JsonPretty));
        else ConsoleBridge.OutLine("wsnap " + VersionString() + " — ok");
        return 0;
    }

    // ---------------------------------------------------------------------------------
    // 결과 방출
    // ---------------------------------------------------------------------------------

    /// <summary>캡처/재캡처 공통 방출: --out - (바이너리) / --out &lt;path&gt; (복사) / --out clipboard / 요약·JSON.</summary>
    private static async Task<int> RunCaptureLike(WsnapCommand cmd, ICommandRouter router, Cli cli)
    {
        bool json = cli.Has("json");
        var r = await router.ExecuteAsync(cmd);
        if (!r.Ok) return Fail(r, json);

        string? outv = cli.Get("out");

        // 원시 PNG 바이트를 stdout으로. stdout을 바이너리로 깨끗이 유지하려 텍스트는 전부 stderr.
        if (outv == "-")
        {
            WritePngToStdout(r.Path);
            if (json) EmitCaptureJson(r, r.Path, toStderr: true);
            else ConsoleBridge.ErrLine(CaptureSummary(r, r.Path));
            return 0;
        }

        string? savedPath = r.Path;
        if (!string.IsNullOrEmpty(outv) && !string.Equals(outv, "clipboard", StringComparison.OrdinalIgnoreCase))
        {
            try { savedPath = CopyTo(r.Path!, outv!); }
            catch (Exception ex)
            {
                if (json) WriteErrorJson(CommandResult.Fail("io", ex.Message));
                else ConsoleBridge.ErrLine("wsnap: " + ex.Message);
                return 1;
            }
        }

        if (json) EmitCaptureJson(r, savedPath, toStderr: false);
        else ConsoleBridge.OutLine(CaptureSummary(r, savedPath));
        return 0;
    }

    private static void EmitCaptureJson(CommandResult r, string? path, bool toStderr) =>
        WriteJsonLine(w =>
        {
            w.WriteBoolean("ok", true);
            w.WriteString("path", path ?? r.Path ?? "");
            w.WriteNumber("width", r.Width);
            w.WriteNumber("height", r.Height);
            if (!string.IsNullOrEmpty(r.App)) w.WriteString("app", r.App);
            if (!string.IsNullOrEmpty(r.Title)) w.WriteString("title", r.Title);
            if (r.Bytes > 0) w.WriteNumber("bytes", r.Bytes);
            w.WriteBoolean("copied", r.Copied);
        }, toStderr);

    private static string CaptureSummary(CommandResult r, string? path) =>
        r.Copied
            ? $"Copied to clipboard ({r.Width}x{r.Height})"
            : $"Saved {path ?? r.Path} ({r.Width}x{r.Height})";

    private static int EmitRecording(CommandResult r, bool json)
    {
        if (!r.Ok) return Fail(r, json);
        if (json)
            WriteJsonLine(w =>
            {
                w.WriteBoolean("ok", true);
                if (r.RecordingId != null) w.WriteString("recording_id", r.RecordingId);
                if (r.Path != null)
                {
                    w.WriteString("path", r.Path);
                    w.WriteNumber("width", r.Width);
                    w.WriteNumber("height", r.Height);
                    w.WriteNumber("frames", r.Frames);
                    w.WriteNumber("seconds", r.Seconds);
                }
            });
        else if (r.Path != null)
            ConsoleBridge.OutLine(string.Format(CultureInfo.InvariantCulture,
                "Saved {0} ({1}x{2}, {3} frames, {4:0.0}s)", r.Path, r.Width, r.Height, r.Frames, r.Seconds));
        else if (r.RecordingId != null)
            ConsoleBridge.OutLine($"Recording started (id={r.RecordingId})");
        else
            ConsoleBridge.OutLine("OK");
        return 0;
    }

    /// <summary>실패를 규약대로 방출하고 종료 코드를 돌려준다.</summary>
    private static int Fail(CommandResult r, bool json)
    {
        if (json) { WriteErrorJson(r); return ExitForError(r.ErrorCode); }
        ConsoleBridge.ErrLine($"wsnap: {r.Error ?? r.ErrorCode ?? "error"}"
            + (r.ErrorCode != null ? $" ({r.ErrorCode})" : ""));
        if (r.ErrorCode == "resident_required")
            ConsoleBridge.ErrLine("hint: this action needs the wsnap app running.");
        return ExitForError(r.ErrorCode);
    }

    private static void WriteErrorJson(CommandResult r, bool toStderr = false) =>
        WriteJsonLine(w =>
        {
            w.WriteBoolean("ok", false);
            w.WriteString("error_code", r.ErrorCode ?? "internal");
            w.WriteString("error", r.Error ?? "");
        }, toStderr);

    /// <summary>ErrorCode → 종료 코드.</summary>
    private static int ExitForError(string? code) => code switch
    {
        "resident_required" => 3,
        "no_region" or "cancelled" or "not_found" or "no_window" => 4,
        "ocr_unavailable" => 5,
        "unknown_cmd" => 2,
        _ => 1,
    };

    // ---------------------------------------------------------------------------------
    // 출력 헬퍼
    // ---------------------------------------------------------------------------------

    /// <summary>단일 JSON 오브젝트를 한 줄로 방출한다(기본 stdout, --out - 겸용 시 stderr).</summary>
    private static void WriteJsonLine(Action<Utf8JsonWriter> body, bool toStderr = false)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        string json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        if (toStderr) ConsoleBridge.ErrLine(json); else ConsoleBridge.OutLine(json);
    }

    private static void WritePngToStdout(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        using var outStream = ConsoleBridge.OpenStdout();
        using var fs = File.OpenRead(path);
        fs.CopyTo(outStream);
        outStream.Flush();
    }

    private static string CopyTo(string srcPath, string destPath)
    {
        string full = Path.GetFullPath(destPath);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.Copy(srcPath, full, overwrite: true);
        return full;
    }

    private static string ReadStdinToTemp()
    {
        string path = Path.Combine(Path.GetTempPath(), "wsnap-ocr-" + Guid.NewGuid().ToString("N") + ".png");
        using (var inp = ConsoleBridge.OpenStdin())
        using (var fs = File.Create(path))
            inp.CopyTo(fs);
        return path;
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    private static string Hex(CommandResult r) => $"#{r.R:X2}{r.G:X2}{r.B:X2}";

    private static string VersionString()
    {
        var v = typeof(CliRouter).Assembly.GetName().Version;
        return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "unknown";
    }

    // ---------------------------------------------------------------------------------
    // 파싱
    // ---------------------------------------------------------------------------------

    /// <summary>키-값 튜플에서 JSON 오브젝트 args를 만든다(null 값은 생략).</summary>
    private static JsonElement Args(params (string key, object? val)[] kv)
    {
        var d = new Dictionary<string, object?>(kv.Length);
        foreach (var (k, v) in kv) if (v != null) d[k] = v;
        return ArgReader.Obj(d);
    }

    /// <summary>기존 JSON 오브젝트 args에 키를 덧붙인 새 오브젝트를 만든다.</summary>
    private static JsonElement Merge(JsonElement obj, params (string key, object? val)[] kv)
    {
        var d = new Dictionary<string, object?>();
        if (obj.ValueKind == JsonValueKind.Object)
            foreach (var p in obj.EnumerateObject()) d[p.Name] = p.Value.Clone();
        foreach (var (k, v) in kv) if (v != null) d[k] = v;
        return ArgReader.Obj(d);
    }

    private static (int x, int y, int w, int h) ParseRect(string? s)
    {
        var p = Split(s, 4, "x,y,w,h");
        int x = ParseIntStrict(p[0]), y = ParseIntStrict(p[1]), w = ParseIntStrict(p[2]), h = ParseIntStrict(p[3]);
        if (w < 1 || h < 1) throw new UsageException("region width/height must be >= 1");
        return (x, y, w, h);
    }

    private static (int x, int y) ParsePoint(string? s)
    {
        var p = Split(s, 2, "x,y");
        return (ParseIntStrict(p[0]), ParseIntStrict(p[1]));
    }

    private static string[] Split(string? s, int count, string shape)
    {
        if (string.IsNullOrWhiteSpace(s)) throw new UsageException($"expected {shape}");
        var parts = s.Split(',');
        if (parts.Length != count) throw new UsageException($"expected {shape}, got '{s}'");
        return parts;
    }

    private static int ParseIntStrict(string? s)
    {
        if (!int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            throw new UsageException($"invalid integer '{s}'");
        return v;
    }

    private static double ParseDoubleStrict(string? s)
    {
        if (!double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            throw new UsageException($"invalid number '{s}'");
        return v;
    }

    private static bool Contains(string[] a, params string[] any)
    {
        foreach (var t in a)
            foreach (var n in any)
                if (string.Equals(t, n, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    // ---------------------------------------------------------------------------------
    // 사용법
    // ---------------------------------------------------------------------------------

    private static int Usage(string msg)
    {
        ConsoleBridge.ErrLine("wsnap: " + msg);
        ConsoleBridge.ErrLine("try 'wsnap --help'");
        return 2;
    }

    private static void PrintVerbHelp(string verb)
    {
        string line = verb switch
        {
            "capture"  => "capture  [--region x,y,w,h | --full [--display N] | --window | --interactive] [--copy] [--out <path>|-|clipboard] [--json]",
            "ocr"      => "ocr      [--region x,y,w,h | --file <path>|- | --last] [--lang <code>] [--json]",
            "color"    => "color    [--at x,y | --cursor] [--format hex|rgb] [--json]",
            "gif"      => "gif      [--region x,y,w,h] [--duration <s>] [--fps <n>] [--json]   |   gif stop [<id>]",
            "video"    => "video    [--region x,y,w,h] [--format mp4|apng] [--duration <s>]",
            "scroll"   => "scroll   [--region x,y,w,h]",
            "history"  => "history  list [--limit N] [--pinned] [--json]   |   history get <id> [--out -|<path>]   |   history open",
            "repeat"   => "repeat   [--copy] [--out <path>|-|clipboard] [--json]",
            "settings" => "settings get [key] | set <key> <value> | path",
            "open"     => "open     (open the save folder)",
            "status"   => "status   [--json]",
            _          => null!,
        };
        if (line != null) ConsoleBridge.OutLine("usage: wsnap " + line);
        else PrintUsage(toStdout: true);
    }

    private static void PrintUsage(bool toStdout)
    {
        void w(string s) { if (toStdout) ConsoleBridge.OutLine(s); else ConsoleBridge.ErrLine(s); }
        w("wsnap — screen capture / OCR / color, controllable from the shell.");
        w("");
        w("usage: wsnap <command> [options]");
        w("");
        w("commands:");
        w("  capture   [--region x,y,w,h | --full [--display N] | --window | --interactive]");
        w("            [--copy] [--out <path>|-|clipboard] [--json]");
        w("  ocr       [--region x,y,w,h | --file <path>|- | --last] [--lang <code>] [--json]");
        w("  color     [--at x,y | --cursor] [--format hex|rgb] [--json]");
        w("  gif       [--region x,y,w,h] [--duration <s>] [--fps <n>] [--json]");
        w("  gif stop  [<recording-id>]");
        w("  video     [--region x,y,w,h] [--format mp4|apng] [--duration <s>]");
        w("  scroll    [--region x,y,w,h]");
        w("  history   list [--limit N] [--pinned] [--json]");
        w("  history   get <id> [--out -|<path>]");
        w("  history   open");
        w("  repeat");
        w("  open");
        w("  settings  get [key] | set <key> <value> | path");
        w("  status    [--json]");
        w("");
        w("global:   --help    --version");
        w("");
        w("exit: 0 ok · 1 error · 2 usage · 3 needs running app · 4 no result/cancelled · 5 ocr unavailable");
    }

    // ---------------------------------------------------------------------------------
    // 인자 파서 — verb 뒤 토큰을 옵션/위치 인자로 나눈다.
    //   · "--name" 은 valueOpts에 있으면 다음 토큰을 값으로 소비, 아니면 불리언 플래그.
    //   · "--out -" 처럼 값이 '-'/음수로 시작해도 값 옵션은 다음 토큰을 그대로 취한다(음수 좌표 대응).
    // ---------------------------------------------------------------------------------

    private sealed class Cli
    {
        private readonly Dictionary<string, string?> _opts = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _pos = new();

        public Cli(string[] tokens, params string[] valueOpts)
        {
            var vset = new HashSet<string>(valueOpts, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < tokens.Length; i++)
            {
                string t = tokens[i];
                if (t.Length > 2 && t[0] == '-' && t[1] == '-')
                {
                    string key = t.Substring(2);
                    if (vset.Contains(key))
                        _opts[key] = (i + 1 < tokens.Length) ? tokens[++i] : null;
                    else
                        _opts[key] = null;
                }
                else if (t.Length > 1 && t[0] == '-' && t != "-" && !(char.IsDigit(t[1])))
                {
                    _opts[t.Substring(1)] = null;   // 짧은 플래그(-h 등)
                }
                else
                {
                    _pos.Add(t);                    // 위치 인자('-' = stdout 센티넬 포함)
                }
            }
        }

        public bool Has(string name) => _opts.ContainsKey(name);
        public string? Get(string name) => _opts.TryGetValue(name, out var v) ? v : null;
        public IReadOnlyList<string> Positional => _pos;
    }

    /// <summary>파싱/사용법 오류. Run이 잡아 종료 코드 2로 변환한다.</summary>
    private sealed class UsageException : Exception
    {
        public UsageException(string message) : base(message) { }
    }

    // 상태 JSON은 라우터가 준 자유 페이로드를 그대로 직렬화한다.
    private static readonly JsonSerializerOptions JsonCompact = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions JsonPretty = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
