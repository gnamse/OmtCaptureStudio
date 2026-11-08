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

Root operator launchers (also tracked): `Launch-OmtCaptureStudio.bat`, `Build-AOT.bat`.
