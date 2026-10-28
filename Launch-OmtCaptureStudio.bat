@echo off
rem Canonical output tree: OmtCaptureStudio\bin\x64\Release\net8.0-windows
rem (pinned by <Platform>x64</Platform> in the csproj). A project-direct build and a
rem solution build now land in the SAME tree, so there is no AnyCPU fallback to guess
rem between and no stale-binary ambiguity.
set "APPDIR=%~dp0OmtCaptureStudio\bin\x64\Release\net8.0-windows"

if not exist "%APPDIR%\OmtCaptureStudio.exe" (
    echo Building OmtCaptureStudio solution...
    dotnet build "%~dp0OmtCaptureStudio.sln" -c Release
)

if not exist "%APPDIR%\OmtCaptureStudio.exe" (
    echo [ERROR] Build produced no "%APPDIR%\OmtCaptureStudio.exe". Aborting launch.
    exit /b 1
)

cd /d "%APPDIR%"
start "" "OmtCaptureStudio.exe"
