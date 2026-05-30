# Changelog

All notable changes to wsnap are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/); versioning is [SemVer](https://semver.org/).

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
