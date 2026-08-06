# DKSI Revit Tools - installer.
#
# Copies the add-in into the CURRENT USER's Revit 2027 add-ins folder. No administrator
# rights, no system changes, nothing written outside your own profile.
#
#   powershell -ExecutionPolicy Bypass -File .\Install.ps1
#
# Uninstall with Uninstall.ps1 beside this file, or by deleting the two items it reports.

$ErrorActionPreference = 'Stop'

$RevitVersion = '2027'
$Here         = Split-Path -Parent $MyInvocation.MyCommand.Path
$AddinsDir    = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$PayloadDir   = Join-Path $Here 'Cda'
$Manifest     = Join-Path $Here 'Cda.Revit.Addin.addin'

function Say([string]$text, [string]$colour = 'Gray') { Write-Host $text -ForegroundColor $colour }

Say ""
Say "DKSI Revit Tools - installer" 'Cyan'
Say "----------------------------" 'Cyan'

# ---------------------------------------------------------------- pre-flight

# Revit locks the add-in DLL while it runs, so a copy over a live session fails halfway
# and leaves a mixed set of files. Refusing up front is kinder than a partial install.
$revit = Get-Process revit -ErrorAction SilentlyContinue
if ($revit) {
    Say ""
    Say "Revit is running (PID $($revit.Id -join ', '))." 'Red'
    Say "Close Revit completely, then run this again. Nothing has been changed." 'Red'
    exit 1
}

if (-not (Test-Path $PayloadDir) -or -not (Test-Path $Manifest)) {
    Say ""
    Say "This script must sit beside the 'Cda' folder and 'Cda.Revit.Addin.addin'." 'Red'
    Say "If you unzipped only part of the package, unzip all of it and try again." 'Red'
    exit 1
}

# A warning rather than a failure: Revit may be installed somewhere non-standard, and
# being wrong about that should not stop someone who knows their own machine.
$revitExe = Join-Path $env:ProgramW6432 "Autodesk\Revit $RevitVersion\Revit.exe"
if (-not (Test-Path $revitExe)) {
    Say ""
    Say "WARNING: Revit $RevitVersion was not found at the usual location." 'Yellow'
    Say "         These tools are built for Revit $RevitVersion and will not load in 2026 or earlier." 'Yellow'
    Say "         Continuing anyway - installing does no harm if you do not have it." 'Yellow'
}

# ---------------------------------------------------------------- unblock

# THE MOST COMMON REASON A HAND-COPIED ADD-IN "DOES NOTHING".
#
# Windows marks files that arrived from a zip, an email or a network share as coming from
# another computer. .NET then refuses to load them, and Revit reports nothing at all - no
# error, no ribbon tab, no clue. Clearing the mark is what makes this install work where a
# manual copy silently does not.
Say ""
Say "Clearing the 'downloaded from another computer' mark..."
Get-ChildItem -Path $Here -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- copy

$target = Join-Path $AddinsDir 'Cda'

Say "Installing to $AddinsDir"

New-Item -ItemType Directory -Force -Path $AddinsDir | Out-Null

# Replace rather than merge: a leftover DLL from an older build that no longer ships is
# still loadable, and diagnosing a mix of two versions is worse than a clean copy.
if (Test-Path $target) { Remove-Item $target -Recurse -Force }

Copy-Item $PayloadDir -Destination $AddinsDir -Recurse -Force
Copy-Item $Manifest   -Destination $AddinsDir -Force

# ---------------------------------------------------------------- verify

$deployedDll = Join-Path $target 'Cda.Revit.Addin.dll'

if (-not (Test-Path $deployedDll)) {
    Say ""
    Say "INSTALL FAILED - the add-in DLL is not where it should be." 'Red'
    exit 1
}

$stamp = (Get-Item $deployedDll).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')

Say ""
Say "Installed." 'Green'
Say ""
Say "  Build      $stamp"
Say "  Add-in     $target"
Say "  Manifest   $(Join-Path $AddinsDir 'Cda.Revit.Addin.addin')"
Say ""
Say "Start Revit $RevitVersion and look for the DKSI tab on the ribbon."
Say "Any DKSI dialog shows the build stamp in its footer - it should read $stamp."
Say ""
