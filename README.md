# wsnap — v1.0

macOS-style screen capture for Windows: **Shift+F1** → drag a region → a floating
thumbnail stacks bottom-right → **drag it out as a real file** (terminal, Explorer,
chat, upload field), click to copy the path, or edit / OCR it on the spot.

심플함과 드래그앤드롭을 1급 기능으로 둔 네이티브 Windows 캡처 도구. 제로 설정,
임의 단축키, PerMonitorV2 정확 캡처, 썸네일에서 바로 OCR까지.

## 기능
- **캡처 → 썸네일 → 드래그앤드롭** (핵심). 우하단에 최대 N개 세로 스택.
- **최소 편집기**: 화살표·사각형·펜·텍스트·모자이크·크롭 (키보드 우선, Undo).
- **OCR**: 썸네일 "텍스트" 버튼 또는 영역 OCR 모드 → 클립보드 복사 (온디바이스·무료).
- **GIF 녹화**: 영역 선택 → 녹화 → 무한루프 애니메이션 GIF.
- **스크롤 캡처**: 자동 휠 스크롤 + 겹침 검출 스티칭 (best-effort).
- **클립보드 감지**: 다른 도구가 복사한 이미지도 자동 썸네일화.
- **상주 골격**: 트레이 메뉴, 자동 시작, 단일 인스턴스, 설정 저장, 크래시 로깅.
- **업로드(옵트인)**: Imgur (본인 Client-ID 필요).

## 소스 구조
| 파일 | 역할 |
|---|---|
| `App.cs` | 진입점·트레이·단일 인스턴스·캡처 흐름 오케스트레이션 |
| `HotkeyHook.cs` | 전역 키보드 훅 (커스텀 단축키 + Win+Shift+S 토글) |
| `CaptureOverlay.cs` | 영역 선택 오버레이 (Capture/OCR/Region 모드) |
| `ScreenGrab.cs` | 화면 픽셀 그랩 + Bitmap→BitmapSource |
| `CaptureStore.cs` | 저장 위치/히스토리 정책 |
| `ThumbnailWindow.cs` | 플로팅 썸네일 스택 (드래그/편집/OCR/삭제) |
| `EditorWindow.cs` | 최소 편집기 |
| `Ocr.cs` | Windows.Media.Ocr 래퍼 |
| `GifRecorder.cs` / `GifWriter.cs` | GIF 녹화 + 딜레이·루프 인코딩 |
| `ScrollCapture.cs` | 스크롤 캡처(겹침 스티칭) |
| `ClipboardWatcher.cs` | 클립보드 이미지 감지 |
| `Uploader.cs` | Imgur 업로드 |
| `Settings.cs` / `SettingsWindow.cs` | 설정 모델·UI |
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
