# wsnap Avalonia 마이그레이션 핸드오프

> 대상: WPF+WinForms → Avalonia UI 마이그레이션(진행 중, 여러 세션에 걸친 장기 프로젝트).
> 원본 계획 파일: `C:\Users\rizz\.claude\plans\humming-meandering-aurora.md` (승인 완료, 로컬 전용 — 이 문서가 저장소에 남는 요약본).
> 새 세션은 이 문서 하나만 읽으면 이어서 진행할 수 있어야 한다.

---

## 0. 왜 하는가 (배경)

v1.8.0("다이어트" 릴리즈, idle RAM 304MB→147MB, DXGI 캡처, 압축 해제 등)로 패키징 레벨 최적화는 끝났다. 사용자가 다음 단계로 요청한 건 UI 프레임워크 자체를 바꿔 더 가볍고 현대적으로 만드는 것.

**스택 결정 근거** (경쟁제품 실제 스택 조사):
- ShareX(wsnap과 기능 가장 유사 — OCR·GIF·비디오·업로드·히스토리) = **.NET/WPF+WinForms**, wsnap과 동일.
- Snipaste(가볍기로 유명) = **C++/Qt**, 단 기능은 훨씬 적음(편집기/비디오/MCP 없음).
- **결정적 근거**: ShareX 팀 자신도 자체 리라이트("XerahS")를 Rust/C++가 아니라 **Avalonia**(C#, Skia 렌더링)로 진행 중.

Rust/C++ 전면 재작성은 두 차례(다이어트 검토 시, 스택 재검토 시) 모두 기각 — 8,600줄 재작성에 수개월, DXGI/OCR은 가능하나 편집기·오버레이·i18n·CLI/MCP 제어층까지 처음부터 재구현해야 함. **Avalonia = 같은 C#으로 UI 레이어만 교체**하는 경로로 확정.

---

## 1. 현재 상태 (2026-07-10 기준)

| PR | 내용 | 상태 |
|---|---|---|
| [#43](https://github.com/openwong2kim/wsnap/pull/43) | v1.8.0 다이어트 릴리즈 | MERGED, [v1.8.0 릴리즈](https://github.com/openwong2kim/wsnap/releases/tag/v1.8.0) 공개됨 |
| [#44](https://github.com/openwong2kim/wsnap/pull/44) | scoop/winget 해시 갱신 | MERGED |
| [#45](https://github.com/openwong2kim/wsnap/pull/45) | Avalonia 마이그레이션 스캐폴드 + 스파이크(a)(d) | MERGED |
| [#46](https://github.com/openwong2kim/wsnap/pull/46) | 이 핸드오프 문서 + 스파이크(b)(c) PASS 기록 | MERGED |
| [#47](https://github.com/openwong2kim/wsnap/pull/47) | **Phase 0**: SkiaSharp 이미징 통일(SkiaImage.cs) + 프레임워크-무관 클립보드 코어(ClipboardCore.cs) + 자체 GIF89a 인코더(GifWriter 재작성) | MERGED |
| [#48](https://github.com/openwong2kim/wsnap/pull/48) | **Phase 1**: Theme.axaml+AppTheme(Avalonia 디자인시스템, FluentTheme dark 기반 class-selector 재설계) + HotkeyHook 프레임워크-무관화·링크 + DevShowcase 검증 창 | MERGED |
| #50 | **Phase 2**: CaptureOverlay Avalonia 이식(4-rect 딤) + ClipboardWatcher 프레임워크-무관 재작성 + MonitorPlacement/ScreenGrab 무관화·링크 + AvImaging/Icons | 작성됨 |

`main`에 `Wsnap.Avalonia/` sibling 프로젝트가 존재. **release.yml은 여전히 `Wsnap.csproj`만 빌드** — Avalonia 작업은 배포에 영향 없음.

---

## 2. 아키텍처 결정 (승인됨, 변경 시 이 문서도 갱신할 것)

1. **별도 sibling 프로젝트, in-place 전환 아님.** `UseWPF`/`UseWindowsForms`는 csproj SDK 레벨 플래그라 한 프로젝트에서 동시 불가. `Wsnap.Avalonia/Wsnap.Avalonia.csproj`가 24개 프레임워크-무관 파일을 `<Compile Include="..\Foo.cs" Link="Foo.cs" />`로 **링크**(복사 아님, 드리프트 없음).
2. **병합 방식**: 마이그레이션 내내 `main`에 점진적으로 머지. "원자적"인 건 배포 전환 시점(release.yml이 WPF exe 대신 Avalonia exe를 배포하기 시작하는 순간)뿐 — 코드 머지가 아니라 설정 스위치.
3. **착수 전 4개 게이팅 스파이크** (하루 이내씩, 실패 기준 명시): (a)오버레이 컴포지팅 [완료→PASS], (d)멀티모니터 mixed-DPI [부분완료], (b)클립보드 라운드트립 [완료→PASS], (c)파일 드래그아웃 [완료→PASS]. **4개 전부 종료 — 게이트 통과, Phase 0 착수 가능.**
4. **클립보드/이미징**: Avalonia `IClipboard`는 Windows에서 실제 열린 버그 있음([#20183](https://github.com/AvaloniaUI/Avalonia/issues/20183) SetImage가 Chrome에서 깨짐, [#20644](https://github.com/AvaloniaUI/Avalonia/issues/20644) 픽셀밀림) → **스파이크(b)로 우회 확정: 클립보드 I/O는 WinForms `Clipboard.SetDataObject`/`GetDataObject`(트레이용으로 이미 유지되는 WinForms 섬이라 신규 의존성 없음). 양방향 실측 PASS라 raw Win32 P/Invoke 컨틴전시는 폐기.** 이미지 인코딩은 SkiaSharp(이미 OCR용 번들됨)로 통일 — **Phase 0로 분리**해 WPF 빌드에서 먼저 처리(Avalonia와 무관, v1.9.0로 독립 출시 가능).
5. **트레이 아이콘: WinForms 유지(의도적 예외).** `NotifyIcon`+`ContextMenuStrip`+`TrayMenuTheme.cs`(v1.8에서 막 검증된 다크테마)를 그대로 유지 — Avalonia `NativeMenu`는 OS 네이티브 렌더링이라 owner-draw 다크테마가 원천적으로 불가능. **나중에 "순수성" 추구한다고 제거하지 말 것.**
6. **단계 순서**: Phase 0(이미징/클립보드, WPF 빌드) → Phase 1(기반: Theme.cs 재설계, HotkeyHook 이식) → Phase 2(CaptureOverlay+MonitorPlacement+ClipboardWatcher) → Phase 3(ThumbnailWindow+HistoryWindow+Toast+SettingsWindow) → Phase 4(GifRecorder/VideoRecorder/ScrollCapture+Control층, 보안 재검증 필수) → Phase 5(EditorWindow, 가장 마지막) → Phase 6(회귀+최소 1릴리즈 preview 옵트인 후 기본전환).
7. **기간**: 주 단위 아니라 **개월 단위** — 스파이크 시작부터 확신을 갖고 전환까지 약 3~5개월(사이드프로젝트 페이스). WPF v1.8.x/v1.9.x 라인은 마이그레이션 내내 평시 버그픽스 릴리즈 계속.

---

## 3. 스파이크 결과 (실측, PR #45 본문에도 기록됨)

### 스파이크(a) 오버레이 컴포지팅 — 완료, PASS + 기법 변경 확정

CaptureOverlay.cs의 핵심(전체 가상데스크톱 크기 topmost/opaque 창 + frozen bitmap + punch-through dim + 드래그)을 검증.

- **성능**: PASS. 합성 드래그(SetCursorPos 60step) + **외부 GDI 스크린샷**(자체 `RenderTargetBitmap` 렌더는 신뢰 불가로 폐기 — 자체 렌더 캡처와 실제 화면 캡처가 불일치하는 걸 발견)으로 측정. pointer-move 간격이 4회 연속 실행 모두 **15~18ms 안정**(baseline 빈 창의 ~19ms와 동급). WPF가 v1.8에서 고친 layered-window CPU 리드로우 stutter 재현 안 됨.
  - **측정 함정 기록**: 초기 측정에서 `gap_max=300ms`대 이상치가 나와 당황했으나, 드래그 루프 **종료 후** 트레일링 이벤트였음(스크린샷 저장+마우스업 사이 지연이 섞임). 실제 드래그 구간만 잘라보면 깨끗함. **향후 유사 측정 시 루프 종료 시점 이후 데이터는 반드시 제외할 것.**
- **펀치스루 지오메트리**: FAIL → 대체 기법 확정. WPF의 `GeometryGroup+FillRule.EvenOdd`와 대안 `CombinedGeometry+GeometryCombineMode.Xor` 둘 다 Avalonia Win32/Skia 백엔드에서 **잘못 렌더링**됨 — 외부 스크린샷 픽셀 휘도 A/B 비교(육안 판단 아님, `0.3R+0.59G+0.11B` 계산)로 정량 확인, "구멍" 영역이 undimmed 대신 대부분 근검정으로 나옴.
  - **해결: 지오메트리 구멍 뚫기 대신 선택영역 바깥을 덮는 불투명 `Rectangle` 4개(상/하/좌/우)로 분리.** 검증 완료(휘도비율 정확히 일치하는 지점 다수 확인). **Phase 2에서 CaptureOverlay 이식 시 이 기법을 사용할 것 — WPF 코드를 그대로 번역하면 안 됨.**

### 스파이크(d) 멀티모니터 mixed-DPI — 부분 완료, 가능한 범위 PASS

- 이 환경은 **단일 모니터**(1920x1080 물리, 125% 스케일)라 진짜 mixed-DPI 검증 불가 — v1.1 WPF 마이그레이션 때도 동일 제약 기록됨.
- 가능한 범위에서 확인: `Avalonia.Controls.Screens.Primary.Bounds`가 실제 물리해상도(1920x1080) 정확 보고, `.Scaling`(1.25)이 `GetScaleFactorForMonitor`(125%, ground truth) 정확 일치 — 가상화/비DPI인식 폴백 아님. `PixelPoint` 기반 창배치가 요청한 물리픽셀 좌표에 정확히 안착(`MonitorPlacement.cs`가 의존하는 기법의 Avalonia 대응 확인).
- **미해결**: 실제 멀티모니터 mixed-DPI(예: 100%+150% 조합)는 실기기 확인 필요. ~~Phase 2 진행 전 반드시 재검증.~~ → 사용자 지시(모니터 없음)로 아래 이론 분석으로 대체하고 진행. **실기기 검증은 Phase 6 컷오버 전 필수로 이월.**

**(d)-보완: mixed-DPI 이론 분석 (2026-07-10, Phase 2 착수 근거)**

- **캡처 정확성은 DPI 변환에 의존하지 않는다.** 결과물에 관여하는 모든 값이 물리픽셀 단일 공간: `GetCursorPos`(드래그 시작/끝), `GetSystemMetrics(SM_*VIRTUALSCREEN)`(freeze 범위), `EnumWindows`/`DwmGetWindowAttribute`의 창 rect(윈도우 캡처), `CopyFromScreen`/DXGI(freeze 자체), freeze 크롭. 논리단위는 시각 표시(선택 테두리·배지·loupe 위치)에만 쓰인다. 따라서 mixed-DPI에서도 저장 PNG의 픽셀·크기·영역 좌표는 이론상 정확하다.
- **시각 레이어의 근사 수준은 현행 WPF와 동급.** WPF 오버레이도 `SystemParameters.VirtualScreen*`(primary DPI 기준 단일 변환)으로 창을 펴고 물리 freeze를 Stretch.Fill로 덮는 구조 — Avalonia 포트는 가상데스크톱 원점 모니터의 스케일 하나로 같은 일을 한다. 두 구현 모두 다른 스케일의 보조 모니터에서 시각 요소가 소폭 어긋날 수 있으나 결과물은 불변.
- **실기기에서만 확인 가능한 잔여 리스크 2건**: ①창이 여러 모니터에 걸칠 때 OS의 WM_DPICHANGED 통지에 Avalonia Win32 백엔드가 리스케일로 반응하며 `SetWindowPos`로 강제한 물리 bounds를 되돌리는지(Opened에서 1회 재고정하지만 통지가 그 뒤에 오면 미검증 영역), ②`Screens` API가 보고하는 보조 모니터 Scaling의 정확성(스파이크(d)에서 primary만 실측). 두 건 모두 시각 레이어에 국한 — 캡처 데이터는 물리 공간이라 영향 없음.
- `MonitorPlacement`(썸네일/토스트 배치)는 이미 커서 모니터의 물리 work-area+DPI 기반(v1.1 WPF에서 검증된 설계)이며 그대로 링크됨 — mixed-DPI 안전.

### 스파이크(b) 클립보드 라운드트립 — 완료, PASS (WinForms 경로 확정)

Avalonia 11.2 + `UseWindowsForms` 조합의 일회용 호스트(스캐폴드와 동일 조건: `[STAThread]` Main, classic desktop lifetime)에서 WinForms 클립보드를 구동, **모든 검증은 별도 외부 프로세스에서** 수행(스파이크(a) 교훈).

- **쓰기 방향** (ImageClipboard.Put()과 동일한 3중 포맷: `SetImage`+`"PNG"` 스트림+`SetFileDropList`, `SetDataObject(copy:true)`): PASS.
  - 외부 프로세스의 raw Win32 포맷 열거: CF_BITMAP/CF_DIB/CF_DIBV5/PNG/CF_HDROP 전부 존재, **쓰기 프로세스 종료 후에도 유지**(OleFlushClipboard 정상).
  - 픽셀 정량 검증(4사분면 고유색): PNG·DIB·FileDrop 전부 정확 일치, PNG 경로 알파(128)도 보존.
  - `SetDataObject` 소요 17~29ms.
- **읽기 방향** (외부 프로세스가 쓴 이미지를 Avalonia 호스트에서 `GetDataObject`의 PNG 스트림 + `GetImage`(DIB)로 읽기 — ClipboardWatcher/에디터 붙여넣기 경로): 둘 다 픽셀 정확, PASS.
- **Chrome 실붙여넣기** (핵심 수용 기준 — Avalonia 자체 버그 #20183이 정확히 이 지점): 실제 설치된 Chrome(headed, playwright-core 구동)에서 ①contenteditable에 Ctrl+V paste 이벤트 ②`navigator.clipboard.read()` 두 경로 모두 image/png 300x200 수신, 4사분면 픽셀 정확 일치. **PASS.**
- 결론: Avalonia `IClipboard`를 아예 사용하지 않으므로 #20183/#20644 비해당. §2-4의 raw P/Invoke 컨틴전시 불필요 — WinForms 경로로 확정.

### 스파이크(c) 파일 드래그아웃 — 완료, PASS (외부 앱 2종 실드롭 확인)

Avalonia 창(topmost, 고정 좌표)에서 `PointerPressed` 시 `DataFormats.Files`+`IStorageFile`로 OLE 드래그 시작, **별도 드라이버 프로세스가 SendInput으로 실제 마우스 드래그**를 주입, 외부 앱에서 수신 검증.

- **Chromium(실제 Chrome) 드롭**: drop 페이지가 파일 수신 — 이름·크기·**SHA-256 완전 일치**, dragover 이벤트 37회, `DoDragDrop` 반환값 `Copy`. PASS.
- **탐색기 폴더 드롭**: 지정 폴더에 파일 복사됨, **SHA-256 완전 일치**. PASS.
- **API 차이 실측**: Avalonia 11.2의 진입점은 `DragDrop.DoDragDrop(PointerEventArgs, IDataObject, DragDropEffects)`이며 `Task<DragDropEffects>` 반환 — **`DoDragDropAsync`라는 이름은 11.2에 없음**(이후 버전 리네임). WPF 동기 `DoDragDrop` 대비 제어흐름은 await로 전환.
- **중대 함정 발견 — 탐색기는 드롭 데이터를 비동기 추출**(`IDataObjectAsyncCapability`): 첫 시도에서 `effect=Copy`가 반환됐는데도 파일이 복사되지 않음 — 소스 프로세스가 `DoDragDrop` 반환 직후 종료해 OLE 데이터 객체가 추출 전에 죽었기 때문. 소스를 드롭 후 생존시키자 정상 복사. **실제 wsnap은 상주 앱이라 비해당이지만, 테스트 하네스·단명 프로세스 경로에선 반드시 고려할 것.** (Chrome은 드롭 시점에 동기 추출이라 이 함정과 무관하게 통과.)
- `IStorageFile`은 `StorageProvider.TryGetFileFromPathAsync`로 **포인터 프레스 이전에 미리 해석**해 둘 것 — 프레스 핸들러 안에서 await하면 드래그 시작 시점에 포인터가 이미 떠 있을 수 있음.

---

## 4. 다음 세션에서 할 것 (순서대로)

1. ~~**Phase 0 착수**~~ **완료(PR #47)**: 모든 PNG 인코딩이 `SkiaImage.cs`(SkiaSharp)로 통일 — `CaptureStore`(구 GDI+ `Bitmap.Save`), `ImageClipboard`(구 WPF `PngBitmapEncoder`), `McpStdioServer` 리사이즈, `Ocr`(PNG 왕복 제거, 직접 픽셀 복사). 클립보드 I/O는 프레임워크-무관 `ClipboardCore.cs`(WinForms OLE, 스파이크(b) 경로)로 분리. `GifWriter`는 WPF `GifBitmapEncoder`+바이트 패치 방식에서 **자체 GIF89a 인코더**(octree 256색 팔레트 + ppmtogif-계열 LZW + GCE/NETSCAPE 루프)로 재작성 — UI 프레임워크 의존성 0. `SkiaImage.cs`/`ClipboardCore.cs`/`GifWriter.cs`는 `Wsnap.Avalonia`에도 링크됨. 검증: `.smoke` 11/11 + 전용 하네스(PNG 픽셀/알파, JPEG 트랜스코드, GIF 프레임·딜레이·루프·그라디언트 양자화 mean err 8.76·노이즈 LZW 오버플로, 클립보드 라운드트립) 7/7 PASS. v1.9.0 독립 출시는 선택지로 남음(버전은 아직 1.8.0).
2. ~~**Phase 1**~~ **완료(PR #48 MERGED)**: (1) `Wsnap.Avalonia/Theme.axaml` — WPF Theme.cs의 keyed style+trigger를 Avalonia 관용구로 **재설계**: FluentTheme(dark) 기반 + Fluent 토큰 오버라이드(SystemAccentColor·TextControl*·ComboBox*) + class-selector 스타일(`Button.primary`/`.ghost`/`.subtle`, `ToggleButton.tool`, `TextBox.field`, `ComboBox.combo`, `CheckBox.toggle` — 상태는 `:pointerover`/`:pressed`/`:checked` pseudo-class + `/template/ ContentPresenter#PART_ContentPresenter` 셀렉터). (2) `Wsnap.Avalonia/Theme.cs`의 `AppTheme` 정적 클래스 — 색 토큰·`Brush(key)`·`Apply(w)`(DWM 다크 타이틀바 동일 P/Invoke, HWND는 `TryGetPlatformHandle()`). (3) 공유 `HotkeyHook.cs` 프레임워크-무관화: WPF Dispatcher 의존 제거, `Install()` 시점 `SynchronizationContext` 캡처 후 `Post`(WPF=DispatcherSynchronizationContext라 동작 동일, Avalonia UI 컨텍스트도 동일 코드) — Avalonia에 링크됨. (4) `--showcase` 플래그로 뜨는 `DevShowcase` 검증 창. 검증: 외부 GDI 스크린샷 픽셀 체크 13/13(토큰 스와치 8종+창배경+primary/tool:checked/ghost) + SendInput Ctrl+Alt+F9 → probe 파일 `test.probe|ui=True`(Avalonia UI 스레드 마셜링 확인). WPF 회귀 `.smoke` 11/11.
3. ~~**Phase 2**~~ **완료(PR #50)**: 실기기 mixed-DPI 재검증은 사용자 지시로 **이론 분석으로 대체**(§3-d-보완 참조, 하드웨어 확보 시 실검증은 Phase 6 컷오버 전 필수로 이월).
   - `Wsnap.Avalonia/CaptureOverlay.cs`: 전체 이식 — frozen 데스크톱(물리픽셀), **4-rect 딤**(스파이크(a) 기법, EvenOdd 지오메트리 금지), 드래그 선택+코너 핸들+W×H 배지, loupe(NearestNeighbor 확대+hex/좌표), 윈도우 자동감지(EnumWindows+DWM cloaked/frame-bounds), 커밋 후 툴바(7버튼)+키보드 라우트(S/C/E/T/G/P/Esc), `RequestAnimationFrame` 기반 move 코얼레싱(CompositionTarget.Rendering 대응). 창 배치는 `PixelPoint`+`SetWindowPos`(물리) — 결과물은 DPI 변환에 의존하지 않음.
   - 공유 파일 무관화: `ClipboardWatcher.cs` **재작성**(WPF HwndSource → raw Win32 메시지-온리 창 + `ClipboardCore`/`SkiaImage`; FileDrop은 의도적으로 무시 — 구 CF_DIB-only 동작과 동일), `ScreenGrab.cs`를 partial로 분리(`ToBitmapSource`만 `ScreenGrabWpf.cs`로), `MonitorPlacement.cs` WPF 폴백 제거, `ClipboardCore.TryReadImageBytes(includeFileDrop)` 파라미터 추가. 셋 다 Avalonia에 링크.
   - Avalonia 전용 신규: `AvImaging.cs`(GDI Bitmap→WriteableBitmap, 알파 강제 불투명), `Icons.cs`(WPF와 동일 path 데이터 — 컷오버까지 양쪽 동기 유지).
   - 검증(외부·정량): 4색 창 + SetCursorPos 드래그 하네스 — 딤 비율 0.55(이론 0.549 정확), 펀치스루 내부 픽셀 원본 일치(Δ0), region 물리좌표 정확(400,300,480,360), 저장 PNG 크기·4사분면 픽셀 정확, Region 모드 rect-only, 툴바 모드 커밋+S키 저장; ClipboardWatcher 런타임 4/4(발화+픽셀·suppress·텍스트 무시·Stop). WPF 회귀 `.smoke` 11/11.
4. **Phase 3**: ThumbnailWindow+HistoryWindow+Toast+SettingsWindow 이식 (§2-6). Toast가 생기면 `ToastStub.cs` 삭제.

---

## 5. 알아두어야 할 함정 (다음 세션이 다시 겪지 않도록)

- **`Wsnap.Avalonia.csproj`의 `AssemblyName`을 `wsnap`(WPF와 동일)으로 두면 안 됨.** 개발 중엔 `wsnap-avalonia`로 분리 유지 — 두 sibling SDK 프로젝트가 같은 AssemblyName을 쓰면 `.smoke` 하네스 빌드가 `CS0579`(중복 AssemblyInfo 특성) 에러로 깨짐. `wsnap`으로 되돌리는 건 **Phase 6 실제 컷오버 시점에만.**
- **`.smoke/smoke.csproj`의 기본 glob이 `.smoke/ocr/` 하위 `OcrSmoke.csproj`를 자동 제외하지 않는 환경 버그가 있음.** `<Compile Remove="ocr\**\*.cs" />`를 명시적으로 추가해야 함(로컬에 이미 적용돼 있으나 `.smoke/`는 gitignore라 이 저장소엔 안 남아있음 — `.smoke` 관련 빌드가 `CS0579`로 깨지면 이 항목부터 의심).
- **`Wsnap.csproj`(WPF)는 `Wsnap.Avalonia\`를 명시적으로 `<Compile Remove>`하고 있음** — 새 하위폴더/프로젝트를 저장소 루트에 추가할 때는 항상 WPF 프로젝트의 기본 glob이 그걸 삼키지 않는지 확인할 것(빌드는 되는데 타입 충돌 에러로 나타남, 예: 두 개의 다른 `Toast` 클래스 충돌).
- **`Control\CommandRouter.cs`/`Control\ControlGate.cs`는 아직 `Wsnap.Avalonia`에 링크 안 됨** — `Toast.*`/`CaptureCore.*` 호출이 있어서. `Wsnap.Avalonia/ToastStub.cs`는 `Ocr.cs`의 Toast 호출 2곳만 임시로 막아주는 스텁 — **Phase 3에서 진짜 Avalonia Toast가 생기면 즉시 삭제할 것.**
- **자체 렌더(`RenderTargetBitmap.Render(this)`) 기반 스크린샷 검증은 신뢰하지 말 것** — 실제 화면과 불일치하는 사례를 스파이크(a)에서 실측 확인. 시각적 검증은 항상 외부(별도 프로세스에서 GDI `CopyFromScreen`) 캡처로 할 것.
- **`Wsnap.Avalonia`에서 수식 없는 `Control` 타입은 Avalonia.Controls.Control이 아니라 `Wsnap.Control` 네임스페이스(링크된 제어층)로 해석됨**(CS0118 실측) — 완전 수식 필요. 같은 계열: `Brushes`/`Bitmap`은 System.Drawing과 Avalonia 양쪽에 있어 using alias로 구분(CaptureOverlay.cs 상단 참조).
- **오버레이류 자동화 하네스에서 SetCursorPos 드래그 중 실제 마우스가 움직이면 릴리즈 좌표가 오염됨**(실측: 사용자 마우스 개입으로 region 오답) — 마우스업 직전 `SetCursorPos` 재고정 2회를 넣을 것.
- **Avalonia 창 안에서 `Theme.Apply(...)`를 그대로 쓰면 안 됨** — Avalonia `StyledElement`에 이미 `Theme` 속성(ControlTheme)이 있어 컨트롤 파생 클래스 안에서는 클래스가 아니라 그 속성으로 해석됨(CS1061). Avalonia 쪽 디자인시스템 클래스는 그래서 `AppTheme`로 명명 — **창 이식 시 `Theme.` → `AppTheme.` 치환 필요.**
- **SkiaSharp 3.x의 `SKBitmap.Decode`는 디코드 불가 바이트에 null을 반환하지 않고 `ArgumentNullException('codec')`을 던짐** — Phase 0 검증 하네스에서 실측 발견. null 체크만 믿지 말고 try/catch 필요(`SkiaImage.TranscodeToPng`에 적용됨).
- **드래그아웃 관련 함정 3건은 §3 스파이크(c) 참조**: ①11.2엔 `DoDragDropAsync` 없음(`DoDragDrop`이 Task 반환), ②탐색기는 드롭 데이터를 비동기 추출하므로 소스 데이터 객체가 드롭 후에도 살아 있어야 함, ③`IStorageFile`은 포인터 프레스 전에 미리 해석.

---

## 6. 관련 메모리 / 참고 문서

- 프로젝트 메모리(에이전트 세션 간 자동 기록): `wsnap-weight-lag-audit`, `wsnap-avalonia-vs-native-review`, `wsnap-avalonia-migration`
- 승인된 원본 계획: `C:\Users\rizz\.claude\plans\humming-meandering-aurora.md`
- v1.1 마이그레이션 시 동일 DPI 제약을 기록한 선례: 프로젝트 메모리 `wsnap-perf-dpi-fixes`
