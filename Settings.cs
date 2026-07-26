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

/// <summary>Save format for new captures. PNG is lossless (default, biggest); WebP/JPEG are
/// lossy but several times smaller — good for screenshots shared in chat/docs where perfect
/// text fidelity is not essential.</summary>
public enum ImageFormat { Png, Webp, Jpeg }

/// <summary>
/// User settings, persisted as JSON at %APPDATA%\wsnap\settings.json.
/// Loaded once into <see cref="Current"/> at startup; call <see cref="Save"/> after edits.
/// </summary>
public sealed class Settings
{
    // ---- Localization ----
    /// <summary>UI language code ("en", "ko", …). Default English. See <see cref="L"/>.</summary>
    public string Language { get; set; } = "en";

    /// <summary>OCR recognition language pack code ("korean", "latin", …). Separate from the UI
    /// language. Default "korean" = the embedded KO+EN model. See <see cref="Ocr.Languages"/>.</summary>
    public string OcrLanguage { get; set; } = "korean";

    // ---- Capture / storage ----
    public string SaveFolder { get; set; } = DefaultSaveFolder();
    public bool KeepHistory { get; set; } = false;          // permanent date-foldered archive

    /// <summary>Image format for new captures. PNG (default) = lossless &amp; biggest; WebP/JPEG
    /// are lossy but ~3–10× smaller. JPEG has no alpha — fine for screen grabs.</summary>
    public ImageFormat SaveFormat { get; set; } = ImageFormat.Png;

    /// <summary>WebP quality 1–100 (ignored for PNG/JPEG). 85 is visually transparent for most
    /// UI/text screenshots at a fraction of PNG's size.</summary>
    public int WebpQuality { get; set; } = 85;

    /// <summary>JPEG quality 1–100 (ignored for PNG/WebP). 90 keeps text crisp; lower if size matters more.</summary>
    public int JpegQuality { get; set; } = 90;

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

    /// <summary>Check for a newer GitHub release on startup (background, opt-out). Default on.
    /// Never silently swaps the binary; surfaces a tray entry + toast linking to the release.</summary>
    public bool UpdateCheck { get; set; } = true;

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

    // ---- Video recording (H.264/MP4 via ffmpeg) ----
    /// <summary>Region video capture framerate. Clamped 1–60 at use. Default 24.</summary>
    public int VideoFps { get; set; } = 24;

    /// <summary>Audio source for MP4 recording: "none" | "mic" | "system" | "both".
    /// APNG cannot carry audio (it's an animated image), so this only affects MP4. Default none (silent).</summary>
    public string VideoAudio { get; set; } = "none";

    /// <summary>Optional explicit dshow microphone device name. Blank → auto-pick the first mic.</summary>
    public string VideoMicDevice { get; set; } = "";

    /// <summary>Optional explicit path to ffmpeg.exe. Blank → resolve PATH then on-demand download.</summary>
    public string FFmpegPath { get; set; } = "";

    // ---- External control: CLI / MCP / pipe (v1.7, opt-in) ----
    /// <summary>Master switch. When false the control pipe server is NEVER created (zero attack
    /// surface) and MCP/CLI delegation is refused. Off by default — privacy-first.</summary>
    public bool ExternalControlEnabled { get; set; } = false;

    /// <summary>Allow silent (no shutter flash/toast) external captures. Audit + tray badge are never
    /// suppressed. Off by default; recommended CLI-only (MCP is always visible).</summary>
    public bool ExternalControlAllowSilent { get; set; } = false;

    /// <summary>Allow returning captured pixels / OCR text to the caller (the real exfiltration
    /// channel — second-tier consent). Off by default.</summary>
    public bool ExternalControlAllowReturnContent { get; set; } = false;

    /// <summary>Rate limit for external-initiated commands, per source, per minute.</summary>
    public int ExternalControlRateLimitPerMin { get; set; } = 30;

    /// <summary>Append an audit line for every external command. On by default.</summary>
    public bool ExternalControlAudit { get; set; } = true;

    // ---- Automation (v1.7) ----
    /// <summary>Auto-OCR any image detected on the clipboard (extends <see cref="ClipboardWatch"/>).</summary>
    public bool ClipboardAutoOcr { get; set; } = false;
    /// <summary>Watch a folder and auto-OCR new images dropped into it.</summary>
    public bool WatchFolderOcr { get; set; } = false;
    public string WatchFolderPath { get; set; } = "";
    /// <summary>Allow hotkey → external shell command (opt-in; {path}=last capture). Never exposed to pipe/MCP.</summary>
    public bool AllowShellCommands { get; set; } = false;

    // ---- Hotkeys (v1.7 multi-binding) ----
    /// <summary>Chord → command bindings. Empty on old configs → migrated from the legacy Hotkey*
    /// fields at <see cref="Load"/> so existing users keep their exact behaviour.</summary>
    public System.Collections.Generic.List<HotkeyBinding> Hotkeys { get; set; } = new();

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

        // Apply the saved (or default English) UI language, normalized to a supported one.
        Current.Language = L.Normalize(Current.Language);
        L.Lang = Current.Language;

        // Migrate the legacy single hotkey (+ optional Win+Shift+S) into the multi-binding list
        // (v1.7) so existing configs keep their exact behaviour under the new multi-binding hook.
        if (Current.Hotkeys.Count == 0)
        {
            Current.Hotkeys.Add(new HotkeyBinding
            {
                Vk = Current.HotkeyVk, Shift = Current.HotkeyShift, Ctrl = Current.HotkeyCtrl,
                Alt = Current.HotkeyAlt, Win = Current.HotkeyWin,
                Command = "capture.interactive", Swallow = true
            });
            if (Current.SwallowWinShiftS)
                Current.Hotkeys.Add(new HotkeyBinding
                {
                    Vk = 0x53 /* S */, Shift = true, Win = true,
                    Command = "capture.interactive", Swallow = true
                });
        }

        // Make sure the target folder exists regardless.
        try { Directory.CreateDirectory(Current.SaveFolder); } catch { }
    }

    /// <summary>
    /// Fold the legacy single-hotkey fields (<see cref="HotkeyVk"/> etc. + <see cref="SwallowWinShiftS"/>)
    /// back into the <see cref="Hotkeys"/> multi-binding list that <see cref="HotkeyHook"/> actually reads.
    /// The settings window edits only the legacy fields; without this the primary "capture.interactive"
    /// binding (and the Win+Shift+S swallow) keep firing the pre-edit chord after the one-time
    /// <see cref="Load"/> migration. User-added custom bindings are preserved untouched. Mirrors the
    /// Load() migration and swaps in a fresh list so the hook's live index walk stays safe.
    /// </summary>
    public static void SyncPrimaryHotkeyFromLegacy()
    {
        var src = Current.Hotkeys;
        var next = new System.Collections.Generic.List<HotkeyBinding>(src.Count + 1);
        bool primaryDone = false;

        for (int i = 0; i < src.Count; i++)
        {
            var b = src[i];

            // Drop the managed Win+Shift+S swallow binding; it's re-added below per SwallowWinShiftS.
            if (IsManagedWinShiftS(b)) continue;

            // Retarget the first default-capture binding to the current chord, keeping its other props.
            if (!primaryDone && b.Command == "capture.interactive")
            {
                next.Add(new HotkeyBinding
                {
                    Vk = Current.HotkeyVk, Shift = Current.HotkeyShift, Ctrl = Current.HotkeyCtrl,
                    Alt = Current.HotkeyAlt, Win = Current.HotkeyWin,
                    Command = b.Command, Args = b.Args, Swallow = b.Swallow, Enabled = b.Enabled
                });
                primaryDone = true;
                continue;
            }

            next.Add(b);   // preserve custom bindings verbatim
        }

        // No default-capture binding was present → create one from the legacy chord (as Load() does).
        if (!primaryDone)
            next.Add(new HotkeyBinding
            {
                Vk = Current.HotkeyVk, Shift = Current.HotkeyShift, Ctrl = Current.HotkeyCtrl,
                Alt = Current.HotkeyAlt, Win = Current.HotkeyWin,
                Command = "capture.interactive", Swallow = true
            });

        // Re-add the Win+Shift+S swallow to match the current toggle (same signature Load() uses).
        if (Current.SwallowWinShiftS)
            next.Add(new HotkeyBinding
            {
                Vk = 0x53 /* S */, Shift = true, Win = true,
                Command = "capture.interactive", Swallow = true
            });

        Current.Hotkeys = next;   // atomic swap — safe against HotkeyHook's live index walk
    }

    /// <summary>The Win+Shift+S → interactive-capture chord that <see cref="SwallowWinShiftS"/> manages.</summary>
    private static bool IsManagedWinShiftS(HotkeyBinding b) =>
        b.Vk == 0x53 && b.Shift && b.Win && !b.Ctrl && !b.Alt && b.Command == "capture.interactive";

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

/// <summary>One global-hotkey chord bound to a wsnap command (v1.7 multi-binding).</summary>
public sealed class HotkeyBinding
{
    public int Vk { get; set; }
    public bool Shift { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Win { get; set; }

    /// <summary>Canonical dotted command id (e.g. "capture.interactive", "ocr.last", "capture.region").</summary>
    public string Command { get; set; } = "capture.interactive";

    /// <summary>Optional command args (e.g. {"seconds":"5"} for capture.delayed).</summary>
    public System.Collections.Generic.Dictionary<string, string>? Args { get; set; }

    public bool Swallow { get; set; } = true;
    public bool Enabled { get; set; } = true;
}
