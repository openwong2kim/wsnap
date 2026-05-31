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
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wsnap;

/// <summary>
/// User settings, persisted as JSON at %APPDATA%\wsnap\settings.json.
/// Loaded once into <see cref="Current"/> at startup; call <see cref="Save"/> after edits.
/// </summary>
public sealed class Settings
{
    // ---- Capture / storage ----
    public string SaveFolder { get; set; } = DefaultSaveFolder();
    public bool KeepHistory { get; set; } = false;          // permanent date-foldered archive

    /// <summary>
    /// Filename template (extension appended automatically). Tokens:
    /// {app} {title} {date} {time} {seq} {w} {h}; literal text; or a raw .NET
    /// date/time format inside braces, e.g. {yyyy-MM-dd}, {HHmmss}. Blank → default.
    /// </summary>
    public string FilenameTemplate { get; set; } = DefaultFilenameTemplate;

    /// <summary>Built-in default; equivalent to the legacy snap_yyyyMMdd_HHmmss base.</summary>
    public const string DefaultFilenameTemplate = "snap_{yyyy-MM-dd}_{HH-mm-ss}";

    /// <summary>Rolling cap on the flat scratch folder so the history gallery has recent shots. 0 = unlimited.</summary>
    public int HistoryKeepRecent { get; set; } = 50;

    // ---- Thumbnails ----
    /// <summary>Seconds before a floating thumbnail auto-dismisses. 0 = never (keep until closed).</summary>
    public int AutoDismissSeconds { get; set; } = 6;
    public int MaxVisible { get; set; } = 5;

    /// <summary>Put the captured image on the clipboard automatically (Ctrl+V ready). On by default.</summary>
    public bool AutoCopyOnCapture { get; set; } = true;

    /// <summary>After selecting a region, show a floating action toolbar at the selection instead of
    /// instantly popping the thumbnail. OFF by default — wsnap's identity is drag → instant thumbnail.</summary>
    public bool PostCaptureToolbar { get; set; } = false;

    // ---- Editor ----
    /// <summary>Last-used annotation stroke thickness, remembered across edits.</summary>
    public int EditorThickness { get; set; } = 5;

    // ---- Resident behaviour ----
    public bool StartWithWindows { get; set; } = false;
    public bool ClipboardWatch { get; set; } = false;       // v1.1: thumbnail anything copied as an image
    public bool TelemetryOptIn { get; set; } = false;       // opt-in only; local log unless a sink is set

    // ---- Hotkey (default Shift+F1) ----
    public int HotkeyVk { get; set; } = 0x70;               // F1
    public bool HotkeyShift { get; set; } = true;
    public bool HotkeyCtrl { get; set; } = false;
    public bool HotkeyAlt { get; set; } = false;
    public bool HotkeyWin { get; set; } = false;

    /// <summary>Also intercept &amp; swallow Win+Shift+S (replaces the OS Snipping Tool). Off by default.</summary>
    public bool SwallowWinShiftS { get; set; } = false;

    // ---- Upload (v1.1, opt-in) ----
    public bool UploadEnabled { get; set; } = false;
    public string ImgurClientId { get; set; } = "";         // user supplies their own; empty = disabled

    [JsonIgnore]
    public string HotkeyText
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>(4);
            if (HotkeyCtrl) parts.Add("Ctrl");
            if (HotkeyWin) parts.Add("Win");
            if (HotkeyAlt) parts.Add("Alt");
            if (HotkeyShift) parts.Add("Shift");
            parts.Add(KeyName(HotkeyVk));
            return string.Join("+", parts);
        }
    }

    public static string KeyName(int vk) => vk switch
    {
        0x70 => "F1", 0x71 => "F2", 0x72 => "F3", 0x73 => "F4",
        0x74 => "F5", 0x75 => "F6", 0x76 => "F7", 0x77 => "F8",
        0x78 => "F9", 0x79 => "F10", 0x7A => "F11", 0x7B => "F12",
        0x2C => "PrtSc",
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        _ => "0x" + vk.ToString("X2")
    };

    // ---------- persistence ----------

    [JsonIgnore] public static Settings Current { get; private set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "wsnap");

    private static string ConfigPath => Path.Combine(ConfigDir, "settings.json");

    private static string DefaultSaveFolder() =>
        Path.Combine(Path.GetTempPath(), "wsnap");

    public static void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var s = JsonSerializer.Deserialize<Settings>(json);
                if (s != null) Current = s;
            }
        }
        catch { /* corrupt file -> fall back to defaults */ }

        // Make sure the target folder exists regardless.
        try { Directory.CreateDirectory(Current.SaveFolder); } catch { }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex) { CrashLog.Write("settings-save", ex); }
    }
}
