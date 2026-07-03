# wsnap 통합 제어 계층 기획서 — CLI · MCP · 핫키 확장

> 대상: .NET 8 WPF Windows 트레이 상주 스크린샷/OCR/녹화 도구 wsnap (GPL-3.0).
> 핵심 가치 제약: **네이티브 · 오프라인 · 계정 없음 · 트래킹 없음 · 단일 self-contained exe.**
> 본 문서는 6개 관점(아키텍처/MCP/CLI/핫키/보안/로드맵) 독립 설계를 하나로 통합하고, 충돌을 근거와 함께 단일 결정으로 확정한다. 모든 설계는 코드 Read로 검증한 사실에 기반한다.

---

## 0. 확인된 코드 사실 (검증됨)

- **진입점**: `App.Main()` 은 현재 **인자 없음**(App.cs:36 `public static void Main()`), `[STAThread]`. → CLI/MCP 추가 시 `Main(string[] args)` 로 시그니처 변경 필요.
- **단일 인스턴스 / 유일한 현 IPC**: `SingleInstance`(SingleInstance.cs) — Mutex `wsnap.singleton.v1` + named `EventWaitHandle "wsnap.signal.capture.v1"`. 2번째 실행은 `existing.Set()` 신호만 보내고 즉시 종료(SingleInstance.cs:45-49). **파라미터/응답 전달 불가.** 리스너 스레드가 `onSecondLaunch()` → `app.Dispatcher.BeginInvoke(StartCapture)`(App.cs:52-53).
- **헤드리스 순수 함수 (UI/STA 무관, 검증)**:
  - `ScreenGrab.Grab(x,y,w,h)->Bitmap` (ScreenGrab.cs:26, GDI `CopyFromScreen`).
  - `CaptureStore.SaveBitmap(bmp, NameContext)->string path` (CaptureStore.cs:71). `NewPath`(53), `BuildName`(152), `EnumerateHistory(int max=600)->List<(string Path, DateTime When, bool Pinned)>`(103).
  - `Ocr.RecognizeAsync(Bitmap)->Task<string?>` (Ocr.cs:86, 내부 `Task.Run` ONNX). `""`=텍스트없음, `null`=엔진불가. **언어를 내부 `CurrentLanguage`로 읽음(Ocr.cs:101)** → per-call 언어 지정하려면 오버로드 필요.
- **대화형 캡처**: `StartCapture`(App.cs:263)가 `CaptureOverlay`를 `Show()`하고 `overlay.Closed += (_,_)=>RouteCapture(overlay)`(App.cs:270)로 **비동기 완료**. 결과는 `overlay.ResultPath/ResultBitmap/RegionPx/Action`. `_overlayOpen` 가드(App.cs:261,265)로 직렬화.
- **부수효과 결합 지점**: `DeliverRegion`(App.cs:447)은 Grab→Save까지 헤드리스지만 `ImageClipboard.CopyImageFile`(STA) + `new ThumbnailWindow().Show()` + `ScheduleTrim()`이 박혀 있음. `RunOcr`(App.cs:340)는 `Toast`+`CopyText` 결합.
- **STA 바운드**: `ImageClipboard.*`(OLE 클립보드), `ThumbnailWindow`/`Toast`/`CaptureOverlay`(WPF). 반드시 상주 STA 디스패처에서.
- **핫키**: `HotkeyHook`(HotkeyHook.cs) — WH_KEYBOARD_LL 저수준 훅 1개, `Settings.Current`의 단일 `HotkeyVk/Shift/Ctrl/Alt/Win`(0x70=F1, Shift=true → 기본 **Shift+F1**) + `SwallowWinShiftS`(기본 false)만 비교해 `CaptureRequested` 이벤트 1종 발화(HookCallback:64-92). 매칭은 `Settings.Current`를 라이브로 읽음.
- **패키징**: `OutputType=WinExe`(Wsnap.csproj:4, **콘솔 없음**). `InvariantGlobalization`·`UseSystemResourceKeys` 등 크기 트리밍. 유일 대형 의존성 `RapidOcrNet`. 임베디드 OCR 모델(det/cls/korean_rec) 런타임 추출. scoop `"bin":"wsnap.exe"`.

---

## 1. 통합 아키텍처 — 단일 CommandBus + 단일 ControlGate

### 1.1 원칙
네 진입점(**트레이 · 핫키 · CLI · MCP**)이 **상주 인스턴스 내부의 단일 CommandRouter(CommandBus)** 를 공유한다. App.cs의 흩어진 액션은 **재작성하지 않고** 결과 반환형 `Cmd_*` 얇은 래퍼로 감싸 재사용한다. 보안·동의 정책은 진입점마다가 아니라 **픽셀에 닿기 직전 ControlGate 한 곳**에서만 강제한다.

### 1.2 토폴로지 다이어그램

```
                         ┌────────────────────────────────────────────────────────────┐
                         │        상주 wsnap.exe  (WinExe · WPF · STA · 트레이)          │
   전역 핫키 ────────────▶│  HotkeyHook ─┐                                              │
   트레이 메뉴 ───────────▶│  Tray ───────┤                                              │
                         │              ▼                                              │
                         │        ┌───────────────┐   ┌───────────────────────────┐   │
   Named Pipe ──────────▶│ Pipe   │ CommandRouter │──▶│  ControlGate (정책 1회 강제) │   │
   wsnap.control.v1      │ Server │  (CommandBus) │   │ master off? · 동의? · 레이트 │   │
   (NDJSON 요청/응답)     │        └──────┬────────┘   │ 리밋? · 가시신호 · 감사로그   │   │
                         │  Dispatcher   │            └────────────┬──────────────┘   │
                         │  .InvokeAsync │                         ▼                  │
                         │        ┌──────▼────────────────────────────────────────┐  │
                         │        │ CaptureCore (헤드리스 순수 · UI 무관)           │  │
                         │        │ ScreenGrab.Grab · CaptureStore.SaveBitmap      │  │
                         │        │ Ocr.RecognizeAsync · ForegroundContext         │  │
                         │        └──────┬─────────────────────────────────────────┘ │
                         │  대화형만 ▲     │ (헤드리스 결과 즉시 반환)                    │
                         │  CaptureOverlay │                                          │
                         │   .Closed → TCS ▼                                          │
                         │        ThumbnailWindow · Toast · ImageClipboard(STA)       │
                         └───────────▲────────────────────────────────────────────────┘
                                     │ Named Pipe (현재 사용자 SID DACL, 오프라인)
              ┌──────────────────────┼────────────────────────┐
              │                      │                         │
   ┌──────────┴─────────┐  ┌─────────┴──────────┐   ┌──────────┴─────────┐
   │  wsnap <verb>      │  │  wsnap mcp         │   │  임의 스크립트/앱    │
   │  얇은 CLI 클라이언트 │  │  stdio JSON-RPC     │   │  (파이프 직접)       │
   │  헤드리스 or 위임    │  │  브리지 (상주 위임)  │   └────────────────────┘
   │  AttachConsole      │  │        ▲           │
   └────────────────────┘  │        │ stdio      │
                           │  Claude Desktop /   │
                           │  Claude Code (spawn)│
                           └────────────────────┘

  레거시 EventWaitHandle "wsnap.signal.capture.v1" 존치:
  인자 없는 재실행 = 대화형 캡처 트리거(가시적·무파라미터·저위험). 파이프 실패 시 폴백.
```

### 1.3 Command / Result 타입 (신규 `Command.cs`)

```csharp
namespace Wsnap.Control;

public enum CommandKind
{
    // 헤드리스 가능(좌표/경로 완결, 오버레이 불요)
    CaptureRegion, CaptureFullScreen, CaptureWindow,
    OcrRegion, OcrImage, OcrLast, ColorAt,
    HistoryList, HistoryGet, OpenFolder,
    // 대화형(오버레이 필수 = 상주 전용)
    CaptureInteractive, OcrInteractive, ColorPick,
    CaptureRepeat, CaptureDelayed, Gif, Video, Scroll,
    // 상태/창
    ShowHistory, ClearThumbnails, OpenSettings,
    // 제어/조회
    SettingsGet, SettingsSet, Ping, ListCommands,
    // 사용자 정의(핫키 전용, opt-in)
    Shell
}

public sealed record WsnapCommand(CommandKind Kind, JsonElement? Args = null,
                                  CommandSource Source = CommandSource.Internal);
public enum CommandSource { Internal, Hotkey, Tray, Cli, Mcp }   // ControlGate 신뢰 등급 판단

public enum ResultType { Ack, File, Text, Color, History, Settings, CommandList }

public sealed record CommandResult
{
    public bool Ok { get; init; }
    public ResultType Type { get; init; }
    public string? Error { get; init; }
    public string? ErrorCode { get; init; }   // busy|no_region|ocr_unavailable|denied|unknown_cmd...
    public string? Path { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? Text { get; init; }
    public string? Hex { get; init; }
    public object? Payload { get; init; }
    public static CommandResult Fail(string code, string msg) => new(){Ok=false, ErrorCode=code, Error=msg};
    public static CommandResult FileSaved(string p, int w, int h) => new(){Ok=true, Type=ResultType.File, Path=p, Width=w, Height=h};
}
```

### 1.4 App 액션 재배치 (최소 침습)
기존 부수효과 메서드는 그대로 두고, **결과 반환형 `Cmd_*` 래퍼**를 추가해 코어를 재사용한다.

```csharp
// App.cs 에 internal 추가 — 기존 순수 함수 재사용, 결과를 CommandResult로 반환
internal CommandResult Cmd_CaptureRegion(JsonElement? a)
{
    var r = ReadRect(a);
    if (r.Width < 1 || r.Height < 1) return CommandResult.Fail("no_region", "invalid rect");
    var ctx = ForegroundContext(r.Width, r.Height);           // 기존 재사용(App.cs:532)
    string path;
    using (var bmp = ScreenGrab.Grab(r.X, r.Y, r.Width, r.Height))  // 기존 재사용
        path = CaptureStore.SaveBitmap(bmp, ctx);             // 기존 재사용
    if (Settings.Current.AutoCopyOnCapture) ImageClipboard.CopyImageFile(path);
    new ThumbnailWindow(path).Show();                          // UI 스레드이므로 OK
    ScheduleTrim();
    return CommandResult.FileSaved(path, r.Width, r.Height);
}
```

트레이/핫키는 이제 버스 경유로 얇게 대체(이미 UI 스레드라 마샬링 불요):
```csharp
_hook.Triggered += b => _bus.ExecuteAsync(new(b.Command, b.Args, CommandSource.Hotkey));
menu.Items.Add(..., (_,_) => _ = _bus.ExecuteAsync(new(CommandKind.CaptureInteractive, null, CommandSource.Tray)));
```

**2단계에서 이 재배치는 "관측 가능한 동작 변화 0"의 순수 리팩터로 수행하고, 캡처→AutoCopy→ThumbnailWindow→ScheduleTrim 시퀀스 동일성을 `.smoke` 하니스로 검증한다.**

---

## 2. 대화형 캡처의 비동기 완료 → 파이프 응답 잇기

드래그 캡처는 사용자 상호작용이 낀 비동기라 즉시 응답 불가. `CaptureOverlay.Closed`를 `TaskCompletionSource`로 Task화하여 동기 명령과 동일한 await 파이프라인으로 통일한다.

```csharp
internal Task<CommandResult> Cmd_CaptureInteractive()
{
    if (_overlayOpen) return Task.FromResult(CommandResult.Fail("busy", "overlay open"));
    _overlayOpen = true; ResetIdleTrim();
    var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    var overlay = new CaptureOverlay(CaptureMode.Capture) { NameCtx = ForegroundContext() };
    overlay.Closed += (_, _) =>
    {
        _overlayOpen = false;
        RouteCapture(overlay);                       // 기존 부수효과 유지(App.cs:276)
        var p = overlay.ResultPath;
        tcs.SetResult(p != null
            ? CommandResult.FileSaved(p, overlay.RegionPx?.Width ?? 0, overlay.RegionPx?.Height ?? 0)
            : CommandResult.Fail("cancelled", "user cancelled"));
    };
    overlay.Show(); overlay.Activate();
    return tcs.Task;   // 드래그 종료 시 완료 → 파이프 서버가 await 후 응답 write
}
```

파이프 수신(ThreadPool) → UI(STA) 마샬링 표준 패턴:
```csharp
CommandResult r = await App.Current.Dispatcher
    .InvokeAsync(() => _router.ExecuteAsync(cmd))
    .Task.Unwrap();   // 대화형의 내부 TCS까지 한 번에 평탄화 await
```

- 동기(헤드리스) 명령: 즉시 완료. 대화형: 사용자 조작 종료까지 pending.
- **취소/타임아웃**: CLI는 `--timeout` + 연결 종료 시 `CancellationToken`으로 오버레이 강제 종료. MCP는 장시간 대기 허용(항상 가시).

---

## 3. IPC — Named Pipe `wsnap.control.v1` (신호-only에서 요청/응답으로 승격)

### 3.1 프레이밍: JSON Lines (NDJSON)
- UTF-8, 개행 구분 JSON(한 줄=한 메시지). 스트림 파싱 단순·언어 중립.
- 요청: `{"id":"<uuid>","cmd":"capture.region","args":{"x":0,"y":0,"w":800,"h":600},"returnContent":false}`
- 응답: `{"id":"<uuid>","ok":true,"result":{"type":"file","path":"C:/.../snap.png","w":800,"h":600}}`
- 오류: `{"id":"<uuid>","ok":false,"error":{"code":"busy","message":"overlay open"}}`
- `cmd` 문자열 = **정규 dotted id**(§4). `CommandCatalog.Parse(cmd,args)` → `WsnapCommand`.

### 3.2 서버 (신규 `PipeServer.cs`)
```csharp
public sealed class PipeServer   // 상주(primary) 전용, OnStartup에서 조건부 Start
{
    public const string PipeName = "wsnap.control.v1";
    // ExternalControlEnabled == false 이면 Start()를 호출하지 않는다 → 리스너 미생성(공격 표면 0)
    private static PipeSecurity CurrentUserOnlyAcl()
    {
        var sec = new PipeSecurity();
        var me = WindowsIdentity.GetCurrent().User!;
        sec.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return sec;   // Everyone/Authenticated Users 금지 — 세션 로컬, 현재 사용자만
    }
    // NamedPipeServerStreamAcl.Create(..., CurrentUserOnlyAcl()) + 다중 AcceptLoop
    // 각 연결: NDJSON 한 줄 읽기 → Dispatcher 마샬링 → ControlGate → Router → 응답 한 줄 write
}
```

- **동시성**: 다중 AcceptLoop로 CLI 단발과 MCP 지속 연결 병렬. 단, 대화형은 `_overlayOpen`으로 직렬화(동시 2오버레이 방지 → `error:overlay_busy`).
- **레거시 호환**: `EventWaitHandle` 신호는 존치. 새 이중 실행 경로는 파이프 우선, 실패 시 신호 폴백. `SingleInstance.TryAcquire` 골격 유지, primary 분기에서 `PipeServer.Start()` 추가.

---

## 4. 커맨드 식별자 규약 — 단일 진실원 `CommandCatalog`

정규 와이어 id = **dotted lowercase**(`capture.region`). 이것이 파이프 `cmd` 문자열이자 핫키 `Command` 필드값. `CommandCatalog`가 네 표현을 매핑한다.

| 정규 id (파이프) | CommandKind (C#) | CLI | MCP 툴 (snake) | 핫키 액션 |
|---|---|---|---|---|
| `capture.region` | CaptureRegion | `wsnap capture --region x,y,w,h` | `capture_region` | capture.region |
| `capture.fullscreen` | CaptureFullScreen | `wsnap capture --full` | `capture_fullscreen` | capture.fullscreen |
| `capture.window` | CaptureWindow | `wsnap capture --window` | `capture_window` | capture.window |
| `capture.interactive` | CaptureInteractive | `wsnap capture --interactive` | `capture_interactive` | capture.interactive (Shift+F1) |
| `ocr.region` | OcrRegion | `wsnap ocr --region ...` | `ocr_region` | ocr.region |
| `ocr.image` | OcrImage | `wsnap ocr --file ...` | `ocr_image` | — |
| `ocr.last` | OcrLast | `wsnap ocr --last` | `ocr_last_capture` | ocr.last |
| `color.at` | ColorAt | `wsnap color --at x,y` | `pick_color` (좌표) | color.at |
| `history.list` | HistoryList | `wsnap history list` | `list_history` | — |
| `history.get` | HistoryGet | `wsnap history get <id>` | `get_capture` | — |

MCP 툴명은 dot 대신 snake_case(MCP 관례). CLI는 verb+flags(사람 친화). 세 표현이 하나의 CommandKind로 수렴한다. (전체 매핑은 별첨 커맨드 카탈로그 표 참조.)

---

## 5. 헤드리스 실행 경계 (WPF/STA 의존성 분석, 검증)

| Command | 헤드리스? | 근거 | 상주 필요 |
|---|---|---|---|
| capture.region/fullscreen/window | ✅ | ScreenGrab.Grab(GDI)+SaveBitmap(파일). WPF/STA 불요 | 없음 |
| ocr.region/image/last | ✅ | RecognizeAsync 내부 Task.Run ONNX | 없음 |
| color.at | ✅ | 1px Grab | 없음 |
| history.list/get, folder.open | ✅ | 파일열거 / explorer Process.Start | 없음 |
| 클립보드 복사(부수) | ⚠️ STA 필요 | ImageClipboard=OLE 클립보드 | CLI 헤드리스는 자체 STA 워커 |
| capture.interactive/ocr.interactive/color.pick | ❌ | CaptureOverlay=WPF Window | 상주 필수 |
| gif/video/scroll | ❌ | DispatcherTimer 녹화 루프·정지 상호작용 | 상주 필수 |
| capture.repeat | ❌ | CaptureOverlay.LastRegion 상주 상태 | 상주 필수 |
| show.history/clear.thumbs/open.settings | ❌ | WPF 창 | 상주 필수 |

**경계 규칙 (통합 결정):**
1. 좌표/경로 완결 명령만 헤드리스.
2. **기본 동작 = 상주가 떠 있으면 위임**(히스토리·클립보드·썸네일·seq 일관, 단일 ControlGate). `--headless`로 in-proc 강제(스크립트/CI, 부작용 0, 썸네일 없음).
3. 상주 부재 시: 순수 명령은 자동 in-proc(클립보드는 자체 STA 워커에서, WPF `Application`은 미기동). 대화형/녹화/창 명령은 상주 자동기동(`--ensure-running`) or `exit 3`.
4. 헤드리스 CLI가 저장한 파일은 상주 히스토리와 갈릴 수 있으므로, 일관성이 필요하면 위임 모드(기본)를 쓴다.

---

## 6. MCP 서버 상세

### 6.1 형태 — `wsnap mcp` 서브커맨드 (별도 exe 아님, 위임 브리지)
Main 최상단에서 WPF 초기화 이전에 분기:
```csharp
[STAThread]
public static void Main(string[] args)
{
    // 클라이언트 모드 분기는 SingleInstance.TryAcquire · Settings.Load 보다 반드시 앞에
    if (args.Length > 0 && args[0] == "mcp")
    { McpStdioServer.RunAsync().GetAwaiter().GetResult(); return; }   // GUI 절대 미기동
    if (args.Length > 0 && CliRouter.IsKnownVerb(args[0]))
    { ConsoleBridge.Bind(); Environment.Exit(CliRouter.Run(args).GetAwaiter().GetResult()); }

    // 인자 없음/미지 토큰 → 기존 트레이 경로 100% 그대로 (하위호환)
    var app = new App(); _instance = app;
    bool primary = SingleInstance.TryAcquire(() => app.Dispatcher.BeginInvoke(() => _instance?.StartCapture()));
    if (!primary) return;
    Settings.Load(); app.Run(); SingleInstance.Release();
}
```

**WinExe stdio 사실 확인**: `OutputType=WinExe`라도 부모(Claude Desktop)가 stdin/stdout을 파이프로 리다이렉트해 spawn하면 OS가 상속된 표준 핸들을 넘기므로 `Console.OpenStandardInput/Output`이 정상 동작한다(콘솔 창은 안 뜸 — 오히려 장점).

**위임 브리지 동작(영구 아키텍처)**: `wsnap mcp`는 stdio JSON-RPC 루프만 돌리고 **모든 툴콜을 `wsnap.control.v1`로 포워딩**한다(직접 캡처 안 함). 상주 부재 시 `Process.Start(self)`로 자동 기동(Mutex 중복 방지) 후 재연결. 이로써 ControlGate·히스토리·상태가 상주 하나로 수렴한다.

> **1단계 예외**: 최초 가치 증명을 위해 파이프·리팩터 없이 순수 **in-proc 헤드리스 MCP**(읽기 전용 툴 4개)로 시작. Claude가 spawn한 same-user·비상주 프로세스라 상시 리스너가 없어 보안 표면 최소. 4단계에서 위임 브리지로 교체.

### 6.2 프로토콜 구현
**손수 최소 JSON-RPC 2.0**(`initialize`/`tools/list`/`tools/call`/`resources/*`) 권장 — `System.Text.Json`만 써 단일 exe 크기·트리밍 보존. 공식 `ModelContextProtocol` C# SDK는 Hosting/DI 유입으로 exe가 커지므로 PublishSingleFile+트리밍 검증 후 선택. MCP 코드는 `wsnap mcp` 프로세스에서만 로드 → 트레이 상주 메모리 무영향.

### 6.3 이미지 반환 전략 (핵심)
4K PNG를 base64로 그대로 반환하면 토큰 폭발 + Claude 비전이 어차피 장변 1568px로 리사이즈.
- `path`: 항상 반환(full-res 원본은 디스크에만, 토큰 0).
- `image`(base64): **장변 max 1568px 다운스케일 preview PNG**. `Ocr.UpscaleIfTiny`(Ocr.cs)의 GDI+ HighQualityBicubic 리샘플을 역방향으로 재활용 → `ImageEncode.PreviewPng(path,maxDim)`.
- `both`(capture_* 기본): text(경로+원본 w/h/bytes) + image(preview). OCR이 목적이면 `ocr_*`가 텍스트만 반환(비전 토큰 0).

### 6.4 등록 스니펫
Claude Desktop(`%APPDATA%\Claude\claude_desktop_config.json`):
```json
{ "mcpServers": { "wsnap": { "command": "wsnap", "args": ["mcp"] } } }
```
Claude Code: `claude mcp add wsnap -- wsnap mcp`. 설정 창에 "MCP 스니펫 복사" 버튼(ImageClipboard.CopyText 재사용)으로 온보딩 마찰 제거.

### 6.5 차별점 (범용 데스크톱 MCP 대비)
픽셀 정밀 영역 캡처(멀티모니터·혼합 DPI 물리좌표) · 우수한 온디바이스 KO/EN(+다국어) OCR(PP-OCRv5) · 프라이버시/오프라인 · 결과 자산화(히스토리/핀 재참조) · 인간-in-the-loop(`capture_interactive`). 포지셔닝: "정밀 캡처 + 프라이버시 OCR 어플라이언스."

---

## 7. CLI 상세

### 7.1 전역 플래그
`--json`(기계용 단일 JSON) · `-q/--quiet` · `-v/--verbose`(진단은 stderr) · `--headless`(위임 안 함) · `--ensure-running`(필요 시 상주 자동기동) · `--copy/--no-copy` · `--out <path>|-|clipboard` · `--timeout ms` · `--help/--version`.

### 7.2 출력 규약
| 항목 | 규약 |
|---|---|
| 사람용(기본) | 한 줄 요약 → stdout (`Saved D:\...\snap.png (1920x1080)`) |
| `--json` | 단일 JSON 오브젝트 → stdout. 로그/진단은 stderr(오염 금지) |
| `--out -` | 원시 PNG 바이트 → stdout. **이때 사람용/로그 전부 stderr 강제** |
| 에러 | 사람용 stderr / 기계용 `{"ok":false,"error":{"code","message"}}` |

### 7.3 exit code
`0` 성공(OCR 텍스트없음도 0+빈출력) · `1` 일반실패 · `2` 사용법오류 · `3` 상주 필요하나 미기동(+`--ensure-running` 없음) · `4` 결과없음/사용자취소 · `5` OCR 엔진불가(RecognizeAsync==null).

### 7.4 스크립팅 예시
```powershell
# 전체화면 → OCR → 클립보드 (상주 불필요)
wsnap capture --full --out - | wsnap ocr --file - --out clipboard
# JSON 경로 수신 후 후처리
$s = wsnap capture --region 0,0,800,600 --json | ConvertFrom-Json; wsnap ocr --file $s.path --lang latin
# 커서 밑 색을 hex로 클립보드
wsnap color --cursor --format hex --copy
```

### 7.5 WinExe 콘솔 처리
`OutputType=WinExe` **유지**(콘솔로 바꾸면 트레이 앱 콘솔 플래시 회귀). 계층적 해법:
1. 리다이렉트/파이프(`wsnap ... > f`, `| ...`)는 부모가 std 핸들 상속 → `Console.SetOut` 재바인딩만.
2. 대화형 터미널은 `AttachConsole(ATTACH_PARENT_PROCESS)` 후 `CONOUT$`/`CONIN$` 재바인딩. 실패(Explorer 실행)면 조용히 무출력.
3. `--out -` 바이너리는 `Console.OpenStandardOutput()`에 원시 바이트만.
- **남는 한계(정직 고지)**: 대화형 인라인에서 프롬프트가 출력보다 먼저 복귀하는 WinExe 고질. 결과를 stdout+파일경로+exit code 삼중으로 무해화. 완성도용 **선택적 `wsnap.com` 콘솔 셔임**(PATH의 .com 우선순위로 인라인 블로킹 UX) — 없어도 GUI/파이프 정상, 우아하게 degrade.

---

## 8. 핫키 확장 · 자동화 · 프로파일

### 8.1 단일 핫키 → 다중 바인딩
`HotkeyBinding` 리스트로 승격, 기존 평면 필드는 **삭제 금지·마이그레이션 소스로만** 유지(JSON 하위호환).
```csharp
public sealed class HotkeyBinding
{
    public int Vk; public bool Shift, Ctrl, Alt, Win;
    public string Command = "capture.interactive";  // 정규 dotted id
    public Dictionary<string,string>? Args;         // 파라미터 핫키
    public bool Swallow = true, Enabled = true;
}
// Settings.cs: public List<HotkeyBinding> Hotkeys { get; set; } = new();
```
`Settings.Load()` 끝에서 `Hotkeys.Count==0`이면 기존 `HotkeyVk/...`(+`SwallowWinShiftS`)로 마이그레이션 → 구버전 사용자 동작 무중단. `Settings.KeyName`(public static) 재사용. 트레이 라벨 회귀 방지용 `PrimaryHotkeyText` 헬퍼 신설.

### 8.2 HookCallback 핫패스 규율 (필수)
콜백은 **시스템 전체 키입력마다** 호출 → LINQ/람다캡처/문자열 할당 금지. index 루프 + 참조 스냅샷 + 정수 비교만:
```csharp
var list = Settings.Current.Hotkeys;   // 루프 시작 시 1회 스냅샷
for (int i = 0; i < list.Count; i++) { var b = list[i]; if (!b.Enabled) continue;
    if (vk==b.Vk && shift==b.Shift && ctrl==b.Ctrl && alt==b.Alt && win==b.Win) {
        Application.Current?.Dispatcher.BeginInvoke(() => Triggered?.Invoke(b));
        return b.Swallow ? (IntPtr)1 : CallNextHookEx(...); } }
```
설정 저장은 **새 List 원자 교체**(in-place 수정 금지 — 순회 중 변경 예외 방지).

### 8.3 파라미터 핫키 (신규 액션, 기존 순수함수 재조합)
- `OcrLastRegion`: `CaptureOverlay.LastRegion` → `ScreenGrab.Grab` → `RunOcr`.
- `SaveLastToFolder`: Grab → `CaptureStore.SaveBitmap`(folderOverride 소폭 확장) → Thumbnail.
- `DelayedCapture{seconds}` 임의값 허용.

### 8.4 자동화 트리거
- **FolderWatcher(신규)**: `ClipboardWatcher`의 Start/Stop/SetEnabled/Dispose + App `ApplyRuntime` 배선을 동형 복제. `FileSystemWatcher`로 감시 폴더 신규 이미지 → debounce → `Ocr.RecognizeAsync` → (옵션)CopyText/사이드카 `.txt`. **가드**: watch 폴더가 `SaveFolder`와 겹치면 자기 캡처 재-OCR 루프 → 처리완료/사이드카 스킵.
- **클립보드 이미지 자동 OCR**: App.cs:73 콜백에 `if (Settings.Current.ClipboardAutoOcr) RunOcr(...)` 추가. `ClipboardWatcher.SuppressNext` 덕에 텍스트 복사가 이미지 감지를 재트리거하지 않음(안전).

### 8.5 프로파일 (MVP: 오버라이드만)
`App.ForegroundContext`(App.cs:532)가 이미 얻는 전경 프로세스명을 재사용해 커맨드 실행 시점에 `SaveFolderOverride`/`AutoCopyOverride`를 적용. `CommandRemap`(같은 키→앱별 다른 동작)은 디버깅 난이도로 후순위.

### 8.6 Shell 커맨드 (opt-in)
`AllowShellCommands=false` 기본. `{path}`=마지막 캡처 경로 치환으로 "핫키→외부 앱 전달". **파이프/MCP에는 절대 비노출**(핫키 전용).

### 8.7 충돌/접근성
저수준 훅이라 OS 등록 충돌 개념 없음(대신 다른 앱 단축키를 뺏는 반대 위험 → `Swallow=off` 옵션+경고). 무수식·단일키 바인딩은 타이핑 삼킴 → **"수식키 1개 이상 요구" 기본 가드**. `HotkeyHook.InstallFailed` 시 전 바인딩 사망 → 트레이/CLI 대안 토스트 안내.

---

## 9. 보안 · 프라이버시 · 동의 모델

### 9.1 위협 모델 (근거 있는 것만)
- **이미 노출된 것(사실)**: `wsnap.signal.capture.v1`은 같은 세션 아무 프로세스나 `Set()`으로 대화형 캡처 트리거 가능하나, 결과는 오버레이(가시)로만 나오고 **호출자에 반환 안 됨** → 유출 채널 아님.
- **MCP/CLI가 새로 만드는 위협**: (T1) 파라미터화된 헤드리스 캡처 + **결과 픽셀/OCR 텍스트를 호출자에 반환**(returnContent) = 진짜 유출. (T2) 초당 반복 캡처=무단 감시. (T3) 비번/뱅킹 창 캡처. (T4) 무표시 시 사용자 인지 불가. (T6) 누군가 MCP를 TCP/HTTP로 열면 원격 감시=치명.
- **명시적 범위 제외**: 동일 사용자 악성코드(OS가 이미 `BitBlt`로 스크린샷 가능) — wsnap이 못 막음. 목표 재정의: ① OS보다 조용/편한 유출도구가 되지 않기 ② AI 캡처는 가시·동의 기반 ③ 기본값 안전.

### 9.2 단일 ControlGate (필수)
정책은 진입점마다가 아니라 **`ScreenGrab.Grab`/`Ocr.RecognizeAsync` 바로 위 ControlGate 한 곳**에서 강제. `source ∈ {Hotkey,Tray,Cli,Mcp}`. **Hotkey/Tray는 사용자 물리 조작이라 최고 신뢰**(오늘 UX 유지). Cli/Mcp의 콘텐츠 반환·무표시만 강하게 게이트.

### 9.3 전송·인증
- 파이프 DACL = **현재 사용자 SID만 RW**(`Global\` 금지, 세션 로컬).
- **`ExternalControlEnabled==false`이면 파이프 서버 미생성** → T1/T2/T5 표면 0.
- MCP는 **stdio 전용**, 네트워크 리스너 절대 금지(코드·CI 가드로 강제) → T6 원천차단.
- 256bit 회전 토큰(`%APPDATA%\wsnap\control-token`, 사용자 ACL, 재시작 회전): **승인 바인딩·감사 주체식별·심층방어**용. 동일 사용자 악성코드엔 무의미함을 정직 고지(기밀성 방어 아님).

### 9.4 동의·가시성 (신뢰의 핵심)
- **마스터 스위치 기본 OFF.** 최초 외부 연결 시 상주가 1회 동의: "Claude(MCP)가 wsnap 화면 캡처 제어를 요청합니다 [이번 세션/항상/거부]." 승인은 `control-grants.json` + 설정창 목록/철회.
- **2등급**: (1)트리거·저장 vs (2)`returnContent`(픽셀/텍스트 반환) — (2)는 더 강한 문구로 재확인.
- **가시 신호**: 외부 개시 캡처마다 셔터 플래시 + `Toast.Show`("Claude가 화면을 캡처했습니다") + **트레이 세션 표식**(활성 동안 배지). Toast·NotifyIcon 재사용.
- **무표시(silent)**: `ExternalControlAllowSilent`(기본 false)일 때만 플래시/토스트 억제. **감사·트레이 표식은 절대 억제 불가.** 권고: MCP는 항상 가시, silent는 CLI 전용.
- **레이트리밋**: 소스/토큰별 토큰버킷(기본 분당 6회+소버스트). 초과 `denied:{reason:"rate_limit"}`+감사.
- **감사로그**: `CrashLog` 인프라 재사용, 별도 `audit.log`. 기록=`시각|source|clientId|cmd|region(w×h)|silent|returnedContent(bool·byte수)|allow/deny+reason`. **절대 미기록**: OCR 원문·이미지 바이트·창 제목.
- **민감창(선택)**: `GetWindowDisplayAffinity==WDA_EXCLUDEFROMCAPTURE` 탐지 시 "보호된 창, 캡처 거부"로 승격(오탐 적음). 제목 블록리스트는 스푸핑 우회 가능→보조.

### 9.5 신규 Settings
```csharp
public bool   ExternalControlEnabled            { get; set; } = false; // 마스터, off면 파이프 미생성
public bool   ExternalControlAllowSilent        { get; set; } = false;
public bool   ExternalControlAllowReturnContent { get; set; } = false;
public int    ExternalControlRateLimitPerMin    { get; set; } = 6;
public bool   ExternalControlAudit              { get; set; } = true;
public string[] SensitiveWindowBlocklist        { get; set; } = Array.Empty<string>();
public bool   ClipboardAutoOcr                  { get; set; } = false;
public bool   WatchFolderOcr                    { get; set; } = false;
public string WatchFolderPath                   { get; set; } = "";
public bool   AllowShellCommands                { get; set; } = false;
```

### 9.6 문서·라이선스
README/PRIVACY에 "원격/에이전트 제어" 절: 기본 꺼짐 · 로컬 stdio/파이프 전용·네트워크 리스너 없음 · AI 캡처는 항상 가시 · 결과는 로컬 저장·반환은 별도 승인 · 토큰/DACL 한계 정직 고지 · 감사로그 위치/내용. GPL-3.0: MCP/CLI 서브프로세스도 동일 소스트리. 외부 라이브러리 추가 시 `THIRD-PARTY-NOTICES.md` 갱신.

---

## 10. 코드 재사용 인벤토리 (그대로 / 래핑 / 리팩터)

| 파일 | 심볼 | 태그 | 근거 |
|---|---|---|---|
| ScreenGrab.cs | `Grab(x,y,w,h)` | **그대로** | GDI CopyFromScreen, 스레드 무관 |
| CaptureStore.cs | `SaveBitmap/NewPath/BuildName/EnumerateHistory` | **그대로(+folderOverride)** | 파일 I/O만, 헤드리스 |
| Ocr.cs | `RecognizeAsync` (임베디드) | **그대로 / 래핑(다운로드)** | 임베디드 UI무관. 비임베디드 다운로드가 Toast.Show 결합→IProgress 추출 필요 |
| ImageClipboard.cs | `CopyImageFile/CopyText` | **래핑** | STA+메시지펌프. 상주 Dispatcher 마샬링 |
| App.cs | 액션 15여 개, DeliverRegion/RouteCapture | **리팩터** | 코어+UI 혼재. 코어/프레젠테이션 분리 후 CaptureCore 이관 |
| App.CaptureActiveWindow | DWM rect 계산(469-477) | **래핑** | static 헬퍼 추출→capture.window 헤드리스 |
| App.ForegroundContext(532) | 전경 앱/타이틀 | **래핑** | P/Invoke만, CaptureCore로 |
| HotkeyHook.cs | 훅+모디파이어 | **리팩터(경미)** | 매칭부만 바인딩 테이블화 |
| Settings.cs | Current/Save/JSON | **그대로(+필드)** | 로드/저장 인프라 재사용 |
| SingleInstance.cs | Mutex+EventWaitHandle | **확장** | 유지+PipeServer 공존 |
| ClipboardWatcher.cs | SetEnabled/Dispose | **복제** | FolderWatcher 동형 |
| MemoryTrim.cs | TrimNow/TrimWorkingSet | **그대로** | 명령 처리 후 ResetIdleTrim 재사용 |
| .smoke 하니스 | — | **재활용** | 코어/버스 회귀 스모크 |

---

## 11. 로드맵 (의존 순서, 1단계=얇은 수직 슬라이스)

| 단계 | Scope | 산출물 | 공수 | 의존 |
|---|---|---|---|---|
| **1. 수직 슬라이스** | 파이프·리팩터 없이 순수 헤드리스 MCP. CaptureCore 최소판(Grab/OcrFile/OcrRegion/ActiveWindowRect, 임베디드 KO+EN) + `wsnap mcp` in-proc stdio(툴 4개). `Main(string[])` 분기 | Claude가 "화면 찍고 읽어줘" 실증. WinExe stdio·코어 재사용·무상주 동작 검증 | S | 없음 |
| **2. 공유 제어 계층** | Command.cs+CommandRouter+CommandCatalog. App 액션 Cmd_* 래핑. DeliverRegion/RouteCapture 코어/프레젠테이션 분리. 트레이·핫키 버스 경유(동작 변화 0). ControlGate 골격 | 단일 버스로 4진입점 통일 | M | 1 |
| **3. Pipe+CLI+보안** | PipeServer(wsnap.control.v1, DACL, 기본 OFF+동의). CLI verbs(헤드리스/위임 자동판단, AttachConsole). 대화형→TCS→상주. 가시신호+감사+레이트리밋 | 완결 CLI+양방향 IPC | L | 2 |
| **4. MCP 정식화** | in-proc MCP→파이프 클라이언트 위임 전환(단일 게이트). capture_interactive, preview 1568px, list_history/get_capture. RecognizeAsync(Bitmap,lang) 오버로드 | 안정 MCP(대화형·상태 일관) | M | 3 |
| **5. 핫키+자동화** | HotkeyHook 다중바인딩(무할당). Settings 마이그레이션+UI. 파라미터 핫키. FolderWatcher+클립보드 자동 OCR. 프로파일(오버라이드). Shell opt-in. 팔레트 | 다중핫키·워치폴더·프로파일 | L | 2 (3·4와 병렬 가능) |
| **6. 하드닝+다국어+패키징** | per-client 승인/철회 UI, 무표시 opt-in, 민감창 탐지. 다국어 헤드리스(Toast→IProgress 분리). 선택 wsnap.com. scoop/winget·Inno·PRIVACY·MCP 스니펫 | 프로덕션 보안·다국어·온보딩 | L | 1~5 |

**의존 근거**: 1은 무의존(순수 코어 3종). 2는 코어 추출 선행. 3은 버스+게이트. 4는 파이프. 5는 버스에만 의존(3·4와 독립 병렬). 6은 전체 위.

---

## 12. 리스크와 완화 (통합)

1. **App.cs 코어/프레젠테이션 분리(2단계)=최대 회귀**: 캡처→AutoCopy→Thumbnail→ScheduleTrim 순서가 메모리(EmptyWorkingSet)·DPI 설계에 민감 → `.smoke`로 시퀀스 동일성 검증.
2. **returnContent=진짜 유출 채널**: 2등급 승인+가시신호+감사 없이는 조용한 감시도구 전락.
3. **WinExe stdout 고질**: 리다이렉트/파이프는 재바인딩으로 해결, 대화형 인라인은 stdout+파일+exit code 삼중, 완성도는 선택 셔임.
4. **대화형 파이프 위임 무한 대기**: CLI `--timeout`+연결종료 CancellationToken으로 오버레이 강제종료.
5. **다국어 OCR 다운로드가 Toast 결합**: MVP 임베디드 KO+EN만, IProgress 추출 후 개방.
6. **HotkeyHook 핫패스**: index 루프+참조 스냅샷+무할당, 저장은 새 List 원자 교체.
7. **파이프 ACL**: NamedPipeServerStreamAcl 현재 사용자 SID 필수.
8. **헤드리스 파일 상태 분리**: 기본 위임+`--headless` opt-in. Main 분기를 TryAcquire/Settings.Load보다 앞에.

---

## 13. 파일별 변경 요약

| 파일 | 변경 |
|---|---|
| `Command.cs`(신규) | CommandKind/WsnapCommand/CommandResult/CommandSource |
| `CommandRouter.cs`(신규) | 버스 디스패치 switch, App Cmd_* 위임 |
| `CommandCatalog.cs`(신규) | dotted id ↔ CLI/MCP/핫키/enum 매핑, Parse/Describe |
| `CaptureCore.cs`(신규) | Grab/Save/Ocr/ActiveWindowRect/ForegroundContext 헤드리스 파사드 |
| `ControlGate.cs`(신규) | 마스터스위치·동의·레이트리밋·가시신호·감사 단일 강제 |
| `PipeServer.cs`(신규) | wsnap.control.v1 NDJSON 서버, 현재사용자 DACL, 조건부 Start |
| `McpStdioServer.cs`(신규) | wsnap mcp stdio JSON-RPC(1단계 in-proc→4단계 위임) |
| `CliRouter.cs`+`ConsoleBridge.cs`(신규) | verb 파싱·출력규약·AttachConsole |
| `FolderWatcher.cs`(신규) | ClipboardWatcher 동형 워치폴더 OCR |
| `App.cs` | Main(string[]) 분기, Cmd_* 래퍼, Triggered 배선, 대화형 TCS, ApplyRuntime 확장 |
| `HotkeyHook.cs` | 이벤트 Action→Action<HotkeyBinding>, 무할당 바인딩 순회 |
| `Settings.cs` | List<HotkeyBinding>, ExternalControl* 등 필드, Load() 마이그레이션, PrimaryHotkeyText |
| `SettingsWindow.cs` | 다중 바인딩 UI(칩 캡처 재사용), 외부제어/자동화/승인목록 카드 |
| `Ocr.cs` | RecognizeAsync(Bitmap,OcrLanguage?) 오버로드, 다운로드 Toast→IProgress 분리 |
| `ClipboardWatcher.cs` | 콜백 조건부 자동 OCR(호출부만) |
| `CaptureStore.cs` | SaveBitmap folderOverride 소폭 확장 |
| `Strings.cs` | cmd.* / set.* / toast.* 신규 키 |
| 패키징/docs | scoop/winget Commands, Inno, claude_desktop_config 스니펫, PRIVACY 절 |

---

## 14. 열린 질문 (사용자 결정 필요)

1. 외부 제어 기본값: 완전 OFF+최초 동의 플로우(권장) vs 설치 옵트인 배너.
2. capture_* 기본 return: both(에이전트가 화면 봄, 권장) vs path(OCR 파이프라인, 토큰 0).
3. 무표시 캡처를 MCP에도 허용할지(권장: MCP 항상 가시, silent는 CLI 전용).
4. wsnap.com 셔임 배포 여부(단일 exe 순수성 vs 터미널 UX).
5. Shell 핫키 커맨드 포함 여부(권장: opt-in 포함).
6. 프로파일 CommandRemap 범위(권장: MVP는 저장폴더/자동복사 오버라이드만).
7. 파이프 화이트리스트에 settings.set/thumbnails.clear 포함 여부(권장: 감사 하에 허용, 파일삭제 계열 비노출).
8. MCP JSON-RPC 손수 구현(권장, 단일 exe) vs 공식 SDK(검증 후).

---

## 15. 부록 — 움짤(GIF)/녹화 MCP 노출 (요청 반영)

> 종합 초안은 GIF/비디오/스크롤을 "대화형(상주 전용)"으로 보고 MCP에서 `(비노출)`로 두었으나, **"Claude가 움짤을 만든다"는 유스케이스는 명시 요구사항**이므로 노출로 승격한다. 본 절이 §4·§5·§6·§11·§14의 GIF 관련 항목을 대체한다.

### 15.1 왜 비노출이었나 → 무엇을 바꾸면 풀리나 (코드 검증: GifRecorder.cs)
- `GifRecorder(Int32Rect region, Action<string> onSaved)` — **region rect만 받는다**. 즉 캡처 자체는 좌표 기반이고, 오버레이(사용자 드래그)는 `StartGifCapture`(App.cs:383)가 앞단에서 영역을 고를 때만 쓰인다. **좌표를 직접 주면 오버레이 불요.**
- `OnTick`(GifRecorder.cs:58): `Fps=12`로 `ScreenGrab.Grab`(**헤드리스 GDI**) → `_frames` 수집. `if (_frames.Count >= Fps * MaxSeconds) Stop();`(:65) — **이미 30초 자동정지 상한 내장**.
- `Stop()`(:70): 타이머 정지 → `GifWriter.Save(_frames, path, 1000/Fps)` → `onSaved(path)`. 정지 트리거는 현재 배지 클릭/`Esc`/자동상한 3가지(:116-117, :65)이고 **`Stop()`은 private**.
- **결론**: 비노출의 유일한 이유는 "정지가 UI 이벤트에 묶임"이었다. `MaxSeconds`를 파라미터화하고 `Stop()`을 외부에서 부를 수 있게 하면 **정지가 프로그래밍 가능**해져 MCP/CLI로 곧장 노출된다. 프레임 수집·인코딩은 한 줄도 안 바꾼다.

### 15.2 GifRecorder 최소 변경
```csharp
// 생성자에 상한/배지 노출 여부 추가 — 기본값은 기존 동작 그대로(하위호환)
public GifRecorder(Int32Rect region, Action<string> onSaved,
                   int maxSeconds = 30, bool showControl = true, int fps = 12)

public void StopExternal() => Stop();   // 프로그래밍 정지(멱등, 이미 _stopped 가드 있음:72)
public bool IsRecording => _timer.IsEnabled && !_stopped;
```
- `maxSeconds`가 곧 duration. `showControl=true`는 **유지가 기본**(§15.4 가시성). `Stop()`은 이미 `_stopped` 멱등 가드가 있어 외부/자동/사용자 정지가 경쟁해도 안전.
- 세션 방식(start/stop)용으로 `recording_id → GifRecorder` 레지스트리를 CommandRouter에 둔다(동시 1개 제한 = `_overlayOpen`과 동형).

### 15.3 도구/명령 표면 (§4·§5·§6 갱신분)

| 정규 id | CommandKind | CLI | MCP 툴 | 핫키 | 헤드리스 |
|---|---|---|---|---|---|
| `capture.gif` | Gif | `wsnap gif --region x,y,w,h [--duration s] [--fps n]` | **`record_gif`** | capture.gif | 프레임=✅, 실행=**상주 위임**(DispatcherTimer STA) |
| `capture.gif.stop` | GifStop | `wsnap gif stop` | **`stop_recording`** | — | 위임 |
| `capture.video` | Video | `wsnap video --region ... --format mp4\|apng [--duration s]` | **`record_video`**(선택) | capture.video.* | 위임 |

**MCP 도구 스펙:**
```
record_gif   — 화면 영역을 지정 시간 동안 녹화해 looping GIF로 저장.
  input : { x,y,width,height, duration_s?:number=5(<=30), fps?:int=12,
            mode?:"fixed"|"until_stop"="fixed", return?:"path"|"image"|"both"="path" }
  output: fixed → 완료까지 대기 후 { path, width, height, frames, seconds }
          until_stop → 즉시 { recording_id, started:true } (이후 stop_recording으로 종료)
  주의  : GIF는 연속 프레임 = 화면의 시계열 유출. returnContent 2등급 동의 대상,
          녹화 중 항상 가시 배지(§15.4). return 기본 path(GIF는 base64 토큰 과다).
stop_recording — 진행 중인 GIF/비디오 녹화를 정지하고 파일을 반환.
  input : { recording_id?:string }   // 생략 시 활성 녹화 1개 정지
  output: { path, width, height, frames, seconds }
```
- **기본 모드 = `fixed`(duration_s)**: AI가 `record_gif({x,y,width,height,duration_s:5})` 한 콜로 완결. `GifRecorder(rect, cb, maxSeconds:5, showControl:true)` → 5초 뒤 자동 `Stop()` → 파이프 응답으로 경로 반환(대화형 캡처의 TCS 패턴 §2 재사용).
- **`until_stop`**: "설명하는 동안 녹화" 같은 열린 시나리오. `recording_id` 반환 후 `stop_recording`으로 종료. 30초 하드 상한은 항상 강제(무한 녹화 방지).

### 15.4 가시성 = 보안 kill-switch (녹화는 더 민감)
GIF는 단발 스크린샷보다 민감(움직임·시계열 노출)하므로 §9 정책을 **더 강하게** 적용:
- **빨간 "녹화 중 N프레임" 배지(GifRecorder.ShowControl) 유지·강제** — 외부(MCP/CLI) 개시 녹화는 `showControl=false` **금지**(silent 옵트인으로도 억제 불가). 배지는 곧 사용자 인지 신호이자 **클릭 한 번으로 강제 종료하는 kill-switch**.
- 시작·종료 각각 감사로그(`시각|source|region|seconds|frames|returnedContent`). 레이트리밋은 녹화에 더 빡빡하게(동시 1개, 분당 소수).
- `returnContent`(GIF 바이트를 호출자에 반환)는 2등급 동의. 기본 `return:"path"`(디스크에만, 토큰 0).

### 15.5 로드맵 반영
- 녹화는 DispatcherTimer(STA)라 **순수 헤드리스 1단계엔 부적합** → 상주 위임이 성립하는 시점에 안착.
- **3단계**(Named Pipe+CLI)에서 `wsnap gif --duration`/`wsnap gif stop` CLI를 먼저 확보(GifRecorder 파라미터화 + StopExternal + recording 레지스트리).
- **4단계**(MCP 브리지 정식화)에서 `record_gif`/`stop_recording` MCP 툴 노출(브리지가 파이프로 위임). capture_interactive와 같은 단계 = 위임·가시성·감사 인프라 공유.
- `record_video`(mp4/apng)는 동일 메커니즘의 저비용 확장(VideoRecorder도 region+콜백 패턴, App.cs:402). 스크롤 캡처는 스크롤 상호작용이 필요해 별도(후순위 유지).

### 15.6 §14 열린 질문 추가
9. **GIF 기본 정지 방식**: `fixed`(duration_s, 한 콜 완결·단순, **권장**) vs `until_stop`(세션·유연) — 권장은 둘 다 제공하되 기본 `fixed`.
10. **`record_video` 동시 노출 여부**: GIF와 같은 단계에서 함께(권장, 메커니즘 동일) vs GIF 먼저 검증 후.
