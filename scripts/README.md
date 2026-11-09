# scripts/

Release and operator scripts for OMT Capture Studio. Tracked in git; run from a machine that already has the local binaries below.

## Required local binaries (not in git)

| Path | Purpose |
| :--- | :--- |
| `OmtCaptureStudio/NativePayload/win-x64/` | Six native libraries embedded at build (`libvmx.dll`, `libomt.dll`, `libomtnet.dll`, `libHarfBuzzSharp.dll`, `libSkiaSharp.dll`, `av_libglesv2.dll`). Missing → app dies at startup; `build-release.ps1` refuses to package. |
| `tools/ffmpeg.exe` | Recording + installer bundling. Missing → recording preflight fails; installer/release scripts refuse. |

Populate those paths before a first run, release build, or CI packaging job. They remain gitignored on purpose (size / redistribution).

## Scripts in this folder

| Script | Purpose |
| :--- | :--- |
| `build-release.ps1` | Version stamp, Native AOT publish, ZIP + SHA-256 under `Releases/`, optional `gh` upload. |
| `ci-provision-artifacts.ps1` | Phase 1.2: download NativePayload + ffmpeg when `CI_PREREQ_ARCHIVE_URL` (or `CI_NATIVE_PAYLOAD_URL` + `CI_FFMPEG_URL`) is set. On GitHub Actions, missing URLs fail this step. Locally, unset URLs no-op. Never commits binaries. |
| `ci-prerequisites.ps1` | Exit non-zero with a clear message if `NativePayload/win-x64` (6 DLLs) or `tools/ffmpeg.exe` is missing. Used by `.github/workflows/ci.yml` after provision, before build. |

## CI artifact owner checklist (Phase 1.2) — make Actions green

Do **not** commit `ffmpeg.exe` or native DLLs into git. Actions downloads them via a repository secret.

### Fastest path (reuse existing public portable release)

The latest portable ZIP already contains the 6 native DLLs + `ffmpeg.exe` at its root. `ci-provision-artifacts.ps1` accepts that flat layout.

1. Open **GitHub → repo → Settings → Secrets and variables → Actions → New repository secret**.
2. Name: `CI_PREREQ_ARCHIVE_URL`
3. Value (public; no bearer token needed):

   `https://github.com/gnamse/OmtCaptureStudio/releases/download/v1.0.0.24/OmtCaptureStudio-v1.0.0.24-win-x64.zip`

4. Re-run the failed workflow (**Actions → failed run → Re-run jobs**), or push a new commit.
5. When you cut a newer release, update the secret URL to that tag’s `*-win-x64.zip` (or switch to a slim zip below).

### Slim zip (optional, smaller than the portable release)

Build a zip whose root contains either layout **(A)** or **(B)**:

**(A) Canonical tree**

```text
NativePayload/win-x64/libvmx.dll
NativePayload/win-x64/libomt.dll
NativePayload/win-x64/libomtnet.dll
NativePayload/win-x64/libHarfBuzzSharp.dll
NativePayload/win-x64/libSkiaSharp.dll
NativePayload/win-x64/av_libglesv2.dll
tools/ffmpeg.exe
```

**(B) Flat** (same as portable release root): the six DLLs + `ffmpeg.exe` at zip root.

Host it anywhere the runner can `GET` over HTTPS (GitHub Release asset, blob storage, etc.). Set `CI_PREREQ_ARCHIVE_URL` to that URL. For **private** assets, also set `CI_ARTIFACTS_BEARER_TOKEN` (Bearer token the runner sends).

### Split URLs

Set `CI_NATIVE_PAYLOAD_URL` (zip of the win-x64 folder / flat 6 DLLs) **and** `CI_FFMPEG_URL` (raw `ffmpeg.exe` or a zip containing it).

### Local / self-hosted only

Populate the gitignored paths on the runner; leave secrets unset. Provision no-ops locally; prerequisites then pass if files exist. On **GitHub-hosted** Actions, unset secrets fail the Provision step on purpose (suite green requires artifacts).

## Same gate as CI (local)

Requires the binaries above. From the repo root:

```powershell
./scripts/ci-prerequisites.ps1
dotnet restore OmtCaptureStudio.sln
dotnet build OmtCaptureStudio.sln -c Release -p:Platform=x64 --no-restore
Copy-Item tools\ffmpeg.exe OmtCaptureStudio.Tests\bin\x64\Release\net8.0-windows\ffmpeg.exe -Force
dotnet run --project OmtCaptureStudio.Tests -c Release -p:Platform=x64 --no-build
```

Default suite only (no soak / no `--step`). Failures exit non-zero. On GitHub Actions, provision runs before build so NativePayload is embedded when secrets are set.

Root operator launchers (also tracked): `Launch-OmtCaptureStudio.bat`, `Build-AOT.bat`.
