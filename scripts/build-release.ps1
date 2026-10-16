<#
.SYNOPSIS
    Builds, stamps version, packages Native AOT release, and prepares GitHub Release upload.
.DESCRIPTION
    Increments build version, updates csproj, compiles Native Ahead-of-Time (Native AOT) binary,
    creates ZIP and SHA256 in Releases/, and optionally uploads via GitHub CLI (gh).
#>

param(
    [string]$VersionOverride = "",
    [switch]$NoPrompt = $false,
    [switch]$SkipUpload = $false
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$versionFile = Join-Path $repoRoot "version.txt"
$projectFile = Join-Path $repoRoot "OmtCaptureStudio\OmtCaptureStudio.csproj"
$publishDir = Join-Path $repoRoot "Publish_AOT"
$releasesDir = Join-Path $repoRoot "Releases"
$sdkLibsDir = Join-Path $repoRoot "OMT_SDK\Libraries\Winx64"
$omtLibsDir = Join-Path $repoRoot "OmtCaptureStudio\libs"

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "   OMT Capture Studio - Release & AOT Packaging Tool    " -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host ""

# 1. Version Detection & Increment
$currentVersion = "1.0.0.0"
if (Test-Path $versionFile) {
    $currentVersion = (Get-Content $versionFile -Raw).Trim()
}

$nextVersion = ""
if (-not [string]::IsNullOrWhiteSpace($VersionOverride)) {
    $nextVersion = $VersionOverride.Trim()
} else {
    try {
        $v = [version]$currentVersion
        $nextBuild = if ($v.Revision -ge 0) { $v.Revision + 1 } else { 1 }
        $nextVersion = "$($v.Major).$($v.Minor).$($v.Build).$nextBuild"
    } catch {
        $nextVersion = "1.0.0.1"
    }
}

Write-Host "Current Version: " -NoNewline
Write-Host $currentVersion -ForegroundColor Yellow
Write-Host "Target Version:  " -NoNewline
Write-Host $nextVersion -ForegroundColor Green

if (-not $NoPrompt -and [Environment]::UserInteractive) {
    Write-Host ""
    $userInput = Read-Host "Press [Enter] to use $nextVersion, or type a custom version"
    if (-not [string]::IsNullOrWhiteSpace($userInput)) {
        $nextVersion = $userInput.Trim()
        Write-Host "Selected Version: $nextVersion" -ForegroundColor Green
    }
}

# Save new version to version.txt
Set-Content -Path $versionFile -Value $nextVersion -NoNewline
Write-Host "[OK] Saved version $nextVersion to version.txt" -ForegroundColor Green

# Update version in installer script
$issScript = Join-Path $repoRoot "installer\OmtCaptureStudio.iss"
if (Test-Path $issScript) {
    (Get-Content $issScript) -replace '#define MyAppVersion ".*"', "#define MyAppVersion `"$nextVersion`"" | Set-Content $issScript
    Write-Host "[OK] Updated MyAppVersion in OmtCaptureStudio.iss to $nextVersion" -ForegroundColor Green
}

# 2. Check for Running Processes
$running = Get-Process -Name "OmtCaptureStudio" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host ""
    Write-Host "[WARNING] OmtCaptureStudio is currently running!" -ForegroundColor Yellow
    if (-not $NoPrompt -and [Environment]::UserInteractive) {
        $resp = Read-Host "Terminate running instances to allow overwriting? (Y/n)"
        if ($resp -eq "" -or $resp -match "^[yY]") {
            $running | Stop-Process -Force
            Start-Sleep -Milliseconds 800
            Write-Host "[OK] Closed running process." -ForegroundColor Green
        }
    } else {
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 800
    }
}

# 3. Clean Staging Folder
if (-not (Test-Path $publishDir)) {
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
} else {
    # Remove old executables to prevent stale builds
    Get-ChildItem -Path $publishDir -Filter "*.exe" | Remove-Item -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path $releasesDir)) {
    New-Item -ItemType Directory -Path $releasesDir -Force | Out-Null
}

# 4. Compile Ahead-of-Time (Native AOT)
Write-Host ""
Write-Host "[Step 1/4] Compiling Ahead-of-Time (Native AOT) Binary..." -ForegroundColor Cyan
Write-Host "   Version: $nextVersion"
Write-Host "   Target:  win-x64 (Native AOT)"

$publishArgs = @(
    "publish",
    $projectFile,
    "-c", "Release",
    "-r", "win-x64",
    "/p:PublishAot=true",
    "/p:Version=$nextVersion",
    "/p:AssemblyVersion=$nextVersion",
    "/p:FileVersion=$nextVersion",
    "-o", $publishDir
)

& dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    Write-Host "[FAILED] dotnet publish failed with exit code $LASTEXITCODE." -ForegroundColor Red
    exit $LASTEXITCODE
}

# 5. Copy Native Dependencies and Docs
Write-Host ""
Write-Host "[Step 2/4] Staging Native Codecs and Dependencies..." -ForegroundColor Cyan

$sourceLibsDir = if (Test-Path "$sdkLibsDir\libomt.dll") { $sdkLibsDir } else { $omtLibsDir }
if (Test-Path "$sourceLibsDir\libomt.dll") {
    Copy-Item "$sourceLibsDir\libomt.dll" "$publishDir\" -Force
}
if (Test-Path "$sourceLibsDir\libvmx.dll") {
    Copy-Item "$sourceLibsDir\libvmx.dll" "$publishDir\" -Force
}
if (Test-Path "$sourceLibsDir\libomtnet.dll") {
    Copy-Item "$sourceLibsDir\libomtnet.dll" "$publishDir\" -Force
}

$readmePath = Join-Path $repoRoot "README.md"
$licensePath = Join-Path $repoRoot "LICENSE.txt"
if (Test-Path $readmePath) { Copy-Item $readmePath "$publishDir\" -Force }
if (Test-Path $licensePath) { Copy-Item $licensePath "$publishDir\" -Force }

# Stage FFmpeg for out-of-the-box recording (HARD REQUIREMENT - see below)
$toolsFfmpeg = Join-Path $repoRoot "tools\ffmpeg.exe"
$ffmpegSource = $null

if (Test-Path $toolsFfmpeg) {
    $ffmpegSource = $toolsFfmpeg
} else {
    # Fallback: seed tools\ffmpeg.exe from an ffmpeg.exe already on PATH.
    $ffmpegCmd = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    if ($ffmpegCmd) {
        $sourceExe = $ffmpegCmd.Source
        $item = Get-Item $sourceExe -ErrorAction SilentlyContinue
        if ($item -and $item.Target) { $sourceExe = $item.Target }
        if (Test-Path $sourceExe) {
            $toolsDir = Join-Path $repoRoot "tools"
            if (-not (Test-Path $toolsDir)) { New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null }
            Copy-Item $sourceExe $toolsFfmpeg -Force
        }
    }
    if (Test-Path $toolsFfmpeg) { $ffmpegSource = $toolsFfmpeg }
}

# ffmpeg is the application's only external runtime dependency, and the installer
# pulls it straight from tools\ffmpeg.exe with skipifsourcedoesntexist. A release
# built without it ships broken and nobody notices until a user presses Record.
# Therefore: fail hard, never warn-and-continue.
if (-not $ffmpegSource) {
    Write-Host "   [FAILED] ffmpeg.exe is missing." -ForegroundColor Red
    Write-Host "            Expected: $toolsFfmpeg" -ForegroundColor Red
    Write-Host "            No ffmpeg.exe was found on PATH to seed it either." -ForegroundColor Red
    throw "ffmpeg.exe is missing (expected '$toolsFfmpeg', or an ffmpeg.exe on PATH). Refusing to build a release without the bundled encoder - installer/OmtCaptureStudio.iss would silently omit it."
}

Copy-Item $ffmpegSource "$publishDir\ffmpeg.exe" -Force
if (-not (Test-Path "$publishDir\ffmpeg.exe")) {
    throw "Staging ffmpeg.exe into '$publishDir' did not produce a file. Aborting release build."
}

# Provenance: record which encoder build went into this release.
$ffmpegVersionLine = $null
try {
    $ffmpegVersionLine = (& $ffmpegSource -version 2>&1 | Select-Object -First 1)
} catch {
    $ffmpegVersionLine = $null
}
if ([string]::IsNullOrWhiteSpace($ffmpegVersionLine)) {
    throw "Could not read 'ffmpeg -version' output from '$ffmpegSource'. Refusing to build a release without encoder provenance."
}
$ffmpegVersionLine = $ffmpegVersionLine.Trim()
$ffmpegSizeMb = [math]::Round(((Get-Item $ffmpegSource).Length / 1MB), 2)
$ffmpegHash = (Get-FileHash -Path $ffmpegSource -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host "   [OK] Staged ffmpeg.exe for out-of-the-box recording ($ffmpegSizeMb MB)" -ForegroundColor Green
Write-Host "        ffmpeg: $ffmpegVersionLine" -ForegroundColor DarkGray

# Build log: per-release provenance, including the encoder build.
$buildLogPath = Join-Path $releasesDir "build-v$nextVersion.log"
$buildLogLines = @(
    "# OMT Capture Studio release build: $nextVersion",
    "# Created: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
    "",
    "[ffmpeg]",
    "source  = $ffmpegSource",
    "version = $ffmpegVersionLine",
    "size    = $ffmpegSizeMb MB",
    "sha256  = $ffmpegHash"
)
Set-Content -Path $buildLogPath -Value $buildLogLines
Write-Host "   [OK] Build log written: $buildLogPath" -ForegroundColor Green

# Remove debug symbols from release staging
Get-ChildItem -Path $publishDir -Filter "*.pdb" | Remove-Item -Force -ErrorAction SilentlyContinue

# 6. Create ZIP Release Archive
Write-Host ""
Write-Host "[Step 3/4] Generating Portable Release ZIP Archive..." -ForegroundColor Cyan

$zipName = "OmtCaptureStudio-v$nextVersion-win-x64.zip"
$zipPath = Join-Path $releasesDir $zipName
$shaPath = "$zipPath.sha256"

# Guard the archive/checksum step: never emit ZIP + SHA256 (or a Setup.exe built
# from them) for a staging directory that lost ffmpeg.exe along the way.
if (-not (Test-Path "$publishDir\ffmpeg.exe")) {
    throw "ffmpeg.exe is missing from the staging directory '$publishDir'. Aborting before ZIP/SHA256 generation so no partial release artifacts are produced."
}

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}
if (Test-Path $shaPath) {
    Remove-Item $shaPath -Force
}

# Create ZIP archive from publish directory
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

# Verify the archive actually carries the encoder before we checksum/publish it.
$ffmpegEntry = $null
$zipProbe = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $ffmpegEntry = $zipProbe.GetEntry("ffmpeg.exe")
} finally {
    $zipProbe.Dispose()
}
if (-not $ffmpegEntry) {
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
    Remove-Item $shaPath -Force -ErrorAction SilentlyContinue
    throw "Staged ZIP '$zipName' does not contain ffmpeg.exe. Removed the partial archive; aborting release build."
}
Write-Host "   [OK] Verified ffmpeg.exe is present in $zipName ($([math]::Round($ffmpegEntry.Length / 1MB, 2)) MB, uncompressed)" -ForegroundColor Green

# Calculate SHA256 checksum
$hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path $shaPath -Value "$hash  $zipName" -NoNewline
$zipSizeMb = [math]::Round(((Get-Item $zipPath).Length / 1MB), 2)
Write-Host "   [OK] Portable ZIP created: $zipName ($zipSizeMb MB)" -ForegroundColor Green


# Automatically update Inno Setup script with the new version
$issScriptPath = Join-Path $repoRoot "installer\OmtCaptureStudio.iss"
if (Test-Path $issScriptPath) {
    $issContent = Get-Content -Path $issScriptPath -Raw
    $issContent = $issContent -replace '(?m)^#define MyAppVersion ".*"', "#define MyAppVersion `"$nextVersion`""
    Set-Content -Path $issScriptPath -Value $issContent -NoNewline
    Write-Host "   [OK] Updated OmtCaptureStudio.iss to v$nextVersion" -ForegroundColor Green
}
# 7. Compile Inno Setup Installer (if iscc.exe is available)
Write-Host ""
Write-Host "[Step 4/4] Generating Inno Setup Installer..." -ForegroundColor Cyan

$isccPath = $null
$isccCmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
$possibleIsccPaths = @(
    $(if ($isccCmd) { $isccCmd.Source }),
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\iscc.exe",
    "C:\Program Files (x86)\Inno Setup 6\iscc.exe",
    "C:\Program Files\Inno Setup 6\iscc.exe"
)
foreach ($p in $possibleIsccPaths) {
    if ($p -and (Test-Path $p)) {
        $isccPath = $p
        break
    }
}

$setupExeName = "OmtCaptureStudio-v$nextVersion-Setup.exe"
$setupExePath = Join-Path $releasesDir $setupExeName
$setupShaPath = "$setupExePath.sha256"
$setupBuilt = $false

if ($isccPath) {
    Write-Host "   Using Inno Setup compiler: $isccPath" -ForegroundColor DarkGray
    $issScript = Join-Path $repoRoot "installer\OmtCaptureStudio.iss"
    if (Test-Path $issScript) {
        if (Test-Path $setupExePath) { Remove-Item $setupExePath -Force -ErrorAction SilentlyContinue }
        if (Test-Path $setupShaPath) { Remove-Item $setupShaPath -Force -ErrorAction SilentlyContinue }

        & $isccPath /DMyAppVersion=$nextVersion /Qp $issScript
        if ($LASTEXITCODE -eq 0 -and (Test-Path $setupExePath)) {
            $setupHash = (Get-FileHash -Path $setupExePath -Algorithm SHA256).Hash.ToLowerInvariant()
            Set-Content -Path $setupShaPath -Value "$setupHash  $setupExeName" -NoNewline
            $setupSizeMb = [math]::Round(((Get-Item $setupExePath).Length / 1MB), 2)
            $setupBuilt = $true
            Write-Host "   [OK] Setup installer built: $setupExeName ($setupSizeMb MB)" -ForegroundColor Green
        } else {
            Write-Host "   [WARNING] Inno Setup compilation failed with exit code $LASTEXITCODE." -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "   [INFO] Inno Setup compiler (iscc.exe) not found. Skipping installer generation." -ForegroundColor Yellow
    Write-Host "          To enable installer builds: winget install JRSoftware.InnoSetup" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "========================================================" -ForegroundColor Green
Write-Host "               RELEASE BUILD COMPLETE!                  " -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Green
Write-Host "Version:    $nextVersion"
Write-Host "Portable:   $zipPath ($zipSizeMb MB)"
Write-Host "   SHA256:  $hash"
if ($setupBuilt) {
    Write-Host "Installer:  $setupExePath ($setupSizeMb MB)"
    Write-Host "   SHA256:  $setupHash"
}
Write-Host ""

# 8. GitHub Releases Upload / CLI Integration
$gh = Get-Command gh -ErrorAction SilentlyContinue
if ($gh) {
    $upload = -not $SkipUpload
    if ($upload -and -not $NoPrompt -and [Environment]::UserInteractive) {
        $resp = Read-Host "Upload release 'v$nextVersion' directly to GitHub now? (Y/n)"
        if ($resp -ne "" -and $resp -notmatch "^[yY]") {
            $upload = $false
        }
    }

    if ($upload) {
        $releaseNotes = @"
### OMT Capture Studio v$nextVersion

Broadcast-grade Open Media Transport (OMT) desktop capture & monitoring studio.

#### Downloads
- **Setup Installer**: \`$setupExeName\` (Recommended for desktop workstations, auto-creates shortcuts and configures firewall)
- **Portable ZIP**: \`$zipName\` (Zero-install, standalone portable execution)

#### Checksums (SHA256)
- \`$zipName\`: \`$hash\`
$([string](if ($setupBuilt) { "- ``$setupExeName``: ``$setupHash``" }))
"@
        $uploadFiles = @($zipPath, $shaPath)
        if ($setupBuilt) {
            $uploadFiles += @($setupExePath, $setupShaPath)
        }

        & gh release create "v$nextVersion" @uploadFiles --title "v$nextVersion" --notes "$releaseNotes"
        if ($LASTEXITCODE -eq 0) {
            Write-Host "[SUCCESS] Published to GitHub Releases!" -ForegroundColor Green
        } else {
            Write-Host "[WARNING] GitHub CLI release creation exited with code $LASTEXITCODE." -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "To publish this release to GitHub:" -ForegroundColor Yellow
    Write-Host "  1. Open: https://github.com/gnamse/OmtCaptureStudio/releases/new"
    Write-Host "  2. Choose tag:     v$nextVersion"
    Write-Host "  3. Release title:  v$nextVersion"
    Write-Host "  4. Drag and drop:  $zipName, $setupExeName (if built)"
    Write-Host ""
    Write-Host "Tip: Install GitHub CLI via 'winget install GitHub.cli' to enable 1-click automatic uploads!" -ForegroundColor DarkGray
}

Write-Host ""

