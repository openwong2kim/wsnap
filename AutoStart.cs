using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Wsnap;

/// <summary>
/// "Start with Windows" via the per-user Run key (no admin rights needed).
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run\wsnap = "<exe path>".
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "wsnap";

    private static string ExePath =>
        Process.GetCurrentProcess().MainModule?.FileName ?? "";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            var val = key?.GetValue(ValueName) as string;
            return !string.IsNullOrEmpty(val);
        }
        catch { return false; }
    }

    public static void Set(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return;

            if (enable)
                key.SetValue(ValueName, $"\"{ExePath}\"");
            else if (key.GetValue(ValueName) != null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) { CrashLog.Write("autostart", ex); }
    }
}
