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
using System.Text.Json;

namespace Wsnap.Control;

/// <summary>
/// 한 명령의 식별자 묶음: 와이어 정규 id(dotted lowercase), MCP snake 툴명(비노출이면 null),
/// 사람이 읽는 CLI usage 힌트, 한 줄 설명. 헤드리스/대화형/녹화 분류는 <see cref="CommandTraits"/>에서 파생한다.
/// </summary>
public readonly record struct CommandInfo(
    CommandKind Kind,
    string Id,
    string? McpTool,
    string Cli,
    string Description)
{
    /// <summary>오버레이·상주 없이 좌표/경로만으로 실행 가능한가.</summary>
    public bool Headless => CommandTraits.IsHeadless(Kind);

    /// <summary>연속 프레임 녹화(GIF/비디오/스크롤)인가.</summary>
    public bool Recording => CommandTraits.IsRecording(Kind);

    /// <summary>상주 UI/사용자 상호작용이 필요한가(헤드리스도 녹화도 아님 = 대화형/창 명령).</summary>
    public bool Interactive => !Headless && !Recording;
}

/// <summary>
/// 명령 식별자의 단일 진실원. CLI 파서, MCP 툴 디스패처, 파이프 프로토콜, 핫키 <c>Command</c> 필드가
/// 전부 여기서 dotted id ↔ <see cref="CommandKind"/> ↔ MCP 툴명을 해석한다.
/// 새 명령을 추가할 때는 <see cref="CommandKind"/>에 값을 넣고 <see cref="Entries"/>에 한 줄만 더하면 된다.
/// </summary>
public static class CommandCatalog
{
    // enum 선언 순서와 동일하게 유지한다(가독성 + Describe 출력 순서).
    private static readonly CommandInfo[] Entries =
    {
        // ---- 헤드리스 ----
        new(CommandKind.CaptureRegion,     "capture.region",     "capture_region",     "capture region --x --y --w --h",                    "Capture a fixed screen rectangle to a PNG file."),
        new(CommandKind.CaptureFullScreen, "capture.fullscreen", "capture_fullscreen", "capture full [--monitor cursor|primary|N]",         "Capture a whole monitor (cursor, primary, or index)."),
        new(CommandKind.CaptureWindow,     "capture.window",     "capture_window",     "capture window",                                    "Capture the current foreground window."),
        new(CommandKind.OcrRegion,         "ocr.region",         "ocr_region",         "ocr region --x --y --w --h [--lang]",               "Recognize text inside a screen rectangle."),
        new(CommandKind.OcrImage,          "ocr.image",          "ocr_image",          "ocr image --path FILE [--lang]",                    "Recognize text in an existing image file."),
        new(CommandKind.OcrLast,           "ocr.last",           "ocr_last_capture",   "ocr last [--lang]",                                 "Recognize text in the most recent capture."),
        new(CommandKind.ColorAt,           "color.at",           "pick_color",         "color at --x --y",                                  "Read the pixel color at a screen coordinate."),
        new(CommandKind.HistoryList,       "history.list",       "list_history",       "history list [--limit N] [--pinned]",               "List recent captures (optionally pinned only)."),
        new(CommandKind.HistoryGet,        "history.get",        "get_capture",        "history get (--id N | --path FILE)",                "Resolve a capture by index, filename, or path."),
        new(CommandKind.OpenFolder,        "folder.open",        null,                 "folder open",                                       "Open the save folder in Explorer."),

        // ---- 대화형(상주 전용) ----
        new(CommandKind.CaptureInteractive, "capture.interactive", "capture_interactive", "capture (interactive drag-select)",             "Drag-select a screen region and capture it."),
        new(CommandKind.OcrInteractive,     "ocr.interactive",     null,                  "ocr (interactive drag-select)",                 "Drag-select a screen region and OCR it."),
        new(CommandKind.ColorPick,          "color.pick",          null,                  "color pick (interactive eyedropper)",           "Pick a color interactively with the eyedropper."),
        new(CommandKind.CaptureRepeat,      "capture.repeat",      null,                  "capture repeat",                                "Re-capture the last selected region."),
        new(CommandKind.CaptureDelayed,     "capture.delayed",     null,                  "capture delayed --seconds N",                   "Count down, then start an interactive capture."),

        // ---- 녹화(상주 전용) ----
        new(CommandKind.Gif,     "record.gif",     "record_gif",      "record gif --x --y --w --h [--duration S] [--fps N]",       "Record a screen region to an animated GIF."),
        new(CommandKind.GifStop, "record.stop",    "stop_recording",  "record stop [--id RECORDING_ID]",                           "Stop the in-progress recording and save it."),
        new(CommandKind.Video,   "record.video",   null,              "record video --x --y --w --h [--format mp4|apng] [--duration S]", "Record a screen region to MP4/APNG video."),
        new(CommandKind.Scroll,  "capture.scroll", null,              "capture scroll --x --y --w --h",                            "Capture a scrolling window into one tall image."),

        // ---- 상태/창(상주 전용 UI) ----
        new(CommandKind.ShowHistory,     "window.history",   null, "window history",   "Open the capture history window."),
        new(CommandKind.ClearThumbnails, "thumbnails.clear", null, "thumbnails clear",  "Dismiss all floating thumbnails."),
        new(CommandKind.OpenSettings,    "window.settings",  null, "window settings",   "Open the settings window."),

        // ---- 제어/조회 ----
        new(CommandKind.SettingsGet,  "settings.get",  null, "settings get [--keys ...]",     "Read current settings (safe subset)."),
        new(CommandKind.SettingsSet,  "settings.set",  null, "settings set --key K --value V", "Change a setting (audited, resident only)."),
        new(CommandKind.Ping,         "ping",          null, "ping",                          "Liveness check; replies with an ack."),
        new(CommandKind.Status,       "status",        null, "status",                        "Report whether the resident tray app is running."),
        new(CommandKind.ListCommands, "commands.list", null, "commands",                      "Describe every available command (this catalog)."),

        // ---- 사용자 정의(핫키 전용, MCP/파이프 비노출) ----
        new(CommandKind.Shell, "shell", null, "(hotkey only) run a user-defined shell command", "Run a user-defined shell command with {path} substitution (hotkey only)."),
    };

    // 조회 인덱스. dotted id·MCP 툴명은 대소문자 무시(견고한 파싱).
    private static readonly Dictionary<string, CommandKind> ById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CommandKind> ByMcp = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<CommandKind, CommandInfo> ByKind = new();

    static CommandCatalog()
    {
        foreach (var e in Entries)
        {
            ById[e.Id] = e.Kind;
            ByKind[e.Kind] = e;
            if (e.McpTool is { } tool) ByMcp[tool] = e.Kind;
        }
    }

    /// <summary>카탈로그의 모든 항목(선언 순서).</summary>
    public static IReadOnlyList<CommandInfo> All => Entries;

    /// <summary>dotted id(예 "capture.region") 또는 핫키 <c>Command</c> 필드를 <see cref="CommandKind"/>로 해석.</summary>
    public static bool TryParseId(string dotted, out CommandKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(dotted)) return false;
        return ById.TryGetValue(dotted.Trim(), out kind);
    }

    /// <summary>MCP snake 툴명(예 "capture_region")을 <see cref="CommandKind"/>로 해석.</summary>
    public static bool TryParseMcpTool(string toolName, out CommandKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(toolName)) return false;
        return ByMcp.TryGetValue(toolName.Trim(), out kind);
    }

    /// <summary><see cref="CommandKind"/>의 정규 dotted id.</summary>
    public static string ToId(CommandKind kind) =>
        ByKind.TryGetValue(kind, out var e) ? e.Id : kind.ToString();

    /// <summary><see cref="CommandKind"/>의 MCP 툴명(비노출이면 null).</summary>
    public static string? ToMcpTool(CommandKind kind) =>
        ByKind.TryGetValue(kind, out var e) ? e.McpTool : null;

    /// <summary><see cref="CommandKind"/>의 전체 식별자 정보(없으면 null).</summary>
    public static CommandInfo? Info(CommandKind kind) =>
        ByKind.TryGetValue(kind, out var e) ? e : null;

    /// <summary>dotted id 또는 MCP 툴명 어느 쪽이든 받아 <see cref="WsnapCommand"/>를 만든다. 못 찾으면 null.</summary>
    public static WsnapCommand? TryBuild(string dottedIdOrToolName, JsonElement? args = null,
                                         CommandSource source = CommandSource.Internal, bool returnContent = true)
    {
        if (string.IsNullOrWhiteSpace(dottedIdOrToolName)) return null;
        var key = dottedIdOrToolName.Trim();
        if (ById.TryGetValue(key, out var kind) || ByMcp.TryGetValue(key, out kind))
            return new WsnapCommand(kind, args, source, returnContent);
        return null;
    }

    /// <summary><see cref="TryBuild"/>의 예외 던지는 버전(알 수 없는 id/툴명이면 <see cref="ArgumentException"/>).</summary>
    public static WsnapCommand Parse(string dottedIdOrToolName, JsonElement? args = null,
                                     CommandSource source = CommandSource.Internal, bool returnContent = true) =>
        TryBuild(dottedIdOrToolName, args, source, returnContent)
        ?? throw new ArgumentException($"unknown wsnap command id or MCP tool: '{dottedIdOrToolName}'",
                                       nameof(dottedIdOrToolName));

    /// <summary>
    /// ListCommands / commands.list 결과용 직렬화 가능 스냅샷. 각 항목은
    /// {id, mcpTool, cli, headless, interactive, recording, description}. System.Text.Json으로 그대로 직렬화된다
    /// (요소가 구체 익명 타입이라 다형성 손실 없음).
    /// </summary>
    public static object Describe() =>
        Array.ConvertAll(Entries, e => new
        {
            id = e.Id,
            mcpTool = e.McpTool,
            cli = e.Cli,
            headless = e.Headless,
            interactive = e.Interactive,
            recording = e.Recording,
            description = e.Description,
        });
}
