# Changelog

All notable changes to wsnap are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/); versioning is [SemVer](https://semver.org/).

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
