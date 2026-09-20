@echo off
rem ---------------------------------------------------------------------------
rem  Double-clickable installer for PrintPacer.
rem
rem  Wraps the PowerShell script so nobody has to think about execution policy.
rem  -ExecutionPolicy Bypass applies to this one process only; it changes no
rem  machine setting. No administrator rights are needed or requested.
rem ---------------------------------------------------------------------------
setlocal

echo.
echo  Installing PrintPacer for the current user...
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-PrintPacer.ps1" %*
set "code=%ERRORLEVEL%"

echo.
if not "%code%"=="0" (
    echo  Installation failed with exit code %code%.
) else (
    echo  Finished. PrintPacer is in your Start Menu.
)

echo.
pause
exit /b %code%
