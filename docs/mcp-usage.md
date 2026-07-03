# Controlling wsnap from Claude (MCP)

wsnap ships a built‑in **MCP server** so that an MCP client — **Claude Desktop** or
**Claude Code** — can drive wsnap directly: capture the screen, run on‑device OCR, read
pixel colors, browse your capture history, and record animated GIFs.

The server speaks the Model Context Protocol over **stdio** (local pipes only — there is no
network listener). It is **off by default** and every externally‑initiated capture is visible
and audited. See [Security & privacy](#security--privacy).

---

## Overview

Run `wsnap mcp` and wsnap becomes a tool provider for Claude. In practice you don't run it by
hand — you register the command once (below), and the client launches it on demand.

- **What Claude can do:** take precise region / full‑screen / window screenshots, OCR a region
  or an image file, pick a color, list and fetch past captures, and record a region to a GIF.
- **Where results go:** captures are always saved as PNG files on disk. By default a tool call
  returns only the **file path** (cheap in tokens); you can opt in to also receive the image.
- **Who's in control:** the wsnap tray app stays the single source of truth for consent,
  visible signals, and the audit log. Interactive and recording tools require the tray app to
  be running.

---

## Requirements

- Windows 10 1809+ / Windows 11.
- **wsnap 1.7.0 or later**, with `wsnap.exe` on your `PATH` (the installer and the Scoop/WinGet
  packages do this; for the portable zip, either add its folder to `PATH` or use the full path
  to `wsnap.exe` in the config below).
- Claude Desktop, or Claude Code (`claude` CLI).

---

## Setup

### 1. Enable external control in wsnap

External control is **disabled by default**. Open the wsnap tray menu → **Settings**, and turn on:

- **Enable external control (MCP / CLI)** — the master switch. While this is off, wsnap exposes
  no control surface at all. *(setting: `ExternalControlEnabled`)*
- *(optional)* **Allow returning content** — lets tool results include the actual **pixels and
  OCR text**, not just a saved file path. Leave this off if you only want Claude to trigger and
  save captures. *(setting: `ExternalControlAllowReturnContent`)*

> These two switches are deliberately separate: triggering a capture and *reading its contents
> back* are different levels of trust. See [Security & privacy](#security--privacy).

### 2. Register with Claude Desktop

Edit `%APPDATA%\Claude\claude_desktop_config.json` and add wsnap under `mcpServers`:

```json
{
  "mcpServers": {
    "wsnap": {
      "command": "wsnap",
      "args": ["mcp"]
    }
  }
}
```

If `wsnap.exe` is not on your `PATH`, use its full path (note the doubled backslashes):

```json
{ "mcpServers": { "wsnap": { "command": "C:\\Program Files\\wsnap\\wsnap.exe", "args": ["mcp"] } } }
```

Restart Claude Desktop. wsnap's tools appear in the tools (🔌) menu.

### 3. Register with Claude Code

```sh
claude mcp add wsnap -- wsnap mcp
```

Then check it with `/mcp` inside Claude Code, or `claude mcp list`.

### 4. First‑run consent

The first time Claude connects and asks wsnap to capture, the tray app shows a one‑time consent
prompt (**This session / Always / Deny**). Grants are remembered and can be reviewed or revoked
in Settings. Denying leaves everything off.

---

## How it works

- **Transport:** newline‑delimited JSON‑RPC 2.0 on stdin/stdout. `wsnap mcp` runs headless — no
  window appears. Nothing but protocol traffic is ever written to stdout.
- **Delegation:** `wsnap mcp` forwards each tool call to the running tray instance so that
  consent, the capture history, and the audit log all converge on one place. Headless work
  (fixed‑coordinate capture, OCR of a file) can run without the tray app; **interactive and
  recording tools require it**.
- **Handshake:** on connect the server reports `protocolVersion 2024-11-05`,
  `serverInfo { name: "wsnap", version: "1.7.0" }`, and a `tools` capability. `tools/list`
  returns the 12 tools below.

---

## Tool reference

All coordinates are **device pixels**, with the origin at the top‑left of the virtual desktop
(multi‑monitor aware; a monitor to the left of the primary has negative `x`).

Every result includes a compact JSON **text** block summarizing what happened. Capture‑family
tools can *also* attach the image — see [Images & tokens](#images--tokens). On failure the result
is flagged `isError: true` and the text is `{ "error": "<code>", "message": "<detail>" }`.

| Tool | Purpose | Requires tray app |
|---|---|---|
| [`capture_region`](#capture_region) | Screenshot a rectangle | no |
| [`capture_fullscreen`](#capture_fullscreen) | Screenshot a whole monitor | no |
| [`capture_window`](#capture_window) | Screenshot the foreground window | no |
| [`capture_interactive`](#capture_interactive) | User drag‑selects, then capture/OCR/color | **yes** |
| [`ocr_region`](#ocr_region) | OCR a screen rectangle | no |
| [`ocr_image`](#ocr_image) | OCR an image file on disk | no |
| [`ocr_last_capture`](#ocr_last_capture) | OCR the most recent capture | no |
| [`pick_color`](#pick_color) | Read a pixel color | only without x/y |
| [`list_history`](#list_history) | List recent captures | no |
| [`get_capture`](#get_capture) | Resolve a stored capture | no |
| [`record_gif`](#record_gif) | Record a region to a GIF | **yes** |
| [`stop_recording`](#stop_recording) | Stop an in‑progress recording | **yes** |

Shared optional inputs:

- `return` — `"path"` (default), `"image"`, or `"both"`. Controls whether the PNG is embedded in
  the result. Only honored when *Allow returning content* is enabled.
- `lang` — OCR language hint such as `"ko"` or `"en"`. Defaults to wsnap's current OCR language.

---

### capture_region

Capture a fixed rectangle and save it as PNG.

**Input:** `x`, `y`, `width`, `height` (required), `copy?` (also copy to clipboard), `return?`

```json
{ "name": "capture_region",
  "arguments": { "x": 100, "y": 200, "width": 640, "height": 480, "return": "path" } }
```

**Result summary:** `{ "path": "…\\snap.png", "width": 640, "height": 480, "copied": false }`
(plus `bytes`, `app`, `title` when available).

### capture_fullscreen

Capture an entire monitor.

**Input:** `monitor?` — `"cursor"` (default), `"primary"`, or a 0‑based index like `"0"`; `return?`

```json
{ "name": "capture_fullscreen", "arguments": { "monitor": "primary" } }
```

**Result summary:** `{ "path": "…", "width": 1920, "height": 1080, "copied": false }`.

### capture_window

Capture the current foreground window (uses the DWM frame bounds, so the invisible resize border
is excluded).

**Input:** `return?`

```json
{ "name": "capture_window", "arguments": { "return": "both" } }
```

**Result summary:** `{ "path": "…", "width": …, "height": …, "copied": false, "app": "chrome", "title": "…" }`.

### capture_interactive

Show the selection overlay and let the **user** drag out a region, then act on it. **Requires the
tray app.**

**Input:** `mode?` — `"region"` (save an image, default), `"ocr"` (recognize text), `"color"`
(pick a pixel color); `timeout_ms?` (auto‑cancel after N ms of no input); `return?`

```json
{ "name": "capture_interactive", "arguments": { "mode": "ocr" } }
```

**Result summary:** shaped by `mode` — a capture (`{path,…}`), OCR (`{text,lang,empty}`), or color
(`{hex,rgb,…}`) result.

### ocr_region

Recognize text inside a screen rectangle. No file is saved.

**Input:** `x`, `y`, `width`, `height` (required), `lang?`

```json
{ "name": "ocr_region", "arguments": { "x": 0, "y": 0, "width": 800, "height": 200, "lang": "ko" } }
```

**Result summary:** `{ "text": "인식된 텍스트", "lang": "ko", "empty": false }`.

### ocr_image

Recognize text in an existing image file.

**Input:** `path` (required), `lang?`

```json
{ "name": "ocr_image", "arguments": { "path": "D:\\shots\\receipt.png" } }
```

**Result summary:** `{ "text": "…", "lang": "en", "empty": false }`.

### ocr_last_capture

Recognize text in the most recent capture in history.

**Input:** `lang?`

```json
{ "name": "ocr_last_capture", "arguments": {} }
```

**Result summary:** `{ "text": "…", "lang": "ko", "empty": false, "source_path": "…" }`.

### pick_color

Read a pixel color. **With** `x` and `y` it reads that exact pixel headlessly; **without** them it
opens the interactive eyedropper (which requires the tray app).

**Input:** `x?`, `y?`

```json
{ "name": "pick_color", "arguments": { "x": 960, "y": 540 } }
```

**Result summary:** `{ "hex": "#3A7BD5", "rgb": { "r": 58, "g": 123, "b": 213 }, "x": 960, "y": 540 }`.

### list_history

List recent captures, newest first.

**Input:** `limit?` (default 30), `pinned_only?`

```json
{ "name": "list_history", "arguments": { "limit": 5, "pinned_only": false } }
```

**Result summary:** `{ "count": 5, "items": [ { "path": "…", "when": "2026-07-03T14:05:11.0000000+09:00", "pinned": false }, … ] }`.

### get_capture

Resolve a stored capture by history index, filename, or absolute path — handy for loading a past
capture back as an image (`return: "image"`).

**Input:** `id?` (0‑based index, `0` = newest, or a filename), `path?` (absolute path), `return?`

```json
{ "name": "get_capture", "arguments": { "id": "0", "return": "image" } }
```

**Result summary:** `{ "path": "…" }` (with the image attached when requested and allowed).

### record_gif

Record a screen region to an animated, looping GIF. **Requires the tray app.** A red
"recording" badge is shown for the whole recording and cannot be hidden — one click on it stops
and saves.

**Input:** `x`, `y`, `width`, `height` (required); `duration_s?` (default **5**, hard cap
**30**); `fps?` (default **12**); `mode?` — `"fixed"` (default) or `"until_stop"`; `return?`

- **`fixed`** — records for `duration_s`, then saves; the call resolves when the file is written.
- **`until_stop`** — returns immediately with a `recording_id`; call `stop_recording` to finish
  (the 30‑second hard cap still applies).

```json
{ "name": "record_gif",
  "arguments": { "x": 200, "y": 200, "width": 480, "height": 320, "duration_s": 3, "fps": 12 } }
```

**Result summary (fixed):** `{ "path": "…\\clip.gif", "frames": 36, "seconds": 3, "width": 480, "height": 320 }`.
**Result summary (until_stop):** `{ "recording_id": "gif-1" }`.

> GIFs are **not** embedded as images even with `return: "image"` (a GIF is far too large in
> tokens); the result always carries the file path.

### stop_recording

Stop an in‑progress recording started with `mode: "until_stop"` and save it.

**Input:** `recording_id?` — omit to stop the single active recording.

```json
{ "name": "stop_recording", "arguments": { "recording_id": "gif-1" } }
```

**Result summary:** `{ "path": "…\\clip.gif", "frames": 42, "seconds": 3.5, "width": 480, "height": 320 }`.

---

## Images & tokens

A 4K PNG encoded as base64 would blow up token usage — and Claude's vision resizes anything
larger to ~1568 px on the long edge anyway. So:

- **`return: "path"` (default):** the result carries only the saved file path. Zero image tokens.
  Full‑resolution pixels stay on disk.
- **`return: "image"` / `"both"`:** the result also embeds the PNG as base64, **downscaled to a
  1568 px long edge** (high‑quality bicubic; images already within the bound are sent as‑is).
  This requires *Allow returning content* to be enabled.

If you only need the *text* in a screenshot, prefer the `ocr_*` tools — they return text and cost
no vision tokens.

---

## Security & privacy

wsnap treats agent control as a privileged, visible operation. The design goal is simple: **never
become a quieter, more convenient exfiltration tool than the OS already is**, and make every
AI‑initiated capture obvious and consensual.

- **Off by default.** `ExternalControlEnabled` starts `false`; while off, no control surface
  exists.
- **Two‑stage consent.** *Triggering* a capture and *returning its contents* are separate opt‑ins.
  With *Allow returning content* off, the Router blanks the text/pixels/color before they leave
  wsnap; MCP results then include `"content_redacted": true` and a one‑line notice telling you to
  enable `ExternalControlAllowReturnContent`. Paths and dimensions still come back (a saved path
  is not "content").
- **Local stdio only.** The MCP server communicates over stdin/stdout pipes. There is **no network
  listener** — remote control is impossible by construction.
- **Visible signals.** Each externally‑initiated capture flashes the shutter and raises a toast
  ("Claude captured the screen"), and the tray shows a session badge while control is active.
  GIF recording additionally shows a **red recording badge that cannot be suppressed** and doubles
  as a one‑click kill‑switch.
- **Rate limited.** Requests are throttled per source (default 6/min); recording is stricter (one
  at a time). Excess is denied and audited.
- **Audited.** Actions are appended to `audit.log` (next to wsnap's log in
  `%APPDATA%\wsnap\`): time, source, command, region size, whether content was returned, and
  allow/deny. **Never logged:** OCR text, image bytes, or window titles.
- **Honest limits.** Access is scoped to your Windows user; wsnap cannot defend against malware
  already running as you (the OS can screenshot too). The value is safety‑by‑default and
  visibility, not defeating local attackers.

---

## Example prompts

Once registered, just ask Claude in natural language:

- *"Capture this window and read the text in it."* → `capture_window` (or `capture_interactive`
  with `mode: "ocr"`).
- *"Take a screenshot of the region 100,200 640×480 and save it."* → `capture_region`.
- *"Let me select an area to OCR."* → `capture_interactive` with `mode: "ocr"`.
- *"What color is the pixel at 960,540?"* → `pick_color`.
- *"Record a 3‑second GIF of this region."* → `record_gif` with `duration_s: 3`.
- *"Start recording this area while I demo, and stop when I say."* → `record_gif` with
  `mode: "until_stop"`, then `stop_recording`.
- *"Show me my last 5 captures and OCR the newest one."* → `list_history` + `ocr_last_capture`.

---

## Troubleshooting

- **Tools don't appear in Claude.** Confirm `wsnap.exe` is on `PATH` (`wsnap --version`), the
  config JSON is valid, and you restarted the client. In Claude Code, check `/mcp`.
- **Every capture is redacted (`content_redacted: true`).** Turn on *Allow returning content*
  in Settings, or keep working with paths and the `ocr_*` tools.
- **`resident_required` errors.** Interactive and recording tools need the wsnap **tray app**
  running — launch wsnap normally, then retry.
- **A consent prompt keeps appearing.** Choose **Always** for this client, or manage grants in
  Settings.
- **Nothing happens and no window shows.** That's expected — `wsnap mcp` is headless. Watch for
  the shutter flash / toast / tray badge as the visible confirmation that a capture ran.
