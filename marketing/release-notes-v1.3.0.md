# wsnap v1.3.0 — much better OCR

This release replaces wsnap's text-recognition engine. The old one (Windows' built-in OCR)
mangled mixed Korean/English — confusing `O`↔`0` and `l`↔`I`, and garbling Hangul (e.g.
`프로토콜` → `하오토콜`). That's a limitation of the engine itself, not something preprocessing
can fix — so the engine had to go.

## ✨ New OCR engine: PaddleOCR PP-OCRv5 on ONNX Runtime

- **Accurate on mixed Korean + English**, code, and UI text. In testing, the exact strings the
  old engine broke (`프로토콜`, `Codex`, `Electron`, `최적화`) now come back correct.
- **Still fully offline and free** — no network, no tracking.
- **No language pack required** — the models ship inside the exe.
- **Respects your memory.** The engine loads lazily on first use and is released after a short
  idle, so OCR doesn't add to wsnap's idle tray footprint (the lean numbers from 1.2.4 stand).

Everything else you use OCR through is unchanged — the thumbnail **Text** button and the tray
**OCR region** mode both feed the new engine and copy recognized text to your clipboard.

## 📝 Notes

- The **first** recognition after launch is slightly slower (model warm-up); subsequent ones are fast.
- Rotated text isn't de-skewed (screenshots are assumed upright).
- The download grew (bundled ONNX runtime + models). This does **not** affect idle memory.
- Still **not code-signed** — SmartScreen may warn "unknown publisher"; choose **More info → Run anyway**. OSS signing is wired up and pending.

## Install

- **Installer:** `wsnap-setup-1.3.0.exe`
- **Portable:** `wsnap-v1.3.0-win-x64.zip` (single exe, no install)
- **Scoop:** `scoop install https://raw.githubusercontent.com/openwong2kim/wsnap/main/packaging/scoop/wsnap.json`

Verify your download against `SHA256SUMS.txt`.

---

OCR powered by [RapidOcrNet](https://github.com/BobLd/RapidOcrNet) + [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) PP-OCRv5 models. Full history in `CHANGELOG.md`.
