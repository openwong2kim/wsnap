<div align="center">

# wsnap

**macOS-style screen capture for Windows.**

Press **Shift+F1**, drag a region, and the image is already on your clipboard.
Pick an action from the toolbar that appears *right at your selection* — copy, save,
edit, OCR, GIF, pin — or just **drag the floating thumbnail straight into any app as a real file**.

Native. Offline. No account, no tracking. GPL-3.0.

![wsnap demo](https://github.com/openwong2kim/wsnap/raw/main/site/demo.gif)

</div>

---

## Why wsnap

Most Windows capture tools make you fish a file out of a folder, or they copy a *file path*
to the clipboard when you actually wanted the image. wsnap treats the two things people do
with a screenshot every day — **paste it** and **drag it somewhere** — as first-class:

- The capture is **on your clipboard as an image the instant you release the mouse.** Paste it anywhere with `Ctrl+V`.
- The floating thumbnail is a **real, draggable file.** Drop it into Slack, a chat, an editor, an email — and it stays put so you can drop it again somewhere else.

Everything else — a precise frozen-screen overlay, a pixel loupe with HEX color readout,
an annotation editor, on-device OCR, GIF recording, scrolling capture — is built around
that core in one consistent dark UI, running quietly from the tray.

## Features

- **Capture → floating thumbnail → drag-and-drop.** Thumbnails stack at the bottom-right, up to a configurable number.
- **Clipboard-first.** Click = copy the image; auto-copy on capture (optional) so it's ready before you even click. `Ctrl+Click` = copy the file path.
- **Action toolbar at your selection** — Copy · Save · Edit · OCR · GIF · Pin (keys `C / Enter / E / T / G / P`, `Esc` to cancel).
- **Precise overlay.** Freezes the screen, brightens only your selection (punch-through dim), shows live W×H, and a **magnifier loupe** with pixel coordinates and the HEX color under the cursor. Physical-pixel cursor grab — correct on mixed-DPI multi-monitor setups.
- **Color picker (eyedropper).** Click any pixel → `#RRGGBB` copied.
- **Annotation editor** — arrow, line, rectangle, ellipse, pen, highlighter, text, numbered steps, **mosaic / blur redaction**, crop. Pick thickness and color, **select / move / delete objects** (`V`), undo *and* redo, undoable crop, copy to clipboard (`Ctrl+C`), `Shift` to constrain (45° / square). Keyboard-driven.
- **Great on-device OCR (KO + EN).** PaddleOCR **PP-OCRv5** models on ONNX Runtime — accurate on mixed Korean/English, code, and UI text. **Free, fully offline, no language pack required** (models ship inside the exe).
- **Many capture modes** — region · full screen · **click-to-capture a window** (auto-detected) · repeat last region · delayed (3 / 5s).
- **Capture history gallery.** Browse every saved capture as thumbnails → re-drag, copy, edit, or delete (to Recycle Bin).
- **Filename templates** — `{app}`, `{title}`, `{date}`, `{seq}`, `{w}`, `{h}` tokens (the foreground app / window title are captured at grab time).
- **Pin** to keep a thumbnail (disables auto-dismiss and promotes the file out of `%TEMP%`).
- **GIF recording · scrolling capture · clipboard-image detection.**
- **One dark design system** across the overlay, editor, and settings — including dark DWM title bars.
- **Lean tray resident.** Idle memory was cut hard in 1.2.4 (single-digit working set / ~85 MB committed); the OCR engine loads lazily and releases after use, so OCR doesn't tax the idle footprint.
- **Optional sharing.** Imgur upload (bring your own Client-ID) from the thumbnail.
- **Control it from the terminal or an AI agent.** A built-in MCP server (`wsnap mcp`, for Claude Desktop / Claude Code) plus a `wsnap <verb>` CLI expose capture, OCR, color, GIF, and history through one command bus. **Off by default** — turn it on in Settings.

## Install

**Windows 10/11, x64.**

**Package managers**

```powershell
# Scoop
scoop install https://raw.githubusercontent.com/openwong2kim/wsnap/main/packaging/scoop/wsnap.json

# winget (once the manifest is accepted into winget-pkgs)
winget install openwong2kim.wsnap
```

**Direct download** — grab the latest from [Releases](https://github.com/openwong2kim/wsnap/releases):

- `wsnap-setup-x.y.z.exe` — installer (Start Menu shortcut, optional run-at-startup)
- `wsnap-vx.y.z-win-x64.zip` — portable single `.exe`, no install

> wsnap is **not code-signed yet**, so Windows SmartScreen may show an "unknown publisher"
> prompt — click **More info → Run anyway**. (OSS code signing is wired up and pending; see `SIGNING.md`.)

## Usage

1. Launch — no window appears, just a tray icon.
2. Press **Shift+F1** (or double-click the tray icon) and drag a region.
3. The thumbnail at the bottom-right:
   - **Left-click drag** → hand off the file (stays available to drag again elsewhere)
   - **Click** → copy the file path
   - **Hover buttons** → Edit / Text (OCR) / ✕
   - **Right-click drag (sideways)** → flick it away
   - Leave it → auto-dismisses after your configured delay
4. Tray menu: Capture · OCR region · GIF record · Scrolling capture · Clear all · Settings · Quit.

**Settings:** save folder, hotkey rebinding, auto-dismiss delay, max thumbnails shown,
run at startup, intercept `Win+Shift+S`, history (date folders), clipboard detection,
telemetry (opt-in), upload.

## Automate & control (CLI · MCP)

wsnap can be driven from a terminal or by an AI agent through one shared command bus.
This is **off by default** — turn on **External control** in Settings first (with it off,
the control pipe server is never even created).

**CLI** — `wsnap <verb>`:

```powershell
wsnap capture --full                       # the monitor under the cursor
wsnap capture --region 0,0,800,600         # a fixed rectangle -> saved PNG
wsnap capture --window --copy              # foreground window, straight to the clipboard
wsnap ocr --last                           # OCR the most recent capture
wsnap ocr --file shot.png --lang latin     # OCR an image file
wsnap color --cursor --format hex          # pixel color under the cursor
wsnap gif --region 0,0,640,480 --duration 5
wsnap history list --limit 10
```

Add `--json` for a single machine-readable object on stdout, or `--out -` to stream raw PNG
bytes. Exit codes: `0` ok · `1` error · `2` usage · `3` needs the running app · `4` no
result / cancelled · `5` OCR unavailable. Run `wsnap --help` for the full list.

**MCP** — register the `wsnap mcp` server with an MCP client. For Claude Code:

```powershell
claude mcp add wsnap -- wsnap mcp
```

For Claude Desktop, add to `%APPDATA%\Claude\claude_desktop_config.json` (Settings has a
**Copy MCP config** button for this snippet):

```json
{ "mcpServers": { "wsnap": { "command": "wsnap", "args": ["mcp"] } } }
```

The server exposes twelve tools: `capture_region`, `capture_fullscreen`, `capture_window`,
`capture_interactive`, `ocr_region`, `ocr_image`, `ocr_last_capture`, `pick_color`,
`list_history`, `get_capture`, `record_gif`, `stop_recording`.

**Security.** Control is local-only — a per-user named pipe plus MCP stdio, with **no network
listener**. Every agent-initiated capture raises a visible signal (shutter flash / toast /
tray badge) and is written to an audit log (`%APPDATA%\wsnap\audit.log`). Returning captured
pixels or OCR text to the caller is a separate opt-in (**Allow returning captured content**,
off by default), and a per-minute rate limit applies.

## Build from source (Windows)

Requires the **.NET 8 SDK** (or 9) with the Windows Desktop workload. The project targets
`net8.0-windows10.0.19041.0` for WinRT projection availability.

The Korean OCR model lives in `models/v5/` and is embedded into the exe at build time.
To (re)download it:

```powershell
pwsh -File tools\fetch-ocr-models.ps1
```

Run, publish a single self-contained exe, and build the installer:

```powershell
dotnet run --project Wsnap.csproj          # run from source
pwsh -File publish.ps1                      # -> publish\wsnap.exe (single file)
ISCC.exe installer.iss                      # -> dist\wsnap-setup-x.y.z.exe (Inno Setup 6)
```

## Source map

| File | Role |
|---|---|
| `App.cs` | Entry point · tray · capture modes · action routing · single instance |
| `Theme.cs` | Shared design system (color · type · control styles · dark title bars) |
| `Icons.cs` | Vector line icons (font-independent) |
| `ImageClipboard.cs` | Multi-format image clipboard (DIB + PNG + FileDrop, with retry) |
| `HotkeyHook.cs` | Global keyboard hook (custom hotkey + `Win+Shift+S` toggle) |
| `CaptureOverlay.cs` | Capture overlay (freeze · dim · W×H · loupe · action toolbar · Capture/OCR/Region/ColorPick) |
| `ScreenGrab.cs` | Screen pixel grab + Bitmap→BitmapSource |
| `CaptureStore.cs` | Save-location / history policy + pin promotion |
| `ThumbnailWindow.cs` | Floating thumbnail stack (copy · save · edit · OCR · folder · share · pin · delete) |
| `HistoryWindow.cs` | Capture history gallery (thumbnail grid · drag-out · re-edit · delete) |
| `EditorWindow.cs` | Annotation editor (11 tools · redo · undoable crop · copy to clipboard) |
| `Ocr.cs` | PaddleOCR PP-OCRv5 (ONNX / RapidOcrNet) wrapper — Korean rec model, lazy load + idle release |
| `GifRecorder.cs` / `GifWriter.cs` | GIF recording + delay/loop encoding |
| `ScrollCapture.cs` | Scrolling capture (overlap stitching) |
| `ClipboardWatcher.cs` | Clipboard image detection |
| `Uploader.cs` | Imgur upload |
| `Settings.cs` / `SettingsWindow.cs` | Settings model · UI (dark cards) |
| `AutoStart.cs` / `SingleInstance.cs` / `CrashLog.cs` / `Toast.cs` | Tray-resident infrastructure |
| `Control/Command.cs` / `CommandCatalog.cs` / `CommandRouter.cs` | Control-layer contracts · command catalog (id ↔ MCP tool ↔ CLI) · local command bus |
| `Control/CaptureCore.cs` / `ControlGate.cs` / `AuditLog.cs` | Headless capture/OCR/color facade · consent · rate-limit gate · audit log |
| `Control/CliRouter.cs` / `ConsoleBridge.cs` | `wsnap <verb>` CLI (parsing · output contract) · WinExe console attach |
| `Control/McpStdioServer.cs` / `JsonRpc.cs` | `wsnap mcp` stdio MCP server · minimal JSON-RPC 2.0 |
| `Control/PipeServer.cs` / `PipeClientRouter.cs` / `PipeProtocol.cs` | Per-user named-pipe control server · client delegation · NDJSON framing |
| `Control/FolderWatcher.cs` | Watch-folder auto-OCR |
| `App.Control.cs` | Resident host — interactive / recording delegation · external-control wiring |

## Good to know

- **OCR:** PP-OCRv5 models are embedded in the exe — no language pack needed. The first
  recognition is slightly slower (engine warm-up), then fast; the engine is released after a
  short idle. Rotated text isn't de-skewed (screenshots are assumed upright).
- **Scrolling capture** is best-effort — solid on text and web pages, weaker on smooth-scroll / parallax content.
- **Privacy:** no tracking. Telemetry is opt-in and local-log only (`%APPDATA%\wsnap\wsnap.log`).
- **External control (CLI / MCP)** stays off until you enable it, talks only over local stdio and a per-user named pipe (**no network listener**), and records every agent-initiated capture — each with a visible on-screen signal — to `%APPDATA%\wsnap\audit.log`.
- **Code signing** is recommended before wide distribution to avoid SmartScreen — see `SIGNING.md` / `ROADMAP.md`.

## License

[GPL-3.0-only](LICENSE). © 2026 openwong2kim and wsnap contributors.

OCR is powered by [RapidOcrNet](https://github.com/BobLd/RapidOcrNet) and
[PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) PP-OCRv5 models on ONNX Runtime.
Bundled third-party components and their licenses (all permissive / GPL-3.0-compatible) are
listed in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).

See `ROADMAP.md` for detailed status and `CHANGELOG.md` for release history.
