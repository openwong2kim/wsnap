# Privacy

wsnap is a local, offline screenshot / OCR / recording tool. It has **no account, no telemetry by
default, no tracking, and no network calls in its core capture path.** Your captures are ordinary
files on your own disk. This document explains exactly what wsnap does with your data — and, in
detail, the privacy model for the **remote & agent control** surface (CLI and MCP) added in
v1.7.0.

wsnap is free software under [GPL-3.0](LICENSE), so you can read every line that does what's
described here — including the MCP and CLI subprocesses, which live in the same source tree.

## The basics (unchanged)

- **Offline.** Capture, annotation, colour picking, GIF/video recording, and OCR all run on your
  machine. OCR uses on-device PaddleOCR PP-OCRv5 models bundled in the executable — recognized
  text is never sent anywhere.
- **No account, no tracking.** There is no sign-in, no analytics, and no ad/tracking SDKs.
- **Telemetry is opt-in and local-only.** Off by default. When enabled it writes to a local log
  (`%APPDATA%\wsnap\wsnap.log`) and nowhere else.
- **The only network features are explicitly opt-in:** the optional Imgur upload (you supply your
  own Client-ID and click Share), the background GitHub update check (it compares version numbers
  only and never auto-replaces the binary), and on-demand download of extra OCR language packs.
  Each can be turned off.

## Remote & agent control (CLI / MCP)

v1.7.0 lets other software drive wsnap: the `wsnap <verb>` command line, and an MCP server
(`wsnap mcp`) that lets an AI agent such as Claude Desktop or Claude Code take screenshots, read
text off the screen, sample colours, record GIFs, and read capture history. Because "let a program
see my screen" is inherently sensitive, this surface is built privacy-first.

### Off by default, and truly off

External control is **disabled until you turn it on** in Settings. While it is off, the local
control pipe server is **never created** — there is no listener and no attack surface at all.
Turning it on does not silently grant access: the **first** external connection prompts you for
consent before anything is captured, and you can revoke access afterward.

### Local only — no network listener, ever

- MCP runs over **local stdio** — the agent process talks to wsnap through standard input/output
  on your own machine. There is no socket involved.
- The control channel for the CLI and the MCP bridge is a **Windows named pipe** whose ACL is
  restricted to your **current user SID** (session-local). Other users on the same machine cannot
  connect; `Everyone` / `Authenticated Users` are never granted access.
- There is **no TCP/HTTP listener** in wsnap, by design and guarded in code. Nothing on your
  network — or the internet — can reach the control surface. "Remote control" here means *another
  program on your own computer*, never a remote computer.

### Every external capture is visible

An externally-initiated capture is never silent by default. It raises a **visible signal** — a
shutter flash / toast ("External capture by …") and a **tray badge** while an external session is
active. **GIF recording additionally shows a red "recording" badge that is always on for external
recordings and acts as a one-click kill-switch** — you can stop an agent's recording instantly.

A "silent" mode (suppressing the flash/toast) exists, but it is **opt-in and CLI/pipe only** — MCP
captures are always visible, and the audit log and tray badge can **never** be suppressed.

### Audit log — metadata only

Every external command appends one line to `%APPDATA%\wsnap\audit.log`, for example:

```
2026-07-03 14:20:00 | Mcp | claude-desktop | CaptureRegion | 800x600 | visible | content | ok
```

That is: time, source, client id, command, captured size, whether it was visible, whether content
was returned, and the outcome. The audit log **never** records the OCR text, the image bytes, the
window title, or pixel colours — only metadata about what happened, so you can review external
activity without the log itself becoming a copy of your screen.

### Returning content is a second, separate consent

Triggering a capture (which saves a file locally) and **returning the captured pixels or OCR text
to the caller** are treated as two different levels of trust. Returning content is the real
exfiltration channel, so it is gated **independently and off by default**. When it is not allowed,
wsnap redacts the pixels / text before the result leaves — the caller learns that a capture
happened and where it was saved, but does not receive the content itself. Per-source rate limits
additionally cap how many requests can be made per minute (GIF recording more tightly than stills).

### What this protects — and what it doesn't

wsnap is honest about its threat model. It **does not** try to defend against malware already
running under your account: any program running as you can screenshot the desktop itself (the OS
provides the capture APIs), so wsnap cannot prevent that and does not claim to. The named-pipe ACL
is defence-in-depth — it lets wsnap restrict and audit callers — **not** a confidentiality boundary
against code already running as you.

What wsnap **does** guarantee is narrower, and honest:

1. It never becomes a *quieter or easier* screen-exfiltration tool than the OS already offers —
   external control is off by default, local-only, visible, audited, and rate-limited.
2. AI / agent captures are **consent-based and visible** — you approve the connection, you see each
   capture, and you can revoke access or kill a recording at any time.
3. The defaults are safe: off, local-only, visible, content-return denied, no network.

## Files wsnap writes

| Path | Contents |
|---|---|
| Your save folder (configurable; default `%TEMP%\wsnap`) | Captured images / GIFs / videos |
| `%APPDATA%\wsnap\settings.json` | Your settings |
| `%APPDATA%\wsnap\wsnap.log` | Crash / event log (and opt-in local telemetry) |
| `%APPDATA%\wsnap\audit.log` | External-control audit trail (metadata only) |
| `%LOCALAPPDATA%\wsnap\…` | On-demand OCR language packs / resolved ffmpeg |

Everything stays on your machine. Deleting these files — or uninstalling wsnap — removes the data.

## Questions

wsnap is open source under [GPL-3.0](LICENSE). If anything here doesn't match the code, that's a
bug — please open an issue. Bundled third-party components and their licenses (all permissive and
GPL-3.0-compatible) are listed in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
