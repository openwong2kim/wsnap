# Show HN

## Title (pick one, ≤80 chars, no emoji — HN style)

- `Show HN: wsnap – macOS-style screen capture for Windows (clipboard-first, drag-out)`
- `Show HN: wsnap – Windows screenshots that are instantly on your clipboard and draggable`

## URL

`<RELEASE_URL or https://github.com/openwong2kim/wsnap>`

## Text (first comment — post immediately after submitting)

I built wsnap because every Windows capture tool I tried got the two things I do with a
screenshot every day slightly wrong: I either had to dig the file out of a folder, or the
tool copied the *file path* to my clipboard when I wanted the *image*.

wsnap makes both first-class. You press Shift+F1, drag a region, and the image is already on
your clipboard — paste it anywhere. A small thumbnail floats at the bottom-right; you can
drag it straight into Slack/an editor/an email as a real file, and it stays put so you can
drop it again somewhere else. A toolbar appears right at your selection for copy / save /
edit / OCR / GIF / pin.

A few things I cared about while building it:

- **OCR that actually works on Korean + English.** Windows' built-in OCR mangled mixed text
  for me (it'd turn `프로토콜` into garbage, `Codex` into `C0dex`). wsnap runs PaddleOCR
  PP-OCRv5 models on ONNX Runtime, fully offline, with the models embedded in the exe — no
  language pack. In my testing the exact strings the built-in engine broke now come back
  correct.
- **Lean in the tray.** It idles at a single-digit working set; the OCR engine is loaded
  lazily and released after use, so it doesn't sit on memory you're not using.
- **Private.** No account, no forced uploads, no tracking. Telemetry is opt-in and writes to
  a local log only.
- **One dark UI** for the overlay, the annotation editor, and settings, with a pixel loupe +
  HEX color readout, and PerMonitorV2 DPI-awareness.

Stack: C# / .NET 8 / WPF, Win32 interop for capture / clipboard / global hotkeys, and
ONNX Runtime (RapidOcrNet) for the OCR. It ships as a single self-contained exe. It's GPL-3.0.

Caveats, up front: it's Windows-only (x64), and it's **not code-signed yet**, so SmartScreen
will warn about an unknown publisher — OSS signing is wired up and pending. Scrolling capture
is best-effort.

I'd love feedback on the capture/annotate/drag-out flow specifically, and on the OCR quality
if you work with non-Latin text. Repo and a portable build are in the link.

## Notes for posting

- Post Tue–Thu, ~8–10am ET tends to do best. Don't ask for upvotes.
- Be present in the thread for the first 2–3 hours to answer.
- Lead replies with substance (how a thing works), not marketing.
