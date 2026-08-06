# Builds the per-user MSI.
#
#   powershell -ExecutionPolicy Bypass -File .\tools\build-installer.ps1
#
# Output: dist\DKSI-Revit-Tools-<version>.msi
#
# Requires the WiX toolset, once per machine:
#   dotnet tool install --global wix --version 6.*
#   wix extension add -g WixToolset.UI.wixext/6.0.2

param(
    # MSI version fields are numeric and capped (255.255.65535), so this is derived from
    # the date rather than from the assembly version: 1.0.<yy><day of year>. Increasing
    # across days is what makes Windows treat a later MSI as an upgrade rather than a
    # second copy. Same-day rebuilds land on the same number, which the package handles
    # with AllowSameVersionUpgrades.
    [string]$Version = ('1.0.{0}{1}' -f (Get-Date).ToString('yy'), (Get-Date).DayOfYear)
)

$ErrorActionPreference = 'Stop'

$Root         = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Project      = Join-Path $Root 'src\Cda.Revit.Addin\Cda.Revit.Addin.csproj'
$Output       = Join-Path $Root 'src\Cda.Revit.Addin\bin\Release'
$InstallerDir = Join-Path $Root 'installer'
$Dist         = Join-Path $Root 'dist'
$Wxs          = Join-Path $InstallerDir 'DKSI-Revit-Tools.wxs'
$Wix          = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'

function Say([string]$text, [string]$colour = 'Gray') { Write-Host $text -ForegroundColor $colour }

Say ""
Say "Building DKSI Revit Tools installer $Version" 'Cyan'

if (-not (Test-Path $Wix)) {
    Say ""
    Say "WiX not found at $Wix" 'Red'
    Say "Install it with:  dotnet tool install --global wix --version 6.*" 'Red'
    exit 1
}

# ---------------------------------------------------------------- build

# DeployToRevit=false: building an installer must not also reinstall onto this machine.
Say ""
Say "Building Release..."

# FileVersion MUST INCREASE WITH EVERY RELEASE, and this is the line that makes it.
#
# Windows Installer compares file versions to decide whether to overwrite, and treats
# "equal version" as "leave the existing file alone". The assembly version is a constant
# 1.0.0 in Directory.Build.props, so without this every DLL ever built looked identical to
# Windows and an upgrade silently kept the OLD add-in while reporting success. That was
# observed, not theorised: the first install test left a DLL from an earlier build in place
# and the MSI still exited 0.
#
# FileVersion only affects the version resource Windows reads. AssemblyVersion is left
# alone, so nothing about how Revit loads the add-in changes.
& dotnet build $Project -c Release -p:DeployToRevit=false -p:FileVersion="$Version.0" | Out-String | Write-Host
if ($LASTEXITCODE -ne 0) { Say "Build failed - nothing packaged." 'Red'; exit 1 }

$dll = Join-Path $Output 'Cda.Revit.Addin.dll'
if (-not (Test-Path $dll)) { Say "Built, but $dll is missing." 'Red'; exit 1 }

# ---------------------------------------------------------------- harvest

# The .addin manifest is referenced from the payload folder as ..\Cda.Revit.Addin.addin,
# so it is staged one level above the binaries - mirroring where it ends up on disk.
$stage   = Join-Path $Dist 'msi-staging'
$payload = Join-Path $stage 'Cda'

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $payload | Out-Null

foreach ($pattern in '*.dll', '*.pdb', '*.json') {
    Get-ChildItem -Path $Output -Filter $pattern -File | Copy-Item -Destination $payload -Force
}

Copy-Item (Join-Path $Root 'src\Cda.Revit.Addin\Cda.Revit.Addin.addin') -Destination $stage -Force

# ---------------------------------------------------------------- link

New-Item -ItemType Directory -Force -Path $Dist | Out-Null
$msi = Join-Path $Dist "DKSI-Revit-Tools-$Version.msi"
if (Test-Path $msi) { Remove-Item $msi -Force }

Say ""
Say "Linking MSI..."

& $Wix build $Wxs `
    -arch x64 `
    -ext WixToolset.UI.wixext `
    -d "ProductVersion=$Version" `
    -d "PayloadDir=$payload" `
    -d "InstallerDir=$InstallerDir" `
    -d "NoticeRtf=$(Join-Path $InstallerDir 'Notice.rtf')" `
    -o $msi

if ($LASTEXITCODE -ne 0) { Say "wix build failed." 'Red'; exit 1 }

Remove-Item $stage -Recurse -Force

$size = [math]::Round((Get-Item $msi).Length / 1MB, 2)
$stamp = (Get-Item $dll).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')

Say ""
Say "Built." 'Green'
Say ""
Say "  $msi"
Say "  $size MB, add-in build $stamp, product version $Version"
Say ""
Say "Install:    double-click, or  msiexec /i `"$msi`""
Say "With time tracking on:        msiexec /i `"$msi`" TIMETRACKING=1"
Say "Silent:                       msiexec /i `"$msi`" /qn"
Say "Uninstall:                    Apps & features, or  msiexec /x `"$msi`""
Say ""
