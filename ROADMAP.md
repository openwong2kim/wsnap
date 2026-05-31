# wsnap 로드맵 (구현 현황)

> Windows용 맥 스타일 캡처 도구. "찍기 → 우하단 썸네일 → 드래그앤드롭 파일".
> 제품명은 **wsnap**으로 확정.

상태 범례: ✅ 구현 완료 · 🟡 best-effort/제약 있음 · ⚙️ 코드 외 인프라(사람이 수행)

---

## v0.1 — MVP ✅
- ✅ 저수준 키보드 훅, 영역 선택 오버레이(PerMonitorV2), 임시 PNG 저장
- ✅ 플로팅 썸네일: 드래그아웃=실제 파일, 클릭=경로 복사

## v0.2 — 상주 앱 기본기 ✅
- ✅ 트레이 아이콘 + 우클릭 메뉴 (캡처 / OCR / GIF / 스크롤 / 전체 지우기 / 설정 / 종료) — `App.cs`
- ✅ Windows 시작 시 자동 실행 (HKCU Run 키) — `AutoStart.cs`
- ✅ 단일 인스턴스 보장 (Mutex) + 재실행 시 캡처 트리거 — `SingleInstance.cs`
- ✅ 단축키 커스터마이즈 + Win+Shift+S 가로채기 토글(기본 off) — `HotkeyHook.cs`, `SettingsWindow.cs`
- ✅ 설정 저장 (저장 폴더 / 단축키 / 페이드 시간 / 최대 개수 등) — `Settings.cs`

## v0.3 — 캡처 히스토리 스택 ✅
- ✅ 여러 장이 우하단에 세로 누적 (newest 아래), 최대 개수 설정
- ✅ 각 썸네일 개별 드래그 / 클릭 / 삭제(✕) / 편집 / OCR 버튼
- ✅ 전체 지우기 (트레이 메뉴)
- ✅ 히스토리 영구 보관 옵션 (날짜별 폴더) — `CaptureStore.cs`

## v0.4 — 최소 편집 ✅
- ✅ 크롭, 화살표, 사각형, 펜, 텍스트, 모자이크 — `EditorWindow.cs`
- ✅ 편집 후 다시 썸네일로 (DnD 흐름 유지)
- ✅ 키보드 중심 (A/R/P/T/M/C, Ctrl+Z 실행취소, Enter 저장, Esc 취소)

## v0.5 — OCR & 텍스트 ✅
- ✅ 썸네일 "텍스트" 버튼 + 트레이 "텍스트 추출" (Windows.Media.Ocr, 온디바이스·무료) — `Ocr.cs`
- ✅ 한국어/영어 인식 (설치된 언어팩 기반)
- ✅ 영역 선택 직후 텍스트만 추출 모드 (CaptureMode.OcrText)

## v1.0 — 공개 릴리스
- ✅ 자체 포함 단일 .exe (`publish.ps1`) + 인스톨러 스크립트(`installer.iss`, Inno Setup)
- ✅ 충돌/예외 처리 (전역 핸들러 + 로컬 로그) — `CrashLog.cs`
- ✅ 텔레메트리(옵트인, 로컬 전용) — `CrashLog.Telemetry`
- ✅ 문서(README), 랜딩 페이지(`site/index.html`), GPL-3.0 라이선스(`LICENSE` + `NOTICE`) — v1.2.1에서 Apache-2.0 → GPL-3.0 전환
- 🟡 다중 모니터·고DPI: 코드 대응 완료(PerMonitorV2), 실제 회귀 테스트는 기기 필요
- ⚙️ 코드 서명 인증서 (SmartScreen 회피) — 인증서 구매·서명 필요
- ⚙️ GitHub 공개 / winget 등록 — 저장소 생성·게시 필요

## v1.1+ — 확장
- ✅ GIF 녹화 (영역, 즉시 인코딩·무한루프) — `GifRecorder.cs`, `GifWriter.cs`
- 🟡 스크롤 캡처 (자동 휠 + 겹침 검출 스티칭, 텍스트/웹에 적합·매끄러운 스크롤엔 취약) — `ScrollCapture.cs`
- ✅ 업로드 목적지 코드 경로 (Imgur, 옵트인) — `Uploader.cs` (⚙️ 사용자가 Client-ID 입력)
- ✅ 클립보드 감지 모드 (다른 도구 캡처도 썸네일화) — `ClipboardWatcher.cs`

## v1.1 — UX/UI 대개편 (best-in-class 정렬) ✅
다각도 전문가 감사(capture UX·편집기·비주얼·아웃풋·경쟁사) 후 임팩트/리스크 순 구현.
- ✅ **클립보드 우선**: 클릭=이미지 복사, 캡처 시 자동 복사(옵션), 다중 포맷(DIB+PNG+FileDrop) — `ImageClipboard.cs`
- ✅ **선택 직후 액션 툴바**(복사·저장·편집·OCR·GIF·고정) — `CaptureOverlay.cs` (설정에서 끄기 가능)
- ✅ **화면 프리즈 + punch-through 딤 + 실시간 W×H + 돋보기(픽셀 좌표·HEX 색)** — 캡처 후 Hide 레이스 제거
- ✅ **혼합 DPI 그랩 정확도**: 물리 커서 좌표(GetCursorPos)로 그랩 → 멀티모니터 정확
- ✅ **색 추출(스포이드) 모드** (CaptureMode.ColorPick)
- ✅ **고정(Pin)**: 자동 사라짐 끄기 + `%TEMP%` 밖 승격 보존, `자동 사라짐 0초=끄기`
- ✅ **썸네일 액션 바**: 복사·저장·편집·OCR·폴더·공유(Imgur)·고정·닫기 (벡터 아이콘) + 입장 팝 모션
- ✅ **편집기 강화**: 직선·원·형광펜·번호 배지·흐림 추가, 두께/커스텀 색, Redo, 되돌릴 수 있는 크롭, 클립보드 복사, Shift 제약, 액티브 상태 표시
- ✅ **캡처 모드 추가**: 전체 화면·현재 창·직전 영역 재캡처·지연(3/5초)·캡처 폴더 열기
- ✅ **통일 디자인 시스템** `Theme.cs`(다크 토큰/컨트롤 스타일) + 다크 타이틀바(DWM), 설정창 다크 카드 재스킨
- ✅ **벡터 아이콘** `Icons.cs` (랜딩과 동일 라인아트, 폰트 의존 없음)

## v1.2 — 파워 유저 확장 ✅
설계 워크플로우(기능별 정밀 스펙)로 도출 후 의존성 순서로 구현·헤드리스 검증.
- ✅ **편집기 객체 선택/이동/삭제** (Select 도구 V, 핸들/마키, 타입별 Translate, MoveOp/DeleteOp undo/redo) — `EditorWindow.cs`
- ✅ **창 자동 감지** (오버레이에서 창 호버 하이라이트 + 클릭=창 캡처, DWM 확장 프레임 경계·cloaked 제외) — `CaptureOverlay.cs`
- ✅ **캡처 히스토리 갤러리** (스크래치+날짜폴더+pinned 열거, 타일 드래그아웃/복사/편집/폴더/열기/삭제, rolling keep-N 보존) — `HistoryWindow.cs`, `CaptureStore.cs`
- ✅ **파일명 템플릿** ({app}/{title}/{date}/{time}/{seq}/{w}/{h} + 날짜 포맷, 새니타이즈·폴백, 전경 창 컨텍스트) — `CaptureStore.cs`, `App.cs`, `Settings.cs`

### 보류 (검증 불가 → 향후)
- 🔲 **진짜 MP4/H.264 녹화(+오디오)** — Media Foundation SinkWriter 인터롭으로 프로토타입(옵트인·코덱 프로브·GIF 폴백 설계)했으나, 본 빌드 환경에서 MF 싱크라이터가 `IMFSinkWriter`에 대해 `E_NOINTERFACE`(기능하는 H.264 싱크 부재)를 반환해 **검증 불가** → 실제 하드웨어 검증 전까지 보류. GIF 녹화가 영상 경로 유지.

---

## 남은 사람 몫 (코드 외)
1. **코드 서명**: Authenticode 인증서로 `wsnap.exe`/`setup.exe` 서명 → SmartScreen 경고 제거.
2. **GitHub 공개**: `git init` 후 원격 생성·푸시 (현재 git 저장소 아님), 릴리스에 exe 첨부.
3. **winget**: 매니페스트 작성 후 microsoft/winget-pkgs PR.
4. **Imgur 키**: 본인 Client-ID를 설정 창에 입력해야 업로드 활성화.
5. **OCR 언어팩**: 설정 > 시간 및 언어 > 언어에서 한국어 OCR 기능 설치돼 있어야 한/영 인식.
