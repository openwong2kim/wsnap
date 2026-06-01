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

- **Capture → floating thumbnail → drag-and-drop.** Up to *N* thumbnails stack at the bottom-right.
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

## Install

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

## Good to know

- **OCR:** PP-OCRv5 models are embedded in the exe — no language pack needed. The first
  recognition is slightly slower (engine warm-up), then fast; the engine is released after a
  short idle. Rotated text isn't de-skewed (screenshots are assumed upright).
- **Scrolling capture** is best-effort — solid on text and web pages, weaker on smooth-scroll / parallax content.
- **Privacy:** no tracking. Telemetry is opt-in and local-log only (`%APPDATA%\wsnap\wsnap.log`).
- **Code signing** is recommended before wide distribution to avoid SmartScreen — see `SIGNING.md` / `ROADMAP.md`.

## License

[GPL-3.0-only](LICENSE). © 2026 openwong2kim and wsnap contributors.

OCR is powered by [RapidOcrNet](https://github.com/BobLd/RapidOcrNet) and
[PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) PP-OCRv5 models on ONNX Runtime.

See `ROADMAP.md` for detailed status and `CHANGELOG.md` for release history.
