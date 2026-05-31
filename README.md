# wsnap — v1.2

macOS-style screen capture for Windows: **Shift+F1** → drag a region → pick an action
(복사·저장·편집·OCR·GIF·고정) from the toolbar **at the selection**, or just drag the
floating thumbnail out as a **real file**. The image is on your clipboard instantly.

심플함과 드래그앤드롭을 1급 기능으로 둔 네이티브 Windows 캡처 도구. 캡처 즉시 클립보드 복사,
픽셀 단위 돋보기 + HEX 색 추출, 통일된 다크 UI, PerMonitorV2 정확 캡처.

## 데모

![wsnap 데모](https://github.com/openwong2kim/wsnap/raw/main/site/demo.gif)

## 기능
- **캡처 → 썸네일 → 드래그앤드롭** (핵심). 우하단에 최대 N개 세로 스택.
- **클립보드 우선**: 클릭=이미지 복사, 캡처 즉시 자동 복사(옵션) — 어디든 `Ctrl+V`. `Ctrl+클릭`=경로 복사.
- **선택 직후 액션 툴바**: 복사·저장·편집·OCR·GIF·고정 (키보드 C/Enter/E/T/G/P·Esc).
- **정밀 오버레이**: 화면 프리즈 + 선택 영역만 밝게(punch-through) + 실시간 W×H + 돋보기(픽셀 좌표·HEX 색).
- **색 추출(스포이드)**: 픽셀 클릭 → `#RRGGBB` 복사.
- **편집기**: 화살표·직선·사각·원·펜·형광펜·텍스트·번호·모자이크·흐림·크롭. 두께/색 선택,
  **객체 선택·이동·삭제(V)**, Redo, 되돌릴 수 있는 크롭, 클립보드 복사(Ctrl+C), Shift 제약(45°/정사각). 키보드 우선.
- **다양한 캡처 모드**: 영역·전체 화면·**창 클릭 자동 감지**·직전 영역 재캡처·지연(3/5초).
- **캡처 히스토리 갤러리**: 저장된 모든 캡처를 썸네일로 탐색 → 재드래그·복사·편집·삭제(휴지통).
- **파일명 템플릿**: `{app}`·`{title}`·`{date}` 등 토큰으로 자동 명명(전경 앱/창 제목 반영).
- **고정(Pin)**: 자동 사라짐 끄기 + `%TEMP%` 밖으로 승격해 보존.
- **OCR**: 영역/썸네일에서 온디바이스 텍스트 인식 → 클립보드 (무료·오프라인).
- **GIF 녹화 / 스크롤 캡처 / 클립보드 감지**.
- **통일된 다크 UI**: 편집기·설정까지 하나의 디자인 시스템(다크 타이틀바 포함).
- **상주 골격**: 트레이 메뉴, 자동 시작, 단일 인스턴스, 설정 저장, 크래시 로깅.
- **업로드(옵트인)**: Imgur (본인 Client-ID 필요) — 썸네일 공유 버튼.

## 소스 구조
| 파일 | 역할 |
|---|---|
| `App.cs` | 진입점·트레이·캡처 모드·액션 라우팅·단일 인스턴스 |
| `Theme.cs` | 공유 디자인 시스템(색·타이포·컨트롤 스타일·다크 타이틀바) |
| `Icons.cs` | 벡터 라인 아이콘(랜딩과 동일 언어, 폰트 의존 없음) |
| `ImageClipboard.cs` | 다중 포맷 이미지 클립보드(DIB+PNG+FileDrop, 재시도) |
| `HotkeyHook.cs` | 전역 키보드 훅 (커스텀 단축키 + Win+Shift+S 토글) |
| `CaptureOverlay.cs` | 캡처 오버레이 (프리즈·딤·W×H·돋보기·액션 툴바·Capture/OCR/Region/ColorPick) |
| `ScreenGrab.cs` | 화면 픽셀 그랩 + Bitmap→BitmapSource |
| `CaptureStore.cs` | 저장 위치/히스토리 정책 + 고정 승격 |
| `ThumbnailWindow.cs` | 플로팅 썸네일 스택 (복사·저장·편집·OCR·폴더·공유·고정·삭제) |
| `HistoryWindow.cs` | 캡처 히스토리 갤러리 (썸네일 그리드·드래그아웃·재편집·삭제) |
| `EditorWindow.cs` | 주석 편집기 (11개 도구·Redo·되돌릴 수 있는 크롭·클립보드 복사) |
| `Ocr.cs` | Windows.Media.Ocr 래퍼 |
| `GifRecorder.cs` / `GifWriter.cs` | GIF 녹화 + 딜레이·루프 인코딩 |
| `ScrollCapture.cs` | 스크롤 캡처(겹침 스티칭) |
| `ClipboardWatcher.cs` | 클립보드 이미지 감지 |
| `Uploader.cs` | Imgur 업로드 |
| `Settings.cs` / `SettingsWindow.cs` | 설정 모델·UI(다크 카드) |
| `AutoStart.cs` / `SingleInstance.cs` / `CrashLog.cs` / `Toast.cs` | 상주 인프라 |

## 빌드 & 실행 (Windows)
.NET 8 SDK (또는 9 SDK) + Windows Desktop 워크로드 필요. WinRT(OCR)용으로
`net8.0-windows10.0.19041.0`을 타깃한다.

```powershell
dotnet run --project Wsnap.csproj
```

단일 자체포함 exe:

```powershell
pwsh -File publish.ps1     # -> publish\wsnap.exe
```

인스톨러 (Inno Setup 6):

```powershell
ISCC.exe installer.iss     # -> dist\wsnap-setup-1.0.0.exe
```

## 사용법
1. 실행 — 창은 안 뜨고 트레이 아이콘만. 
2. **Shift+F1** (또는 트레이 더블클릭) → 영역 드래그.
3. 우하단 썸네일:
   - **좌클릭 드래그** → 파일/경로 전달 (유지되어 여러 곳에 재드래그 가능)
   - **클릭** → 경로 복사
   - **호버 버튼** → 편집 / 텍스트(OCR) / ✕
   - **우클릭 드래그(옆으로)** → 밀어서 치우기
   - 방치 → 설정한 시간 뒤 자동 사라짐
4. 트레이 메뉴: 캡처 / OCR 영역 / GIF 녹화 / 스크롤 캡처 / 전체 지우기 / 설정 / 종료.

설정: 저장 폴더, 단축키 재바인딩, 자동 사라짐 시간, 최대 표시 개수, 자동 시작,
Win+Shift+S 가로채기, 히스토리(날짜 폴더), 클립보드 감지, 텔레메트리(옵트인), 업로드.

## 알아둘 점
- **OCR**: Windows 설정에서 한국어 OCR 기능이 설치돼 있어야 한·영 인식 (없으면 안내 토스트).
- **스크롤 캡처**는 best-effort — 텍스트/웹엔 잘 되지만 부드러운 스크롤·패럴럭스엔 취약.
- **배포 시 코드 서명** 권장 (SmartScreen 경고 회피) — `ROADMAP.md` v1.0 참고.
- 추적 없음: 텔레메트리는 옵트인·로컬 로그 전용(`%APPDATA%\wsnap\wsnap.log`).

자세한 단계별 현황은 `ROADMAP.md` 참고.
