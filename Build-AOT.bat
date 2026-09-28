@echo off
echo ========================================================
echo   Publishing OmtCaptureStudio Ahead-of-Time (AOT / R2R)
echo ========================================================
echo.
dotnet publish "%~dp0OmtCaptureStudio\OmtCaptureStudio.csproj" -c Release -r win-x64 --self-contained true /p:PublishReadyToRun=true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o "%~dp0Publish_AOT"
copy /y "%~dp0OMT_SDK\Libraries\Winx64\libomt.dll" "%~dp0Publish_AOT\" >nul
copy /y "%~dp0OMT_SDK\Libraries\Winx64\libvmx.dll" "%~dp0Publish_AOT\" >nul
echo.
echo Build succeeded! Standalone executable is in:
echo   %~dp0Publish_AOT\OmtCaptureStudio.exe
echo.
pause
