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
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Wsnap;
using WinForms = System.Windows.Forms;

namespace Wsnap.Control;

/// <summary>
/// 헤드리스 캡처/OCR/색/히스토리의 실제 구현. UI·WPF·오버레이·상주 상태에 의존하지 않는 순수 파사드로,
/// 기존 순수 함수(<see cref="ScreenGrab"/>, <see cref="CaptureStore"/>, <see cref="Ocr"/>)를 재사용한다.
/// 부수효과(썸네일·자동복사·트림)는 담지 않는다 — 상주에서 실행될 때만 <see cref="IResidentHost.PresentCapture"/>가 붙인다.
/// 좌표는 전부 device px. CLI(--headless), 상주 위임, MCP 브리지가 모두 이 클래스를 공유한다.
/// </summary>
public static class CaptureCore
{
    // ---------------- 캡처 ----------------

    public static CommandResult CaptureRegion(int x, int y, int w, int h) => SaveRegion(x, y, w, h);

    public static CommandResult CaptureFullScreen(string? monitor)
    {
        System.Drawing.Rectangle b;
        var screens = WinForms.Screen.AllScreens;
        if (string.Equals(monitor, "primary", StringComparison.OrdinalIgnoreCase))
            b = (WinForms.Screen.PrimaryScreen ?? screens[0]).Bounds;
        else if (int.TryParse(monitor, out var idx) && idx >= 0 && idx < screens.Length)
            b = screens[idx].Bounds;
        else   // "cursor" or null/unknown → the monitor under the cursor
            b = WinForms.Screen.FromPoint(WinForms.Cursor.Position).Bounds;
        return SaveRegion(b.X, b.Y, b.Width, b.Height);
    }

    public static CommandResult CaptureWindow()
    {
        var rect = ForegroundWindowRect();
        if (rect is not { } r) return CommandResult.Fail("no_window", "no foreground window");
        return SaveRegion(r.x, r.y, r.w, r.h);
    }

    /// <summary>Grab a device-px rect, save it as PNG, and report the path + foreground metadata. Pure.</summary>
    private static CommandResult SaveRegion(int x, int y, int w, int h)
    {
        if (w < 1 || h < 1) return CommandResult.Fail("no_region", "region must be at least 1x1");
        try
        {
            var ctx = ForegroundContext(w, h);
            string path;
            using (var bmp = ScreenGrab.GrabFast(x, y, w, h))
                path = CaptureStore.SaveBitmap(bmp, ctx);
            return CommandResult.FileSaved(path, w, h, ctx.App, ctx.Title, SafeLen(path));
        }
        catch (Exception ex) { CrashLog.Write("core-capture", ex); return CommandResult.Fail("internal", ex.Message); }
    }

    // ---------------- OCR ----------------

    public static async Task<CommandResult> OcrRegion(int x, int y, int w, int h, string? lang)
    {
        if (w < 1 || h < 1) return CommandResult.Fail("no_region", "region must be at least 1x1");
        try
        {
            using var bmp = ScreenGrab.GrabFast(x, y, w, h);
            return await RunOcr(bmp, lang);
        }
        catch (Exception ex) { CrashLog.Write("core-ocr-region", ex); return CommandResult.Fail("internal", ex.Message); }
    }

    public static async Task<CommandResult> OcrImage(string? path, string? lang)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return CommandResult.Fail("not_found", $"image not found: {path}");
        try
        {
            using var bmp = new Bitmap(path);
            return await RunOcr(bmp, lang);
        }
        catch (Exception ex) { CrashLog.Write("core-ocr-image", ex); return CommandResult.Fail("internal", ex.Message); }
    }

    public static async Task<CommandResult> OcrLast(string? lang)
    {
        var hist = CaptureStore.EnumerateHistory(1);
        if (hist.Count == 0) return CommandResult.Fail("not_found", "no capture in history");
        var res = await OcrImage(hist[0].Path, lang);
        // annotate with the source path so callers know what was read
        return res.Ok ? res with { Path = hist[0].Path } : res;
    }

    private static async Task<CommandResult> RunOcr(Bitmap bmp, string? lang)
    {
        Ocr.OcrLanguage? lo = string.IsNullOrWhiteSpace(lang) ? null : Ocr.Resolve(lang);
        string? text = await Ocr.RecognizeAsync(bmp, lo);
        if (text == null) return CommandResult.Fail("ocr_unavailable", "OCR engine unavailable (models missing or failed)");
        string langCode = (lo ?? Ocr.CurrentLanguage).Code;
        return CommandResult.OcrText(text, langCode);
    }

    // ---------------- 색 ----------------

    public static CommandResult ColorAt(int x, int y)
    {
        try
        {
            using var bmp = ScreenGrab.Grab(x, y, 1, 1);
            var c = bmp.GetPixel(0, 0);
            string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            return CommandResult.ColorResult(hex, c.R, c.G, c.B, x, y);
        }
        catch (Exception ex) { CrashLog.Write("core-color", ex); return CommandResult.Fail("internal", ex.Message); }
    }

    // ---------------- 히스토리 ----------------

    public static CommandResult HistoryList(int limit, bool pinnedOnly)
    {
        if (limit <= 0) limit = 30;
        var all = CaptureStore.EnumerateHistory(Math.Max(limit, pinnedOnly ? 600 : limit));
        var outp = new System.Collections.Generic.List<HistoryItem>(Math.Min(limit, all.Count));
        foreach (var (path, when, pinned) in all)
        {
            if (pinnedOnly && !pinned) continue;
            outp.Add(new HistoryItem(path, when, pinned));
            if (outp.Count >= limit) break;
        }
        return CommandResult.HistoryResult(outp);
    }

    /// <summary>Resolve an existing capture by 0-based index (newest=0) or by filename/path.</summary>
    public static CommandResult HistoryGet(string? idOrPath)
    {
        if (string.IsNullOrWhiteSpace(idOrPath)) return CommandResult.Fail("no_region", "id or path required");

        // Direct existing path wins.
        if (File.Exists(idOrPath))
            return CommandResult.FileSaved(Path.GetFullPath(idOrPath), 0, 0, bytes: SafeLen(idOrPath));

        var all = CaptureStore.EnumerateHistory(600);
        if (int.TryParse(idOrPath, out var idx))
        {
            if (idx < 0 || idx >= all.Count) return CommandResult.Fail("not_found", $"index out of range: {idx}");
            var p = all[idx].Path;
            return CommandResult.FileSaved(p, 0, 0, bytes: SafeLen(p));
        }
        foreach (var (path, _, _) in all)
            if (string.Equals(Path.GetFileName(path), idOrPath, StringComparison.OrdinalIgnoreCase))
                return CommandResult.FileSaved(path, 0, 0, bytes: SafeLen(path));
        return CommandResult.Fail("not_found", $"no capture matching: {idOrPath}");
    }

    // ---------------- 폴더 ----------------

    public static CommandResult OpenFolder()
    {
        try
        {
            string dir = Settings.Current.SaveFolder;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            return CommandResult.Ack();
        }
        catch (Exception ex) { CrashLog.Write("core-open-folder", ex); return CommandResult.Fail("internal", ex.Message); }
    }

    // ---------------- 전경 창 컨텍스트(파일명 템플릿 / 창 캡처) ----------------

    /// <summary>Snapshot the foreground app/title now (for {app}/{title} filename tokens). P/Invoke only.</summary>
    public static NameContext ForegroundContext(int w = 0, int h = 0)
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            string title = "", app = "";
            if (fg != IntPtr.Zero)
            {
                int len = GetWindowTextLength(fg);
                if (len > 0) { var sb = new System.Text.StringBuilder(len + 1); GetWindowText(fg, sb, sb.Capacity); title = sb.ToString(); }
                GetWindowThreadProcessId(fg, out uint pid);
                if (pid != 0) { try { using var p = Process.GetProcessById((int)pid); app = p.ProcessName; } catch { } }
            }
            return new NameContext { App = app, Title = title, Width = w, Height = h };
        }
        catch (Exception ex) { CrashLog.Write("core-fg-context", ex); return NameContext.Empty; }
    }

    /// <summary>Foreground window's device-px rect, using DWM extended frame bounds (excludes the
    /// ~7px invisible resize border), falling back to GetWindowRect. Null if there is no foreground window.</summary>
    public static (int x, int y, int w, int h)? ForegroundWindowRect()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0)
            if (!GetWindowRect(hwnd, out r)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w < 1 || h < 1) return null;
        return (r.Left, r.Top, w, h);
    }

    private static long SafeLen(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }

    // ---------------- native ----------------
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder buf, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
