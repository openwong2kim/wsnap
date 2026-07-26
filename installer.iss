; wsnap installer — Inno Setup 6 script.
; Build the single-file exe first (publish.ps1), then compile this with Inno Setup.
;   1) pwsh -File publish.ps1
;   2) ISCC.exe installer.iss            (uses the default version below)
;      ISCC.exe /DAppVersion=1.0.1 installer.iss   (override version, e.g. from a CI tag)

#define AppName "wsnap"
#ifndef AppVersion
  #define AppVersion "1.7.0"
#endif
#define AppExe "wsnap.exe"

[Setup]
AppId={{8F3C2A14-5B6D-4E7F-9A1B-2C3D4E5F6071}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=openwong2kim and wsnap contributors
AppPublisherURL=https://github.com/openwong2kim/wsnap
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
SetupIconFile=wsnap.ico
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=dist
OutputBaseFilename=wsnap-setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
; Framework-dependent publish: wsnap.exe (~9 MB) + the loose native it needs (libSkiaSharp.dll ~11 MB).
; The .NET 8 Desktop Runtime is NOT bundled — the [Code]/[Run] sections check for it at install time
; and download the official x64 installer on demand if missing (same model ShareX uses for .NET Framework).
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "NOTICE"; DestDir: "{app}"; Flags: ignoreversion
Source: "THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{#AppName} 제거"; Filename: "{uninstallexe}"

[Tasks]
Name: "startup"; Description: "Windows 시작 시 자동 실행"; GroupDescription: "추가 작업:"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "wsnap"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
; Download + install the .NET 8 Desktop Runtime (x64) when absent, silently. wpassthrough keeps
; the wsnap setup moving — the runtime installer's own progress dialog is the only visible UI.
Filename: "{tmp}\dotnet-runtime.exe"; Parameters: "/quiet /norestart"; \
  StatusMsg: "Microsoft .NET 8 Desktop Runtime 설치 중…"; \
  Check: NeedsRuntime; BeforeInstall: DownloadRuntime

Filename: "{app}\{#AppExe}"; Description: "지금 wsnap 실행"; Flags: nowait postinstall skipifsilent; \
  Check: not NeedsRuntime

[Code]
// .NET 8 Windows Desktop Runtime x64 leaves this key once installed. The bundled-frameworks
// detection (RegistryInstall) isn't reliable across side-by-side installs, but this key is
// created by the official runtime installer on every successful setup.
function NeedsRuntime: Boolean;
begin
  Result := not RegKeyExists(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App');
end;

procedure DownloadRuntime;
begin
  // aka.ms shortlink always redirects to the current 8.0.x Windows Desktop Runtime x64 installer.
  // Stable across patch releases — no need to bump the URL per monthly CU.
  DownloadTemporaryFile('https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe', 'dotnet-runtime.exe', '', nil);
end;

; NOTE: code-sign {#AppExe} and the resulting setup.exe before distributing
; (Authenticode cert) to avoid SmartScreen warnings. See ROADMAP.
