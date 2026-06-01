# wsnap vs. other Windows capture tools

An honest comparison to use in posts, the landing page, and AlternativeTo. wsnap's edge is
**workflow** (clipboard-first + drag-out), **offline KO/EN OCR quality**, and **a clean,
private, open-source** package — not raw feature count (ShareX wins on sheer breadth).

| | **wsnap** | Snipping Tool | ShareX | PicPick | Greenshot | Lightshot |
|---|:--:|:--:|:--:|:--:|:--:|:--:|
| Image on clipboard instantly | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| **Drag thumbnail out as a real file** | ✅ | ❌ | ⚠️ via after-capture window | ❌ | ❌ | ❌ |
| Floating, re-draggable thumbnail | ✅ | ❌ | ⚠️ | ❌ | ❌ | ❌ |
| Action toolbar *at the selection* | ✅ | ⚠️ | ⚠️ | ⚠️ | ⚠️ | ✅ |
| Pixel loupe + HEX color readout | ✅ | ❌ | ✅ | ✅ | ❌ | ⚠️ |
| Annotation editor | ✅ (11 tools, undo+redo, redaction) | ⚠️ basic | ✅ | ✅ | ✅ | ⚠️ basic |
| **Offline OCR, strong on KO+EN** | ✅ PP-OCRv5 | ⚠️ Win OCR | ⚠️ Win/Tesseract | ❌ | ⚠️ plugin | ❌ |
| GIF / screen recording | ✅ GIF | ⚠️ video | ✅ | ❌ | ❌ | ❌ |
| Scrolling capture | ✅ | ❌ | ✅ | ✅ | ❌ | ❌ |
| Mixed-DPI multi-monitor correct | ✅ | ✅ | ✅ | ⚠️ | ⚠️ | ⚠️ |
| No account / no forced upload | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ uploads by default |
| Free for commercial use | ✅ GPL-3.0 | ✅ | ✅ | ⚠️ free personal only | ✅ | ✅ |
| Open source | ✅ | ❌ | ✅ | ❌ | ✅ | ❌ |
| Resident footprint | Lean (single-digit WS idle) | n/a built-in | Moderate–heavy | Light (native) | Light | Light |

⚠️ = partial / with caveats. Built from public behavior at time of writing; verify before quoting competitors.

## The honest one-paragraph pitch

If you live in **ShareX**, keep it — nothing beats its breadth. If you want the **macOS
screenshot experience on Windows** — press a key, drag, and it's instantly on your clipboard
*and* draggable into the next app as a file — with a beautiful dark editor, a real pixel
loupe, and offline OCR that actually handles Korean and English, wsnap is for you. It's
quiet in the tray, doesn't phone home, and it's open source (GPL-3.0).

## Known gaps (say these plainly)

- Not code-signed yet → SmartScreen "unknown publisher" prompt (signing is in progress).
- Scrolling capture is best-effort (weak on smooth-scroll / parallax pages).
- Windows-only, x64.
- No cloud history / sync (by design — it's local and private).
