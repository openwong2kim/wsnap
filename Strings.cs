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
using System.Collections.Generic;
using System.Text.Json;

namespace Wsnap;

/// <summary>
/// Tiny string-table localization. <see cref="T(string)"/> returns the entry for the
/// current <see cref="Lang"/>, falling back to English (the default) when a language is
/// missing a key. English and Korean are built in (below). Additional languages are
/// community translation packs shipped as <c>locales/&lt;code&gt;.json</c> (embedded as
/// <c>wsnap.locale.&lt;code&gt;.json</c>): drop one in, add it to the csproj glob, and it
/// auto-registers here — no code change. Each pack may carry a <c>"_native"</c> key for its
/// display name. See <c>locales/README.md</c>. The picker in <see cref="SettingsWindow"/> is
/// driven by <see cref="Available"/>. All format-bearing strings use {0}, {1}, … so
/// <see cref="T(string, object[])"/> can fill them.
/// </summary>
public static class L
{
    /// <summary>BCP-47-ish short code of the active UI language. Default: English.</summary>
    public static string Lang { get; set; } = "en";

    private static readonly Dictionary<string, Dictionary<string, string>> _tables = new();
    private static readonly List<(string Code, string Name)> _available = new();

    /// <summary>Languages offered in the settings picker: (code, native display name).</summary>
    public static IReadOnlyList<(string Code, string Name)> Available => _available;

    // Runs AFTER all field initializers, so En/Ko are populated. Registers the built-ins,
    // then merges any embedded locale packs (contributed translations).
    static L()
    {
        _tables["en"] = En; _available.Add(("en", "English"));
        _tables["ko"] = Ko; _available.Add(("ko", "한국어"));
        LoadEmbeddedPacks();
    }

    /// <summary>Discover embedded <c>wsnap.locale.&lt;code&gt;.json</c> packs and merge them in.</summary>
    private static void LoadEmbeddedPacks()
    {
        const string prefix = "wsnap.locale.", suffix = ".json";
        try
        {
            var asm = typeof(L).Assembly;
            foreach (var res in asm.GetManifestResourceNames())
            {
                if (!res.StartsWith(prefix, StringComparison.Ordinal) || !res.EndsWith(suffix, StringComparison.Ordinal))
                    continue;
                string code = res.Substring(prefix.Length, res.Length - prefix.Length - suffix.Length);
                if (_tables.ContainsKey(code)) continue;   // never override a built-in
                try
                {
                    using var s = asm.GetManifestResourceStream(res);
                    if (s == null) continue;
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(s);
                    if (dict == null || dict.Count == 0) continue;
                    string native = dict.TryGetValue("_native", out var n) && !string.IsNullOrWhiteSpace(n) ? n : code;
                    dict.Remove("_native");
                    _tables[code] = dict;
                    _available.Add((code, native));
                }
                catch (Exception ex) { CrashLog.Write("locale-pack", ex); }   // skip a bad pack
            }
        }
        catch (Exception ex) { CrashLog.Write("locale-scan", ex); }
    }

    /// <summary>Native display name for a language code (falls back to the code itself).</summary>
    public static string NameOf(string code)
    {
        foreach (var (c, n) in _available) if (c == code) return n;
        return code;
    }

    /// <summary>Normalize an arbitrary/stored code to a supported one (else English).</summary>
    public static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "en";
        foreach (var (c, _) in _available) if (c == code) return c;
        return "en";
    }

    /// <summary>Look up a key in the active language; fall back to English, then the key itself.</summary>
    public static string T(string key)
    {
        if (_tables.TryGetValue(Lang, out var t) && t.TryGetValue(key, out var v)) return v;
        if (En.TryGetValue(key, out var e)) return e;
        return key;
    }

    /// <summary>Look up a format string and fill its {0}, {1}, … placeholders.</summary>
    public static string T(string key, params object[] args) => string.Format(T(key), args);

    // ---------------------------------------------------------------- English (default)
    private static readonly Dictionary<string, string> En = new()
    {
        // ---- tray menu (App.cs) ----
        ["tray.captureRegion"]  = "Capture Region  ({0})",
        ["tray.captureShort"]   = "Capture  ({0})",
        ["tray.captureFull"]    = "Capture Full Screen",
        ["tray.captureWindow"]  = "Capture Active Window",
        ["tray.repeatRegion"]   = "Repeat Last Region",
        ["tray.delay"]          = "Delayed Capture",
        ["tray.delay3"]         = "Capture Region in 3s",
        ["tray.delay5"]         = "Capture Region in 5s",
        ["tray.ocr"]            = "Extract Text (OCR Region)",
        ["tray.colorPick"]      = "Pick Color (Eyedropper)",
        ["tray.gif"]            = "Record GIF (Region)",
        ["tray.scroll"]         = "Scrolling Capture (Region)",
        ["tray.openFolder"]     = "Open Capture Folder",
        ["tray.history"]        = "Capture History…",
        ["tray.clearThumbs"]    = "Clear All Thumbnails",
        ["tray.settings"]       = "Settings…",
        ["tray.exit"]           = "Exit",
        ["tray.tip"]            = "wsnap — capture with {0}",

        // ---- toasts (App.cs) ----
        ["toast.hookFailed"]    = "Failed to install the capture hotkey — security software may have blocked it. Use the tray menu to capture.",
        ["toast.imageCopied"]   = "Image copied ✓",
        ["toast.ocrBusy"]       = "Recognizing text…",
        ["toast.ocrUnavailable"]= "OCR unavailable (model files missing)",
        ["toast.ocrNoText"]     = "No text recognized",
        ["toast.textCopied"]    = "Text copied ✓",
        ["toast.ocrFailed"]     = "OCR failed",
        ["toast.noRegion"]      = "No region to capture",
        ["toast.captureFailed"] = "Capture failed",
        ["toast.noActiveWindow"]= "Couldn't find the active window",
        ["toast.windowReadFail"]= "Couldn't read the window bounds",
        ["toast.noLastRegion"]  = "No region captured yet — select a region first",
        ["toast.countdown"]     = "Capturing in {0}s…",
        ["toast.ocrDownloading"]= "Downloading {0} OCR model… ({1})",
        ["toast.ocrDownloadFail"]= "Failed to download the {0} OCR model",

        // ---- settings window (SettingsWindow.cs) ----
        ["set.title"]           = "wsnap Settings",
        ["set.header"]          = "Settings",
        ["set.cardStorage"]     = "Storage",
        ["set.saveFolder"]      = "Save folder",
        ["set.browse"]          = "Browse",
        ["set.filenameTemplate"]= "Filename format",
        ["set.templateHint"]    = "Tokens: {app} {title} {date} {time} {seq} {w} {h} · or a date format like {yyyy-MM-dd_HHmmss}",
        ["set.keepHistory"]     = "Keep captures permanently in date folders (history)",
        ["set.cardCapture"]     = "Capture",
        ["set.autoCopy"]        = "Copy to clipboard automatically on capture (ready for Ctrl+V)",
        ["set.toolbar"]         = "Show an action toolbar after selecting a region (off: drag → instant thumbnail bottom-right)",
        ["set.toolbarHint"]     = "Default: off — a drag pops the capture straight into a bottom-right thumbnail. When on, the selection shows a copy/save/edit/OCR/GIF/pin toolbar.",
        ["set.cardThumbs"]      = "Thumbnails",
        ["set.autoDismiss"]     = "Auto-dismiss (seconds) · 0 = off",
        ["set.maxVisible"]      = "Max shown at once",
        ["set.off"]             = "Off",
        ["set.cardHotkey"]      = "Hotkey",
        ["set.captureHotkey"]   = "Capture hotkey",
        ["set.change"]          = "Change",
        ["set.pressKeys"]       = "Press a key combination…",
        ["set.swallowWinShiftS"]= "Also intercept Win+Shift+S (replaces the OS Snipping Tool)",
        ["set.cardResident"]    = "Resident behavior",
        ["set.startWithWindows"]= "Start automatically with Windows",
        ["set.clipboardWatch"]  = "Auto-thumbnail images copied to the clipboard",
        ["set.telemetry"]       = "Keep an anonymous usage log (local only, opt-in)",
        ["set.cardUpload"]      = "Upload (optional)",
        ["set.uploadEnabled"]   = "Enable Imgur upload",
        ["set.imgurId"]         = "Imgur Client-ID",
        ["set.cardLanguage"]    = "Language",
        ["set.language"]        = "Display language",
        ["set.languageHint"]    = "Some already-open windows update after a restart.",
        ["set.cardOcr"]         = "OCR",
        ["set.ocrLanguage"]     = "Text recognition language",
        ["set.ocrLanguageHint"] = "Language used for text extraction (OCR), independent of the display language above. Korean (incl. English) ships built in; other packs download on first use (~8–85 MB).",
        ["set.cancel"]          = "Cancel",
        ["set.save"]            = "Save",

        // ---- thumbnail window (ThumbnailWindow.cs) ----
        ["thumb.edited"]        = "Edited",
        ["thumb.copy"]          = "Copy image (Ctrl+click = path)",
        ["thumb.saveAs"]        = "Save as",
        ["thumb.edit"]          = "Edit",
        ["thumb.ocr"]           = "Extract text (OCR)",
        ["thumb.reveal"]        = "Show in folder",
        ["thumb.share"]         = "Upload and copy link",
        ["thumb.close"]         = "Close",
        ["thumb.pin"]           = "Pin (disable auto-dismiss)",
        ["thumb.pinned"]        = "Pinned — won't auto-dismiss",
        ["thumb.pathCopied"]    = "Path copied",
        ["thumb.copyFail"]      = "Copy failed — another app may be using the clipboard",
        ["thumb.pngFilter"]     = "PNG image (*.png)|*.png|All files (*.*)|*.*",
        ["thumb.saveTitle"]     = "Save capture",
        ["thumb.saved"]         = "Saved ✓",
        ["thumb.saveFail"]      = "Save failed",
        ["thumb.uploadDisabled"]= "Upload disabled — enable Imgur in settings",
        ["thumb.uploading"]     = "Uploading…",
        ["thumb.uploadFail"]    = "Upload failed",
        ["thumb.linkCopied"]    = "Link copied ✓",

        // ---- editor (EditorWindow.cs) ----
        ["ed.title"]            = "wsnap — Edit",
        ["ed.toolSelect"]       = "Select",
        ["ed.toolSelectTip"]    = "Select · move (V) · Del to delete",
        ["ed.toolArrow"]        = "Arrow",
        ["ed.toolArrowTip"]     = "Arrow (A)",
        ["ed.toolLine"]         = "Line",
        ["ed.toolLineTip"]      = "Line (L) · Shift = 45°",
        ["ed.toolRect"]         = "Rect",
        ["ed.toolRectTip"]      = "Rectangle (R) · Shift = square",
        ["ed.toolEllipse"]      = "Circle",
        ["ed.toolEllipseTip"]   = "Ellipse (O) · Shift = circle",
        ["ed.toolPen"]          = "Pen",
        ["ed.toolPenTip"]       = "Pen (P)",
        ["ed.toolHighlight"]    = "Marker",
        ["ed.toolHighlightTip"] = "Highlighter (H)",
        ["ed.toolText"]         = "Text",
        ["ed.toolTextTip"]      = "Text (T)",
        ["ed.toolCounter"]      = "Number",
        ["ed.toolCounterTip"]   = "Numbered step (N)",
        ["ed.toolMosaic"]       = "Mosaic",
        ["ed.toolMosaicTip"]    = "Mosaic (M) · thickness sets strength (thick = fully hidden)",
        ["ed.toolBlur"]         = "Blur",
        ["ed.toolBlurTip"]      = "Blur (B) · thickness sets strength",
        ["ed.toolCrop"]         = "Crop",
        ["ed.toolCropTip"]      = "Crop (C) · Shift = square",
        ["ed.thin"]             = "Thin",
        ["ed.medium"]           = "Medium",
        ["ed.thick"]            = "Thick",
        ["ed.customColor"]      = "Custom color",
        ["ed.copy"]             = "Copy",
        ["ed.copyTip"]          = "Copy (Ctrl+C)",
        ["ed.save"]             = "Save",
        ["ed.saveTip"]          = "Save (Enter)",
        ["ed.cancel"]           = "Cancel",
        ["ed.cancelTip"]        = "Cancel (Esc)",
        ["ed.strokeTip"]        = "Stroke width {0}px",
        ["ed.copied"]           = "Image copied ✓",
        ["ed.copyFail"]         = "Copy failed",
        ["ed.copyFailEx"]       = "Copy failed: {0}",
        ["ed.saveFailEx"]       = "Save failed: {0}",

        // ---- history (HistoryWindow.cs) ----
        ["hist.title"]          = "wsnap — Capture History",
        ["hist.header"]         = "Capture History",
        ["hist.refresh"]        = "Refresh",
        ["hist.openFolder"]     = "Open folder",
        ["hist.empty"]          = "No captures saved yet",
        ["hist.emptyHint"]      = "Turn on 'Keep history' in settings to archive captures by date",
        ["hist.openSettings"]   = "Open settings",
        ["hist.count"]          = "{0}",
        ["hist.copy"]           = "Copy image",
        ["hist.edit"]           = "Edit",
        ["hist.reveal"]         = "Show in folder",
        ["hist.open"]           = "Open",
        ["hist.delete"]         = "Delete",
        ["hist.pathCopied"]     = "Path copied",
        ["hist.imageCopied"]    = "Image copied ✓",
        ["hist.copyFail"]       = "Copy failed",
        ["hist.notFound"]       = "File not found",
        ["hist.confirmDelete"]  = "Delete this capture?",
        ["hist.deleteTitle"]    = "Delete",

        // ---- capture overlay (CaptureOverlay.cs) ----
        ["ov.hintOcr"]          = "Drag a text region · click a window · Esc to cancel",
        ["ov.hintColor"]        = "Click a pixel to copy its color · Esc to cancel",
        ["ov.hint"]             = "Drag = region · click window = window capture · C = copy color · Esc to cancel",
        ["ov.colorCopied"]      = "{0} copied ✓",
        ["ov.window"]           = "Window",
        ["ov.copy"]             = "Copy (C)",
        ["ov.save"]             = "Save (Enter)",
        ["ov.edit"]             = "Edit (E)",
        ["ov.ocr"]              = "Extract text (T)",
        ["ov.gif"]              = "Record GIF (G)",
        ["ov.pin"]              = "Pin (P)",
        ["ov.cancel"]           = "Cancel (Esc)",

        // ---- GIF recorder (GifRecorder.cs) ----
        ["gif.recording"]       = "● Recording · {0} frames · stop (click/Esc)",
        ["gif.recording0"]      = "● Recording · 0 frames · stop (click/Esc)",
        ["gif.canceled"]        = "Recording canceled",
        ["gif.encoding"]        = "Encoding GIF…",
        ["gif.saveFail"]        = "Failed to save GIF",

        // ---- scroll capture (ScrollCapture.cs) ----
        ["scroll.canceled"]     = "Scrolling capture canceled",
        ["scroll.saveFail"]     = "Failed to save scrolling capture",
        ["scroll.recording"]    = "Scrolling capture… stop (click/Esc)",
    };

    // ---------------------------------------------------------------- 한국어
    private static readonly Dictionary<string, string> Ko = new()
    {
        // ---- tray menu ----
        ["tray.captureRegion"]  = "영역 캡처  ({0})",
        ["tray.captureShort"]   = "캡처  ({0})",
        ["tray.captureFull"]    = "전체 화면 캡처",
        ["tray.captureWindow"]  = "현재 창 캡처",
        ["tray.repeatRegion"]   = "직전 영역 다시 캡처",
        ["tray.delay"]          = "지연 캡처",
        ["tray.delay3"]         = "3초 후 영역 캡처",
        ["tray.delay5"]         = "5초 후 영역 캡처",
        ["tray.ocr"]            = "텍스트 추출 (OCR 영역)",
        ["tray.colorPick"]      = "색 추출 (스포이드)",
        ["tray.gif"]            = "GIF 녹화 (영역)",
        ["tray.scroll"]         = "스크롤 캡처 (영역)",
        ["tray.openFolder"]     = "캡처 폴더 열기",
        ["tray.history"]        = "캡처 히스토리…",
        ["tray.clearThumbs"]    = "열린 썸네일 전체 지우기",
        ["tray.settings"]       = "설정…",
        ["tray.exit"]           = "종료",
        ["tray.tip"]            = "wsnap — {0}로 캡처",

        // ---- toasts ----
        ["toast.hookFailed"]    = "단축키 훅 설치 실패 — 보안 소프트웨어가 막았을 수 있어요. 트레이 메뉴로 캡처하세요.",
        ["toast.imageCopied"]   = "이미지 복사됨 ✓",
        ["toast.ocrBusy"]       = "텍스트 인식 중…",
        ["toast.ocrUnavailable"]= "OCR 사용 불가 (모델 파일 누락)",
        ["toast.ocrNoText"]     = "인식된 텍스트 없음",
        ["toast.textCopied"]    = "텍스트 복사됨 ✓",
        ["toast.ocrFailed"]     = "OCR 실패",
        ["toast.noRegion"]      = "캡처할 영역이 없어요",
        ["toast.captureFailed"] = "캡처 실패",
        ["toast.noActiveWindow"]= "활성 창을 찾지 못했어요",
        ["toast.windowReadFail"]= "창 영역을 읽지 못했어요",
        ["toast.noLastRegion"]  = "아직 캡처한 영역이 없어요 — 먼저 영역을 잡아주세요",
        ["toast.countdown"]     = "{0}초 후 캡처…",
        ["toast.ocrDownloading"]= "{0} OCR 모델 다운로드 중… ({1})",
        ["toast.ocrDownloadFail"]= "{0} OCR 모델 다운로드 실패",

        // ---- settings window ----
        ["set.title"]           = "wsnap 설정",
        ["set.header"]          = "설정",
        ["set.cardStorage"]     = "저장",
        ["set.saveFolder"]      = "저장 폴더",
        ["set.browse"]          = "찾아보기",
        ["set.filenameTemplate"]= "파일 이름 형식",
        ["set.templateHint"]    = "토큰: {app} {title} {date} {time} {seq} {w} {h} · 또는 {yyyy-MM-dd_HHmmss} 같은 날짜 형식",
        ["set.keepHistory"]     = "캡처를 날짜별 폴더에 영구 보관 (히스토리)",
        ["set.cardCapture"]     = "캡처",
        ["set.autoCopy"]        = "캡처하면 자동으로 클립보드에 복사 (Ctrl+V 바로 붙여넣기)",
        ["set.toolbar"]         = "영역 선택 후 액션 툴바 표시 (끄면: 드래그하면 즉시 우하단 썸네일)",
        ["set.toolbarHint"]     = "기본값: 끔 — 드래그하면 캡처가 바로 우하단 썸네일로 떠오릅니다. 켜면 선택 영역에 복사·저장·편집·OCR·GIF·고정 툴바가 표시됩니다.",
        ["set.cardThumbs"]      = "썸네일",
        ["set.autoDismiss"]     = "자동 사라짐(초) · 0=끄기",
        ["set.maxVisible"]      = "최대 동시 표시 개수",
        ["set.off"]             = "끄기",
        ["set.cardHotkey"]      = "단축키",
        ["set.captureHotkey"]   = "캡처 단축키",
        ["set.change"]          = "변경",
        ["set.pressKeys"]       = "키 조합을 누르세요…",
        ["set.swallowWinShiftS"]= "Win+Shift+S도 가로채기 (OS 스니핑툴 대체)",
        ["set.cardResident"]    = "상주 동작",
        ["set.startWithWindows"]= "Windows 시작 시 자동 실행",
        ["set.clipboardWatch"]  = "클립보드 이미지 자동 썸네일화",
        ["set.telemetry"]       = "익명 사용 로그 남기기(로컬 전용, 옵트인)",
        ["set.cardUpload"]      = "업로드 (선택)",
        ["set.uploadEnabled"]   = "Imgur 업로드 활성화",
        ["set.imgurId"]         = "Imgur Client-ID",
        ["set.cardLanguage"]    = "언어",
        ["set.language"]        = "표시 언어",
        ["set.languageHint"]    = "이미 열려 있는 일부 창은 재시작 후 적용됩니다.",
        ["set.cardOcr"]         = "OCR",
        ["set.ocrLanguage"]     = "텍스트 인식 언어",
        ["set.ocrLanguageHint"] = "텍스트 추출(OCR)에 사용할 언어로, 위의 표시 언어와는 별개입니다. 한국어(영어 포함)는 기본 내장이며, 다른 언어팩은 첫 사용 시 다운로드됩니다 (~8~85 MB).",
        ["set.cancel"]          = "취소",
        ["set.save"]            = "저장",

        // ---- thumbnail window ----
        ["thumb.edited"]        = "수정됨",
        ["thumb.copy"]          = "이미지 복사 (Ctrl+클릭=경로)",
        ["thumb.saveAs"]        = "다른 이름으로 저장",
        ["thumb.edit"]          = "편집",
        ["thumb.ocr"]           = "텍스트 추출 (OCR)",
        ["thumb.reveal"]        = "폴더에서 보기",
        ["thumb.share"]         = "업로드 후 링크 복사",
        ["thumb.close"]         = "닫기",
        ["thumb.pin"]           = "고정 (자동 사라짐 끄기)",
        ["thumb.pinned"]        = "고정됨 — 자동으로 사라지지 않아요",
        ["thumb.pathCopied"]    = "경로 복사됨",
        ["thumb.copyFail"]      = "복사 실패 — 클립보드를 사용하는 다른 앱이 있을 수 있어요",
        ["thumb.pngFilter"]     = "PNG 이미지 (*.png)|*.png|모든 파일 (*.*)|*.*",
        ["thumb.saveTitle"]     = "캡처 저장",
        ["thumb.saved"]         = "저장됨 ✓",
        ["thumb.saveFail"]      = "저장 실패",
        ["thumb.uploadDisabled"]= "업로드 비활성화됨 — 설정에서 Imgur 켜기",
        ["thumb.uploading"]     = "업로드 중…",
        ["thumb.uploadFail"]    = "업로드 실패",
        ["thumb.linkCopied"]    = "링크 복사됨 ✓",

        // ---- editor ----
        ["ed.title"]            = "wsnap — 편집",
        ["ed.toolSelect"]       = "선택",
        ["ed.toolSelectTip"]    = "선택·이동 (V) · Del 삭제",
        ["ed.toolArrow"]        = "화살표",
        ["ed.toolArrowTip"]     = "화살표 (A)",
        ["ed.toolLine"]         = "직선",
        ["ed.toolLineTip"]      = "직선 (L) · Shift=45°",
        ["ed.toolRect"]         = "사각",
        ["ed.toolRectTip"]      = "사각형 (R) · Shift=정사각",
        ["ed.toolEllipse"]      = "원",
        ["ed.toolEllipseTip"]   = "타원 (O) · Shift=정원",
        ["ed.toolPen"]          = "펜",
        ["ed.toolPenTip"]       = "펜 (P)",
        ["ed.toolHighlight"]    = "형광",
        ["ed.toolHighlightTip"] = "형광펜 (H)",
        ["ed.toolText"]         = "텍스트",
        ["ed.toolTextTip"]      = "텍스트 (T)",
        ["ed.toolCounter"]      = "번호",
        ["ed.toolCounterTip"]   = "번호 단계 (N)",
        ["ed.toolMosaic"]       = "모자이크",
        ["ed.toolMosaicTip"]    = "모자이크 (M) · 두께로 강도 조절 (굵게=완전 가림)",
        ["ed.toolBlur"]         = "흐림",
        ["ed.toolBlurTip"]      = "흐림 (B) · 두께로 강도 조절",
        ["ed.toolCrop"]         = "자르기",
        ["ed.toolCropTip"]      = "자르기 (C) · Shift=정사각",
        ["ed.thin"]             = "가늘게",
        ["ed.medium"]           = "보통",
        ["ed.thick"]            = "굵게",
        ["ed.customColor"]      = "사용자 지정 색",
        ["ed.copy"]             = "복사",
        ["ed.copyTip"]          = "복사 (Ctrl+C)",
        ["ed.save"]             = "저장",
        ["ed.saveTip"]          = "저장 (Enter)",
        ["ed.cancel"]           = "취소",
        ["ed.cancelTip"]        = "취소 (Esc)",
        ["ed.strokeTip"]        = "선 두께 {0}px",
        ["ed.copied"]           = "이미지 복사됨 ✓",
        ["ed.copyFail"]         = "복사 실패",
        ["ed.copyFailEx"]       = "복사 실패: {0}",
        ["ed.saveFailEx"]       = "저장 실패: {0}",

        // ---- history ----
        ["hist.title"]          = "wsnap — 캡처 히스토리",
        ["hist.header"]         = "캡처 히스토리",
        ["hist.refresh"]        = "새로고침",
        ["hist.openFolder"]     = "폴더 열기",
        ["hist.empty"]          = "아직 저장된 캡처가 없어요",
        ["hist.emptyHint"]      = "설정에서 '히스토리 보관'을 켜면 날짜별로 영구 저장됩니다",
        ["hist.openSettings"]   = "설정 열기",
        ["hist.count"]          = "{0}장",
        ["hist.copy"]           = "이미지 복사",
        ["hist.edit"]           = "편집",
        ["hist.reveal"]         = "폴더에서 보기",
        ["hist.open"]           = "열기",
        ["hist.delete"]         = "삭제",
        ["hist.pathCopied"]     = "경로 복사됨",
        ["hist.imageCopied"]    = "이미지 복사됨 ✓",
        ["hist.copyFail"]       = "복사 실패",
        ["hist.notFound"]       = "파일을 찾을 수 없어요",
        ["hist.confirmDelete"]  = "이 캡처를 삭제할까요?",
        ["hist.deleteTitle"]    = "삭제",

        // ---- capture overlay ----
        ["ov.hintOcr"]          = "텍스트 영역 드래그 · 창 클릭 · Esc 취소",
        ["ov.hintColor"]        = "픽셀을 클릭해 색을 복사 · Esc 취소",
        ["ov.hint"]             = "드래그=영역 · 창 클릭=창 캡처 · C=색 복사 · Esc 취소",
        ["ov.colorCopied"]      = "{0} 복사됨 ✓",
        ["ov.window"]           = "창",
        ["ov.copy"]             = "복사 (C)",
        ["ov.save"]             = "저장 (Enter)",
        ["ov.edit"]             = "편집 (E)",
        ["ov.ocr"]              = "텍스트 추출 (T)",
        ["ov.gif"]              = "GIF 녹화 (G)",
        ["ov.pin"]              = "고정 (P)",
        ["ov.cancel"]           = "취소 (Esc)",

        // ---- GIF recorder ----
        ["gif.recording"]       = "● 녹화 중 · {0} 프레임 · 중지(클릭/Esc)",
        ["gif.recording0"]      = "● 녹화 중 · 0 프레임 · 중지(클릭/Esc)",
        ["gif.canceled"]        = "녹화 취소됨",
        ["gif.encoding"]        = "GIF 인코딩 중…",
        ["gif.saveFail"]        = "GIF 저장 실패",

        // ---- scroll capture ----
        ["scroll.canceled"]     = "스크롤 캡처 취소됨",
        ["scroll.saveFail"]     = "스크롤 캡처 저장 실패",
        ["scroll.recording"]    = "스크롤 캡처 중… 중지(클릭/Esc)",
    };
}
