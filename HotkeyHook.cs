using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wsnap;

/// <summary>
/// Global low-level keyboard hook (WH_KEYBOARD_LL).
/// Fires the capture flow on the user-configured hotkey (default Shift+F1) and,
/// optionally, on Win+Shift+S (swallowing it to replace the OS Snipping Tool).
/// Modifiers/keys are read live from <see cref="Settings.Current"/>, so rebinding
/// in the settings window takes effect without reinstalling the hook.
/// </summary>
public sealed class HotkeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const int VK_SHIFT = 0x10, VK_CTRL = 0x11, VK_ALT = 0x12;
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const int VK_S = 0x53;

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _installFailed;

    /// <summary>Raised on a captured trigger. Handler should start the capture flow.</summary>
    public event Action? CaptureRequested;

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
            var s = Settings.Current;

            bool shift = Down(VK_SHIFT);
            bool ctrl  = Down(VK_CTRL);
            bool alt    = Down(VK_ALT);
            bool win    = Down(VK_LWIN) || Down(VK_RWIN);

            bool configured =
                vk == s.HotkeyVk &&
                shift == s.HotkeyShift &&
                ctrl == s.HotkeyCtrl &&
                alt == s.HotkeyAlt &&
                win == s.HotkeyWin;

            bool winShiftS = s.SwallowWinShiftS && win && shift && vk == VK_S;

            if (configured || winShiftS)
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                    () => CaptureRequested?.Invoke());
                return (IntPtr)1; // swallow
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
