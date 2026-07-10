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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Wsnap;

/// <summary>
/// Code-side companion to Theme.axaml (Phase 1 — the Avalonia redesign of the WPF Theme.cs).
/// The styles themselves live in Theme.axaml as compiled class-selector styles over FluentTheme;
/// this class exposes the same color tokens to code-behind, resource lookup by token name, and
/// per-window chrome (dark DWM title bar), mirroring the WPF Theme's public surface.
/// Named AppTheme (not Theme like the WPF side) because Avalonia's StyledElement already has a
/// <c>Theme</c> property (ControlTheme) — inside any control subclass a bare <c>Theme.Apply</c>
/// would bind to that property instead of the class. Window ports do s/Theme./AppTheme./.
/// </summary>
public static class AppTheme
{
    // ---- color tokens (mirror the landing page :root and Theme.axaml) ----
    public static readonly Color Bg          = Color.Parse("#0E0F11");
    public static readonly Color Panel       = Color.Parse("#16181B");
    public static readonly Color Panel2      = Color.Parse("#1C1F23");
    public static readonly Color Surface     = Color.Parse("#23262B"); // inputs / resting controls
    public static readonly Color SurfaceHi   = Color.Parse("#2C3036"); // hover
    public static readonly Color Text        = Color.Parse("#F4F5F7");
    public static readonly Color Muted       = Color.Parse("#AEB1BA");
    public static readonly Color Muted2      = Color.Parse("#8C909A");
    public static readonly Color Accent      = Color.Parse("#3B82F6");
    public static readonly Color AccentDeep  = Color.Parse("#2563EB");
    public static readonly Color AccentSoft  = Color.FromArgb(0x24, 0x3B, 0x82, 0xF6);
    public static readonly Color Danger      = Color.Parse("#EF4444");
    public static readonly Color Warn        = Color.Parse("#FBBF24");
    public static readonly Color Success     = Color.Parse("#22C55E");
    public static readonly Color Border      = Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF);
    public static readonly Color BorderStrong= Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF);

    public const string FontStack =
        "Segoe UI Variable Text, Segoe UI, Malgun Gothic, Apple SD Gothic Neo";

    public static readonly FontFamily Font = FontFamily.Parse(FontStack);

    /// <summary>Pull a brush token (e.g. "Accent", "Muted") from Theme.axaml's resources.
    /// Immutable at authoring time, so instances are safely shared across windows.</summary>
    public static IBrush Brush(string key) =>
        Application.Current!.FindResource(key + "Brush") as IBrush
        ?? throw new InvalidOperationException($"theme brush missing: {key}");

    /// <summary>Set window-wide defaults. Unlike WPF (which merged a dictionary per window),
    /// the styles are app-global via App.axaml — this only sets what is per-window: background,
    /// font, and the dark OS title bar.</summary>
    public static void Apply(Window w)
    {
        w.Background = Brush("Bg");
        w.FontFamily = Font;
        if (w.IsLoaded || w.PlatformImpl != null) SetDarkTitleBar(w);
        else w.Opened += (_, _) => SetDarkTitleBar(w);
    }

    /// <summary>Ask DWM for a dark caption + matching caption color (Win10 1809+ / Win11) —
    /// same calls as the WPF Theme; only the HWND acquisition differs.</summary>
    private static void SetDarkTitleBar(Window w)
    {
        try
        {
            IntPtr hwnd = w.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) return;
            int on = 1;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 2004+); 19 on earlier builds.
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
            // 35 = DWMWA_CAPTION_COLOR (Win11): tint to our base. COLORREF = 0x00BBGGRR.
            int caption = Bg.R | (Bg.G << 8) | (Bg.B << 16);
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        }
        catch { /* unsupported OS build — keep default chrome */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
