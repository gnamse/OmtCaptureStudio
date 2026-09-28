@echo off
if exist "%~dp0OmtCaptureStudio\bin\x64\Release\net8.0-windows\OmtCaptureStudio.exe" (
    cd /d "%~dp0OmtCaptureStudio\bin\x64\Release\net8.0-windows"
    start "" "OmtCaptureStudio.exe"
    exit /b
)
if exist "%~dp0OmtCaptureStudio\bin\Release\net8.0-windows\OmtCaptureStudio.exe" (
    cd /d "%~dp0OmtCaptureStudio\bin\Release\net8.0-windows"
    start "" "OmtCaptureStudio.exe"
    exit /b
)
echo Building OmtCaptureStudio solution...
dotnet build "%~dp0OmtCaptureStudio.sln" -c Release
cd /d "%~dp0OmtCaptureStudio\bin\x64\Release\net8.0-windows"
start "" "OmtCaptureStudio.exe"
