# Release checklist — OMT Capture Studio

Copy-pasteable gate before shipping a build. Detail lives in
`scripts/build-release.ps1` and `installer/OmtCaptureStudio.iss`; this page is
the operator sequence.

## Prerequisites (local machine)

1. `OmtCaptureStudio/NativePayload/win-x64/` populated (release script refuses without it).
2. `tools/ffmpeg.exe` present (recording preflight + installer bundle).
3. Visual Studio / .NET 8 SDK with Native AOT workload; Inno Setup 6.x if building the installer.
4. Working tree clean enough that you know what you are shipping (no throwaway spike trees in the release payload).

## Sequence

1. **Bump version** — edit root `version.txt` (four-part, e.g. `1.0.0.25`), or let
   `scripts/build-release.ps1` increment the revision when you run it.
2. **Build + suite (JIT)** — from repo root:
   ```powershell
   dotnet build OmtCaptureStudio.sln -c Release -p:Platform=x64
   dotnet run --project OmtCaptureStudio.Tests -c Release -p:Platform=x64
   ```
   Suite must exit 0 (throw-on-fail). Run soak variants only when the change touched
   record/render paths (`AGENTS.md`).
3. **Optional AOT smoke** (required when touching reflection-sensitive / generic hot-loop
   code; recommended every release):
   ```powershell
   dotnet publish OmtCaptureStudio/OmtCaptureStudio.csproj -c Release -r win-x64 /p:PublishAot=true
   ```
   Nightly automation: `.github/workflows/aot-nightly.yml`.
4. **Package** — from repo root:
   ```powershell
   powershell -File scripts/build-release.ps1
   ```
   Use `-SkipUpload` to package without `gh` upload; `-NoPrompt` for non-interactive;
   `-VersionOverride 1.0.0.N` to pin the stamp. Script updates `version.txt`,
   `installer/OmtCaptureStudio.iss` `MyAppVersion`, publishes AOT into `Publish_AOT/`,
   and writes ZIP + SHA-256 under `Releases/`.
5. **Installer** — compile `installer/OmtCaptureStudio.iss` with Inno Setup (script
   output also lands under `Releases/` as `OmtCaptureStudio-v{version}-Setup.exe`).
6. **Verify artifacts**
   - ZIP / setup name matches `version.txt`.
   - SHA-256 file next to the ZIP matches the archive bytes.
   - Smoke-launch the AOT exe once (native payload extract + mode picker appears).
7. **Publish** — upload `Releases/` artifacts (GitHub Release or your channel). Prefer
   the script's upload path only when authenticated `gh` is intentional.

## Do not ship

- Any revived `spikes/` tree (former `mf-sinkwriter` was deleted with ADR-0012) as production code.
- A release that skipped the headless suite after recording/sink/UI shell changes.
- Binaries (`ffmpeg.exe`, native payload DLLs) committed into git.
