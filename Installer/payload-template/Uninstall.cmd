@echo off
REM ---------------------------------------------------------------------------
REM  DKSI Revit add-in suite - uninstaller
REM
REM  Double-click to remove your own install.
REM  Right-click -> "Run as administrator" to also remove an all-users install.
REM ---------------------------------------------------------------------------

setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall.ps1" %*
echo.
pause
