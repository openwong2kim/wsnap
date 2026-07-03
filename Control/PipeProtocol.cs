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
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Wsnap.Control;

// =====================================================================================
// 파이프 와이어 프로토콜 — NDJSON(개행 구분 JSON). 한 줄 = 한 메시지, UTF-8(BOM 없음).
// PipeClientRouter(외부 프로세스)와 PipeServer(상주) 사이의 유일한 직렬화 지점이다.
//   요청:  { id, cmd(dotted id), args?, returnContent, clientId? }
//   응답:  { id, ok, result?, error? }   error = { code, message }
// 중요: 외부(CLI/MCP)는 이 와이어 JSON을 직접 보지 않는다. PipeClientRouter가 응답을 다시
// CommandResult로 되돌려 주므로, 여기 result 포맷은 (같은 파일 안의) 직렬화/역직렬화가
// 서로 역함수이기만 하면 되는 순수 내부 전송 포맷이다.
// =====================================================================================

/// <summary>파이프 요청 한 줄. <see cref="Cmd"/>는 CommandCatalog의 dotted id.</summary>
public sealed record PipeRequest
{
    /// <summary>요청/응답 상관용 id(호출자가 생성, 응답이 그대로 되돌려줌).</summary>
    public string Id { get; init; } = "";

    /// <summary>정규 명령 id(dotted lowercase, 예: "capture.region").</summary>
    public string Cmd { get; init; } = "";

    /// <summary>명령 인자(JSON 오브젝트). 없으면 null.</summary>
    public JsonElement? Args { get; init; }

    /// <summary>픽셀/OCR 텍스트 등 콘텐츠 반환 허용 여부(생략 시 true).</summary>
    public bool ReturnContent { get; init; } = true;

    /// <summary>호출 클라이언트 식별(감사/레이트리밋용, 선택).</summary>
    public string? ClientId { get; init; }
}

/// <summary>파이프 응답의 오류 페이로드.</summary>
public sealed record PipeError
{
    /// <summary>기계 판독용 코드(busy|resident_required|unknown_cmd|denied|internal ...).</summary>
    public string Code { get; init; } = "";

    /// <summary>사람이 읽는 메시지.</summary>
    public string Message { get; init; } = "";
}

/// <summary>파이프 응답 한 줄. 성공이면 <see cref="Result"/>, 실패면 <see cref="Error"/>.</summary>
public sealed record PipeResponse
{
    /// <summary>대응하는 요청의 <see cref="PipeRequest.Id"/>를 그대로 반향.</summary>
    public string Id { get; init; } = "";

    /// <summary>성공 여부.</summary>
    public bool Ok { get; init; }

    /// <summary>성공 시 평평한 결과 JSON(<see cref="PipeProtocol.SerializeResult"/> 형식).</summary>
    public JsonElement? Result { get; init; }

    /// <summary>실패 시 오류.</summary>
    public PipeError? Error { get; init; }
}

/// <summary>
/// 파이프 메시지의 직렬화/역직렬화와 NDJSON 프레이밍(한 줄 = 한 메시지). 상태 없는 static 유틸.
/// <see cref="WsnapCommand"/> ↔ <see cref="PipeRequest"/>, <see cref="CommandResult"/> ↔ <see cref="PipeResponse"/> 변환을 담당.
/// </summary>
public static class PipeProtocol
{
    /// <summary>모든 직렬화에 쓰는 공유 옵션. 양끝이 같은 옵션을 써서 camelCase 대칭을 보장.</summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // ---------------- 상관 id ----------------

    /// <summary>새 요청 상관 id(하이픈 없는 GUID).</summary>
    public static string NewId() => Guid.NewGuid().ToString("n");

    // ---------------- 요청: WsnapCommand ↔ PipeRequest ----------------

    /// <summary>명령을 와이어 요청으로. <see cref="CommandKind"/>는 <see cref="CommandCatalog.ToId"/>로 dotted id 변환.</summary>
    public static PipeRequest BuildRequest(WsnapCommand command, string id) => new()
    {
        Id = id,
        Cmd = CommandCatalog.ToId(command.Kind),
        Args = command.Args,
        ReturnContent = command.ReturnContent,
        ClientId = command.ClientId,
    };

    /// <summary>
    /// 와이어 요청을 <see cref="CommandSource.Pipe"/> 명령으로. 알 수 없는 id면 false(호출자가 unknown_cmd 처리).
    /// dotted id ↔ <see cref="CommandKind"/> 해석은 단일 진실원 <see cref="CommandCatalog"/>에 위임한다.
    /// </summary>
    public static bool TryBuildCommand(PipeRequest request, out WsnapCommand command)
    {
        if (!CommandCatalog.TryParseId(request.Cmd, out var kind))
        {
            command = new WsnapCommand(default);
            return false;
        }
        command = new WsnapCommand(kind, request.Args, CommandSource.Pipe, request.ReturnContent, request.ClientId);
        return true;
    }

    // ---------------- 응답: CommandResult ↔ PipeResponse ----------------

    /// <summary>실행 결과를 와이어 응답으로. 실패는 error 오브젝트, 성공은 평평한 result로.</summary>
    public static PipeResponse BuildResponse(string id, CommandResult result)
    {
        if (!result.Ok)
            return ErrorResponse(id, result.ErrorCode ?? "internal", result.Error ?? "command failed");
        return new PipeResponse { Id = id, Ok = true, Result = SerializeResult(result) };
    }

    /// <summary>오류 응답을 만든다.</summary>
    public static PipeResponse ErrorResponse(string id, string code, string message) =>
        new() { Id = id, Ok = false, Error = new PipeError { Code = code, Message = message } };

    /// <summary>와이어 응답을 <see cref="CommandResult"/>로 되돌린다(클라이언트 측).</summary>
    public static CommandResult ToResult(PipeResponse response)
    {
        if (!response.Ok)
            return CommandResult.Fail(response.Error?.Code ?? "internal", response.Error?.Message ?? "unknown error");
        return response.Result is { } element ? DeserializeResult(element) : CommandResult.Ack();
    }

    // ---------------- 문자열 직렬화(한 줄) ----------------

    /// <summary>요청을 한 줄 JSON으로.</summary>
    public static string SerializeRequest(PipeRequest request) => JsonSerializer.Serialize(request, Options);

    /// <summary>응답을 한 줄 JSON으로.</summary>
    public static string SerializeResponse(PipeResponse response) => JsonSerializer.Serialize(response, Options);

    /// <summary>한 줄 JSON을 요청으로. 잘못된 JSON은 <see cref="JsonException"/>을 던진다.</summary>
    public static PipeRequest? ParseRequest(string line) => JsonSerializer.Deserialize<PipeRequest>(line, Options);

    /// <summary>한 줄 JSON을 응답으로. 잘못된 JSON은 <see cref="JsonException"/>을 던진다.</summary>
    public static PipeResponse? ParseResponse(string line) => JsonSerializer.Deserialize<PipeResponse>(line, Options);

    // ---------------- NDJSON 스트림 프레이밍 ----------------

    /// <summary>한 연결에서 재사용할 라인 리더(UTF-8, BOM 무시, 파이프는 열어 둠).</summary>
    public static StreamReader CreateReader(Stream stream) =>
        new(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

    /// <summary>다음 메시지(한 줄)를 읽는다. EOF면 null.</summary>
    public static ValueTask<string?> ReadMessageAsync(StreamReader reader, CancellationToken ct) =>
        reader.ReadLineAsync(ct);

    /// <summary>메시지 한 줄을 쓴다(UTF-8 + LF, 즉시 flush). 컴팩트 JSON이라 본문에 개행이 없다.</summary>
    public static async Task WriteMessageAsync(Stream stream, string json, CancellationToken ct)
    {
        var payload = Utf8NoBom.GetBytes(json + "\n");
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    // ---------------- 결과 평탄화(내부 전송 포맷) ----------------

    /// <summary>성공 결과를 평평한 result JSON으로. 기본값 필드는 생략해 와이어를 간결하게 유지.</summary>
    public static JsonElement SerializeResult(CommandResult r)
    {
        var dto = new ResultDto
        {
            Type = r.Type.ToString(),
            Path = r.Path,
            Width = r.Width == 0 ? null : r.Width,
            Height = r.Height == 0 ? null : r.Height,
            Bytes = r.Bytes == 0 ? null : r.Bytes,
            Copied = r.Copied ? true : null,
            // 콘텐츠 게이팅 신호: Path/Width/Height는 redaction 후에도 살아남으므로(경로는 콘텐츠 아님)
            // 이 플래그를 반드시 보존해야 클라(MCP/CLI)가 이미지·텍스트 방출을 억제한다. 유실 시 게이팅 우회.
            ContentRedacted = r.ContentRedacted ? true : null,
            App = r.App,
            Title = r.Title,
            Text = r.Text,
            Lang = r.Lang,
            Empty = r.Empty ? true : null,
            Hex = r.Hex,
            R = r.R == 0 ? null : r.R,
            G = r.G == 0 ? null : r.G,
            B = r.B == 0 ? null : r.B,
            X = r.X == 0 ? null : r.X,
            Y = r.Y == 0 ? null : r.Y,
            RecordingId = r.RecordingId,
            Frames = r.Frames == 0 ? null : r.Frames,
            Seconds = r.Seconds == 0 ? null : r.Seconds,
            History = r.History?.Select(h => new HistoryDto(h.Path, h.When, h.Pinned)).ToArray(),
            // Payload는 자유 페이로드라 파이프 경계에선 JSON으로만 왕복한다(수신 측에서 JsonElement로 노출).
            Payload = SerializePayload(r.Payload),
        };
        return JsonSerializer.SerializeToElement(dto, Options);
    }

    /// <summary>평평한 result JSON을 성공 <see cref="CommandResult"/>로 되돌린다.</summary>
    public static CommandResult DeserializeResult(JsonElement element)
    {
        var dto = element.Deserialize<ResultDto>(Options) ?? new ResultDto();
        var type = Enum.TryParse<ResultType>(dto.Type, ignoreCase: true, out var t) ? t : ResultType.Ack;
        return new CommandResult
        {
            Ok = true,
            Type = type,
            Path = dto.Path,
            Width = dto.Width ?? 0,
            Height = dto.Height ?? 0,
            Bytes = dto.Bytes ?? 0,
            Copied = dto.Copied ?? false,
            ContentRedacted = dto.ContentRedacted ?? false,
            App = dto.App,
            Title = dto.Title,
            Text = dto.Text,
            Lang = dto.Lang,
            Empty = dto.Empty ?? false,
            Hex = dto.Hex,
            R = dto.R ?? 0,
            G = dto.G ?? 0,
            B = dto.B ?? 0,
            X = dto.X ?? 0,
            Y = dto.Y ?? 0,
            RecordingId = dto.RecordingId,
            Frames = dto.Frames ?? 0,
            Seconds = dto.Seconds ?? 0,
            History = dto.History?.Select(h => new HistoryItem(h.Path, h.When, h.Pinned)).ToArray(),
            Payload = dto.Payload,
        };
    }

    // ---------------- CommandResult 와이어 DTO(평평, 기본값 생략) ----------------

    private sealed record ResultDto
    {
        public string Type { get; init; } = nameof(ResultType.Ack);
        public string? Path { get; init; }
        public int? Width { get; init; }
        public int? Height { get; init; }
        public long? Bytes { get; init; }
        public bool? Copied { get; init; }
        public bool? ContentRedacted { get; init; }
        public string? App { get; init; }
        public string? Title { get; init; }
        public string? Text { get; init; }
        public string? Lang { get; init; }
        public bool? Empty { get; init; }
        public string? Hex { get; init; }
        public int? R { get; init; }
        public int? G { get; init; }
        public int? B { get; init; }
        public int? X { get; init; }
        public int? Y { get; init; }
        public string? RecordingId { get; init; }
        public int? Frames { get; init; }
        public double? Seconds { get; init; }
        public HistoryDto[]? History { get; init; }
        public JsonElement? Payload { get; init; }
    }

    private sealed record HistoryDto(string Path, DateTime When, bool Pinned);

    /// <summary>
    /// 자유 <see cref="CommandResult.Payload"/>를 파이프용 JSON으로. 이미 JsonElement면 그대로 통과시키고,
    /// 그 외에는 런타임 타입으로 직렬화한다(선언 타입 object라 다형성 손실을 피하려 명시적 Type 오버로드 사용).
    /// </summary>
    private static JsonElement? SerializePayload(object? payload)
    {
        if (payload is null) return null;
        if (payload is JsonElement element) return element;
        return JsonSerializer.SerializeToElement(payload, payload.GetType(), Options);
    }
}
