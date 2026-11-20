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
   `-VersionOverride 1.0.0.N` to pin the stamp. Authenticode is optional: pass
   `-SignThumbprint`, pass `-SelfSign` for a self-signed certificate (see *Self-signed code
   signing* below), or set `OMT_CODESIGN_THUMBPRINT`; the script then signs the exe (before
   the zip) and the setup exe (before its `.sha256`) with `signtool`. With none of those,
   signing is skipped and unsigned releases stay valid. Script updates `version.txt`,
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

## Self-signed code signing (no purchased certificate)

For internal builds, `-SelfSign` signs with a **self-signed** code-signing certificate:

```powershell
powershell -File scripts/build-release.ps1 -SelfSign -NoPrompt -SkipUpload
```

On the first run it creates `CN=OMT Capture Studio (self-signed)` in `Cert:\CurrentUser\My`
(type **Code Signing**, EKU `1.3.6.1.5.5.7.3.3`, key usage Digital Signature, 5-year validity,
exportable private key). Later runs **reuse** that certificate, so the thumbprint — and with it
the signer identity — stays stable across releases; minting a new certificate per release would
invalidate the trust granted to the previous one. Precedence is `-SignThumbprint` ->
`-SelfSign` -> `OMT_CODESIGN_THUMBPRINT`; with none of them signing is still skipped, exactly as
before. Signing requires `signtool.exe` (Windows SDK); if a thumbprint resolves and signtool is
absent, the build fails loudly rather than shipping unsigned.

**Target machines must trust the certificate, or the signature buys nothing.** Without it
`Get-AuthenticodeSignature` reports `UnknownError` — *"A certificate chain processed, but
terminated in a root certificate which is not trusted by the trust provider"* — and users get an
unknown-publisher prompt. Install the public certificate into **both** stores:

```powershell
# 1. signing machine: export the public cert (private key stays in Cert:\CurrentUser\My)
Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
  Where-Object { $_.Subject -eq 'CN=OMT Capture Studio (self-signed)' } |
  Export-Certificate -FilePath OMT-Capture-Studio-selfsigned.cer

# 2. each target machine, elevated: trust the root AND the publisher
Import-Certificate -FilePath .\OMT-Capture-Studio-selfsigned.cer -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate -FilePath .\OMT-Capture-Studio-selfsigned.cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
```

Equivalent GUI route: double-click the `.cer` -> *Install Certificate* -> *Local Machine* ->
*Trusted Root Certification Authorities*, then repeat for *Trusted Publishers*.

Honest limits, stated plainly:

- Self-signing is for **internal / known-fleet** distribution. It does not buy SmartScreen
  reputation — that accumulates only for CA-issued certificates on signed downloads — so Windows
  may still warn a first-time downloader even after the trust install.
- Timestamping (`-TimestampUrl`, default DigiCert) needs network access; the timestamp is what
  keeps an already-issued signature valid after the certificate expires.
- Trust installation changes the machine's trust store and is an operator action — the build
  never does it for you.

## Do not ship

- Any revived `spikes/` tree (former `mf-sinkwriter` was deleted with ADR-0012) as production code.
- A release that skipped the headless suite after recording/sink/UI shell changes.
- Binaries (`ffmpeg.exe`, native payload DLLs) committed into git.
