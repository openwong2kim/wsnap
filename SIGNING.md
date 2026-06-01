# Code signing (SignPath)

wsnap ships **unsigned** today, so Windows SmartScreen shows an "unknown publisher"
warning on first run / install. This document is the plan to fix that with a **free**
OSS code-signing certificate from the [SignPath Foundation](https://signpath.org/).

The release workflow (`.github/workflows/release.yml`) is already wired for it: the
signing steps stay **dormant** (skipped) until the repository variables below are set,
so releases keep working unsigned in the meantime. Once SignPath is approved and the
variables are filled in, every tagged release signs the `wsnap.exe` and the installer
automatically — no workflow edits needed.

## Why we likely qualify

SignPath Foundation signs OSS projects that meet their conditions
([terms](https://signpath.org/terms.html)):

| Requirement | wsnap |
|---|---|
| OSI-approved license, no commercial dual-licensing | ✅ GPL-3.0-only |
| No proprietary code (system libraries OK) | ✅ |
| Actively maintained, already released | ✅ |
| Functionality documented on a download page | ✅ GitHub Pages landing |
| Built from source verifiably + manual approval per release | ✅ GitHub Actions CI |
| All team members use MFA (GitHub **and** SignPath) | ⚠️ enable GitHub 2FA first |
| "Verifiable reputation" for the binary | ⚠️ judged case-by-case — the one soft spot |

Note: SignPath Foundation issues an **OV** certificate. SmartScreen reputation builds up
as signed downloads accumulate; it is not the instant-trust an EV cert gives. The big win
is the publisher going from "Unknown" to a verified identity.

## One-time setup (done by a maintainer in the browser)

1. Turn on **2FA** for every maintainer's GitHub account.
2. Apply to the SignPath Foundation OSS program: <https://signpath.org/> → request a
   certificate. Describe wsnap, link the repo and the landing page.
3. After approval, in the SignPath.io console:
   - Create an **organization** and note its **Organization ID**.
   - Create a **project** with slug `wsnap`.
   - Connect the **trusted build system**: GitHub Actions → this repo → the `Release`
     workflow (`release.yml`). SignPath verifies the artifact's build provenance.
   - Create **artifact configurations** for the two PE files we sign — slugs `exe` and
     `installer` (Authenticode signing of a single `.exe`).
   - Create a **signing policy** with slug `release-signing` (requires manual approval).
   - Create an **API token** for the CI user.

## Repository variables & secrets to set

Settings → Secrets and variables → Actions.

**Secret:**
- `SIGNPATH_API_TOKEN` — the SignPath REST API token

**Variables** (the workflow keys off `SIGNPATH_ORGANIZATION_ID` being non-empty):
- `SIGNPATH_ORGANIZATION_ID` — **required to enable signing**
- `SIGNPATH_PROJECT_SLUG` — optional, defaults to `wsnap`
- `SIGNPATH_SIGNING_POLICY_SLUG` — optional, defaults to `release-signing`
- `SIGNPATH_ARTIFACT_CONFIG_EXE` — optional, defaults to `exe`
- `SIGNPATH_ARTIFACT_CONFIG_INSTALLER` — optional, defaults to `installer`

If `SIGNPATH_ORGANIZATION_ID` is unset, all signing steps are skipped and the release is
unsigned (current behavior).

## After signing is live

- The exe is signed **before** the installer is built, so the installer embeds the signed
  exe; then the installer itself is signed. The portable zip and `SHA256SUMS.txt` are
  produced **after** signing, so their hashes are the signed ones.
- Because the signed `wsnap.exe` hash differs from the unsigned one, the **scoop/winget
  manifests** (`packaging/`) must be re-hashed from the signed release asset — same step
  we already do each release, just using the post-signing `SHA256SUMS.txt`.
- A signing request waits for **manual approval** in SignPath (the `release-signing`
  policy), so a tagged release will pause until a maintainer approves it.
