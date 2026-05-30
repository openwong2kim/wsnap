; wsnap installer — Inno Setup 6 script.
; Build the single-file exe first (publish.ps1), then compile this with Inno Setup.
;   1) pwsh -File publish.ps1
;   2) ISCC.exe installer.iss            (uses the default version below)
;      ISCC.exe /DAppVersion=1.0.1 installer.iss   (override version, e.g. from a CI tag)

#define AppName "wsnap"
#ifndef AppVersion
  #define AppVersion "1.0.2"
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
Source: "publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "NOTICE"; DestDir: "{app}"; Flags: ignoreversion
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
Filename: "{app}\{#AppExe}"; Description: "지금 wsnap 실행"; Flags: nowait postinstall skipifsilent

; NOTE: code-sign {#AppExe} and the resulting setup.exe before distributing
; (Authenticode cert) to avoid SmartScreen warnings. See ROADMAP.
