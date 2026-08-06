@echo off
REM ---------------------------------------------------------------------------
REM  DKSI Revit add-in suite - installer
REM
REM  Double-click this file to install for yourself.
REM  Right-click -> "Run as administrator" to install for everyone on the PC.
REM
REM  -ExecutionPolicy Bypass applies to THIS script only. It does not change any
REM  machine setting, and it is what lets a colleague run the installer without
REM  an administrator first relaxing PowerShell's policy for them.
REM ---------------------------------------------------------------------------

setlocal

REM Detect elevation. If the user right-clicked "Run as administrator" we install
REM for all users, because that is plainly what they meant by doing so.
net session >nul 2>&1
if %errorlevel%==0 (
    echo Running elevated - installing for ALL USERS.
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -AllUsers %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
)

set EXITCODE=%errorlevel%

echo.
if %EXITCODE%==0 (
    echo Finished. You can close this window.
) else (
    echo The installer stopped with a problem. Read the message above.
)
echo.
pause
exit /b %EXITCODE%
