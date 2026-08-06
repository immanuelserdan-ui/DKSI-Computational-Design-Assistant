# DKSI Revit Tools - uninstaller.
#
#   powershell -ExecutionPolicy Bypass -File .\Uninstall.ps1
#
# Removes the add-in from your Revit 2027 add-ins folder. It does NOT touch your models,
# and it does NOT remove the parameters or schedules the tools created in a project -
# those are part of the model now and stay there, which is what you want.

$ErrorActionPreference = 'Stop'

$RevitVersion = '2027'
$AddinsDir    = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$Target       = Join-Path $AddinsDir 'Cda'
$Manifest     = Join-Path $AddinsDir 'Cda.Revit.Addin.addin'

function Say([string]$text, [string]$colour = 'Gray') { Write-Host $text -ForegroundColor $colour }

Say ""
Say "DKSI Revit Tools - uninstaller" 'Cyan'

$revit = Get-Process revit -ErrorAction SilentlyContinue
if ($revit) {
    Say ""
    Say "Revit is running (PID $($revit.Id -join ', ')). Close it and run this again." 'Red'
    exit 1
}

$removed = 0

if (Test-Path $Target)   { Remove-Item $Target -Recurse -Force; Say "Removed $Target"; $removed++ }
if (Test-Path $Manifest) { Remove-Item $Manifest -Force;        Say "Removed $Manifest"; $removed++ }

Say ""

if ($removed -eq 0) {
    Say "Nothing to remove - it was not installed for this user." 'Yellow'
} else {
    Say "Uninstalled. The DKSI tab will be gone next time Revit starts." 'Green'
}

Say ""
Say "Left in place on purpose:"
Say "  $env:LOCALAPPDATA\Cda\RevitAddin\   logs, reports and settings"
Say "Delete that folder too if you want no trace left."
Say ""
