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
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Wsnap;

/// <summary>Scaffold Avalonia Application. Phase 1 replaces this with real startup: HotkeyHook,
/// tray icon (WinForms, deliberately retained), Theme.cs's Avalonia rewrite. For now it proves
/// Settings.Load() (a linked, framework-agnostic file) runs correctly under the Avalonia host.</summary>
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Settings.Load();
            // Scaffold: nothing to show yet, exit clean after proving startup works. Deferred via
            // Post so the dispatcher's main loop is actually pumping before Shutdown() runs —
            // calling Shutdown() synchronously here (before Start() enters its loop) throws.
            Dispatcher.UIThread.Post(() => desktop.Shutdown());
        }
        base.OnFrameworkInitializationCompleted();
    }
}
