# X / Twitter launch thread

Keep each post tight. Attach media to posts 1, 3, 5. End with the repo link (links can
suppress reach, so the CTA link goes last, not in post 1).

---

**1/ (hook + hero GIF)**
Windows screenshot tools kept getting 2 everyday things wrong:
— I had to dig the file out of a folder, or
— they copied the file *path* when I wanted the *image*.

So I built wsnap: macOS-style capture for Windows.
Press a key, drag — it's already on your clipboard. 🧵

**2/**
The thumbnail that floats after capture? It's a real, draggable file.
Drag it straight into Slack, an editor, an email — and it stays put, so you can drop it in
several places. No "save → find file → attach."

**3/ (toolbar GIF/screenshot)**
A toolbar appears right at your selection:
copy · save · edit · OCR · GIF · pin
Keyboard-driven (C / Enter / E / T / G / P). The whole thing freezes the screen, dims around
your selection, and gives you a pixel loupe with a live HEX color readout.

**4/ (OCR before/after image)**
The part I'm proudest of: offline OCR that's actually good on Korean + English.
Windows' built-in OCR turned `프로토콜` into garbage and `Codex` into `C0dex`.
wsnap runs PaddleOCR PP-OCRv5 on ONNX — fully offline, models baked into the exe, no language
pack. The strings the old engine broke now come back correct.

**5/ (editor/settings screenshot)**
Also in the box: 11-tool annotation editor (blur/mosaic redaction, numbered steps, undo+redo),
window auto-detection, scrolling capture, GIF recording, capture history gallery, filename
templates. One consistent dark UI. Lean in the tray.

**6/ (CTA)**
No account. No forced uploads. No tracking. Open source (GPL-3.0).
Single self-contained exe + portable zip + Scoop.

Not code-signed yet (SmartScreen will warn — signing's in progress), Windows x64.

Grab it / star it 👇
`<REPO_URL>`

---

## Short standalone post (for reuse)

wsnap: macOS-style screen capture for Windows.
Press a key, drag → the image is on your clipboard instantly, and the floating thumbnail
drags out as a real file. Dark editor, pixel loupe, offline KO/EN OCR, GIF, scrolling capture.
Free & open source. `<REPO_URL>`
