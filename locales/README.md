# Translating wsnap

wsnap's UI can be translated by adding a single JSON file here — **no C# or build-file changes needed.**

## How to add a language

1. **Copy `en.json`** (the reference, every key) to `<code>.json`, where `<code>` is the
   language's short code (e.g. `de` for German, `fr` for French, `ja` for Japanese, `es` for
   Spanish). Use a BCP-47-ish lowercase code.
2. **Set `"_native"`** at the top to the language's name *in that language* (e.g. `"Deutsch"`,
   `"Français"`, `"日本語"`). This is what shows in Settings → Language.
3. **Translate each value.** Leave the **keys** (left side) unchanged. Translate only the
   right-side text.
4. Open a pull request. That's it — the build embeds `locales/*.json` automatically and the app
   registers the new language on startup (`L.LoadEmbeddedPacks`).

`en.json` itself is **not shipped** — English and Korean are built into `Strings.cs`. `en.json`
exists only as the up-to-date key reference for translators.

## Rules

- **Don't translate placeholders.** Keep `{0}`, `{1}` exactly where they make sense in your
  language — they're filled at runtime (e.g. a count, a key combo, a file size). Word order may
  change; the placeholder must remain.
- **Don't translate format tokens** in `set.templateHint`: `{app} {title} {date} {time} {seq}
  {w} {h}` and date patterns like `{yyyy-MM-dd_HHmmss}` are literal — leave them as-is.
- **Keep file-dialog filters parseable**: in `thumb.pngFilter`, keep the `|`-separated structure
  `Label (*.png)|*.png|Label (*.*)|*.*` — translate only the labels.
- **Missing keys fall back to English**, so a partial translation still works; you can ship what
  you have and fill the rest later.
- Symbols like `✓`, `●`, `·`, `…` can stay as-is.

## Note: UI language vs OCR language

This folder is about the **display language** (menus, buttons, settings). It's separate from the
**OCR recognition language** (which script the text-extraction model reads), configured under
Settings → OCR. Translating the UI does not require touching OCR.
