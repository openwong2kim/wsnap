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
using System.Threading;
using System.Threading.Tasks;
using Wsnap;

namespace Wsnap.Control;

/// <summary>
/// 로컬 명령 버스 — <see cref="ICommandRouter"/>의 기본 구현. 모든 진입점(핫키/트레이/CLI/MCP/파이프)이
/// 여기로 <see cref="WsnapCommand"/>를 흘려보낸다. 실행 순서는 항상 동일하다:
/// <list type="number">
///   <item>게이트 판정(<see cref="IControlGate.Evaluate"/>) — 거부 시 즉시 감사 후 실패 반환.</item>
///   <item>헤드리스면 <see cref="CaptureCore"/>로 직접 실행(상주라면 캡처에 UX 부수효과를 붙임).</item>
///   <item>대화형/녹화/UI면 <see cref="IResidentHost"/>로 위임(상주 없으면 resident_required).</item>
///   <item>게이트가 콘텐츠 반환을 거부했으면 픽셀/텍스트/색을 마스킹한 뒤, 결과를 감사(<see cref="IControlGate.Audit"/>)하고 반환.</item>
/// </list>
/// 상주(트레이) 프로세스에서는 <paramref name="host"/>가 non-null이고, 헤드리스 CLI에서는 null이다.
/// </summary>
public sealed class CommandRouter : ICommandRouter
{
    private readonly IControlGate _gate;
    private readonly IResidentHost? _host;

    /// <param name="gate">보안·동의·가시성·감사 정책. 필수.</param>
    /// <param name="host">상주 UI/대화형 능력. 헤드리스 CLI에서는 null.</param>
    public CommandRouter(IControlGate gate, IResidentHost? host = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _host = host;
    }

    /// <inheritdoc/>
    public async Task<CommandResult> ExecuteAsync(WsnapCommand cmd, CancellationToken ct = default)
    {
        // 1) 게이트: 픽셀 접근 이전 단일 강제 지점.
        var decision = _gate.Evaluate(cmd);
        if (!decision.Allowed)
        {
            var denied = CommandResult.Fail(decision.DenyCode ?? "denied", decision.DenyReason ?? "denied");
            _gate.Audit(cmd, denied, decision);
            return denied;
        }

        // 2) 실행. 모든 예외를 삼켜 감사가 항상 실행되도록 한다(감사 누락 = 정책 구멍).
        CommandResult result;
        try
        {
            if (CommandTraits.IsHeadless(cmd.Kind))
            {
                result = await ExecuteHeadless(cmd);

                // 상주에서 실행된 헤드리스 캡처에는 기존 UX(썸네일·자동복사·메모리트림)를 붙인다.
                if (result.Ok && CommandTraits.HasPresentation(cmd.Kind) &&
                    _host?.IsResident == true && result.Path != null)
                    _host.PresentCapture(result.Path);
            }
            else if (_host?.IsResident != true)
            {
                // 대화형/녹화/UI는 상주 트레이 앱이 있어야만 한다.
                result = CommandResult.Fail("resident_required",
                    "this command needs the running wsnap tray app");
            }
            else
            {
                result = await _host.ExecuteInteractiveAsync(cmd, ct);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("router", ex);
            result = CommandResult.Fail("internal", ex.Message);
        }

        // 3) 콘텐츠 게이팅: 게이트가 콘텐츠 반환을 거부했다면(2등급 미동의) 호출자에게 되돌아가는
        //    픽셀/OCR 텍스트/색값만 마스킹한다. Path/Width/Height/Bytes는 유지한다 — 파일은 로컬에
        //    저장됐고 경로는 콘텐츠가 아니다. 서버는 ContentRedacted를 보고 이미지 바이트 방출을 억제한다.
        //    Internal/Hotkey/Tray(사용자 물리조작)는 AllowReturnContent=true라 여기서 무영향.
        if (result.Ok && !decision.AllowReturnContent && CommandTraits.ReturnsContent(cmd.Kind))
            result = result with
            {
                Text = null, Hex = null, R = 0, G = 0, B = 0, Empty = false, ContentRedacted = true
            };

        // 4) 감사(+ 필요 시 가시 신호). 성공/실패/거부 모두 마스킹된 result로 기록된다.
        _gate.Audit(cmd, result, decision);
        return result;
    }

    /// <summary>
    /// 상주 상태 없이 <see cref="CaptureCore"/>만으로 완결되는 명령을 실행한다.
    /// args는 <see cref="ArgReader"/>로 뽑는다(x/y/width/height, lang, path 등).
    /// </summary>
    private static async Task<CommandResult> ExecuteHeadless(WsnapCommand cmd)
    {
        var a = cmd.Args;
        switch (cmd.Kind)
        {
            // ---- 캡처 ----
            case CommandKind.CaptureRegion:
            {
                var (x, y, w, h) = ArgReader.Rect(a);
                return CaptureCore.CaptureRegion(x, y, w, h);
            }
            case CommandKind.CaptureFullScreen:
                return CaptureCore.CaptureFullScreen(ArgReader.Str(a, "monitor"));
            case CommandKind.CaptureWindow:
                return CaptureCore.CaptureWindow();

            // ---- OCR ----
            case CommandKind.OcrRegion:
            {
                var (x, y, w, h) = ArgReader.Rect(a);
                return await CaptureCore.OcrRegion(x, y, w, h, ArgReader.Str(a, "lang"));
            }
            case CommandKind.OcrImage:
                return await CaptureCore.OcrImage(ArgReader.Str(a, "path"), ArgReader.Str(a, "lang"));
            case CommandKind.OcrLast:
                return await CaptureCore.OcrLast(ArgReader.Str(a, "lang"));

            // ---- 색 ----
            case CommandKind.ColorAt:
                return CaptureCore.ColorAt(ArgReader.Int(a, "x"), ArgReader.Int(a, "y"));

            // ---- 히스토리 ----
            case CommandKind.HistoryList:
            {
                int limit = ArgReader.Int(a, "limit", 30);
                bool pinnedOnly = ArgReader.Bool(a, "pinnedOnly") || ArgReader.Bool(a, "pinned_only");
                return CaptureCore.HistoryList(limit, pinnedOnly);
            }
            case CommandKind.HistoryGet:
                return CaptureCore.HistoryGet(ArgReader.Str(a, "id") ?? ArgReader.Str(a, "path"));

            // ---- 폴더 ----
            case CommandKind.OpenFolder:
                return CaptureCore.OpenFolder();

            // ---- 제어/조회 ----
            case CommandKind.Ping:
                return CommandResult.Ack();
            case CommandKind.Status:
                return CommandResult.Ack(ResultType.Status, new { running = true });
            case CommandKind.ListCommands:
                return CommandResult.Ack(ResultType.CommandList, CommandCatalog.Describe());
            case CommandKind.SettingsGet:
                return CommandResult.Ack(ResultType.Settings, SettingsSnapshot());

            default:
                return CommandResult.Fail("unknown_cmd", $"unsupported headless command: {cmd.Kind}");
        }
    }

    /// <summary>
    /// settings.get / SettingsGet 응답용 안전 스냅샷. 비밀(예 Imgur client id)이나 실행 상태는 제외하고
    /// 동작에 영향을 주는 필드만 노출한다.
    /// </summary>
    private static object SettingsSnapshot()
    {
        var s = Settings.Current;
        return new
        {
            saveFolder = s.SaveFolder,
            language = s.Language,
            ocrLanguage = s.OcrLanguage,
            autoCopyOnCapture = s.AutoCopyOnCapture,
            postCaptureToolbar = s.PostCaptureToolbar,
            keepHistory = s.KeepHistory,
            historyKeepRecent = s.HistoryKeepRecent,
            externalControlEnabled = s.ExternalControlEnabled,
            externalControlAllowSilent = s.ExternalControlAllowSilent,
            externalControlAllowReturnContent = s.ExternalControlAllowReturnContent,
            externalControlRateLimitPerMin = s.ExternalControlRateLimitPerMin,
            externalControlAudit = s.ExternalControlAudit,
        };
    }
}
