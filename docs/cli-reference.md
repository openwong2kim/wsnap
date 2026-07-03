# wsnap CLI reference

`wsnap <command> [options]` drives the running wsnap tray app — or performs a
headless capture/OCR/color read — straight from your terminal, so screenshots,
text extraction and pixel colors can be scripted and piped like any other Unix
tool.

The same executable is the GUI tray app and the CLI: run `wsnap` with no
arguments (or double-click it) to start the tray app; run `wsnap <command>` to
issue a one-shot command and exit.

---

## Overview

- **Headless actions** — fixed-region / full-screen / window capture, OCR, color
  reads, and history queries — resolve from coordinates or file paths alone and
  can run without the tray app.
- **Interactive & recording actions** — drag-select capture, `repeat`, `gif`,
  `video`, `scroll`, and window commands — need the tray app running because they
  drive an on-screen overlay or a long-running recorder. If it isn't running they
  fail with exit code **3** (`resident_required`).

### External control must be enabled

For privacy, the whole CLI/MCP/pipe surface is **off by default**. Commands that
come from the terminal are refused until you turn **External control** on in the
tray app's **Settings** window. While it's off, external commands fail with an
error (exit code 1) and the message *"external control is off (enable it in wsnap
settings)"*.

> **You cannot enable it from the CLI.** `wsnap settings set …` is itself an
> external command and is blocked while external control is off, so the switch
> must be flipped once in the tray app's Settings window. After that, the CLI
> works normally.

Related opt-ins in the same Settings section refine the policy: allow returning
screen content to callers, allow silent (no on-screen toast) reads, a
per-minute rate limit, and an audit log. Recording is rate-limited more tightly
than one-shot commands.

---

## Synopsis

```text
wsnap capture   [--region x,y,w,h | --full [--display N] | --window | --interactive]
                [--copy] [--out <path>|-|clipboard] [--json]
wsnap ocr       [--region x,y,w,h | --file <path>|- | --last] [--lang <code>] [--json]
wsnap color     [--at x,y | --cursor] [--format hex|rgb] [--json]
wsnap gif       [--region x,y,w,h] [--duration <s>] [--fps <n>] [--json]
wsnap gif stop  [<recording-id>]
wsnap video     [--region x,y,w,h] [--format mp4|apng] [--duration <s>]
wsnap scroll    [--region x,y,w,h]
wsnap history   list [--limit N] [--pinned] [--json]
wsnap history   get <id> [--out -|<path>] [--json]
wsnap history   open
wsnap repeat    [--copy] [--out <path>|-|clipboard] [--json]
wsnap open
wsnap settings  get [key] | set <key> <value> | path
wsnap status    [--json]
wsnap mcp

wsnap --help | --version
```

Every command also accepts `--help` (or `-h`) to print its own usage line, e.g.
`wsnap capture --help`.

---

## Commands

### `capture` — take a screenshot

| Mode flag | Captures |
|---|---|
| `--region x,y,w,h` | A fixed rectangle in device pixels (`w`, `h` ≥ 1). |
| `--full [--display N]` | A whole monitor. `--display` takes an index (`0`, `1`, …) or `primary`; omitted, it captures the monitor under the cursor. |
| `--window` | The current foreground window. |
| `--interactive` *(default)* | A drag-selected region via the overlay. Needs the tray app. |

With no mode flag, `capture` is interactive.

Output options:

- `--copy` or `--out clipboard` — copy the image to the clipboard instead of
  reporting a saved path.
- `--out <path>` — write the PNG to that path (prints the absolute destination).
- `--out -` — stream raw PNG bytes to **stdout**; the summary/JSON goes to stderr.
- default — save to the app's capture folder and print `Saved <path> (WxH)`.

```console
$ wsnap capture --region 100,100,640,480
Saved C:\Users\me\Pictures\wsnap\2026-07-03_14-21-08.png (640x480)

$ wsnap capture --full --display primary --out desktop.png
C:\work\desktop.png

$ wsnap capture --window --copy
Copied to clipboard (1280x800)
```

`--json` fields: `ok`, `path`, `width`, `height`, `app?`, `title?`, `bytes?`,
`copied`.

### `ocr` — recognize text

| Source flag | Reads text from |
|---|---|
| `--region x,y,w,h` | A live screen rectangle. |
| `--file <path>` | An existing image file. `--file -` reads a PNG from **stdin**. |
| `--last` *(default)* | The most recent capture. |

`--lang <code>` is an optional language hint (e.g. `ko`, `en`).

By default the recognized text is printed to stdout (empty result → no output,
exit 0). With `--json`: `ok`, `text`, `lang`, `empty`, `source?`.

```console
$ wsnap ocr --region 0,0,900,60
Build succeeded — 0 errors, 2 warnings

$ wsnap ocr --file diagram.png --lang en --json
{"ok":true,"text":"...","lang":"en","empty":false,"source":"diagram.png"}
```

### `color` — read a pixel color

- `--at x,y` — read at explicit device-pixel coordinates.
- `--cursor` *(default)* — read under the current cursor position.
- `--format hex|rgb` — output format for the human line (default `hex`).

```console
$ wsnap color --at 12,48
#2E7D32

$ wsnap color --cursor --format rgb
rgb(46, 125, 50)
```

`--json` fields: `ok`, `hex`, `r`, `g`, `b`, `x`, `y`.

### `gif` — record an animated GIF

- `--region x,y,w,h` — the area to record.
- `--duration <s>` — record for a fixed number of seconds. Omitted, recording
  runs until you stop it (see `gif stop`).
- `--fps <n>` — frame rate.

```console
$ wsnap gif --region 0,0,480,360 --duration 5 --fps 12
Saved C:\Users\me\Pictures\wsnap\clip.gif (480x360, 60 frames, 5.0s)
```

#### `gif stop [<recording-id>]`

Stops the in-progress recording and saves it. With no id it stops the current
recording; pass the `recording_id` returned when the recording started to target
a specific one.

Recording `--json` fields: `ok`, `recording_id?`, and when a file is produced
`path`, `width`, `height`, `frames`, `seconds`.

### `video` — record MP4 / APNG

- `--region x,y,w,h` — the area to record.
- `--format mp4|apng` — output container.
- `--duration <s>` — length.

### `scroll` — capture a scrolling window

`--region x,y,w,h` selects the window/area; wsnap scrolls it and stitches the
frames into one tall image.

### `history` — browse past captures

- `history list [--limit N] [--pinned] [--json]` — list recent captures (default
  limit 30; `--pinned` shows only pinned items). Human output is tab-separated:
  `index`, timestamp, `pin`/blank, path. `--json` yields
  `{ ok, items:[{ index, path, when, pinned }] }`.
- `history get <id> [--out -|<path>] [--json]` — resolve a capture. `<id>` is a
  list index, a filename, or a full path. `--out -` streams its PNG to stdout
  (path to stderr); `--out <path>` copies it there; otherwise the path is printed.
- `history open` — open the capture-history window (needs the tray app).

```console
$ wsnap history list --limit 3
0    2026-07-03 14:21    pin    C:\...\a.png
1    2026-07-03 14:05           C:\...\b.png
2    2026-07-03 13:52           C:\...\c.png

$ wsnap history get 0 --out latest.png
C:\work\latest.png
```

### `repeat` — re-capture the last region

Re-runs the most recent region capture (needs the tray app for the remembered
region). Accepts the same output options as `capture`
(`--copy`, `--out …`, `--json`).

### `open` — open the save folder

Opens the capture folder in Explorer.

### `settings` — read / change configuration

- `settings path` — print the path of `settings.json` (a pure local lookup;
  works even with external control off).
- `settings get [key]` — print all settings, or one `key`. `--json` wraps them
  under `settings`.
- `settings set <key> <value>` — change a setting (audited).

```console
$ wsnap settings path
C:\Users\me\AppData\Roaming\wsnap\settings.json
```

### `status` — is the tray app running?

Reports whether the resident tray app is up. `--json` returns `{ ok, status? }`.

### `mcp` — Model Context Protocol server

`wsnap mcp` starts an MCP server over stdio for AI agents/clients to spawn — it
is not meant to be used interactively. Its tools mirror the CLI capabilities
(`capture_region`, `capture_fullscreen`, `capture_window`, `ocr_region`,
`ocr_image`, `ocr_last_capture`, `pick_color`, `list_history`, `get_capture`,
`record_gif`, `stop_recording`, …).

---

## Output modes

- **Human (default)** — a one-line summary on **stdout**; diagnostics on stderr.
- **`--json`** — a single JSON object, one line, on **stdout**. Ideal for
  scripting.
- **`--out -`** — raw PNG bytes on **stdout**; the human/JSON summary is diverted
  to **stderr** so the binary stream stays clean.

Errors always print `ok:false` with `error_code` and `error` under `--json`, or a
`wsnap: <message> (<code>)` line on stderr otherwise.

---

## Exit codes

| Code | Meaning |
|:---:|---|
| `0` | Success. |
| `1` | General error (includes external control disabled, rate-limited, and I/O errors). |
| `2` | Usage error — unknown command or bad flags. |
| `3` | `resident_required` — the action needs the wsnap tray app running. |
| `4` | No result or cancelled — empty region, cancelled overlay, item not found, no foreground window. |
| `5` | `ocr_unavailable` — the OCR engine could not be used. |

---

## Running wsnap from a shell

`wsnap.exe` is a **GUI-subsystem (WinExe) binary**, not a console program. It has
no console of its own, so how its output reaches you depends on the context:

- **Redirected or piped** (`wsnap ocr --json > out.json`, `wsnap … | …`) — wsnap
  writes to the redirected handle and the shell waits for the stream. **This is
  the reliable path for scripts.**
- **Interactive console, not redirected** — wsnap attaches to the parent console
  (`AttachConsole`) and prints there. But the shell does not block on a GUI app,
  so the prompt can return before the output is flushed and lines may interleave.
  Fine for eyeballing, unreliable for scripting.
- **No console** (double-click, launched from the tray) — console output is
  silently dropped; wsnap never throws because of it.

### cmd.exe

Redirection works as expected; use `start /wait` if you need the shell to block
without redirecting:

```bat
wsnap capture --region 0,0,800,600 --out - > shot.png
wsnap color --cursor --json > color.json
```

### PowerShell

Piping or redirecting a native command makes PowerShell wait for it, which is
what you want. For binary output prefer writing to a file with `--out <path>`, or
use `Start-Process` with redirection.

```powershell
# OCR the last capture into a variable (the pipe forces wait + redirect)
$text = wsnap ocr --last | Out-String

# Parse JSON output
$c = wsnap color --cursor --json | ConvertFrom-Json
"{0} = ({1},{2},{3})" -f $c.hex, $c.r, $c.g, $c.b

# Most robust for scripts: run to completion with explicit redirection
Start-Process wsnap -ArgumentList 'capture','--region','0,0,800,600','--out','shot.png' `
  -NoNewWindow -Wait -RedirectStandardError err.log
```

---

## Scripting examples

Capture a region, then OCR that image. In PowerShell, route binary through a file
(native-to-native pipes re-encode bytes and can corrupt a PNG):

```powershell
wsnap capture --region 40,120,900,300 --out shot.png
wsnap ocr --file shot.png --lang en
```

In **cmd.exe** the stdout pipe is byte-clean, so the two can be chained directly:

```bat
wsnap capture --region 40,120,900,300 --out - | wsnap ocr --file - --lang en
```

Copy the color under the cursor to the clipboard and echo it:

```powershell
wsnap color --cursor --copy --json | ConvertFrom-Json | Select-Object hex
```

Pull the newest capture's path for further processing:

```powershell
$path = (wsnap history get 0 --json | ConvertFrom-Json).path
```

---

## See also

- **Settings** — `wsnap settings path` prints the config file location; the
  **External control** switch and its opt-ins live in the tray app's Settings
  window.
- **MCP** — `wsnap mcp` exposes the same capabilities as MCP tools for AI agents.
```
