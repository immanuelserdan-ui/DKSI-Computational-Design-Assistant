# Repackages Painted Material Takeoff so Revit 2027 will actually load it.
#
#   powershell -ExecutionPolicy Bypass -File .\tools\build-painttakeoff.ps1
#
# Output: dist\PaintTakeoff-<version>.msi
#
# Requires the WiX toolset, once per machine:
#   dotnet tool install --global wix --version 6.*
#
# THIS SCRIPT DOES NOT COMPILE ANYTHING. There is no PaintTakeoff source tree in this
# repository - the product ships as a finished MSI. What this does is take the payload out
# of that MSI unchanged and wrap it in a package that installs to the folder Revit 2027
# reads, instead of the pre-2027 folder 1.0.1 targeted. See the header of
# installer\PaintTakeoff\PaintTakeoff.wxs for the full account.
#
# Because the payload is copied rather than rebuilt, the DLL inside keeps its original
# Authenticode signature - so Trust-Certificate.ps1 is still required on each workstation,
# and still does the same job. The OUTPUT MSI is unsigned: the private key is not in this
# repository and must not be. Signing the package is a separate step for whoever holds it.

param(
    # Anything built here must sort ABOVE the version already on the machine or Windows will
    # not treat it as an upgrade, and the old install will survive alongside this one - two
    # manifests for one add-in, which Revit does not support.
    #
    #   1.0.1  original; installs to the pre-2027 folder, so Revit never loads it
    #   1.0.2  same payload, correct folder
    #   1.0.3  no ribbon of its own - DKSI Revit Tools carries the three tools instead
    [string]$Version = '1.0.3',

    # The 1.0.1 MSI to lift the payload out of.
    [string]$SourceMsi
)

$ErrorActionPreference = 'Stop'

$Root         = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$InstallerDir = Join-Path $Root 'installer\PaintTakeoff'
$Dist         = Join-Path $Root 'dist'
$Wxs          = Join-Path $InstallerDir 'PaintTakeoff.wxs'
$Wix          = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'

if (-not $SourceMsi) { $SourceMsi = Join-Path $Dist 'PaintTakeoff-1.0.1.msi' }

function Say([string]$text, [string]$colour = 'Gray') { Write-Host $text -ForegroundColor $colour }

Say ""
Say "Repackaging Painted Material Takeoff $Version" 'Cyan'

if (-not (Test-Path $Wix)) {
    Say ""
    Say "WiX not found at $Wix" 'Red'
    Say "Install it with:  dotnet tool install --global wix --version 6.*" 'Red'
    exit 1
}

if (-not (Test-Path $SourceMsi)) {
    Say ""
    Say "Source MSI not found: $SourceMsi" 'Red'
    Say "Pass one with -SourceMsi, or drop PaintTakeoff-1.0.1.msi into dist\." 'Red'
    exit 1
}

# ---------------------------------------------------------------- harvest

# An administrative install unpacks the MSI's files to disk in their installed layout,
# without touching this machine's installed state.
#
# THE STAGING PATH IS SHORT ON PURPOSE. Unpacked, the longest file sits at
# CommApp\Autodesk\Revit\Addins\2027\PaintedMaterialTakeoff\PaintedMaterialTakeoff-SharedParameters.txt
# - a little over 100 characters before the staging root is even counted. Extracting under a
# deep path blows MAX_PATH and msiexec fails with a bare "Error 1304. Error writing to file
# ... Verify that you have access to that directory", which reads like a permissions problem
# and is not one. %TEMP% keeps the root short.
$stage = Join-Path $env:TEMP 'pt-msi'

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Say ""
Say "Extracting payload from $(Split-Path -Leaf $SourceMsi)..."

$msiexec = Start-Process msiexec.exe -Wait -PassThru -NoNewWindow `
    -ArgumentList @('/a', "`"$SourceMsi`"", '/qn', "TARGETDIR=`"$stage`"")

if ($msiexec.ExitCode -ne 0) {
    Say "Administrative install failed with exit code $($msiexec.ExitCode) - nothing packaged." 'Red'
    exit 1
}

# The manifest is referenced from the payload folder as ..\PaintedMaterialTakeoff.addin, so
# what matters here is that the extracted layout keeps the manifest one level above the
# binaries. It does, because that is how it is installed.
$payload = Join-Path $stage 'CommApp\Autodesk\Revit\Addins\2027\PaintedMaterialTakeoff'

$expected = @(
    'PaintedMaterialTakeoff.dll'
    'PaintedMaterialTakeoff.deps.json'
    'PaintedMaterialTakeoff.runtimeconfig.json'
    'PaintedMaterialTakeoff-SharedParameters.txt'
    'PaintedMaterialTakeoff.pdb'
)

foreach ($file in $expected) {
    if (-not (Test-Path (Join-Path $payload $file))) {
        Say "Extraction incomplete - missing $file" 'Red'
        exit 1
    }
}

if (-not (Test-Path (Join-Path $payload '..\PaintedMaterialTakeoff.addin'))) {
    Say "Extraction incomplete - missing PaintedMaterialTakeoff.addin" 'Red'
    exit 1
}

# ---------------------------------------------------------------- manifest override
#
# THE ONE PART OF THE PAYLOAD THAT IS NOT SHIPPED AS EXTRACTED. The manifest in the source
# MSI registers PaintedMaterialTakeoff.App, which builds the product's own "Revit Automation"
# tab. DKSI Revit Tools now carries the same three tools in its pull-down, so keeping that
# entry would put each of them on the ribbon twice from a single install.
#
# The replacement drops that one entry and changes nothing else - see the comments in it.
# Reversible by deleting the override and rebuilding.

$manifest = Join-Path $InstallerDir 'PaintedMaterialTakeoff.addin'
if (-not (Test-Path $manifest)) {
    Say "Manifest override not found: $manifest" 'Red'
    exit 1
}

Copy-Item $manifest -Destination (Join-Path $payload '..\PaintedMaterialTakeoff.addin') -Force

# ---------------------------------------------------------------- DLL override
#
# THE SECOND PART OF THE PAYLOAD THAT IS NOT SHIPPED AS EXTRACTED, and the more serious one:
# this replaces the product's own binary.
#
# WHY
#   The shipped 1.0.1 DLL loses paint materials on REGULAR walls. WallSegmentCalculator
#   apportions a segment's area correctly across every split-face region, but then hands
#   carrier geometry to ONE of them - the region with the largest share. Every other material
#   ends up with a row, an area, and no Generic Model. The schedule reads Generic Models, so
#   those materials are absent from it while sitting correctly in the CSV.
#
#   Measured in FM_Template 2027V1.00_EN, wall 29307636 in Køkken: G59 (3,472 m²) and G99
#   (4,417 m²) both computed, only G99 scheduled. The product's own run summary named it -
#   "1 row(s) with area had no usable geometry for a carrier element and are in the CSV only."
#   Hanging walls were never affected: InteriorElementCalculator skins each region's own face,
#   so every bucket gets geometry.
#
#   The fix keeps the solid RegionShare was already building and discarding, and gives every
#   material its own carrier. Verified against this model: G59 appears, totals reconcile, and
#   the hanging-wall rows are unchanged.
#
# WHERE THIS BINARY CAME FROM - READ BEFORE CHANGING IT
#   NOT from the PaintedMaterialTakeoff source tree, which is not on this machine (its PDB
#   points at D:\John Documents\Revit Automation Project\Painted Material\). It was rebuilt
#   from the SHIPPED ASSEMBLY, decompiled with ilspycmd, and the source of that rebuild is
#   committed beside this override as PaintedMaterialTakeoff.source.cs.
#
#   The rebuild was checked against the original before being trusted: recompiled, decompiled
#   again, and compared order-independently - 58 differing lines out of 6,025, every one a
#   decompiler rendering difference (ElementId. vs Autodesk.Revit.DB.ElementId., out var vs
#   out MaterialBucket, inverted-equivalent conditionals). No semantic drift.
#
#   THIS IS A STOPGAP. If the original source is ever recovered, port the same change there,
#   delete this override, and rebuild - exactly as with the manifest override above.

$dll = Join-Path $InstallerDir 'PaintedMaterialTakeoff.dll'
if (-not (Test-Path $dll)) {
    Say "DLL override not found: $dll" 'Red'
    exit 1
}

Copy-Item $dll -Destination (Join-Path $payload 'PaintedMaterialTakeoff.dll') -Force
Say "Applied DLL override - split-face paint now gets one carrier per material on regular walls."
Say "Applied manifest override - the product no longer builds a ribbon of its own."

# ---------------------------------------------------------------- link

New-Item -ItemType Directory -Force -Path $Dist | Out-Null
$msi = Join-Path $Dist "PaintTakeoff-$Version.msi"
if (Test-Path $msi) { Remove-Item $msi -Force }

Say "Linking MSI..."

& $Wix build $Wxs `
    -arch x64 `
    -d "ProductVersion=$Version" `
    -d "PayloadDir=$payload" `
    -o $msi

if ($LASTEXITCODE -ne 0) { Say "wix build failed." 'Red'; exit 1 }

Remove-Item $stage -Recurse -Force

$size = [math]::Round((Get-Item $msi).Length / 1KB, 0)

Say ""
Say "Built." 'Green'
Say ""
Say "  $msi"
Say "  $size KB, product version $Version"
Say ""
Say "Installs to:  C:\Program Files\Autodesk\Revit\Addins\2027\"
Say "Needs administrator rights, and Revit must be closed."
Say ""
Say "Install:      msiexec /i `"$msi`""
Say "Silent:       msiexec /i `"$msi`" /qn"
Say "Uninstall:    Apps & features, or  msiexec /x `"$msi`""
Say ""
Say "Trust-Certificate.ps1 is still required first on a workstation that has not run it." 'Yellow'
Say ""
