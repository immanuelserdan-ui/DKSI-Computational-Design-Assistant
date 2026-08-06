<#
.SYNOPSIS
    Builds the DKSI add-in and packages it into one distributable ZIP.

.DESCRIPTION
    Produces dist\DKSI-Revit-Suite-<version>.zip containing the add-in, its
    manifest, an installer and an uninstaller. Hand that one file to anyone in the
    office; they extract it and double-click Install.cmd.

    The name still says "Suite" because that is the file name colleagues already
    have, and the installer it carries still removes Vision Modeler from machines
    that got an earlier package - renaming it would only make that history harder
    to follow.

    Builds in RELEASE and with DeployToRevit=false, deliberately. Release because a
    Debug build carries no optimisation and ships JIT-time overhead into everyone's
    Revit session; DeployToRevit=false because packaging must not depend on, or
    disturb, whatever happens to be installed on the machine doing the build.

.PARAMETER Version
    Package version, stamped into VERSION.txt and the file name.

.EXAMPLE
    .\build-installer.ps1
#>
[CmdletBinding()]
param(
    [string] $Version = '1.0.0',

    [string] $CdaProject = 'C:\Users\user\Desktop\Claude Projects\Computational Design Assistant\Revit Add-in\src\Cda.Revit.Addin\Cda.Revit.Addin.csproj',

    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root     = Split-Path -Parent $MyInvocation.MyCommand.Definition
$Template = Join-Path $Root 'payload-template'
$Staging  = Join-Path $Root 'staging'
$Dist     = Join-Path $Root 'dist'

function Step { param([string] $m) Write-Host "==> $m" -ForegroundColor Cyan }
function Note { param([string] $m) Write-Host "    $m" }

# ---------------------------------------------------------------- build

# Ship = goes into the package. Everything here is BUILT either way, so a project
# that is temporarily not shipped still fails the build if someone breaks it —
# which is the whole reason not to simply comment it out.
#
# REMOVED — DKSI Vision Modeler. It was carried here as a parked Ship = $false entry
# so that breaking it would still fail this build. That guard stopped being useful
# when the product was retired in 70288e5 ("Stop shipping Vision Modeler, and remove
# it from machines that have it") and became a liability instead: the Test-Path check
# below throws for EVERY entry, shipped or not, so this script would die with
# "Project not found" the moment the RevitVisionModeler folder is deleted - which is
# the natural next step after retiring something. It survived only because that folder
# still happens to sit on this machine, outside the repo.
#
# Install.ps1 and Uninstall.ps1 keep their Vision Modeler entries on purpose. Those
# are the uninstall path for colleagues who received an earlier package, and they must
# outlive the build entry.
$builds = @(
    @{ Name = 'DKSI Revit Tools'; Project = $CdaProject; Folder = 'Cda'; Manifest = 'Cda.Revit.Addin.addin'; Ship = $true }
)

foreach ($build in $builds) {
    if (-not (Test-Path $build.Project)) { throw "Project not found: $($build.Project)" }
}

# FileVersion MUST CHANGE WITH EVERY RELEASE, and this is the line that makes it.
#
# AssemblyVersion is a constant 1.0.0 in Directory.Build.props, so without this every
# DLL this script ever produced carried FileVersion 1.0.0.0 - the binary inside
# DKSI-Revit-Suite-1.1.0.zip reported 1.0.0.0, identical to the one in the 1.0.0 zip.
# Two packages, two version numbers on the outside, indistinguishable binaries inside.
#
# The MSI channel already does this (Revit Add-in\tools\build-installer.ps1) because a
# missing FileVersion there is worse than cosmetic: Windows Installer treats "equal
# version" as "leave the existing file alone", and an upgrade was once observed keeping
# the OLD DLL while still exiting 0. This ZIP channel copies with -Force so it does not
# have that failure, but shipping unidentifiable binaries makes a bug report from a
# colleague impossible to tie back to a build.
#
# Same 1.0.<yy><doy>.0 scheme as the MSI and package-for-colleague, so all three
# channels stamp identical binaries on a given day.
$fileVersion = '1.0.{0}{1}.0' -f (Get-Date).ToString('yy'), (Get-Date).DayOfYear

if (-not $SkipBuild) {
    foreach ($build in $builds) {
        Step "Building $($build.Name) (Release, FileVersion $fileVersion)"

        # DeployToRevit=false keeps the packaging build from touching the local Revit
        # install - and stops a running Revit from failing the build over a locked DLL.
        & dotnet build $build.Project -c Release -p:DeployToRevit=false -p:FileVersion=$fileVersion --nologo -v quiet

        if ($LASTEXITCODE -ne 0) { throw "$($build.Name) failed to build. Package not created." }
    }
}

# ---------------------------------------------------------------- stage

Step 'Staging the package'

if (Test-Path $Staging) { Remove-Item $Staging -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $Staging 'payload') -Force | Out-Null

foreach ($build in $builds) {
    if (-not $build.Ship) {
        Note "$($build.Name): built but NOT shipped (parked)"
        continue
    }

    $projectDir = Split-Path -Parent $build.Project
    $binDir     = Join-Path $projectDir 'bin\Release'

    if (-not (Test-Path $binDir)) {
        throw "No Release output for $($build.Name) at $binDir. Run without -SkipBuild."
    }

    $dest = Join-Path $Staging "payload\$($build.Folder)"
    New-Item -ItemType Directory -Path $dest -Force | Out-Null

    # Exactly the three kinds of file Revit needs: the assemblies, the .deps.json /
    # .runtimeconfig.json that let the private AssemblyLoadContext resolve the NuGet
    # closure, and the PDBs. PDBs are included on purpose - they cost a few hundred KB
    # and turn an unreadable stack trace in a colleague's log into a file and line.
    $files = Get-ChildItem $binDir -Recurse -Include *.dll, *.json, *.pdb
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($binDir.Length).TrimStart('\')
        $targetPath = Join-Path $dest $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $targetPath) -Force | Out-Null
        Copy-Item $file.FullName -Destination $targetPath -Force
    }

    Copy-Item (Join-Path $projectDir $build.Manifest) -Destination (Join-Path $Staging "payload\$($build.Manifest)") -Force

    $count = (Get-ChildItem $dest -Recurse -File).Count
    Note "$($build.Name): $count file(s)"
}

Copy-Item (Join-Path $Template '*') -Destination $Staging -Recurse -Force

# A build stamp travels with the package, so "which version is on that machine?" is
# answerable from the installed folder rather than by comparing file dates.
$stamp = @"
DKSI Revit add-in suite
Version      : $Version
FileVersion  : $fileVersion
Built        : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
Built on     : $env:COMPUTERNAME
Revit target : 2027 (.NET 10)

Contents
  DKSI Revit Tools - finish areas, lining automation, schedules, time tracking

FileVersion is the number stamped into the DLL itself, readable from its Properties
dialog on any machine. Quote it in a bug report; Version alone names the package.
"@
Set-Content -Path (Join-Path $Staging 'VERSION.txt') -Value $stamp -Encoding UTF8

# ---------------------------------------------------------------- zip

Step 'Creating the ZIP'

New-Item -ItemType Directory -Path $Dist -Force | Out-Null
$zip = Join-Path $Dist "DKSI-Revit-Suite-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

# ZipFile::CreateFromDirectory, NOT Compress-Archive.
#
# Windows PowerShell 5.1's Compress-Archive writes entry paths with BACKSLASH
# separators, which the ZIP specification does not allow - section 4.4.17.1 requires
# forward slashes. Windows extractors cope, so the package still installs, but a
# recipient on macOS or Linux, or an email gateway that repacks attachments, can end up
# with twenty files called "payload\Cda\something.dll" flat in one folder, and then
# Install.cmd cannot find its own payload.
#
# The .NET API always writes forward slashes and does not depend on which PowerShell
# the caller happened to use, which matters because the documented rebuild command runs
# `powershell` (5.1) rather than `pwsh` (7).
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory(
    $Staging, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

Remove-Item $Staging -Recurse -Force

$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 2)

Write-Host ''
Write-Host "  Package ready: $zip" -ForegroundColor Green
Write-Host "  Size: $sizeMb MB"
Write-Host ''
Write-Host '  Send that one file. The recipient extracts it and double-clicks Install.cmd.'
Write-Host ''
