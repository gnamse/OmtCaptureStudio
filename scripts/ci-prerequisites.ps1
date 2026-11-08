<#
.SYNOPSIS
    Fails non-zero with a clear message when CI/local binaries required by the default suite are missing.
.DESCRIPTION
    Native payload DLLs and tools/ffmpeg.exe are gitignored (size / redistribution). Phase 1.1 CI
    checks for them and fails loudly until Phase 1.2 provisions runner artifacts. Do not commit binaries.

    Native payload must be present *before* `dotnet build` so EmbeddedResource embedding succeeds;
    after provisioning, re-run the full gate (restore → build → tests), not tests alone.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
} else {
    $RepoRoot = (Resolve-Path $RepoRoot).Path
}

$payloadDir = Join-Path $RepoRoot "OmtCaptureStudio\NativePayload\win-x64"
$requiredPayload = @(
    "libvmx.dll",
    "libomt.dll",
    "libomtnet.dll",
    "libHarfBuzzSharp.dll",
    "libSkiaSharp.dll",
    "av_libglesv2.dll"
)
$ffmpegPath = Join-Path $RepoRoot "tools\ffmpeg.exe"

$missing = New-Object System.Collections.Generic.List[string]

if (-not (Test-Path -LiteralPath $payloadDir -PathType Container)) {
    $missing.Add("directory: OmtCaptureStudio\NativePayload\win-x64\")
} else {
    foreach ($name in $requiredPayload) {
        $path = Join-Path $payloadDir $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            $missing.Add("OmtCaptureStudio\NativePayload\win-x64\$name")
        }
    }
}

if (-not (Test-Path -LiteralPath $ffmpegPath -PathType Leaf)) {
    $missing.Add("tools\ffmpeg.exe")
}

if ($missing.Count -eq 0) {
    Write-Host "[ci-prerequisites] OK — native payload (6 DLLs) and tools\ffmpeg.exe are present." -ForegroundColor Green
    exit 0
}

Write-Host ""
Write-Host "========================================================================" -ForegroundColor Red
Write-Host " CI prerequisites missing (Phase 1.1 gate / awaiting Phase 1.2 artifacts)" -ForegroundColor Red
Write-Host "========================================================================" -ForegroundColor Red
Write-Host ""
Write-Host "Native payload and ffmpeg.exe are gitignored and are NOT restored by this" -ForegroundColor Yellow
Write-Host "workflow yet. Do not commit those binaries." -ForegroundColor Yellow
Write-Host ""
Write-Host "Populate locally (see scripts/README.md and README Quick Start §0), or complete" -ForegroundColor Yellow
Write-Host "Phase 1.2 (CI cache / secret-stored zip for NativePayload + ffmpeg)." -ForegroundColor Yellow
Write-Host ""
Write-Host "Missing:" -ForegroundColor Red
foreach ($item in $missing) {
    Write-Host "  - $item" -ForegroundColor Red
}
Write-Host ""
Write-Host "After provisioning: re-run restore → build → default suite so NativePayload" -ForegroundColor Yellow
Write-Host "is embedded at build time (empty embed = TestNativePayloadVerification fails)." -ForegroundColor Yellow
Write-Host ""
exit 1
