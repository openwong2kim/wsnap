# Changelog

All notable changes to wsnap are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/); versioning is [SemVer](https://semver.org/).

## [1.9.0] - 2026-07-10

The preview release. wsnap's next UI stack ships today as an **opt-in preview** — the same app,
rebuilt on Avalonia — while the trusty WPF build stays the default you install and update.

### Added — Avalonia preview (`wsnap-avalonia.exe`)
- **The whole app, ported to Avalonia UI** (Skia rendering — the same path ShareX's own rewrite
  is taking): capture overlay with loupe and window detection, editor (12 tools, undo/redo,
  crop), thumbnails, history, settings, toasts, GIF/video/scroll recorders, OCR, i18n, the dark
  tray, and the full CLI/MCP control layer. Feature parity with the WPF build, verified
  screen-pixel-by-pixel along the way.
- **Runs side-by-side** with regular wsnap: a portable exe with its own name, sharing the
  single-instance guard — only one resident app at a time, so hotkeys never fight. Download it,
  run it, and your existing settings, history, and hotkeys just work (same config).
- **Preview status:** unsigned portable exe, no installer yet. If the preview holds up, Avalonia
  becomes the default in an upcoming release. Found something off? Open an issue.

### Changed
- **PNG encoding unified on SkiaSharp** (already bundled for OCR) — capture saves, clipboard
  writes, and MCP resizes all use one image stack now instead of three.
- **GIF recording uses wsnap's own GIF89a encoder** (octree palette + proper LZW) instead of the
  WPF encoder plus byte patching — same files, one less framework dependency, and measurably
  better gradient quantization.

## [1.8.0] - 2026-07-10

The diet release. Same features, roughly **half the memory** and a capture that fires the
instant you press the hotkey — measured, not promised: idle footprint went from ~304 MB to
~147 MB working set (159 → 82 MB private) on the same machine, and the post-idle "why is the
overlay taking a second" page-fault storm is gone entirely.

### Changed — performance & weight
- **No more working-set purging.** The idle EmptyWorkingSet trim made Task Manager look good and
  the next hotkey press feel terrible (the whole process had to page back in before the overlay
  could appear). Removed; a compacting GC after each capture keeps the footprint honest instead.
- **GPU screen grab.** The overlay freeze and full-screen captures now use DXGI desktop
  duplication (the compositor's own frame, no yellow border) with automatic GDI fallback for
  RDP/rotated-display/driver edge cases. Recorders (GIF/video/scroll) stay on GDI where per-frame
  duplication setup would cost more than it saves.
- **Render pipeline prewarm.** The first window WPF shows pays JIT + D3D composition setup —
  wsnap now pays it invisibly at startup instead of on your first capture.
- **25 MB of WinRT gone.** Nothing had used the Windows SDK projection since OCR moved to ONNX in
  v1.3; retargeting to plain `net8.0-windows` deletes it from the exe.
- **Korean OCR pack downloads on first use** (13.4 MB out of the exe), exactly like every other
  language pack already did — same HuggingFace files, byte-identical. Upgrading users keep their
  already-extracted models (migrated in place, no re-download). det/cls stay embedded (5.4 MB).
- **Single-file compression off.** Compressed assemblies can't be memory-mapped, so the runtime
  inflated ~110 MB of them into swap-backed private pages at every start — that was a third of
  the resident footprint and ~half the startup CPU. The exe on disk grows; the installer/zip
  re-compress it for download, and file-backed pages are reclaimable for free.

### Changed — design
- **Dark tray menu.** The last stock-Windows-looking surface now matches the design system:
  dark panel, rounded hover, hairline separators, themed submenus.
- **Overlay selection got macOS-style corner handles**, and the size badge / hint became bordered
  pills on design-system tokens.
- **Toast refresh:** design-system panel, hairline border, accent dot, ease-out rise animation.

## [1.7.0] - 2026-07-03

wsnap becomes controllable — by you from the command line, and by AI agents through MCP —
without giving up its offline, no-account, privacy-first identity. A new command layer lets
Claude (or any script) drive capture, OCR, colour, GIF, and history, all behind a single
security gate that stays **off until you turn it on**.

### Added
- **Command-line interface (`wsnap <verb>`).** Drive wsnap from a terminal or a script: capture a
  region / full screen / window, OCR a region or an image file, read a pixel's colour, record a
  GIF, and query capture history — with `--json` for machine-readable output and pipe-friendly
  `--out -` streaming. Headless commands run without the tray; interactive ones delegate to the
  running instance so history, clipboard, and thumbnails stay consistent.
- **MCP server (`wsnap mcp`) — control wsnap from Claude Desktop and Claude Code.** wsnap now
  speaks the Model Context Protocol over local stdio, so an AI agent can take a screenshot, read
  text off the screen, sample a colour, record a short GIF, or pull up a past capture — as real
  tools, on demand. Register it with `claude mcp add wsnap -- wsnap mcp` (or the Claude Desktop
  config snippet). Everything wsnap already does well — pixel-precise region capture across
  mixed-DPI monitors, accurate on-device KO/EN OCR — is what the agent gets, and nothing leaves
  your machine.
- **Multiple hotkey bindings.** The single global hotkey became a list: bind as many chords as you
  like, each to its own command (interactive capture, repeat last region, OCR last, delayed
  capture, colour pick, …), each with its own swallow behaviour. Existing single-hotkey configs
  (and the optional Win+Shift+S interception) migrate automatically, so nothing changes for you
  unless you add more.
- **Automation: watch-folder OCR and clipboard auto-OCR.** Point wsnap at a folder and any image
  dropped into it is auto-OCR'd — the text lands on your clipboard and beside the image as a
  `.txt` sidecar. Optionally, any image copied to the clipboard is OCR'd the same way. Both are
  opt-in and run entirely on-device.

### Security & privacy
- **One command bus, one gate.** Every command — from the tray, a hotkey, the CLI, or MCP — flows
  through a single **ControlGate** that enforces consent, visibility, rate limits, and
  content-return policy at the one place pixels are actually read. Physical, user-initiated
  triggers (hotkey, tray) keep their exact existing behaviour; only external (CLI / MCP) sources
  are policed.
- **External control is OFF by default.** While it is off, the local control pipe server is never
  even created — no listener, no attack surface. Turn it on in Settings, and the **first** external
  connection asks for your consent before anything is captured.
- **Local only, never the network.** MCP is local stdio; the CLI/agent control channel is a Windows
  named pipe restricted to your own user account (session-local DACL). There is **no network
  listener**, by design and enforced in code — an agent on another machine cannot reach wsnap.
- **Visible by default, with an audit trail.** Every externally-initiated capture raises a visible
  signal (shutter / toast, plus a tray badge while an external session is active) and appends a
  line to `%APPDATA%\wsnap\audit.log`. The audit log records **metadata only** — time, source,
  command, size, allow/deny — and never the OCR text, image bytes, or window titles. GIF recording
  always shows a red recording badge that doubles as a one-click kill-switch.
- **Returning content is a separate, second opt-in.** Handing captured pixels or OCR text back to
  an external caller (the real exfiltration channel) is gated independently and off by default;
  when it's disallowed, results are redacted before they leave. Per-source rate limits cap bursts.
  wsnap does **not** claim to defend against malware already running as you (the OS can screenshot
  anyway) — the goal is to never be the quieter, easier tool for it, and to make every AI capture
  consensual and visible.

### Changed
- Version bumped to 1.7.0.

[1.7.0]: https://github.com/openwong2kim/wsnap/releases/tag/v1.7.0

## [1.6.0] - 2026-07-02

### Added
- **Region video recording (MP4 + APNG).** wsnap could record a region as a looping GIF; it
  can now also record real video. Pick **Record Video (Region)** from the tray and choose:
  - **MP4 (H.264)** — a small, universally-playable `.mp4` (yuv420p, faststart).
  - **APNG (Animated PNG)** — a lossless, full-colour RGBA looping `.apng` (effectively a
    lossless GIF). APNG has no 256-colour limit and supports alpha, so it beats GIF on
    anything with gradients, text, or transparency.
  The thumbnail works for both: an MP4 gets an auto-extracted first-frame poster (H.264 has
  no image decoder); an APNG is itself a valid PNG so WPF shows frame 1 directly. Drag the
  thumbnail to hand off the file; Edit/OCR are hidden for video (they're image-only), and a
  plain click copies the path.
- **On-device ffmpeg resolution.** Video encodes through a bundled-or-resolved ffmpeg
  (found on PATH, or downloaded on demand to `%LOCALAPPDATA%\wsnap\ffmpeg` — the same
  pattern as the OCR language packs, so the single-file exe stays lean). This deliberately
  avoids the Media Foundation SinkWriter path, which needs `mfplat.dll` and is absent on
  Windows N / stripped images (the reason the earlier H.264 prototype was deferred). ffmpeg
  is environment-agnostic and works everywhere; when it can't be found, video recording
  falls back to GIF so the capture is never lost.
- **Audio for video.** MP4 recording can now include sound — microphone (dshow), system audio
  (wasapi loopback, when the ffmpeg build supports it), or both — selected under Settings →
  Video recording. APNG stays silent (it's an animated image). Unavailable sources are skipped
  so the recording still succeeds.

### Notes
- Audio capture needs a microphone (dshow) and/or a wasapi-enabled ffmpeg for system audio.
  When a chosen source can't be resolved, recording still succeeds as video-only.

### Added (continued)
- **Update checker.** wsnap now compares its version against the latest GitHub Release in the
  background after startup, and on demand from the tray ("Check for updates"). When a newer
  version exists it surfaces a toast + a tray entry linking to the release. It deliberately
  does NOT silently swap the binary (auto-replacing an unsigned exe is a security smell); full
  silent self-update activates after code signing.
- **Scroll capture v2.** The overlap matcher now uses a two-component row signature (brightness
  + chroma) so equal-brightness rows no longer collide, and a confidence gate rejects
  low-overlap matches (smooth-scroll lag, parallax) instead of forcing a noisy shift. When
  content can't be aligned the result is a shorter but clean image rather than a garbled one.

### Changed
- New **Settings → Video recording** card: frame rate and audio source (None / Mic / System /
  Mic+system). Video is silent by default; pick a source to record sound with MP4.
- Version bumped to 1.6.0.

### Fixed
- **No lag opening the capture overlay after the app sits idle.** A background timer emptied the
  process working set every 45 seconds, paging wsnap's own code out; the next hotkey press then
  paid a hard page-fault storm before the region overlay could appear. The working-set trim now
  fires once after a genuine long idle and resets on every capture, so an active session stays
  instant. (`TrimNow` also no longer purges the working set — it keeps only its compacting GC.)
- **Thumbnail no longer clips or lands in the wrong place on mixed-DPI multi-monitor setups.** The
  bottom-right thumbnail is placed in physical pixels; crossing a monitor DPI boundary let
  Windows' default handling override that placement, so it could render truncated or off to the
  side. It now re-asserts its position after a DPI change (the tray toast had the same latent
  issue and is fixed too).
- **Smoother region selection.** The window-hover highlight no longer re-invalidates the full
  dimmed backdrop on every mouse-move tick, and freezing the desktop on overlay open now copies
  the screen once instead of twice.

[1.6.0]: https://github.com/openwong2kim/wsnap/releases/tag/v1.6.0
[1.5.1]: https://github.com/openwong2kim/wsnap/releases/tag/v1.5.1

## [1.5.1] - 2026-06-03

### Changed
- **Language picker is now a dropdown with live preview.** All 13 UI languages were already
  available, but the segmented buttons overflowed the window so only the first few (English,
  Korean, German) were visible. **Settings → Language** is now a proper scrollable dropdown
  listing every language, and picking one **re-localizes the settings window instantly** —
  no more saving and reopening to see the change. Cancelling restores your original language.

### Fixed
- **OCR pack download survives a language switch.** Changing the display language while an OCR
  language pack was still downloading left the new chip with no progress and re-clickable
  (which could kick off a duplicate download). The download state now carries across the live
  re-render, so progress keeps showing on the correct chip and the pack can't be double-fetched.

## [1.5.0] - 2026-06-03

### Added
- **OCR in many more languages.** Text extraction was Korean-only (the model also covers
  English). You can now pick the OCR recognition language under **Settings → OCR**, separate
  from the display language. Korean (incl. English) stays built in; other PP-OCRv5 script packs
  — Latin (English/German/French/Spanish/Italian and ~27 more), Chinese (+Japanese), Cyrillic,
  Greek, Arabic, Devanagari, Tamil, Telugu, Thai — **download on demand** the moment you pick
  them (with a progress indicator), so the first OCR is instant afterward. Packs are cached
  per-user; the single-file exe stays the same size.
- **UI now available in 13 languages.** English and Korean are built in; German, French,
  Spanish, Portuguese, Italian, Japanese, Simplified Chinese, Russian, Arabic, Hindi, and
  Vietnamese ship as community translation packs (`locales/<code>.json`). Pick the display
  language under **Settings → Language**. The non-English/Korean translations are community
  drafts — corrections are welcome via a pull request (see `locales/README.md`).

[1.5.0]: https://github.com/openwong2kim/wsnap/releases/tag/v1.5.0

## [1.4.0] - 2026-06-02

### Added
- **Display language is now switchable, and defaults to English.** wsnap was Korean-only;
  all user-facing text (tray menu, settings, editor, history, thumbnails, capture overlay,
  toasts) now goes through a small string table (`Strings.cs`) with English and Korean
  tables. The new **Language** card at the top of Settings lets you pick the UI language;
  English is the default. Changing it rebuilds the tray menu immediately and re-localizes
  every window the next time it's opened. Existing installs (whose `settings.json` predates
  the field) start in English. Adding a language later is a table plus one list entry.

[1.4.0]: https://github.com/openwong2kim/wsnap/releases/tag/v1.4.0

## [1.3.3] - 2026-06-02

### Fixed
- **Region selection is smooth now.** The remaining drag stutter (after the 1.3.2 input
  coalescing) was the overlay being a full-virtual-desktop *layered* window
  (`AllowsTransparency=true`): Windows re-pushed the entire window surface on the CPU every
  frame. Since the overlay already shows a frozen snapshot of the desktop, it's now an
  **opaque** window — rendered on the GPU-composited path with only dirty regions repainted —
  so the selection drag stays fluid even on large / multi-monitor / high-refresh setups. (We
  fall back to the transparent window only in the rare case the desktop freeze fails.)
  Verified: idle memory unchanged (single-digit working set / ~86 MB committed).

### Changed
- **New tray icon.** A black rounded tile with a white **W**, replacing the blue
  viewfinder tile. Drawn as a vector so it stays crisp down to 16px, with a faint border so
  it reads on a dark taskbar.

[1.3.3]: https://github.com/openwong2kim/wsnap/releases/tag/v1.3.3

## [1.3.2] - 2026-06-02

### Fixed
- **Smoother region selection.** Dragging a selection could stutter, especially with a
  high-polling-rate mouse. The capture overlay was doing its heavy per-move work — rebuilding
  the zoomed magnifier loupe and re-rendering the full-screen dim — on *every* mouse-move
  event (hundreds to thousands per second). That work is now coalesced to one render frame
  (~display refresh), and the loupe's zoomed bitmap is rebuilt only when the cursor moves to a
  new pixel. Same look and behavior, far less work per drag.

[1.3.2]: https://github.com/openwong2kim/wsnap/releases/tag/v1.3.2

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
