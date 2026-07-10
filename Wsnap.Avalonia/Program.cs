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
using Avalonia;

namespace Wsnap;

/// <summary>
/// Scaffold-only entry point for the Avalonia UI migration (plans/humming-meandering-aurora.md).
/// Proves the 24 UI-framework-agnostic files (Ocr.cs, Settings.cs, CaptureStore.cs, Control\*,
/// etc. — linked, not copied, from ..\) compile and link correctly under Avalonia's SDK/package
/// set. Real windows (CaptureOverlay, EditorWindow, ...) land in later migration phases; this is
/// deliberately just enough to build and boot.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
