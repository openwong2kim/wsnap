# wsnap — marketing kit

Ready-to-use launch copy and positioning for wsnap. Everything here is honest about the
product as it ships today (including that it isn't code-signed yet). Edit the placeholders
(`<RELEASE_URL>`, screenshots) before posting.

## One-liner

> **wsnap** — macOS-style screen capture for Windows. Capture, and the image is already on
> your clipboard; drag the floating thumbnail anywhere as a real file. Free, offline, open source.

## Positioning

wsnap is *not* trying to out-feature ShareX. It's trying to bring the **macOS screenshot
feel** to Windows: you press a key, drag, and the screenshot is instantly usable — on your
clipboard, or as a file you drag straight into the next app. The two everyday verbs —
**paste** and **drag** — are first-class. Everything else (precise overlay, pixel loupe,
annotation editor, offline OCR, GIF, scrolling capture) is built around that in one clean
dark UI that lives in the tray.

## Who it's for

- People who screenshot constantly and want it to *just be on the clipboard*.
- Anyone who pastes screenshots into Slack / chat / docs and is tired of "copied the path, not the image."
- Developers, designers, and writers who want a fast annotation pass + drag-out.
- Korean/English users burned by Windows' built-in OCR mangling mixed text.
- Privacy-minded users who don't want a capture tool that phones home or forces uploads.

## The pillars (lead with these)

1. **Clipboard-first** — capture = image on clipboard, instantly. No folder digging, no "path vs image" trap.
2. **Drag-out as a real file** — the floating thumbnail is a draggable file, and it persists so you can drop it in several places.
3. **Genuinely good offline OCR** — PaddleOCR PP-OCRv5 on ONNX, accurate on mixed Korean/English, fully offline, no language pack.
4. **One beautiful dark UI** — overlay, editor, settings all share a design system; pixel loupe + HEX readout; PerMonitorV2 DPI-aware.
5. **Quiet & private** — lean tray footprint, no tracking, opt-in everything. GPL-3.0, open source.

## Proof points

- Clipboard image written in multiple formats (DIB + PNG + FileDrop) for broad app compatibility.
- OCR verified on mixed KO/EN: the exact strings Windows OCR garbled (`프로토콜`, `Codex`, `Electron`) come back correct.
- Idle resident memory cut to a single-digit working set / ~85 MB committed (v1.2.4).
- 11-tool annotation editor with object select/move/delete, undo + redo, undoable crop, and mosaic/blur redaction.

## Channels checklist

- [ ] GitHub Release polished (notes from `release-notes-v1.3.0.md`)
- [ ] Show HN (`launch-hacker-news.md`)
- [ ] r/Windows, r/software, r/coolgithubprojects, r/korea (`launch-reddit.md`)
- [ ] Product Hunt (`launch-producthunt.md`)
- [ ] X/Twitter thread (`launch-x-thread.md`)
- [ ] Add wsnap to AlternativeTo (as alternative to ShareX / Greenshot / Lightshot / Snipping Tool)
- [ ] Submit to awesome-windows / awesome-screenshot lists (PRs)
- [ ] (Optional) short demo video / GIF refresh showing clipboard-paste + drag-out + OCR

See `comparison.md` for the competitive table to reuse in posts and the landing page.
