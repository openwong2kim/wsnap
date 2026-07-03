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
using System.Threading;
using System.Threading.Tasks;
using Wsnap;

namespace Wsnap.Control;

/// <summary>
/// 외부 프로세스(CLI / MCP)에서 쓰는 <see cref="ICommandRouter"/> 구현. 명령을 상주(트레이)의
/// <see cref="PipeServer"/>에 파이프로 위임한다. 상주가 없으면(연결 실패) "resident_required"로 실패시킨다.
/// 기본은 짧은 단발 연결(요청 1 / 응답 1)이며, 연결 재사용은 하지 않는다.
/// </summary>
public sealed class PipeClientRouter : ICommandRouter
{
    private const string ResidentMutexName = "wsnap.singleton.v1"; // SingleInstance와 공유하는 상주 탐지 신호

    private readonly int _connectTimeoutMs;

    /// <param name="connectTimeoutMs">상주 파이프 연결 대기(ms). 초과 시 resident_required.</param>
    public PipeClientRouter(int connectTimeoutMs = 800) => _connectTimeoutMs = connectTimeoutMs;

    /// <summary>명령을 상주에 위임하고 결과를 받는다. 상주 미기동은 resident_required로 응답.</summary>
    public async Task<CommandResult> ExecuteAsync(WsnapCommand cmd, CancellationToken ct = default)
    {
        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(".", PipeServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            try { await pipe.ConnectAsync(_connectTimeoutMs, ct).ConfigureAwait(false); }
            catch (TimeoutException) { return NotRunning(); }
            catch (IOException) { return NotRunning(); }
            catch (UnauthorizedAccessException) { return NotRunning(); }

            // 요청 한 줄 write.
            var id = PipeProtocol.NewId();
            var request = PipeProtocol.BuildRequest(cmd, id);
            await PipeProtocol.WriteMessageAsync(pipe, PipeProtocol.SerializeRequest(request), ct).ConfigureAwait(false);

            // 응답 한 줄 read → CommandResult.
            using var reader = PipeProtocol.CreateReader(pipe);
            var line = await PipeProtocol.ReadMessageAsync(reader, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(line))
                return CommandResult.Fail("internal", "no response from wsnap");

            var response = PipeProtocol.ParseResponse(line);
            return response is null
                ? CommandResult.Fail("internal", "invalid response from wsnap")
                : PipeProtocol.ToResult(response);
        }
        catch (OperationCanceledException)
        {
            throw;  // 호출자 취소는 그대로 전파
        }
        catch (IOException)
        {
            // write/read 중 상주가 사라진 경우.
            return NotRunning();
        }
        catch (Exception ex)
        {
            CrashLog.Write("pipe", ex);
            return CommandResult.Fail("internal", ex.Message);
        }
        finally
        {
            pipe?.Dispose();
        }
    }

    private static CommandResult NotRunning() =>
        CommandResult.Fail("resident_required", "wsnap tray app is not running");

    /// <summary>
    /// 상주(트레이) 기동 여부. 통합자가 자동기동/위임 판단에 쓴다. 먼저 SingleInstance 뮤텍스를 탐지하고,
    /// 없으면 짧게(200ms) 파이프 연결을 시도한다.
    /// </summary>
    public static bool IsResidentRunning()
    {
        // 빠른 경로: 상주가 잡고 있는 싱글턴 뮤텍스.
        try
        {
            if (Mutex.TryOpenExisting(ResidentMutexName, out var mutex))
            {
                mutex.Dispose();
                return true;
            }
        }
        catch (UnauthorizedAccessException)
        {
            return true;  // 존재하나 기본 권한으로 못 여는 경우 → 그래도 기동 중
        }
        catch { /* 미존재/기타 → 폴백 */ }

        // 폴백: 짧은 파이프 연결 시도.
        try
        {
            using var probe = new NamedPipeClientStream(".", PipeServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            probe.Connect(200);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
