<#
.SYNOPSIS
    Builds both DKSI add-ins and packages them into one distributable ZIP.

.DESCRIPTION
    Produces dist\DKSI-Revit-Suite-<version>.zip containing both add-ins, their
    manifests, an installer and an uninstaller. Hand that one file to anyone in the
    office; they extract it and double-click Install.cmd.

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

    [string] $VisionProject = 'C:\Users\user\Desktop\Claude Projects\RevitVisionModeler\src\Dksi.VisionModeler.Addin\Dksi.VisionModeler.Addin.csproj',

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

$builds = @(
    @{ Name = 'DKSI Revit Tools';   Project = $CdaProject;    Folder = 'Cda';           Manifest = 'Cda.Revit.Addin.addin' }
    @{ Name = 'DKSI Vision Modeler'; Project = $VisionProject; Folder = 'VisionModeler'; Manifest = 'Dksi.VisionModeler.Addin.addin' }
)

foreach ($build in $builds) {
    if (-not (Test-Path $build.Project)) { throw "Project not found: $($build.Project)" }
}

if (-not $SkipBuild) {
    foreach ($build in $builds) {
        Step "Building $($build.Name) (Release)"

        # DeployToRevit=false keeps the packaging build from touching the local Revit
        # install - and stops a running Revit from failing the build over a locked DLL.
        & dotnet build $build.Project -c Release -p:DeployToRevit=false --nologo -v quiet

        if ($LASTEXITCODE -ne 0) { throw "$($build.Name) failed to build. Package not created." }
    }
}

# ---------------------------------------------------------------- stage

Step 'Staging the package'

if (Test-Path $Staging) { Remove-Item $Staging -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $Staging 'payload') -Force | Out-Null

foreach ($build in $builds) {
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
Built        : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
Built on     : $env:COMPUTERNAME
Revit target : 2027 (.NET 10)

Contents
  DKSI Revit Tools    - finish areas, lining automation, schedules, time tracking
  DKSI Vision Modeler - Drawings to BIM
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
