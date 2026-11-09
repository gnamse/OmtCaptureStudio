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
| `ci-provision-artifacts.ps1` | Phase 1.2: optional download of NativePayload + ffmpeg when `CI_PREREQ_ARCHIVE_URL` (or `CI_NATIVE_PAYLOAD_URL` + `CI_FFMPEG_URL`) is set. No-op if unset. Never commits binaries. |
| `ci-prerequisites.ps1` | Exit non-zero with a clear message if `NativePayload/win-x64` (6 DLLs) or `tools/ffmpeg.exe` is missing. Used by `.github/workflows/ci.yml` after provision. |

## CI artifact owner actions (Phase 1.2)

GitHub Actions will stay red on the prerequisites step until one of these is done:

1. **Preferred:** Host a private zip (release asset or blob) whose layout includes `NativePayload/win-x64/*` (the 6 DLLs) and `tools/ffmpeg.exe`. Set repository secret **`CI_PREREQ_ARCHIVE_URL`** to an HTTPS URL the runner can GET. For private assets, also set **`CI_ARTIFACTS_BEARER_TOKEN`**.
2. **Split:** Set **`CI_NATIVE_PAYLOAD_URL`** (zip of the win-x64 folder) and **`CI_FFMPEG_URL`** (raw `ffmpeg.exe` or a zip containing it).
3. **Local only:** Populate the gitignored paths on a self-hosted runner; leave secrets unset (provision script no-ops; prerequisites then pass if files exist).

Do **not** commit `ffmpeg.exe` or native DLLs into git.

Root operator launchers (also tracked): `Launch-OmtCaptureStudio.bat`, `Build-AOT.bat`.

## Same gate as CI (local)

Requires the binaries above. From the repo root:

```powershell
dotnet restore OmtCaptureStudio.sln
dotnet build OmtCaptureStudio.sln -c Release -p:Platform=x64 --no-restore
./scripts/ci-prerequisites.ps1
Copy-Item tools\ffmpeg.exe OmtCaptureStudio.Tests\bin\x64\Release\net8.0-windows\ffmpeg.exe -Force
dotnet run --project OmtCaptureStudio.Tests -c Release -p:Platform=x64 --no-build
```

Default suite only (no soak / no `--step`). Failures exit non-zero. On GitHub Actions without provisioned binaries, the prerequisites step fails on purpose until Phase 1.2.
