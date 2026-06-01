# Reddit launch posts

Reddit hates marketing speak and link-dropping. Lead with the story/problem, show the GIF,
be a real person in the comments, and follow each subreddit's self-promo rules (many require
you to be an active participant, not a drive-by).

Good targets: r/Windows, r/software, r/coolgithubprojects, r/opensource, r/productivity,
r/korea (for the KO OCR angle), r/csharp / r/dotnet (for the build).

---

## r/Windows · r/software — title options

- `I made a macOS-style screenshot tool for Windows — capture and it's instantly on your clipboard, or drag it out as a file`
- `wsnap: a free, open-source capture tool where the screenshot is on your clipboard the moment you release the mouse`

## Body

I kept getting annoyed that Windows capture tools either dump the screenshot in a folder I
have to go find, or copy the *file path* when I wanted the *image*. So I built **wsnap**.

You press Shift+F1, drag a region, and:

- the image is **already on your clipboard** — paste anywhere with Ctrl+V, and
- a little thumbnail floats bottom-right that you can **drag straight into Slack / an editor /
  an email as a real file** (and it stays so you can drop it in more than one place).

A toolbar pops up right at your selection: copy, save, edit, OCR, GIF, pin. There's a proper
dark annotation editor (arrows, boxes, blur/mosaic redaction, numbered steps, undo+redo), a
pixel loupe with a live HEX color readout, window auto-detection, scrolling capture, GIF
recording, and a capture history gallery.

The OCR is the part I'm proudest of: it uses PaddleOCR PP-OCRv5 models running offline on
ONNX, and it's genuinely good on **mixed Korean + English** (Windows' built-in OCR kept
mangling that for me). Models are baked into the exe, so there's no language pack to install.

It's lean in the tray, doesn't phone home (no account, opt-in telemetry only, local log),
and it's **open source, GPL-3.0**. Single self-contained exe; there's a portable zip and a
Scoop manifest.

Heads up: it's **not code-signed yet**, so SmartScreen will warn "unknown publisher" — signing
is in progress. Windows x64 only.

GitHub (with a demo GIF and downloads): `<REPO_URL>`

Happy to answer anything — and very open to feedback on the capture/drag-out flow.

---

## r/korea (or KO communities) — Korean version

윈도우 캡처 도구들이 스크린샷을 폴더에 던져두거나, 이미지가 아니라 *파일 경로*를 클립보드에
복사하는 게 늘 답답해서 **wsnap**을 만들었습니다.

Shift+F1 → 영역 드래그 → 그 순간 **이미지가 클립보드에 바로** 올라가서 어디든 Ctrl+V로 붙고,
우하단에 뜬 썸네일을 슬랙/에디터/메일로 **실제 파일처럼 드래그앤드롭**할 수 있습니다(여러 번
재드래그 가능). 선택 영역 옆에 복사·저장·편집·OCR·GIF·고정 툴바가 뜹니다.

특히 **OCR이 한·영 혼합에 강합니다.** 윈도우 내장 OCR이 `프로토콜`을 깨뜨리던 게 싫어서,
PaddleOCR PP-OCRv5 모델을 ONNX로 **완전 오프라인** 구동하도록 했습니다. 모델이 exe에
내장돼 언어팩 설치도 필요 없습니다.

트레이 상주 메모리도 가볍고, 추적 없음(텔레메트리 옵트인·로컬 로그), **오픈소스(GPL-3.0)**입니다.
단일 exe + 포터블 zip + Scoop 지원.

참고: 아직 **코드 서명 전**이라 SmartScreen "알 수 없는 게시자" 경고가 뜹니다(서명 진행 중).

깃허브(데모 GIF·다운로드): `<REPO_URL>`

## Notes

- Don't cross-post the identical text to many subs at once; tailor each.
- In r/csharp/r/dotnet, lead with the stack (WPF, ONNX OCR, single-file publish, memory work).
