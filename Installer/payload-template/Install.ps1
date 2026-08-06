<#
.SYNOPSIS
    Installs the DKSI Revit add-in suite.

.DESCRIPTION
    Copies both add-ins and their manifests into Revit's add-in folder and verifies the
    result. Run Install.cmd instead of this file if you would rather double-click.

.PARAMETER AllUsers
    Install for everyone on this machine instead of just you. Requires an elevated
    prompt, because it writes into Program Files.

.PARAMETER RevitVersion
    Install for one specific release, e.g. 2027. Omit to install for every Revit
    version found on the machine that the add-ins support.

.EXAMPLE
    .\Install.ps1
    Installs for the current user only. No admin rights needed.

.EXAMPLE
    .\Install.ps1 -AllUsers
    Installs for everyone. Must be run from an elevated PowerShell prompt.
#>
[CmdletBinding()]
param(
    [switch] $AllUsers,
    [string] $RevitVersion,
    [switch] $Quiet
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$Payload = Join-Path $Root 'payload'
$LogFile = Join-Path $env:TEMP "dksi-revit-install-$(Get-Date -Format yyyyMMdd-HHmmss).log"

# Versions this package contains binaries for. Installing into a Revit release the
# add-ins were not built against loads a .NET 10 assembly into a .NET 8 host, which
# fails with a type-load error that looks like a corrupt install rather than a version
# mismatch — so it is refused up front instead.
$SupportedVersions = @('2027')

function Write-Log {
    param([string] $Message, [string] $Level = 'INFO')
    $line = "{0} [{1}] {2}" -f (Get-Date -Format 'HH:mm:ss'), $Level, $Message
    Add-Content -Path $LogFile -Value $line -Encoding UTF8
    if ($Quiet -and $Level -eq 'INFO') { return }
    switch ($Level) {
        'ERROR' { Write-Host $Message -ForegroundColor Red }
        'WARN'  { Write-Host $Message -ForegroundColor Yellow }
        'OK'    { Write-Host $Message -ForegroundColor Green }
        default { Write-Host $Message }
    }
}

function Fail {
    param([string] $Message)
    Write-Log $Message 'ERROR'
    Write-Log "Full log: $LogFile"
    exit 1
}

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ---------------------------------------------------------------- preflight

Write-Host ''
Write-Host '  DKSI Revit Tools + Vision Modeler' -ForegroundColor Cyan
Write-Host '  ---------------------------------' -ForegroundColor Cyan
Write-Host ''

Write-Log "Installer started. Package: $Root"

if (-not (Test-Path $Payload)) {
    Fail "This package looks incomplete - the 'payload' folder is missing. Extract the whole ZIP and run Install.cmd from the extracted folder, not from inside the ZIP viewer."
}

# THE most common cause of "I installed it and nothing appeared".
#
# Windows tags every file that arrived from the internet, an email attachment or a
# network share with a Zone.Identifier stream. .NET refuses to load a tagged assembly,
# and Revit reports nothing at all - the add-in is simply absent, with no error. Since
# this package will be sent round by email or Teams, unblocking is not optional.
Write-Log 'Removing the "downloaded from the internet" mark from the package files...'
Get-ChildItem -Path $Root -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

# Revit holds its add-in DLLs open for the life of the process, so a copy over a
# running Revit fails halfway and leaves a half-installed suite.
$revit = Get-Process -Name 'Revit' -ErrorAction SilentlyContinue
if ($revit) {
    Fail ("Revit is running (process {0}). Close Revit completely, then run this installer again. " -f ($revit.Id -join ', ') +
          "Revit keeps its add-in files locked while it is open, so installing now would leave a broken install.")
}

# ---------------------------------------------------------------- targets

$installed = @()
foreach ($version in $SupportedVersions) {
    $apiDll = Join-Path ${env:ProgramW6432} "Autodesk\Revit $version\RevitAPI.dll"
    if (Test-Path $apiDll) { $installed += $version }
}

if ($RevitVersion) {
    if ($SupportedVersions -notcontains $RevitVersion) {
        Fail "This package contains binaries for Revit $($SupportedVersions -join ', ') only, not $RevitVersion."
    }
    if ($installed -notcontains $RevitVersion) {
        Fail "Revit $RevitVersion does not appear to be installed on this machine."
    }
    $installed = @($RevitVersion)
}

if ($installed.Count -eq 0) {
    Fail "No supported Revit installation was found. This package targets Revit $($SupportedVersions -join ', ')."
}

Write-Log ("Revit found: {0}" -f ($installed -join ', ')) 'OK'

if ($AllUsers -and -not (Test-Admin)) {
    Fail ("Installing for all users writes into Program Files, which needs administrator rights. " +
          "Right-click Install.cmd and choose 'Run as administrator', or run without -AllUsers to " +
          "install for yourself only.")
}

$scope = if ($AllUsers) { 'all users' } else { 'you only' }
Write-Log "Installing for: $scope"

# The two add-ins, and the folder each one's manifest points at with a relative path.
$addins = @(
    @{ Manifest = 'Cda.Revit.Addin.addin';           Folder = 'Cda';           Name = 'DKSI Revit Tools' }
    @{ Manifest = 'Dksi.VisionModeler.Addin.addin';  Folder = 'VisionModeler'; Name = 'DKSI Vision Modeler' }
)

foreach ($version in $installed) {
    $userDir  = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$version"
    $allDir   = Join-Path ${env:ProgramW6432} "Autodesk\Revit $version\AddIns"

    $target = if ($AllUsers) { $allDir } else { $userDir }
    $other  = if ($AllUsers) { $userDir } else { $allDir }

    Write-Log ''
    Write-Log "Revit $version -> $target"

    New-Item -ItemType Directory -Path $target -Force | Out-Null

    foreach ($addin in $addins) {
        # Remove the SAME add-in from the other scope first.
        #
        # Revit reads both folders and loads whatever it finds in each. Two copies of one
        # add-in means the ribbon tab is built twice, every DocumentChanged handler is
        # registered twice, and the finish automation runs two passes per edit. It looks
        # like a performance bug and is really a leftover install.
        $strayManifest = Join-Path $other $addin.Manifest
        $strayFolder   = Join-Path $other $addin.Folder

        if (Test-Path $strayManifest) {
            Write-Log ("  Removing an earlier {0} install from the other scope: {1}" -f $addin.Name, $other) 'WARN'
            Remove-Item $strayManifest -Force -ErrorAction SilentlyContinue
            if (Test-Path $strayFolder) { Remove-Item $strayFolder -Recurse -Force -ErrorAction SilentlyContinue }
        }

        $sourceFolder = Join-Path $Payload $addin.Folder
        $destFolder   = Join-Path $target  $addin.Folder

        if (-not (Test-Path $sourceFolder)) { Fail "Package is missing $($addin.Folder). The ZIP did not extract completely." }

        # Delete before copy so a file removed in this release does not survive as a
        # stale assembly that Revit still loads.
        if (Test-Path $destFolder) { Remove-Item $destFolder -Recurse -Force }

        Copy-Item $sourceFolder -Destination $destFolder -Recurse -Force
        Copy-Item (Join-Path $Payload $addin.Manifest) -Destination (Join-Path $target $addin.Manifest) -Force

        Write-Log ("  Installed {0}" -f $addin.Name) 'OK'
    }

    # ------------------------------------------------------------ verify

    $problems = @()
    foreach ($addin in $addins) {
        $manifest = Join-Path $target $addin.Manifest
        $dll = Get-ChildItem (Join-Path $target $addin.Folder) -Filter *.dll -ErrorAction SilentlyContinue |
               Select-Object -First 1

        if (-not (Test-Path $manifest)) { $problems += "$($addin.Name): manifest missing" }
        if (-not $dll)                  { $problems += "$($addin.Name): no assemblies were copied" }
    }

    if ($problems.Count -gt 0) {
        Fail ("Install finished but verification failed:`n  " + ($problems -join "`n  "))
    }

    Write-Log "  Verified: manifests and assemblies are in place." 'OK'
}

# ---------------------------------------------------------------- done

Write-Host ''
Write-Log 'Installation complete.' 'OK'
Write-Host ''
Write-Host '  Start Revit and look for the DKSI tab on the ribbon.' -ForegroundColor Cyan
Write-Host ''
Write-Host '  Revit will ask once whether to load an add-in from an unknown publisher.'
Write-Host "  Choose 'Always Load' - these assemblies are unsigned, so 'Load Once' means"
Write-Host '  answering the same question at every startup.'
Write-Host ''
Write-Log "Log: $LogFile"
exit 0
