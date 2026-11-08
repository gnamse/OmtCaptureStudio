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
| `ci-prerequisites.ps1` | Exit non-zero with a clear message if `NativePayload/win-x64` (6 DLLs) or `tools/ffmpeg.exe` is missing. Used by `.github/workflows/ci.yml` before the default suite. |

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
