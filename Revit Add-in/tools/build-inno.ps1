# Builds the Inno Setup installer.
#
#   powershell -ExecutionPolicy Bypass -File .\tools\build-inno.ps1
#
# Output: dist\DKSI-Revit-Tools-Setup-<version>.exe
#
# Requires Inno Setup 6, once per machine:
#   winget install -e --id JRSoftware.InnoSetup
#
# THIS PRODUCES THE PER-USER INSTALLER. It writes to %AppData% and needs no
# admin, which is right for someone installing it themselves and WRONG for a
# push from SCCM or Intune - those run as SYSTEM and %AppData% resolves to
# SYSTEM's profile. For one-push-per-machine use tools\build-suite.ps1, which
# builds the per-machine MSI and bundle. See the header of the .iss.

param(
    # MSI-style date version, kept identical to the other packages so a machine
    # can be described by one number whichever installer put it there.
    [string]$Version = ('1.0.{0}{1}' -f (Get-Date).ToString('yy'), (Get-Date).DayOfYear),

    # Include time tracking switched on. Off by default, deliberately - see the
    # [Files] section of the .iss.
    [switch]$TimeTrackingOn
)

$ErrorActionPreference = 'Stop'

$Root         = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Project      = Join-Path $Root 'src\Cda.Revit.Addin\Cda.Revit.Addin.csproj'
$Output       = Join-Path $Root 'src\Cda.Revit.Addin\bin\Release'
$InstallerDir = Join-Path $Root 'installer'
$Dist         = Join-Path $Root 'dist'
$Iss          = Join-Path $InstallerDir 'DKSI-Revit-Tools.iss'

function Say([string]$text, [string]$colour = 'Gray') { Write-Host $text -ForegroundColor $colour }

# Three locations, because winget installs Inno Setup PER-USER by default and a
# script that only looks in Program Files reports "not installed" on a machine
# that has it. The manual installer uses the Program Files paths.
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }

Say ""
Say "Building $($(if ($TimeTrackingOn) { 'DKSI Revit Tools (time tracking ON)' } else { 'DKSI Revit Tools' })) $Version" 'Cyan'

if (-not $iscc) {
    Say ""
    Say "Inno Setup 6 not found." 'Red'
    Say "Install it with:  winget install -e --id JRSoftware.InnoSetup" 'Red'
    exit 1
}

# ---------------------------------------------------------------- build

# DeployToRevit=false: building an installer must not also reinstall onto this
# machine. FileVersion must increase every release or Windows treats two
# different builds as the same file - the assembly version is a constant 1.0.0.
Say ""
Say "Building Release..."

& dotnet build $Project -c Release -p:DeployToRevit=false -p:FileVersion="$Version.0" | Out-String | Write-Host
if ($LASTEXITCODE -ne 0) { Say "Build failed - nothing packaged." 'Red'; exit 1 }

# The .iss lists its payload file by file rather than by wildcard, so a missing
# one is a compile error there rather than a silently thinner installer. This
# check just fails earlier and more clearly.
$required = @(
    'Cda.Revit.Addin.dll'
    'Cda.Revit.Addin.deps.json'
    'Cda.Revit.Addin.runtimeconfig.json'
    'Cda.Revit.Addin.pdb'
)
foreach ($f in $required) {
    if (-not (Test-Path (Join-Path $Output $f))) {
        Say "Built, but $f is missing from $Output" 'Red'
        exit 1
    }
}

# ---------------------------------------------------------------- compile

New-Item -ItemType Directory -Force -Path $Dist | Out-Null

Say ""
Say "Compiling installer..."

& $iscc "/DAppVersion=$Version" "/DPayloadDir=$Output" $Iss | Out-String | Write-Host
if ($LASTEXITCODE -ne 0) { Say "ISCC failed." 'Red'; exit 1 }

$exe = Join-Path $Dist "DKSI-Revit-Tools-Setup-$Version.exe"
if (-not (Test-Path $exe)) { Say "ISCC reported success but $exe is missing." 'Red'; exit 1 }

$size = [math]::Round((Get-Item $exe).Length / 1KB, 0)

Say ""
Say "Built." 'Green'
Say ""
Say "  $exe"
Say "  $size KB, version $Version"
Say ""
Say "Installs to:  %AppData%\Autodesk\Revit\Addins\2027"
Say "No administrator rights needed. Revit must be closed."
Say ""
Say "Interactive:  `"$exe`""
Say "Silent (IT):  `"$exe`" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"%TEMP%\dksi.log`""
Say "With time tracking on:  add  /TIMETRACKING=1"
Say "Uninstall silently:     `"%AppData%\Autodesk\Revit\Addins\2027\Cda\unins000.exe`" /VERYSILENT"
Say ""
Say "PER-USER. A push from SCCM or Intune runs as SYSTEM, and %AppData% then" 'Yellow'
Say "resolves to SYSTEM's profile - success reported, no user ever gets it." 'Yellow'
Say "Deploy in user context, or use build-suite.ps1 for the per-machine MSI." 'Yellow'
Say ""
