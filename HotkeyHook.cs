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
using System.Runtime.InteropServices;

namespace Wsnap;

/// <summary>
/// Global low-level keyboard hook (WH_KEYBOARD_LL).
/// Matches each keystroke against <see cref="Settings.Hotkeys"/> (the multi-binding list) and
/// raises <see cref="Triggered"/> with the binding that fired, so its command can be dispatched.
/// Bindings are read live from <see cref="Settings.Current"/>, so editing them in the settings
/// window takes effect without reinstalling the hook. A binding with
/// <see cref="HotkeyBinding.Swallow"/> set consumes the chord (e.g. to replace Win+Shift+S).
/// </summary>
public sealed class HotkeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const int VK_SHIFT = 0x10, VK_CTRL = 0x11, VK_ALT = 0x12;
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _installFailed;

    /// <summary>Raised when a keystroke matches an enabled binding, carrying that binding so the
    /// handler can dispatch its <see cref="HotkeyBinding.Command"/>. Marshalled to the UI dispatcher.</summary>
    public event Action<HotkeyBinding>? Triggered;

    /// <summary>True if the OS refused the hook (e.g. blocked by security software).</summary>
    public bool InstallFailed => _installFailed;

    public HotkeyHook() => _proc = HookCallback;

    public void Install()
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc,
            GetModuleHandle(curModule.ModuleName), 0);
        if (_hookId == IntPtr.Zero)
        {
            _installFailed = true;
            CrashLog.Write($"hook-install-failed: GetLastError={Marshal.GetLastWin32Error()}");
        }
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            int vk = Marshal.ReadInt32(lParam);

            bool shift = Down(VK_SHIFT);
            bool ctrl  = Down(VK_CTRL);
            bool alt   = Down(VK_ALT);
            bool win   = Down(VK_LWIN) || Down(VK_RWIN);

            // Hot path: this runs on EVERY system-wide keystroke. Snapshot the list reference once,
            // walk it by index, and compare only ints/bools — no LINQ, no foreach enumerator, no
            // closures or string work until a binding actually matches (rare, user-initiated).
            var list = Settings.Current.Hotkeys;
            for (int i = 0; i < list.Count; i++)
            {
                var b = list[i];
                if (vk == b.Vk && shift == b.Shift && ctrl == b.Ctrl &&
                    alt == b.Alt && win == b.Win && b.Enabled)
                {
                    // Defer off the hook (BeginInvoke): a low-level hook must return fast, and the
                    // handler opens windows / runs the command. Capturing b here is fine — cold path.
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                        () => Triggered?.Invoke(b));
                    if (b.Swallow) return (IntPtr)1; // consume the chord (e.g. replaces Win+Shift+S)
                    break;                           // fired, but let the keystroke pass through
                }
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
