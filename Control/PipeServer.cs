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
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wsnap;

namespace Wsnap.Control;

/// <summary>
/// 상주(트레이) 전용 요청/응답 파이프 서버. 외부 프로세스(CLI 단발 / MCP 지속연결)가 보낸 NDJSON 명령을
/// 받아 <see cref="ICommandRouter"/>(상주 로컬 라우터)로 실행하고 응답을 한 줄로 되돌린다.
/// 접근은 DACL로 현재 사용자 SID에만 허용한다 — 파이프 이름은 머신 전역이라 ACL이 유일한 격리 수단이다.
/// router.ExecuteAsync 내부에서 상주 host가 UI 마샬링(Dispatcher)을 책임지므로, accept 스레드에서 그대로 await하면 된다.
/// </summary>
public sealed class PipeServer : IDisposable
{
    /// <summary>파이프 이름. 클라이언트(<see cref="PipeClientRouter"/>)와 공유하는 계약 상수.</summary>
    public const string PipeName = "wsnap.control.v1";

    private const int BufferSize = 4096;

    private readonly ICommandRouter _router;
    private readonly CancellationTokenSource _cts = new();
    private int _started;   // Interlocked 가드(중복 Start 방지)
    private Task? _acceptLoop;

    /// <summary>상주 로컬 라우터를 위임 대상으로 받는다.</summary>
    public PipeServer(ICommandRouter router) =>
        _router = router ?? throw new ArgumentNullException(nameof(router));

    /// <summary>백그라운드 accept 루프를 시작한다(멱등). 외부 제어가 켜졌을 때만 호출.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>accept 루프와 진행 중 연결을 취소한다.</summary>
    public void Stop()
    {
        try { _cts.Cancel(); } catch { /* 이미 정리됨 */ }
    }

    public void Dispose()
    {
        Stop();
        try { _acceptLoop?.Wait(500); } catch { /* best-effort 종료 */ }
        _cts.Dispose();
    }

    // ---------------- accept 루프 ----------------

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServerStream();
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                // 연결 성립: 소유권을 핸들러로 넘기고 즉시 다음 인스턴스를 리슨한다(동시 연결 허용).
                var connection = server;
                server = null;
                _ = Task.Run(() => HandleConnectionAsync(connection, ct), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                // 인스턴스 생성/수락 실패는 격리하고 짧게 백오프(과부하·경합 시 CPU 스핀 방지).
                server?.Dispose();
                CrashLog.Write("pipe", ex);
                try { await Task.Delay(200, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    // ---------------- 연결 처리(연결 단위 격리) ----------------

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using (pipe)
            using (var reader = PipeProtocol.CreateReader(pipe))
            {
                // 지속연결: 한 연결에서 여러 요청을 순차 처리(EOF/오류 시 종료).
                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    string? line;
                    try { line = await PipeProtocol.ReadMessageAsync(reader, ct).ConfigureAwait(false); }
                    catch (IOException) { break; }                 // 클라이언트 조기 종료
                    catch (OperationCanceledException) { break; }

                    if (line is null) break;                       // EOF
                    if (line.Length == 0) continue;                // 빈 줄은 무시

                    PipeRequest? request;
                    try { request = PipeProtocol.ParseRequest(line); }
                    catch (JsonException) { break; }               // 깨진 JSON → 이 연결만 종료

                    if (request is null || string.IsNullOrEmpty(request.Cmd))
                    {
                        if (!await TryWriteAsync(pipe,
                                PipeProtocol.ErrorResponse(request?.Id ?? "", "bad_request", "malformed or empty command"),
                                ct).ConfigureAwait(false))
                            break;
                        continue;
                    }

                    PipeResponse response;
                    try
                    {
                        if (!PipeProtocol.TryBuildCommand(request, out var cmd))
                            response = PipeProtocol.ErrorResponse(request.Id, "unknown_cmd", $"unknown command '{request.Cmd}'");
                        else
                            response = PipeProtocol.BuildResponse(request.Id, await _router.ExecuteAsync(cmd, ct).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        // 라우터/직렬화 예외는 이 요청의 오류 응답으로 격리(연결·서버는 유지).
                        CrashLog.Write("pipe", ex);
                        response = PipeProtocol.ErrorResponse(request.Id, "internal", ex.Message);
                    }

                    if (!await TryWriteAsync(pipe, response, ct).ConfigureAwait(false)) break;
                }
            }
        }
        catch (Exception ex)
        {
            // 한 연결의 어떤 오류도 서버를 죽이지 않는다.
            CrashLog.Write("pipe", ex);
        }
    }

    /// <summary>응답을 쓴다. 클라이언트가 사라졌으면(IOException) false를 돌려 루프를 끝내게 한다.</summary>
    private static async Task<bool> TryWriteAsync(NamedPipeServerStream pipe, PipeResponse response, CancellationToken ct)
    {
        try
        {
            await PipeProtocol.WriteMessageAsync(pipe, PipeProtocol.SerializeResponse(response), ct).ConfigureAwait(false);
            return true;
        }
        catch (IOException) { return false; }
        catch (OperationCanceledException) { return false; }
    }

    // ---------------- 파이프 인스턴스 생성(DACL 격리) ----------------

    private static NamedPipeServerStream CreateServerStream()
    {
        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,  // 다중 인스턴스(동시 CLI + MCP)
            PipeTransmissionMode.Byte,                        // NDJSON은 바이트 스트림 위 개행 프레이밍
            PipeOptions.Asynchronous,
            inBufferSize: BufferSize,
            outBufferSize: BufferSize,
            pipeSecurity: BuildSecurity());
    }

    /// <summary>
    /// 현재 사용자 SID에만 ReadWrite(+ 인스턴스 생성)를 허용하는 DACL. Everyone/AuthenticatedUsers는
    /// ACE를 아예 넣지 않아 배제된다. 다른 세션의 다른 사용자는 SID가 달라 접근 불가(세션 로컬 효과).
    /// </summary>
    private static PipeSecurity BuildSecurity()
    {
        var security = new PipeSecurity();
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new InvalidOperationException("current user SID unavailable");
        security.AddAccessRule(new PipeAccessRule(
            sid,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        return security;
    }
}
