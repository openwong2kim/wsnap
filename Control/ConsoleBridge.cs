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
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Wsnap.Control;

/// <summary>
/// wsnap는 WinExe(SUBSYSTEM:WINDOWS)라 콘솔이 없다. CLI/MCP 진입점이 표준 입출력을 쓰려면
/// 실행 문맥에 맞춰 표준 핸들을 확보해야 한다:
/// <list type="bullet">
///   <item>stdout/stderr이 파일·파이프로 리다이렉트돼 있으면(예: <c>wsnap ocr --json &gt; out.txt</c>)
///         그 핸들을 그대로 쓴다.</item>
///   <item>리다이렉트가 없고 부모가 콘솔(cmd/PowerShell)이면 <c>AttachConsole(ATTACH_PARENT_PROCESS)</c>로
///         부모 콘솔에 붙어 사용자가 타이핑한 곳에 출력한다.</item>
///   <item>둘 다 아니면(Explorer 더블클릭·트레이 실행) 출력은 조용히 no-op — 절대 예외를 던지지 않는다.</item>
/// </list>
/// 규약: 사람용·진단 메시지는 stderr, 기계용(JSON)·바이너리(--out -)는 stdout. 이 헬퍼가 그 분리를 돕는다.
/// </summary>
public static class ConsoleBridge
{
    private static readonly object Gate = new();
    private static bool _bound;
    private static bool _attached;   // AttachConsole으로 부모 콘솔을 빌렸는가(종료 시 Free 대상)
    private static bool _hasOut;      // stdout으로 안전하게 쓸 수 있는가
    private static bool _hasErr;      // stderr으로 안전하게 쓸 수 있는가

    /// <summary>
    /// 표준 입출력을 실행 문맥에 맞게 결선한다. 여러 번 불러도 최초 1회만 동작. 어떤 실패도 삼킨다.
    /// </summary>
    public static void Bind()
    {
        lock (Gate)
        {
            if (_bound) return;
            _bound = true;
            try
            {
                bool outRedir = Console.IsOutputRedirected;
                bool errRedir = Console.IsErrorRedirected;

                // 리다이렉트되지 않은 표준 스트림이 하나라도 있으면 부모 콘솔을 빌려 본다.
                if (!outRedir || !errRedir)
                    _attached = AttachConsole(AttachParentProcess);

                // UTF-8: 한국어 OCR 텍스트와 요약이 콘솔/파이프를 통과해도 깨지지 않도록.
                // (리다이렉트된 스트림은 여기서 인코딩만 바꾸고, 붙인 콘솔은 아래 Rebind가 최종 결선.)
                if (_attached || outRedir || errRedir)
                    TrySetUtf8();

                bool outReady = outRedir;
                bool errReady = errRedir;
                if (_attached)
                {
                    if (!outRedir) outReady = Rebind(StdOutputHandle, isError: false);
                    if (!errRedir) errReady = Rebind(StdErrorHandle, isError: true);
                }

                _hasOut = outReady;
                _hasErr = errReady;
            }
            catch
            {
                // 콘솔 셋업이 GUI 실행(Explorer 등)을 절대 죽이지 않게 한다.
            }
        }
    }

    /// <summary>붙였던 부모 콘솔을 놓아준다(선택적, 종료 직전 호출). no-op 안전.</summary>
    public static void Unbind()
    {
        lock (Gate)
        {
            if (!_attached) return;
            try { Console.Out.Flush(); } catch { }
            try { Console.Error.Flush(); } catch { }
            try { FreeConsole(); } catch { }
            _attached = false;
        }
    }

    /// <summary>stdout에 개행 없이 쓴다(기계용/요약). 콘솔이 없으면 no-op.</summary>
    public static void Out(string s)
    {
        if (!_hasOut) return;
        try { Console.Out.Write(s); Console.Out.Flush(); } catch { }
    }

    /// <summary>stdout에 한 줄 쓴다(JSON 한 줄·요약). 콘솔이 없으면 no-op.</summary>
    public static void OutLine(string s)
    {
        if (!_hasOut) return;
        try { Console.Out.Write(s); Console.Out.Write('\n'); Console.Out.Flush(); } catch { }
    }

    /// <summary>stderr에 한 줄 쓴다(사람용/진단). 콘솔이 없으면 no-op.</summary>
    public static void ErrLine(string s)
    {
        if (!_hasErr) return;
        try { Console.Error.Write(s); Console.Error.Write('\n'); Console.Error.Flush(); } catch { }
    }

    /// <summary>바이너리 stdout 스트림(캡처 PNG의 <c>--out -</c> 등). Console.Out 텍스트 라이터와 독립.</summary>
    public static Stream OpenStdout() => Console.OpenStandardOutput();

    /// <summary>바이너리 stdin 스트림(<c>ocr --file -</c> 등).</summary>
    public static Stream OpenStdin() => Console.OpenStandardInput();

    // ---------------- 내부 ----------------

    /// <summary>AttachConsole 이후 아직 결선되지 않은 표준 스트림을 콘솔 핸들에 UTF-8로 붙인다.</summary>
    private static bool Rebind(int stdHandleId, bool isError)
    {
        IntPtr h = GetStdHandle(stdHandleId);
        if (h == IntPtr.Zero || h == InvalidHandleValue) return false;
        try
        {
            var stream = new FileStream(new SafeFileHandle(h, ownsHandle: false), FileAccess.Write);
            var writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = true };
            if (isError) Console.SetError(writer); else Console.SetOut(writer);
            return true;
        }
        catch { return false; }
    }

    private static void TrySetUtf8()
    {
        try { Console.OutputEncoding = Utf8NoBom; } catch { /* 콘솔 없음/리다이렉트 등에서 무시 */ }
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // ---------------- native ----------------
    private const uint AttachParentProcess = 0xFFFFFFFF; // (DWORD)-1
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);
}
