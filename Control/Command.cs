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
using System.Threading;
using System.Threading.Tasks;

namespace Wsnap.Control;

// =====================================================================================
// wsnap 통합 제어 계층 — 계약(contract) 타입.
// CLI(wsnap <verb>) / MCP(wsnap mcp) / 핫키 / 트레이 네 진입점이 이 타입들을 공유한다.
// 이 파일은 "단일 진실원"이며, CommandRouter/ControlGate/McpStdioServer/CliRouter/PipeServer가
// 전부 여기 정의된 인터페이스·타입에만 의존해 서로 몰라도 컴파일된다.
// 실제 헤드리스 실행은 CaptureCore(static), 대화형/녹화·UI 부수효과는 IResidentHost가 담당.
// =====================================================================================

/// <summary>모든 wsnap 명령의 종류. 와이어 정규 id(dotted lowercase)는 CommandCatalog가 매핑한다.</summary>
public enum CommandKind
{
    // ---- 헤드리스 가능(좌표/경로 완결, 오버레이·상주 불요) ----
    CaptureRegion,        // args: x,y,width,height
    CaptureFullScreen,    // args: monitor? ("cursor"|"primary"|index)
    CaptureWindow,        // args: 없음(전경 창)
    OcrRegion,            // args: x,y,width,height, lang?
    OcrImage,             // args: path, lang?
    OcrLast,              // args: lang?
    ColorAt,              // args: x,y
    HistoryList,          // args: limit?, pinnedOnly?
    HistoryGet,           // args: id|path
    OpenFolder,           // 저장 폴더 열기(explorer)

    // ---- 대화형(오버레이/사용자 상호작용 필수 = 상주 전용) ----
    CaptureInteractive,   // 드래그 영역 캡처
    OcrInteractive,       // 드래그 후 OCR
    ColorPick,            // 대화형 스포이드
    CaptureRepeat,        // 마지막 영역 재캡처(상주 상태 필요)
    CaptureDelayed,       // args: seconds — 카운트다운 후 대화형

    // ---- 녹화(상주 전용; 프레임 수집은 헤드리스지만 DispatcherTimer=STA) ----
    Gif,                  // args: x,y,width,height, duration_s?, fps?, mode?("fixed"|"until_stop")
    GifStop,              // args: recording_id? — 진행 중 녹화 정지
    Video,                // args: x,y,width,height, format?("mp4"|"apng"), duration_s?
    Scroll,               // args: x,y,width,height

    // ---- 상태/창(상주 전용 UI) ----
    ShowHistory, ClearThumbnails, OpenSettings,

    // ---- 제어/조회 ----
    SettingsGet,          // args: keys?
    SettingsSet,          // args: key, value  (상주 위임·감사 대상)
    Ping, Status, ListCommands,

    // ---- 사용자 정의(핫키 전용, opt-in) ----
    Shell                 // args: cmd, args? — {path} 치환. 파이프/MCP 비노출
}

/// <summary>명령의 출처. ControlGate가 신뢰 등급(Hotkey/Tray=사용자 물리조작 최고신뢰)을 판단한다.</summary>
public enum CommandSource { Internal, Hotkey, Tray, Cli, Mcp, Pipe }

/// <summary>결과 페이로드의 형태(소비자가 분기용으로 사용).</summary>
public enum ResultType { Ack, File, Text, Color, History, Settings, CommandList, Recording, Status }

/// <summary>히스토리 항목(list_history / history list).</summary>
public readonly record struct HistoryItem(string Path, DateTime When, bool Pinned);

/// <summary>
/// 진입점 중립 명령. <paramref name="Args"/>는 JSON 오브젝트(없으면 null). <see cref="ArgReader"/>로 읽는다.
/// <paramref name="ReturnContent"/>는 픽셀/OCR 텍스트를 호출자에 반환해도 되는지(ControlGate 2등급).
/// </summary>
public sealed record WsnapCommand(
    CommandKind Kind,
    JsonElement? Args = null,
    CommandSource Source = CommandSource.Internal,
    bool ReturnContent = true,
    string? ClientId = null);

/// <summary>
/// 모든 명령의 결과. 성공/실패 + 타입별 페이로드를 한 레코드에 담는다(각 서버가 자기 와이어 포맷으로 변환).
/// 팩토리로만 만들도록 유도해 일관성을 지킨다.
/// </summary>
public sealed record CommandResult
{
    public bool Ok { get; init; }
    public ResultType Type { get; init; }
    public string? ErrorCode { get; init; }   // busy|no_region|ocr_unavailable|denied|resident_required|unknown_cmd|not_found|internal
    public string? Error { get; init; }        // 사람이 읽는 메시지

    // 파일/캡처
    public string? Path { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long Bytes { get; init; }
    public bool Copied { get; init; }

    /// <summary>True when ControlGate denied returning captured content (pixels / OCR text / color)
    /// to an external caller: Router blanks Text/Hex/RGB and servers must not emit image bytes.
    /// Path/Width/Height survive (the file is saved locally; a path is not content).</summary>
    public bool ContentRedacted { get; init; }
    public string? App { get; init; }          // 캡처 시 전경 앱/제목(있으면)
    public string? Title { get; init; }

    // OCR
    public string? Text { get; init; }
    public string? Lang { get; init; }
    public bool Empty { get; init; }

    // 색
    public string? Hex { get; init; }
    public int R { get; init; }
    public int G { get; init; }
    public int B { get; init; }
    public int X { get; init; }
    public int Y { get; init; }

    // 녹화
    public string? RecordingId { get; init; }
    public int Frames { get; init; }
    public double Seconds { get; init; }

    // 컬렉션/스냅샷
    public IReadOnlyList<HistoryItem>? History { get; init; }
    public object? Payload { get; init; }      // Settings/CommandList 등 자유 페이로드

    // ---- 팩토리 ----
    public static CommandResult Fail(string code, string message) =>
        new() { Ok = false, ErrorCode = code, Error = message };

    public static CommandResult FileSaved(string path, int w, int h, string? app = null, string? title = null,
                                          long bytes = 0, bool copied = false) =>
        new() { Ok = true, Type = ResultType.File, Path = path, Width = w, Height = h,
                App = app, Title = title, Bytes = bytes, Copied = copied };

    public static CommandResult OcrText(string text, string lang) =>
        new() { Ok = true, Type = ResultType.Text, Text = text, Lang = lang, Empty = text.Length == 0 };

    public static CommandResult ColorResult(string hex, int r, int g, int b, int x, int y) =>
        new() { Ok = true, Type = ResultType.Color, Hex = hex, R = r, G = g, B = b, X = x, Y = y };

    public static CommandResult HistoryResult(IReadOnlyList<HistoryItem> items) =>
        new() { Ok = true, Type = ResultType.History, History = items };

    public static CommandResult RecordingStarted(string id) =>
        new() { Ok = true, Type = ResultType.Recording, RecordingId = id };

    public static CommandResult RecordingSaved(string path, int w, int h, int frames, double seconds) =>
        new() { Ok = true, Type = ResultType.Recording, Path = path, Width = w, Height = h,
                Frames = frames, Seconds = seconds };

    public static CommandResult Ack(ResultType type = ResultType.Ack, object? payload = null) =>
        new() { Ok = true, Type = type, Payload = payload };
}

/// <summary>명령별 실행 특성. Router가 헤드리스/대화형/부수효과/콘텐츠반환을 이 표로 판단한다.</summary>
public static class CommandTraits
{
    /// <summary>오버레이·상주 상태 없이 좌표/경로만으로 실행 가능한가.</summary>
    public static bool IsHeadless(CommandKind k) => k is
        CommandKind.CaptureRegion or CommandKind.CaptureFullScreen or CommandKind.CaptureWindow or
        CommandKind.OcrRegion or CommandKind.OcrImage or CommandKind.OcrLast or CommandKind.ColorAt or
        CommandKind.HistoryList or CommandKind.HistoryGet or CommandKind.OpenFolder or
        CommandKind.Ping or CommandKind.Status or CommandKind.ListCommands or CommandKind.SettingsGet;

    /// <summary>상주에서 실행될 때 캡처 결과에 UI 부수효과(썸네일·자동복사·트림)를 붙일 명령.</summary>
    public static bool HasPresentation(CommandKind k) => k is
        CommandKind.CaptureRegion or CommandKind.CaptureFullScreen or CommandKind.CaptureWindow;

    /// <summary>결과가 화면 픽셀/OCR 텍스트/색을 호출자에게 되돌려주는가(ControlGate 2등급 콘텐츠 반환).
    /// CaptureRepeat는 LastRegion을 다시 캡처해 픽셀을 되돌려주므로 반드시 게이트 대상에 포함한다.</summary>
    public static bool ReturnsContent(CommandKind k) => k is
        CommandKind.CaptureRegion or CommandKind.CaptureFullScreen or CommandKind.CaptureWindow or
        CommandKind.CaptureRepeat or
        CommandKind.OcrRegion or CommandKind.OcrImage or CommandKind.OcrLast or
        CommandKind.ColorAt or CommandKind.HistoryGet or CommandKind.Gif or CommandKind.Video;

    /// <summary>연속 프레임 녹화(더 민감 — 항상 가시 배지·강한 게이트).</summary>
    public static bool IsRecording(CommandKind k) => k is
        CommandKind.Gif or CommandKind.Video or CommandKind.Scroll;
}

// =====================================================================================
// 인터페이스 — 병렬 구현의 이음매. 각 컴포넌트는 이 인터페이스 뒤에서 서로를 부른다.
// =====================================================================================

/// <summary>명령 버스. 진입점(MCP/CLI/Pipe)이 이걸 통해 실행한다. 구현: CommandRouter(로컬), PipeClientRouter(위임).</summary>
public interface ICommandRouter
{
    Task<CommandResult> ExecuteAsync(WsnapCommand cmd, CancellationToken ct = default);
}

/// <summary>보안·동의·가시성·감사 정책을 픽셀 접근 직전 한 곳에서 강제. 구현: ControlGate.</summary>
public interface IControlGate
{
    /// <summary>이 명령을 허용할지 판정(마스터 스위치·동의·레이트리밋·콘텐츠등급).</summary>
    GateDecision Evaluate(WsnapCommand cmd);

    /// <summary>실행 후 감사 로그 기록 + 필요 시 가시 신호(셔터/토스트/트레이 배지).</summary>
    void Audit(WsnapCommand cmd, CommandResult result, GateDecision decision);
}

/// <summary>ControlGate의 판정 결과.</summary>
public readonly record struct GateDecision(
    bool Allowed,
    string? DenyCode = null,
    string? DenyReason = null,
    bool RequireVisibleSignal = false,
    bool AllowReturnContent = true)
{
    public static GateDecision Allow(bool requireSignal = false, bool allowContent = true) =>
        new(true, null, null, requireSignal, allowContent);
    public static GateDecision Deny(string code, string reason) =>
        new(false, code, reason, false, false);
}

/// <summary>
/// 상주(트레이) 인스턴스가 제공하는 UI/대화형 능력. Router가 대화형·녹화·부수효과를 여기로 위임한다.
/// 헤드리스 CLI(상주 없음)에서는 null이며, Router가 "resident_required"로 처리한다. 구현: App.
/// </summary>
public interface IResidentHost
{
    bool IsResident { get; }

    /// <summary>헤드리스 캡처가 상주에서 실행됐을 때 썸네일·자동복사·메모리트림 등 기존 UX를 붙인다.</summary>
    void PresentCapture(string path);

    /// <summary>대화형/녹화 명령(오버레이 드래그, GIF, 비디오, 스크롤, 창 조작)을 실행하고 완료를 기다린다.</summary>
    Task<CommandResult> ExecuteInteractiveAsync(WsnapCommand cmd, CancellationToken ct);
}

// =====================================================================================
// Args 헬퍼 — JsonElement 오브젝트에서 안전하게 값을 뽑고, 결과를 빌드한다.
// =====================================================================================

/// <summary>WsnapCommand.Args(JsonElement 오브젝트)에서 타입 안전하게 값을 읽는다.</summary>
public static class ArgReader
{
    public static bool TryProp(JsonElement? args, string name, out JsonElement value)
    {
        value = default;
        if (args is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v))
        { value = v; return true; }
        return false;
    }

    public static int Int(JsonElement? args, string name, int def = 0)
    {
        if (!TryProp(args, name, out var v)) return def;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(v.GetString(), out var i) => i,
            _ => def
        };
    }

    public static double Double(JsonElement? args, string name, double def = 0)
    {
        if (!TryProp(args, name, out var v)) return def;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(v.GetString(), out var d) => d,
            _ => def
        };
    }

    public static string? Str(JsonElement? args, string name, string? def = null)
    {
        if (!TryProp(args, name, out var v)) return def;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
    }

    public static bool Bool(JsonElement? args, string name, bool def = false)
    {
        if (!TryProp(args, name, out var v)) return def;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            _ => def
        };
    }

    public static bool HasProp(JsonElement? args, string name) => TryProp(args, name, out _);

    /// <summary>(x,y,width,height) 사각형을 읽는다. width/height는 w/h 별칭도 허용.</summary>
    public static (int x, int y, int w, int h) Rect(JsonElement? args)
    {
        int x = Int(args, "x");
        int y = Int(args, "y");
        int w = HasProp(args, "width") ? Int(args, "width") : Int(args, "w");
        int h = HasProp(args, "height") ? Int(args, "height") : Int(args, "h");
        return (x, y, w, h);
    }

    /// <summary>키-값 쌍으로 JSON 오브젝트 Args를 만든다(테스트/내부 호출용).</summary>
    public static JsonElement Obj(IReadOnlyDictionary<string, object?> kv)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(kv));
        return doc.RootElement.Clone();
    }
}
