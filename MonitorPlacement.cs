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
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace Wsnap;

/// <summary>
/// Physical-pixel placement for floating bottom-right widgets (thumbnail, toast).
///
/// WPF's logical (device-independent) coordinates — Window.Left/Top and
/// SystemParameters.WorkArea — are anchored to the PRIMARY monitor's DPI. Under
/// PerMonitorV2 awareness on a multi-monitor / mixed-DPI desktop that mapping breaks:
/// a window placed by logical coordinates lands in the wrong spot and clashes with the
/// taskbar. Resolving the cursor's monitor and placing in real device pixels via
/// SetWindowPos sidesteps WPF's conversion entirely, so the widget sits correctly on
/// whichever screen the user is actually working on.
/// </summary>
internal static class MonitorPlacement
{
    /// <summary>
    /// Work area (taskbar excluded) and DPI scale of the monitor under the cursor,
    /// both in physical device pixels. Falls back to the primary work area on failure.
    /// </summary>
    public static (System.Drawing.Rectangle WorkPx, double Scale) CursorWorkArea()
    {
        try
        {
            var cur = WinForms.Cursor.Position;                          // physical px (DPI-agnostic)
            var work = WinForms.Screen.FromPoint(cur).WorkingArea;       // physical px, excludes taskbar
            IntPtr mon = MonitorFromPoint(new POINT { X = cur.X, Y = cur.Y }, MONITOR_DEFAULTTONEAREST);
            double scale = (GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
                ? dpiX / 96.0
                : 1.0;
            return (work, scale);
        }
        catch
        {
            // Primary work area; logical ≈ physical on the primary so this stays usable.
            var wa = System.Windows.SystemParameters.WorkArea;
            return (new System.Drawing.Rectangle((int)wa.Left, (int)wa.Top, (int)wa.Width, (int)wa.Height), 1.0);
        }
    }

    /// <summary>Move + resize a window in physical device pixels. No-op without an HWND.</summary>
    public static void SetBoundsPx(IntPtr hwnd, double xPx, double yPx, double wPx, double hPx)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero,
            (int)Math.Round(xPx), (int)Math.Round(yPx),
            (int)Math.Round(wPx), (int)Math.Round(hPx),
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Move a window in physical device pixels, keeping its current size. No-op without an HWND.</summary>
    public static void MovePx(IntPtr hwnd, double xPx, double yPx)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero,
            (int)Math.Round(xPx), (int)Math.Round(yPx), 0, 0,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSIZE);
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
