# Installs the rebuilt Painted Material Takeoff over the copy in Program Files.
#
#   Right-click > Run with PowerShell, or from an ELEVATED prompt:
#     powershell -ExecutionPolicy Bypass -File .\Install-Fix.ps1
#
# WHY THIS NEEDS ADMIN: the product installs to Program Files, which a normal user cannot
# write to. Nothing here touches anything else.
#
# WHAT IT DOES
#   1. Refuses to run while Revit is open - the DLL is loaded and the copy would half-finish.
#   2. Backs up the DLL it is about to replace, next to this script, stamped with the time.
#   3. Copies the rebuilt DLL in.
#   4. Removes the side-by-side DIAG manifest, if present. Leaving it would register two
#      assemblies that both carry the fix and both delete each other's carrier elements on
#      every run, which looks exactly like the takeoff losing rows.
#   5. Verifies by size and timestamp and says which build is now installed.
#
# TO ROLL BACK: run this again with -Restore <path to a backup file>.

param(
    [string]$Restore
)

$ErrorActionPreference = 'Stop'

$Here      = Split-Path -Parent $MyInvocation.MyCommand.Path
$Source    = Join-Path $Here 'PaintedMaterialTakeoff.dll'
$Installed = 'C:\Program Files\Autodesk\Revit\Addins\2027\PaintedMaterialTakeoff\PaintedMaterialTakeoff.dll'
$DiagAddin = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2027\PaintedMaterialTakeoffDiag.addin'

function Say([string]$text, [string]$colour = 'Gray') { Write-Host $text -ForegroundColor $colour }

Say ""
Say "Painted Material Takeoff - install rebuilt assembly" 'Cyan'
Say "---------------------------------------------------" 'Cyan'

# ---------------------------------------------------------------- pre-flight

$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)

if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Say ""
    Say "This needs to run as Administrator - the target is under Program Files." 'Red'
    Say "Nothing has been changed." 'Red'
    exit 1
}

$revit = Get-Process revit -ErrorAction SilentlyContinue
if ($revit) {
    Say ""
    Say "Revit is running (PID $($revit.Id -join ', '))." 'Red'
    Say "Close it completely, then run this again. Nothing has been changed." 'Red'
    exit 1
}

if (-not (Test-Path $Installed)) {
    Say ""
    Say "Not found: $Installed" 'Red'
    Say "The product does not appear to be installed for Revit 2027." 'Red'
    exit 1
}

# ---------------------------------------------------------------- restore mode

if ($Restore) {
    if (-not (Test-Path $Restore)) { Say "Backup not found: $Restore" 'Red'; exit 1 }

    Copy-Item $Restore $Installed -Force
    Say ""
    Say "Restored from $Restore" 'Green'
    Say ("Installed build is now {0:yyyy-MM-dd HH:mm:ss}" -f (Get-Item $Installed).LastWriteTime)
    exit 0
}

if (-not (Test-Path $Source)) {
    Say ""
    Say "Not found: $Source" 'Red'
    Say "Build it first:  dotnet build PaintedMaterialTakeoff.rebuild.csproj -c Release" 'Red'
    exit 1
}

# ---------------------------------------------------------------- install

$stamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = Join-Path $Here "PaintedMaterialTakeoff.installed-backup-$stamp.dll"

Copy-Item $Installed $backup -Force
Say ""
Say "Backed up the current DLL to:"
Say "  $backup"

Copy-Item $Source $Installed -Force

if (Test-Path $DiagAddin) {
    Remove-Item $DiagAddin -Force
    Say ""
    Say "Removed the side-by-side DIAG manifest - the fix is in the product now." 'Yellow'
}

# ---------------------------------------------------------------- verify

$now = Get-Item $Installed
$src = Get-Item $Source

Say ""
if ($now.Length -eq $src.Length) {
    Say ("Installed: build {0:yyyy-MM-dd HH:mm:ss}, {1} bytes." -f $now.LastWriteTime, $now.Length) 'Green'
} else {
    Say "The copy landed but the sizes differ - installed $($now.Length), source $($src.Length)." 'Red'
    Say "Restore with:  .\Install-Fix.ps1 -Restore `"$backup`"" 'Red'
    exit 1
}

Say ""
Say "Start Revit and re-run the Paint Takeoff. A wall face with Split Face regions"
Say "should now produce one row per region, labelled R1 / R2, whose areas sum to what"
Say "the single row carried before."
Say ""
