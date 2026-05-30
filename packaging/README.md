# 배포 채널 안내

릴리즈(GitHub Release)는 태그 `v*` 푸시 시 `.github/workflows/release.yml`이 자동으로
exe·인스톨러·zip·체크섬을 만들어 올린다. 아래는 그 위에 얹는 패키지 매니저 배포다.

## Scoop
`packaging/scoop/wsnap.json` 은 그대로 Scoop 매니페스트다.
- 단건 설치: `scoop install https://raw.githubusercontent.com/openwong2kim/wsnap/main/packaging/scoop/wsnap.json`
- 버킷으로 운영하려면 별도 repo(예: `openwong2kim/scoop-bucket`)에 넣고
  `scoop bucket add wsnap https://github.com/openwong2kim/scoop-bucket` 후 `scoop install wsnap`.
- `hash`는 릴리즈된 `wsnap.exe`의 SHA256과 일치해야 한다(릴리즈 후 자동/수동 보정).

## winget
`packaging/winget/` 의 3개 YAML(version/installer/locale)을 microsoft/winget-pkgs에 PR.
가장 쉬운 방법:
```powershell
winget install wingetcreate
wingetcreate submit --token <gh_token> packaging/winget
```
또는 `wingetcreate new <InstallerUrl>` 로 새로 생성. `InstallerSha256`은 릴리즈 자산 해시와 일치해야 통과.

## Chocolatey (선택)
`choco` 배포는 community.chocolatey.org 계정 + API 키 + 패키지 심사가 필요하다.
nuspec + `tools/chocolateyinstall.ps1`(릴리즈 exe 다운로드)을 만들고
`choco pack` → `choco push --api-key <key>`. (이 저장소엔 미포함 — 필요 시 추가)

## 코드 서명 (권장)
서명 없는 exe는 SmartScreen 경고가 뜬다. Authenticode 인증서(OV/EV)로
`signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 wsnap.exe`.
CI에 서명 단계를 넣으려면 인증서를 GitHub Secrets로 주입.
