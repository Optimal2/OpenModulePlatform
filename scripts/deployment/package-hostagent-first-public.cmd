@echo off
setlocal
set SCRIPT_DIR=%~dp0
set REPO_ROOT=%SCRIPT_DIR%..\..
powershell -NoProfile -File "%SCRIPT_DIR%package-hostagent-first.ps1" -ConfigPath "%SCRIPT_DIR%omp-suite.config.sample.psd1" -OutputRoot "%REPO_ROOT%\artifacts\hostagent-first-public"
endlocal
