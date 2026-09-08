# Builds the office-wide suite installer: both MSIs plus the Burn bundle that chains them.
#
#   powershell -ExecutionPolicy Bypass -File .\tools\build-suite.ps1
#
# Output: dist\DKSI-Revit-Suite-<version>.exe
#
# Requires the WiX toolset and two extensions, once per machine:
#   dotnet tool install --global wix --version 6.*
#   wix extension add -g WixToolset.UI.wixext/6.0.2
#   wix extension add -g WixToolset.BootstrapperApplications.wixext/6.0.2
#
# THIS BUILDS THE MACHINE-WIDE VARIANT, NOT THE PER-USER ONE. The two are different packages
# with different UpgradeCodes and they must not both be installed on one workstation - Revit
# does not support reading one add-in from two manifests. Use tools\build-installer.ps1 for the
# per-user MSI that people install themselves; use this one for what IT pushes.

param(
    # Same date-derived scheme as build-installer.ps1: MSI version fields are numeric and
    # capped, so 1.0.<yy><day of year>. Increasing across days is what makes Windows treat a
    # later build as an upgrade rather than a second copy.
    [string]$Version = ('1.0.{0}{1}' -f (Get-Date).ToString('yy'), (Get-Date).DayOfYear),

    # The takeoff MSI to chain. Built separately by tools\build-painttakeoff.ps1, because it
    # repackages a third-party payload rather than compiling anything.
    [string]$PaintTakeoffMsi
)

$ErrorActionPreference = 'Stop'

$Root         = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Project      = Join-Path $Root 'src\Cda.Revit.Addin\Cda.Revit.Addin.csproj'
$Output       = Join-Path $Root 'src\Cda.Revit.Addin\bin\Release'
$InstallerDir = Join-Path $Root 'installer'
$Dist         = Join-Path $Root 'dist'
$Wix          = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'

# NEWEST BY VERSION, NEVER A HARDCODED ONE - same reason as build-inno.ps1. The default here
# was 'PaintTakeoff-1.0.3.msi', which predates the DLL override in installer\PaintTakeoff, so
# anyone building the suite without passing -PaintTakeoffMsi chained a takeoff missing the
# split-face carrier fix and had no way to tell from the output.
if (-not $PaintTakeoffMsi) {
    $PaintTakeoffMsi = Get-ChildItem -Path $Dist -Filter 'PaintTakeoff-*.msi' -ErrorAction SilentlyContinue |
        ForEach-Object {
            $parsed = $null
            if ([version]::TryParse(($_.BaseName -replace '^PaintTakeoff-',''), [ref]$parsed)) {
                [pscustomobject]@{ Path = $_.FullName; Version = $parsed }
            }
        } |
        Sort-Object Version -Descending |
        Select-Object -First 1 -ExpandProperty Path
}

function Say([string]$text, [string]$colour = 'Gray') { Write-Host $text -ForegroundColor $colour }

Say ""
Say "Building DKSI Revit Suite $Version" 'Cyan'

if (-not (Test-Path $Wix)) {
    Say ""
    Say "WiX not found at $Wix" 'Red'
    Say "Install it with:  dotnet tool install --global wix --version 6.*" 'Red'
    exit 1
}

if (-not (Test-Path $PaintTakeoffMsi)) {
    Say ""
    Say "Painted Material Takeoff MSI not found: $PaintTakeoffMsi" 'Red'
    Say "Build it first:  .\tools\build-painttakeoff.ps1" 'Red'
    Say "Or pass one with -PaintTakeoffMsi." 'Red'
    exit 1
}

# ---------------------------------------------------------------- build

# DeployToRevit=false: building an installer must not also reinstall onto this machine.
#
# FileVersion MUST INCREASE WITH EVERY RELEASE. The assembly version is a constant 1.0.0, so
# without this every DLL ever built looks identical to Windows Installer, and an upgrade
# silently keeps the OLD add-in while reporting success.
Say ""
Say "Building Release..."

& dotnet build $Project -c Release -p:DeployToRevit=false -p:FileVersion="$Version.0" | Out-String | Write-Host
if ($LASTEXITCODE -ne 0) { Say "Build failed - nothing packaged." 'Red'; exit 1 }

$dll = Join-Path $Output 'Cda.Revit.Addin.dll'
if (-not (Test-Path $dll)) { Say "Built, but $dll is missing." 'Red'; exit 1 }

# ---------------------------------------------------------------- harvest

# The .addin manifest is referenced from the payload folder as ..\Cda.Revit.Addin.addin, so it
# is staged one level above the binaries - mirroring where it ends up on disk. The SAME
# unmodified manifest serves both the per-user and machine-wide packages, because the <Assembly>
# path inside it is relative to the manifest's own folder.
$stage   = Join-Path $Dist 'suite-staging'
$payload = Join-Path $stage 'Cda'

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $payload | Out-Null

foreach ($pattern in '*.dll', '*.pdb', '*.json') {
    Get-ChildItem -Path $Output -Filter $pattern -File | Copy-Item -Destination $payload -Force
}

Copy-Item (Join-Path $Root 'src\Cda.Revit.Addin\Cda.Revit.Addin.addin') -Destination $stage -Force

# ---------------------------------------------------------------- link the all-users MSI

New-Item -ItemType Directory -Force -Path $Dist | Out-Null
$dksiMsi = Join-Path $Dist "DKSI-Revit-Tools-AllUsers-$Version.msi"
if (Test-Path $dksiMsi) { Remove-Item $dksiMsi -Force }

Say ""
Say "Linking machine-wide MSI..."

& $Wix build (Join-Path $InstallerDir 'DKSI-Revit-Tools-AllUsers.wxs') `
    -arch x64 `
    -ext WixToolset.UI.wixext `
    -d "ProductVersion=$Version" `
    -d "PayloadDir=$payload" `
    -d "InstallerDir=$InstallerDir" `
    -d "NoticeRtf=$(Join-Path $InstallerDir 'Notice.rtf')" `
    -o $dksiMsi

if ($LASTEXITCODE -ne 0) { Say "wix build failed on the MSI." 'Red'; exit 1 }

# ---------------------------------------------------------------- link the bundle

$bundle = Join-Path $Dist "DKSI-Revit-Suite-$Version.exe"
if (Test-Path $bundle) { Remove-Item $bundle -Force }

Say "Linking suite bundle..."

& $Wix build (Join-Path $InstallerDir 'DKSI-Suite.wxs') `
    -arch x64 `
    -ext WixToolset.BootstrapperApplications.wixext `
    -d "ProductVersion=$Version" `
    -d "DksiMsi=$dksiMsi" `
    -d "PaintTakeoffMsi=$PaintTakeoffMsi" `
    -d "NoticeRtf=$(Join-Path $InstallerDir 'Notice.rtf')" `
    -o $bundle

if ($LASTEXITCODE -ne 0) { Say "wix build failed on the bundle." 'Red'; exit 1 }

Remove-Item $stage -Recurse -Force

$size = [math]::Round((Get-Item $bundle).Length / 1MB, 2)

Say ""
Say "Built." 'Green'
Say ""
Say "  $bundle"
Say "  $size MB, suite version $Version"
Say ""
Say "  chains:  $(Split-Path -Leaf $dksiMsi)"
Say "           $(Split-Path -Leaf $PaintTakeoffMsi)"
Say ""
Say "Installs to:  C:\Program Files\Autodesk\Revit\Addins\2027\"
Say "Needs administrator rights. Revit must be closed."
Say ""
Say "Interactive:  `"$bundle`""
Say "Silent (IT):  `"$bundle`" /quiet /norestart /log C:\Temp\dksi.log"
Say "Progress bar: `"$bundle`" /passive /norestart"
Say "Uninstall:    Apps & features, or  `"$bundle`" /uninstall /quiet"
Say ""
Say "NOT /VERYSILENT - that is an Inno Setup switch. Burn uses /quiet." 'Yellow'
Say "No certificate step needed - both shipped assemblies are unsigned." 'Gray'
Say ""
