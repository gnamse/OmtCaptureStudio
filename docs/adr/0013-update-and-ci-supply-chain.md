# ADR-0013: Update and CI supply chain — hash, size, opt-in Authenticode

- **Status:** Accepted
- **Scope:** `UpdateService` / `HttpUpdateTransport`, `scripts/ci-provision-artifacts.ps1`,
  `scripts/ci-artifact-checksums.txt`, `scripts/build-release.ps1`, `.github/workflows/ci.yml`,
  `.github/workflows/aot-nightly.yml`
- **Related:** ADR-0006 (settings are not the place this lives — the opt-in is an environment
  variable so a machine policy can turn it on without a UI)

## Context

The auto-updater downloads an installer and its `.sha256` from the same GitHub release, then
launches the installer elevated. That hash detects corruption. It does not detect a publisher who
replaces both files. The download also had no size cap, so a hostile or broken response could be
buffered without limit.

CI provision (`ci-provision-artifacts.ps1`) downloaded the native payload and `ffmpeg.exe` from a
release URL with no checksum. The workflows referenced `actions/checkout` and `actions/setup-dotnet`
by the floating `v4` tag.

Released installers are unsigned today. Refusing them would turn the update channel off.

## Decision

1. **Size cap.** An installer whose published size or downloaded body exceeds 512 MiB
   (`UpdateService.DefaultMaxInstallerDownloadBytes`) is discarded and not launched. The HTTP
   transport enforces the cap while reading, so the cap is not only a check after the buffer exists.
   Tests inject a smaller cap.

2. **Authenticode is opt-in.** Before launch, when enforcement is on, the installer is checked with
   `WinVerifyTrust` (UI none, revocation cache only — a broadcast machine must not block on a live
   CRL fetch) and, when configured, the signer name and SHA-1 thumbprint. Enforcement is **off**
   unless one of these is set:
   - `OMT_UPDATE_REQUIRE_SIGNATURE=1` (or `true`)
   - `OMT_UPDATE_SIGN_PUBLISHER` (case-insensitive substring of the signer's simple display name)
   - `OMT_UPDATE_SIGN_THUMBPRINT` (hex, spaces and colons ignored)

   Unsigned releases keep installing. A present policy that cannot be satisfied refuses the launch.
   The verifier is P/Invoke only (no `X509Certificate.CreateFromSignedFile`), so it stays Native AOT
   compatible. The headless suite injects the verdict; it does not call WinVerifyTrust.

3. **CI artifacts are pinned.** `scripts/ci-artifact-checksums.txt` holds the SHA-256 of each
   portable zip provision is known to download: the workflow fallback (`v1.0.0.25`) and the zip the
   repository secret currently selects (`v1.0.0.24`). Provision verifies the download before extract.
   A URL with no pin is refused unless `CI_PREREQ_ARCHIVE_SHA256`, `CI_NATIVE_PAYLOAD_SHA256`, or
   `CI_FFMPEG_SHA256` is set, and an override that disagrees with a pin fails.

4. **GitHub Actions are pinned to commit SHAs** (`actions/checkout` v4.4.0, `actions/setup-dotnet`
   v4.3.1), the commits the `v4` tags resolved to when this decision was made.

5. **Release signing is cert-gated.** `build-release.ps1` signs `OmtCaptureStudio.exe` (before the
   zip and the installer are built from it) and the setup exe (before its `.sha256` is written) when
   `-SignThumbprint` or `OMT_CODESIGN_THUMBPRINT` is set and `signtool.exe` is on the machine.
   Neither is required. Asking for a signature without `signtool` fails the release rather than
   publishing an unsigned file that was supposed to be signed.

## Consequences

Turning the update gate on is an operator action, done when a signing certificate exists, not a
silent change to today's unsigned channel. CI will fail closed if the pinned zip is replaced
without a checksum update. Action tags no longer float.
