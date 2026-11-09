<#
.SYNOPSIS
    Fails non-zero with a clear message when CI/local binaries required by the default suite are missing.
.DESCRIPTION
    Native payload DLLs and tools/ffmpeg.exe are gitignored (size / redistribution). CI runs
    scripts/ci-provision-artifacts.ps1 first (optional download via secrets), then this script
    fails loudly if the tree is still empty. Do not commit binaries.

    Native payload must be present *before* `dotnet build` so EmbeddedResource embedding succeeds.
    `.github/workflows/ci.yml` provisions and checks before build; locally run this script before build.
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
Write-Host " CI prerequisites missing (Phase 1.2 artifacts not provisioned)" -ForegroundColor Red
Write-Host "========================================================================" -ForegroundColor Red
Write-Host ""
Write-Host "Native payload and ffmpeg.exe are gitignored. Do not commit those binaries." -ForegroundColor Yellow
Write-Host ""
Write-Host "Populate locally (scripts/README.md + README Quick Start §0), OR set Actions" -ForegroundColor Yellow
Write-Host "secrets for scripts/ci-provision-artifacts.ps1 (CI_PREREQ_ARCHIVE_URL preferred)." -ForegroundColor Yellow
Write-Host ""
Write-Host "Missing:" -ForegroundColor Red
foreach ($item in $missing) {
    Write-Host "  - $item" -ForegroundColor Red
}
Write-Host ""
Write-Host "CI order is provision → check → build → suite so NativePayload embeds at build" -ForegroundColor Yellow
Write-Host "time (building before populate leaves an empty embed; TestNativePayloadVerification fails)." -ForegroundColor Yellow
Write-Host ""
exit 1
