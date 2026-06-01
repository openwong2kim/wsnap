# Changelog

All notable changes to wsnap are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/); versioning is [SemVer](https://semver.org/).

## [1.3.1] - 2026-06-02

### Legal / packaging
- **Third-party license notices now ship with wsnap.** v1.3.0 bundles the ONNX Runtime,
  SkiaSharp, RapidOcrNet, Clipper2, and PaddleOCR PP-OCRv5 models into the exe — all under
  permissive licenses (MIT / Apache-2.0 / BSD-3 / Boost) compatible with wsnap's GPL-3.0,
  but their attribution/license texts weren't being conveyed with the binary. Added
  `THIRD-PARTY-NOTICES.md`, included it in the installer, and the portable zip now bundles
  `LICENSE`, `NOTICE`, and `THIRD-PARTY-NOTICES.md` alongside the exe. No code change.

[1.3.1]: https://github.com/openwong2kim/wsnap/releases/tag/v1.3.1

## [1.3.0] - 2026-06-01

### Changed
- **Far better OCR.** The text-recognition engine moved off the built-in Windows OCR
  (`Windows.Media.Ocr`), which mangled mixed Korean/English — confusing `O`↔`0`, `l`↔`I`,
  dropping or garbling Hangul (e.g. `프로토콜` → `하오토콜`). wsnap now runs **PaddleOCR
  PP-OCRv5 models on ONNX Runtime** (via RapidOcrNet) with a dedicated Korean recognition
  model that also covers English, digits, and symbols. Still **fully offline and free** — no
  network, no tracking. In testing, the exact strings the old engine garbled
  (`프로토콜`, `Codex`, `Electron`, `최적화`) now come back correct.
  - No language pack required anymore — the models ship inside the exe.
  - The engine is **loaded lazily on first use and released after a short idle**, so the
    resident tray footprint from 1.2.4 is preserved when you're not actively running OCR.
  - The download grew (bundled ONNX runtime + models); this does not affect idle memory.

[1.3.0]: https://github.com/openwong2kim/wsnap/releases/tag/v1.3.0

## [1.2.4] - 2026-06-01

### Changed
- **Much smaller resident memory footprint.** Sitting idle in the tray, wsnap used to hold
  ~125 MB working set / ~240 MB committed; it now sits at a single-digit working set and
  ~85 MB committed (measured on the same machine, idle).
  - The **working set is returned to the OS** on an idle timer — and once after startup
    settles, and after a capture's transient bitmaps are freed — so Task Manager reflects
    what's actually in use rather than everything the process has ever touched.
  - The **GC runs in memory-conserving mode** and hands freed memory back to the OS instead
    of retaining the virtual address space (`System.GC.ConserveMemory`, `RetainVM=false`).
  - **Floating thumbnails decode to ~2× their on-screen size** instead of the capture's full
    resolution. A 4K grab was a ~33 MB in-memory bitmap; pinned thumbnails stay resident, so
    this directly shrinks idle memory. Drag-out and all actions still use the original file.
  - **ICU globalization data (~28 MB) is dropped** (`InvariantGlobalization`) — every date /
    number format in the app already uses an invariant culture, so output is unchanged.

[1.2.4]: https://github.com/openwong2kim/wsnap/releases/tag/v1.2.4

## [1.2.3] - 2026-06-01

### Internal
- Wired **SignPath code signing** into the release pipeline (`release.yml`), kept dormant
  until the signing variables are configured, and added `SIGNING.md`. No user-facing
  behavior change from 1.2.2 — releases remain unsigned until SignPath is enabled.

[1.2.3]: https://github.com/openwong2kim/wsnap/releases/tag/v1.2.3

## [1.2.2] - 2026-06-01

### Fixed
- **Multi-monitor placement.** The bottom-right floating widgets — the capture thumbnail
  and the toast notification — clashed with the taskbar once a second monitor was attached
  (single-monitor was fine). Root cause: `SystemParameters.WorkArea` and `Window.Left/Top`
  are primary-monitor-only logical (DIU) coordinates, and under PerMonitorV2 awareness a
  secondary monitor at a different scale breaks that mapping. They are now placed in real
  device pixels on the monitor under the cursor via a new `MonitorPlacement` helper
  (`SetWindowPos`), so they sit correctly on whichever screen you're working on.

[1.2.2]: https://github.com/openwong2kim/wsnap/releases/tag/v1.2.2

## [1.2.1] - 2026-06-01

### Changed
- **License: Apache-2.0 → GPL-3.0.** wsnap is now copyleft — redistributed or modified
  versions must also be open source under the GPL. `LICENSE` (full GPLv3), `NOTICE`, every
  source file header, and the scoop/winget/landing metadata were updated accordingly.
- Capture default restored to **instant thumbnail**: dragging a region pops the bottom-right
  thumbnail immediately (auto-copy still on). The post-capture action toolbar is now **opt-in**
  (Settings → 캡처) instead of the default, matching wsnap's drag→thumbnail identity.

### Fixed
- **Mosaic/blur now actually redacts.** A GDI+ edge-sampling bug dropped the alpha of the
  top/edge blocks (~50% transparent), so the original text showed straight through. Edge
  sampling is now clamped (WrapMode.TileFlipXY) → fully opaque blocks; verified that OCR can
  no longer read a mosaicked region. Block strength is also tied to the thickness control
  (가늘게/보통/굵게) so it can be cranked up.
- **OCR on small text** is more accurate: captures are auto-upscaled (high-quality, up to 3×)
  before recognition, which the Windows OCR engine handles much better.

[1.2.1]: https://github.com/openwong2kim/wsnap/releases/tag/v1.2.1

## [1.2.0] - 2026-05-31

Power-user round: the editor gets real object manipulation, captures gain a history
gallery and window detection, and filenames become yours to template.

### Added
- **Editor object select / move / delete** — a Select tool (V): click an annotation to
  pick it (handles + marquee), drag to move, Delete to remove. Move and delete are fully
  **undo/redo**-able. Per-type aware (shapes by position, pen by points, arrows by geometry).
- **Window auto-detection** — in the capture overlay, hover a window to highlight it
  (punch-through + title), click (no drag) to capture just that window. Physical-pixel
  rects via DWM extended frame bounds; cloaked/minimized windows skipped.
- **Capture history gallery** (tray → 캡처 히스토리…) — browse every saved shot (scratch +
  date folders + pinned) as a light thumbnail grid; per-tile **drag-out as a file**,
  click-to-copy-image, edit, reveal, open, delete (to Recycle Bin). A rolling
  `HistoryKeepRecent` buffer (default 50) keeps recent shots even with history off.
- **Filename templates** — `{app} {title} {date} {time} {seq} {w} {h}` plus raw .NET date
  formats (e.g. `{yyyy-MM-dd_HHmmss}`); the foreground app/window title is captured before
  the overlay steals focus. Sanitized (invalid chars, reserved names, length) with a safe
  fallback. Set it in Settings → 저장 → 파일 이름 형식.

### Notes
- True **MP4/H.264 video** was scoped and prototyped (Media Foundation SinkWriter, gated
  opt-in with a GIF fallback) but **deferred**: it could not be verified in this build
  environment (the MF sink writer object returned `E_NOINTERFACE` for `IMFSinkWriter`, i.e.
  no functional H.264 sink to validate against), and shipping unverifiable COM interop
  risks crashes on real hardware. GIF recording remains the video path. Tracked for a
  future release pending validation on real machines.

[1.2.0]: https://github.com/openwong2kim/wsnap/releases/tag/v1.2.0

## [1.1.0] - 2026-05-31

Big UX/UI release: the clipboard now works the way Windows users expect, the capture
overlay is a precision tool, the editor is genuinely capable, and the whole app shares
one dark design system.

### Added
- **Click = copy IMAGE** (not the file path) — and an opt-in **auto-copy on capture**, so
  a shot is `Ctrl+V`-ready in Slack/Jira/Figma instantly. Multi-format clipboard
  (DIB + PNG-with-alpha + FileDrop) with retry. `Ctrl+click` still copies the path.
- **Post-capture action toolbar** at the selection: 복사 · 저장 · 편집 · 텍스트(OCR) ·
  GIF · 고정, keyboard-driven (C/Enter/E/T/G/P, Esc). Toggle off in Settings for the
  old instant flow.
- **Frozen-screen overlay** with a **punch-through dim** (the selection reads bright, the
  rest dims), a live **W×H badge**, and a **magnifier loupe** showing zoomed pixels,
  cursor coordinates and the **hex colour** under the cursor (press **C** to copy it).
- **Colour picker** (eyedropper) tray mode — click a pixel, get its `#RRGGBB`.
- **Pin** a thumbnail so it never auto-dismisses; pinned shots are promoted out of `%TEMP%`
  so temp cleanup can't delete them. `자동 사라짐 0초 = 끄기`.
- **Thumbnail action bar**: 복사 · 저장 · 편집 · 텍스트 · 폴더에서 보기 · 공유(업로드) ·
  고정 · 닫기 (icon buttons). The **Imgur upload** path is now wired to a Share button.
- **Editor**: line, ellipse, highlighter, numbered-step badges, and a smooth blur (next to
  mosaic); **redo** (Ctrl+Y / Ctrl+Shift+Z); **undoable crop**; **copy to clipboard**
  (Ctrl+C); a **thickness** control; a custom **colour** picker; **Shift** to constrain
  (45° lines / squares); active-tool & active-colour highlighting.
- **More capture modes** in the tray: 전체 화면 · 현재 창 · 직전 영역 다시 캡처 ·
  지연(3/5초) 캡처 · 캡처 폴더 열기.

### Changed
- **One dark design system** (`Theme.cs`) — the editor and settings no longer drop to white
  OS chrome; title bars are dark (DWM). Settings re-skinned into cards with themed inputs.
- Capture grab now uses physical cursor coordinates, fixing region size/position on
  mixed-DPI multi-monitor setups; cropping from the frozen bitmap removes the old
  grab-after-hide race.
- Thumbnails get an entrance pop and a fading action bar.

[1.1.0]: https://github.com/openwong2kim/wsnap/releases/tag/v1.1.0

## [1.0.2] - 2026-05-30

### Fixed
- Editor drawing tools did nothing — the drawing canvas had no background so it never
  received mouse input. Now hit-testable; all tools (arrow/rect/pen/text/mosaic/crop) work.
- Text annotation: typing no longer leaks into tool shortcuts and Enter no longer saves
  mid-typing (keys are owned by the focused text box).

### Changed
- Saving an edit now pops the edited result as its own fresh bottom-right thumbnail
  (drag-and-droppable), leaving the original in place.
- Edited thumbnails show a "수정됨" (edited) badge in the top-right corner.

[1.0.2]: https://github.com/openwong2kim/wsnap/releases/tag/v1.0.2

## [1.0.1] - 2026-05-30

### Added
- Real application icon (blue rounded tile + white viewfinder corner-marks), embedded so
  the tray icon and exe/window icons use it instead of the stock system icon.

### Changed
- Installer is now version-parameterized (`ISCC /DAppVersion=…`), sets its own icon, and
  installs a per-user startup registry entry when the "start with Windows" task is chosen.
- Production landing page redesign (served via GitHub Pages).

[1.0.1]: https://github.com/openwong2kim/wsnap/releases/tag/v1.0.1

## [1.0.0] - 2026-05-30

First public release. macOS-style capture for Windows with drag-and-drop as the primary action.

### Added
- **Capture → floating thumbnail → drag-and-drop** as a real file (path in terminals, file in Explorer/chat/upload). Click to copy path.
- **Thumbnail stack** (configurable max, newest at the bottom) with per-thumbnail edit / OCR / delete and right-drag to dismiss.
- **Minimal editor**: crop, arrow, rectangle, pen, text, mosaic — keyboard-first with undo.
- **On-device OCR** (Windows.Media.Ocr, KO/EN) from the thumbnail or a dedicated region mode.
- **GIF recording** of a region (looping animated GIF with proper frame delays).
- **Scroll capture** (best-effort wheel-scroll + overlap stitching).
- **Clipboard watch** mode — thumbnails images copied by any tool.
- **Optional Imgur upload** (opt-in, user-supplied Client-ID).
- Resident essentials: tray menu, configurable hotkey (default Shift+F1) with optional Win+Shift+S interception, start-with-Windows, single instance, settings persistence, crash logging, opt-in local telemetry.
- PerMonitorV2 DPI-correct capture across mixed-DPI / fractional-scaling monitors.

### Packaging
- Self-contained single-file `wsnap.exe` (`publish.ps1`).
- Inno Setup installer (`installer.iss`, built in CI).
- GitHub Actions: CI build, tag-triggered release, GitHub Pages landing.

[1.0.0]: https://github.com/openwong2kim/wsnap/releases/tag/v1.0.0
