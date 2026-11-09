<#
.SYNOPSIS
    Downloads CI-only NativePayload + ffmpeg into the gitignored paths.
.DESCRIPTION
    Phase 1.2. Does NOT invent URLs or commit binaries. When the owner configures
    GitHub Actions secrets (or local env vars), this script populates:

      OmtCaptureStudio\NativePayload\win-x64\   (6 DLLs)
      tools\ffmpeg.exe

    Supported env vars (set as repository/organization Actions secrets):

      CI_PREREQ_ARCHIVE_URL   Preferred. HTTPS URL to a zip. Accepted layouts:
                                (A) NativePayload/win-x64/<6 dlls> + tools/ffmpeg.exe
                                (B) Portable release zip: <6 dlls> + ffmpeg.exe at root
                                    (as produced by scripts/build-release.ps1)
      CI_NATIVE_PAYLOAD_URL   Optional. HTTPS URL to a zip of the win-x64 payload folder
                              (dlls at zip root or under win-x64/).
      CI_FFMPEG_URL           Optional. HTTPS URL to ffmpeg.exe (raw) or a zip containing it.

    Auth: if CI_ARTIFACTS_BEARER_TOKEN is set, it is sent as Authorization: Bearer <token>
    (for private release assets). Leave unset for public HTTPS.

    Exit codes:
      0  — provisioned successfully, OR nothing configured and not running on GitHub Actions
           (local no-op; caller still runs ci-prerequisites.ps1)
      1  — configured URL failed to download/extract/validate, OR running on GitHub Actions
           with no CI_* URL secrets (fails loudly so the red step is Provision, not Check)
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

$archiveUrl = [Environment]::GetEnvironmentVariable("CI_PREREQ_ARCHIVE_URL")
$payloadUrl = [Environment]::GetEnvironmentVariable("CI_NATIVE_PAYLOAD_URL")
$ffmpegUrl  = [Environment]::GetEnvironmentVariable("CI_FFMPEG_URL")
$bearer     = [Environment]::GetEnvironmentVariable("CI_ARTIFACTS_BEARER_TOKEN")
$onGitHubActions = -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable("GITHUB_ACTIONS"))

$requiredPayload = @(
    "libvmx.dll",
    "libomt.dll",
    "libomtnet.dll",
    "libHarfBuzzSharp.dll",
    "libSkiaSharp.dll",
    "av_libglesv2.dll"
)

if ([string]::IsNullOrWhiteSpace($archiveUrl) -and
    [string]::IsNullOrWhiteSpace($payloadUrl) -and
    [string]::IsNullOrWhiteSpace($ffmpegUrl)) {
    Write-Host "[ci-provision-artifacts] No CI_* URL secrets/env vars set — skipping download."
    Write-Host "                         Owner action: set CI_PREREQ_ARCHIVE_URL (preferred) or"
    Write-Host "                         CI_NATIVE_PAYLOAD_URL + CI_FFMPEG_URL, then re-run CI."
    Write-Host "                         See scripts/README.md for zip layouts and a public release URL."
    if ($onGitHubActions) {
        Write-Host "[ci-provision-artifacts] FAILING on GitHub Actions: suite cannot go green without artifacts." -ForegroundColor Red
        exit 1
    }
    exit 0
}

function Get-AuthHeaders {
    if ([string]::IsNullOrWhiteSpace($bearer)) { return @{} }
    return @{ Authorization = "Bearer $bearer" }
}

function Invoke-Download([string]$Url, [string]$OutFile) {
    Write-Host "[ci-provision-artifacts] GET $Url -> $OutFile"
    $headers = Get-AuthHeaders
    Invoke-WebRequest -Uri $Url -OutFile $OutFile -Headers $headers -UseBasicParsing
    if (-not (Test-Path -LiteralPath $OutFile -PathType Leaf)) {
        throw "Download produced no file: $OutFile"
    }
}

function Expand-ZipSafe([string]$ZipPath, [string]$DestDir) {
    if (Test-Path -LiteralPath $DestDir) {
        Remove-Item -LiteralPath $DestDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $DestDir | Out-Null
    Expand-Archive -LiteralPath $ZipPath -DestinationPath $DestDir -Force
}

function Test-PayloadFolder([string]$Dir) {
    if (-not (Test-Path -LiteralPath $Dir -PathType Container)) { return $false }
    foreach ($name in $requiredPayload) {
        if (-not (Test-Path -LiteralPath (Join-Path $Dir $name) -PathType Leaf)) { return $false }
    }
    return $true
}

function Copy-PayloadAndFfmpeg([string]$SrcPayloadDir, [string]$SrcFfmpeg, [string]$PayloadDest, [string]$FfmpegDest) {
    if (-not (Test-PayloadFolder $SrcPayloadDir)) {
        throw "Payload folder incomplete (need 6 DLLs): $SrcPayloadDir"
    }
    if (-not (Test-Path -LiteralPath $SrcFfmpeg -PathType Leaf)) {
        throw "ffmpeg.exe missing at: $SrcFfmpeg"
    }
    if (Test-Path -LiteralPath $PayloadDest) {
        Remove-Item -LiteralPath $PayloadDest -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $PayloadDest | Out-Null
    foreach ($name in $requiredPayload) {
        Copy-Item -LiteralPath (Join-Path $SrcPayloadDir $name) -Destination (Join-Path $PayloadDest $name) -Force
    }
    Copy-Item -LiteralPath $SrcFfmpeg -Destination $FfmpegDest -Force
}

$work = Join-Path $RepoRoot ".ci-artifacts-tmp"
try {
    if (Test-Path -LiteralPath $work) {
        Remove-Item -LiteralPath $work -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $work | Out-Null

    $payloadDest = Join-Path $RepoRoot "OmtCaptureStudio\NativePayload\win-x64"
    $ffmpegDest  = Join-Path $RepoRoot "tools\ffmpeg.exe"
    New-Item -ItemType Directory -Force -Path (Split-Path $payloadDest -Parent) | Out-Null
    New-Item -ItemType Directory -Force -Path (Split-Path $ffmpegDest -Parent) | Out-Null

    if (-not [string]::IsNullOrWhiteSpace($archiveUrl)) {
        $zip = Join-Path $work "prereq.zip"
        Invoke-Download $archiveUrl $zip
        $extract = Join-Path $work "prereq"
        Expand-ZipSafe $zip $extract

        # Accept either flat extract or one wrapping folder.
        $roots = @(Get-ChildItem -LiteralPath $extract -Directory)
        $searchRoot = $extract
        if (-not (Test-Path (Join-Path $extract "tools")) -and
            -not (Test-Path (Join-Path $extract "NativePayload")) -and
            -not (Test-PayloadFolder $extract) -and
            $roots.Count -eq 1) {
            $searchRoot = $roots[0].FullName
        }

        $srcPayload = $null
        $candidatePayload = Join-Path $searchRoot "NativePayload\win-x64"
        if (Test-PayloadFolder $candidatePayload) {
            $srcPayload = $candidatePayload
        } else {
            $alt = Join-Path $searchRoot "win-x64"
            if (Test-PayloadFolder $alt) {
                $srcPayload = $alt
            } elseif (Test-PayloadFolder $searchRoot) {
                # Portable release zip: 6 DLLs live at archive root beside ffmpeg.exe.
                $srcPayload = $searchRoot
            }
        }

        $srcFfmpeg = Join-Path $searchRoot "tools\ffmpeg.exe"
        if (-not (Test-Path -LiteralPath $srcFfmpeg -PathType Leaf)) {
            $hit = Get-ChildItem -LiteralPath $searchRoot -Filter "ffmpeg.exe" -Recurse -File -ErrorAction SilentlyContinue |
                Select-Object -First 1
            if ($hit) { $srcFfmpeg = $hit.FullName }
        }

        if ($null -eq $srcPayload) {
            throw "Archive missing NativePayload/win-x64, win-x64, or flat 6-DLL payload under $searchRoot"
        }
        if (-not (Test-Path -LiteralPath $srcFfmpeg -PathType Leaf)) {
            throw "Archive missing ffmpeg.exe (tools/ffmpeg.exe or flat) under $searchRoot"
        }

        Copy-PayloadAndFfmpeg $srcPayload $srcFfmpeg $payloadDest $ffmpegDest
        Write-Host "[ci-provision-artifacts] Provisioned from CI_PREREQ_ARCHIVE_URL."
        exit 0
    }

    if (-not [string]::IsNullOrWhiteSpace($payloadUrl)) {
        $zip = Join-Path $work "payload.zip"
        Invoke-Download $payloadUrl $zip
        $extract = Join-Path $work "payload"
        Expand-ZipSafe $zip $extract

        $srcPayload = $extract
        if (Test-PayloadFolder (Join-Path $extract "win-x64")) {
            $srcPayload = Join-Path $extract "win-x64"
        } elseif (Test-PayloadFolder (Join-Path $extract "NativePayload\win-x64")) {
            $srcPayload = Join-Path $extract "NativePayload\win-x64"
        } elseif (-not (Test-PayloadFolder $extract)) {
            throw "CI_NATIVE_PAYLOAD_URL zip does not contain the 6 required DLLs"
        }

        if (Test-Path -LiteralPath $payloadDest) {
            Remove-Item -LiteralPath $payloadDest -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $payloadDest | Out-Null
        foreach ($name in $requiredPayload) {
            Copy-Item -LiteralPath (Join-Path $srcPayload $name) -Destination (Join-Path $payloadDest $name) -Force
        }
        Write-Host "[ci-provision-artifacts] Native payload provisioned from CI_NATIVE_PAYLOAD_URL."
    }

    if (-not [string]::IsNullOrWhiteSpace($ffmpegUrl)) {
        $tmp = Join-Path $work "ffmpeg-download"
        New-Item -ItemType Directory -Force -Path $tmp | Out-Null
        if ($ffmpegUrl -match '\.zip(\?|$)') {
            $zip = Join-Path $tmp "ffmpeg.zip"
            Invoke-Download $ffmpegUrl $zip
            Expand-ZipSafe $zip (Join-Path $tmp "out")
            $hit = Get-ChildItem -LiteralPath (Join-Path $tmp "out") -Filter "ffmpeg.exe" -Recurse -File |
                Select-Object -First 1
            if (-not $hit) { throw "CI_FFMPEG_URL zip did not contain ffmpeg.exe" }
            Copy-Item -LiteralPath $hit.FullName -Destination $ffmpegDest -Force
        } else {
            Invoke-Download $ffmpegUrl $ffmpegDest
        }
        Write-Host "[ci-provision-artifacts] ffmpeg.exe provisioned from CI_FFMPEG_URL."
    }
}
catch {
    Write-Host "[ci-provision-artifacts] FAILED: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    if (Test-Path -LiteralPath $work) {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "[ci-provision-artifacts] Done. Next step: scripts/ci-prerequisites.ps1 validates the tree."
exit 0
