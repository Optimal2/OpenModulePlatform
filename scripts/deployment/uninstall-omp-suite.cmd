@echo off
setlocal
cd /d "%~dp0"
echo Running OpenModulePlatform suite uninstaller from %CD%
echo Expected config: %CD%\omp-suite.local.psd1
echo.
powershell.exe -NoProfile -File "%~dp0uninstall-omp-suite.ps1"
set EXITCODE=%ERRORLEVEL%
echo.
if not "%EXITCODE%"=="0" echo OpenModulePlatform suite uninstall failed with exit code %EXITCODE%.
pause
exit /b %EXITCODE%
