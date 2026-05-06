@echo off
setlocal
cd /d "%~dp0"
echo Running OpenModulePlatform suite installer from %CD%
echo Expected config: %CD%\omp-suite.local.psd1
echo DeploymentMode is read from the config file when no argument is supplied.
echo.
powershell.exe -NoProfile -File "%~dp0install-omp-suite.ps1"
set EXITCODE=%ERRORLEVEL%
echo.
if not "%EXITCODE%"=="0" echo OpenModulePlatform suite installation failed with exit code %EXITCODE%.
pause
exit /b %EXITCODE%
