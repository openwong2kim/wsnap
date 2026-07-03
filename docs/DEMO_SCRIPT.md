# wsnap — 사용법 데모 촬영 대본 (ShareX)

촬영 도구: **ShareX** · 대상: wsnap v1.5.1 · 화면: 1080p 이상, 배율 100% 권장
자막은 **영어**(13개 언어 앱 → 국제 배포), 디렉션 노트는 한국어.

산출물 2종:
- **A. 히어로 GIF** — README 상단용, 무음, ~18초, 핵심 루프만 (캡처→썸네일→드래그→붙여넣기)
- **B. 풀 사용법 영상** — 기능 투어, 자막/나레이션, ~75초

---

## 0. 사전 준비 (촬영 전 1회)

### wsnap 설정
- **Settings → Capture → "Show an action toolbar after selecting a region" 켜기**
  (기본은 off=드래그 즉시 썸네일. 영상 B에서 툴바를 보여주려면 ON. 영상 A는 OFF가 더 깔끔)
- Settings → Thumbnails → Auto-dismiss를 **0(off)** 으로 (촬영 중 썸네일이 사라지지 않게)
- Settings → Storage → 저장 폴더를 깨끗한 데모 폴더로
- 히스토리 데모를 찍을 거면 미리 캡처 3~4개 만들어 갤러리 채워두기
- OCR 데모용으로 **한/영 텍스트가 섞인 화면**(코드 에디터나 기사) 준비

### ShareX 설정
- **Task settings → Screen recorder → Screen recording options**
  - FPS: **30** (GIF는 15도 충분), 출력: 영상=MP4(H.264), GIF=ShareX GIF
  - "Show cursor" **켜기**
- **Application settings → "마우스 클릭 시각화"**(Mouse click effect) 켜면 클릭 지점이 표시돼 따라하기 쉬움
- **Hotkey settings** — 녹화 시작/정지 핫키를 wsnap과 안 겹치게:
  - wsnap 캡처 = **Shift+F1** (이건 영상에 나와야 하니 건드리지 말 것)
  - ShareX 녹화 토글 = 예 **Ctrl+Shift+F12** 같은 빈 조합으로
- **영역 고정 녹화**: "Screen recording (custom region)"로 wsnap 동작 영역만 잡으면 결과물이 작고 깔끔
- 바탕화면 정리: 알림 끄기(집중 지원/방해 금지), 배경 단색, 불필요한 트레이 아이콘 숨기기

> 팁: 한 번에 길게 찍지 말고 **장면별로 끊어 찍어** 나중에 합치면 NG 재촬영이 쉽다.

---

## A. 히어로 GIF (~18초, 무음, 자막만)

핵심 메시지 한 줄: **"찍으면 이미 클립보드에 있고, 썸네일은 그대로 끌어 쓰는 진짜 파일이다."**

| # | 시간 | 화면 / 동작 | 화면 자막(영어) | 디렉션 노트 |
|---|------|------------|----------------|------------|
| 1 | 0:00–0:02 | 깨끗한 바탕화면, 트레이의 wsnap 아이콘 살짝 강조 | `Press Shift+F1` | 마우스를 트레이 근처에서 천천히 |
| 2 | 0:02–0:05 | Shift+F1 → 화면 freeze + 어두워짐, 영역을 드래그. **라이브 W×H 숫자**와 **돋보기 루페+HEX**가 보이게 | `Freeze · drag · precise` | 드래그를 또박또박, 루페가 보이도록 잠깐 멈춤 |
| 3 | 0:05–0:07 | 마우스를 떼면 우하단에 **플로팅 썸네일**이 팝업 | `Already on your clipboard` | 떼는 순간 강조 |
| 4 | 0:07–0:11 | 썸네일을 **좌클릭 드래그**해서 Explorer 창(또는 채팅 입력창)에 **드롭 → 실제 파일** 생성 | `Drag it out as a real file` | 드롭 후 파일이 생기는 걸 분명히 |
| 5 | 0:11–0:14 | 메모장/Slack 등에 **Ctrl+V** → 이미지가 바로 붙음 | `…or just paste it` | Ctrl+V 키캡 표시 권장 |
| 6 | 0:14–0:18 | wsnap 로고 + 태그라인 | `wsnap — native · offline · GPL-3.0` | 페이드 아웃 |

후처리: 15–18초, 폭 800–960px로 축소, 루프. (아래 ffmpeg 섹션)

---

## B. 풀 사용법 영상 (~75초, 자막 + 선택적 나레이션)

나레이션은 짧고 담백하게. 자막만으로도 성립하게 작성.

### 인트로 (0:00–0:06)
- 화면: 바탕화면 + 트레이 아이콘.
- 자막: `wsnap — macOS-style screen capture for Windows`
- 나레이션: "wsnap은 트레이에 상주하는 가벼운 캡처 도구입니다. 창이 따로 없어요."

### 1. 캡처 → 썸네일 → 드래그앤드롭 (0:06–0:22) — *가장 중요, 천천히*
- 동작: Shift+F1 → 영역 드래그(루페·W×H 노출) → 떼면 썸네일 → 좌클릭 드래그로 다른 앱에 드롭 → 그리고 **다시 한 번 다른 곳에 드롭**(파일이 남아있음 강조) → 마지막으로 Ctrl+V로 붙여넣기.
- 자막: `Capture → floating thumbnail → drag anywhere, again and again` / `Image is on your clipboard the instant you release`
- 나레이션: "찍는 순간 이미지가 클립보드에 올라가고, 썸네일은 진짜 파일이라 여러 곳에 끌어다 놓을 수 있습니다."

### 2. 액션 툴바 (0:22–0:32)
- 동작: 다시 영역 선택 → 선택 지점에 뜨는 **Copy·Save·Edit·OCR·GIF·Pin** 툴바를 보여주고, 키 힌트 자막.
- 자막: `Action toolbar right at your selection — C / Enter / E / T / G / P`
- 나레이션: "툴바를 켜두면 선택한 자리에서 바로 복사·저장·편집·OCR·GIF·고정을 고를 수 있습니다."

### 3. 주석 편집기 (0:32–0:45)
- 동작: 캡처 → Edit(E). 화살표 그리기 → 숫자 단계 → **모자이크로 민감정보 가리기** → 텍스트 한 줄 → undo/redo 한 번 → Ctrl+C(복사) 또는 저장.
- 자막: `Annotate: arrow · steps · mosaic redaction · text — undo/redo, then copy`
- 나레이션: "편집기에는 화살표, 번호 단계, 모자이크 가림, 텍스트가 있고 전부 키보드로 됩니다."

### 4. OCR (0:45–0:55)
- 동작: 한/영 섞인 텍스트 영역을 캡처 → OCR(T) → 추출된 텍스트가 뜨고 복사되는 모습.
- 자막: `On-device OCR (Korean + English) — free, fully offline`
- 나레이션: "텍스트 인식은 기기 안에서 돌아갑니다. 인터넷도, 언어팩 설치도 필요 없어요."

### 5. 히스토리 갤러리 (0:55–1:03)
- 동작: 트레이 메뉴 또는 핫키로 히스토리 → 썸네일 격자에서 하나를 **다시 드래그**하거나 편집.
- 자막: `Every capture in a history gallery — re-drag, edit, delete`
- 나레이션: "지난 캡처는 갤러리에 모여서 언제든 다시 끌어 쓰거나 편집할 수 있습니다."

### 6. ✨ 신기능: 언어 라이브 프리뷰 (1:03–1:12) — *v1.5.1 하이라이트*
- 동작: Settings → Language 드롭다운 열기 → 13개 언어 목록 → **한국어 선택 → 설정창 전체가 즉시 그 언어로 바뀜** → 다시 English → Cancel하면 원래 언어로 복원.
- 자막: `New in 1.5.1 — pick a language and the UI re-localizes instantly`
- 나레이션: "1.5.1부터 언어를 고르면 설정창이 그 자리에서 바로 번역됩니다. 저장·재실행 필요 없어요."

### 아웃트로 (1:12–1:18)
- 화면: 로고 + 설치 명령.
- 자막: `Install: scoop install … / winget install openwong2kim.wsnap` / `github.com/openwong2kim/wsnap · GPL-3.0`
- 나레이션: "Scoop이나 winget으로 설치하세요. 오픈소스입니다."

---

## 촬영 팁 (공통)

- **마우스를 평소보다 30% 느리게.** 데모는 빠르면 안 보인다.
- 클릭/키 입력 전 0.3초 멈춤 → 시청자가 "무엇을 누르는지" 인지할 시간.
- 키 입력은 **키캡 오버레이**(ShareX 클릭 효과 + 후처리 자막)로 보강.
- 각 장면 끝에 0.5초 여백 → 컷 편집이 쉬움.
- 같은 장면 2~3테이크 찍고 제일 깔끔한 걸 채택.
- 드래그앤드롭 대상(Explorer/Slack/메모장)은 **미리 열어 화면에 배치**.

---

## 후처리 (ffmpeg) — 선택

ShareX가 직접 GIF를 뽑지만, README용으로 더 작고 선명한 GIF가 필요하면 MP4로 찍고 ffmpeg로 변환:

```powershell
# 1) MP4 → 팔레트 생성 (색 번짐 줄이고 용량↓) — 폭 900px, 15fps
ffmpeg -i demo.mp4 -vf "fps=15,scale=900:-1:flags=lanczos,palettegen" palette.png

# 2) 팔레트 적용해 GIF 출력
ffmpeg -i demo.mp4 -i palette.png -lavfi "fps=15,scale=900:-1:flags=lanczos[x];[x][1:v]paletteuse" site/demo.gif

# (영상 B를 MP4로 README/릴리즈에 첨부할 땐 그대로 두거나 가볍게 재인코딩)
ffmpeg -i demo.mp4 -c:v libx264 -crf 23 -preset slow -pix_fmt yuv420p site/usage.mp4
```

목표 용량: 히어로 GIF는 **5MB 이하** 권장(GitHub README 로딩). 넘으면 폭/ fps를 더 낮춘다.

---

## 산출물 배치
- 히어로 GIF → `site/demo.gif` (README 상단이 이미 이 경로를 참조)
- 풀 영상 → `site/usage.mp4` 또는 릴리즈 자산/유튜브 링크로
