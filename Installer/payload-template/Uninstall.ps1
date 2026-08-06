<#
.SYNOPSIS
    Removes the DKSI Revit add-in suite.

.DESCRIPTION
    Removes both add-ins from BOTH the per-user and the all-users folder, for every
    Revit version, so nothing is left behind to load silently. Settings and logs under
    %LocalAppData%\Cda and %LocalAppData%\Dksi are kept unless -PurgeSettings is given —
    they hold your automation preferences and time-tracking entries.
#>
[CmdletBinding()]
param(
    [string] $RevitVersion,
    [switch] $PurgeSettings
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$SupportedVersions = @('2027')

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

Write-Host ''
Write-Host '  Removing DKSI Revit add-ins' -ForegroundColor Cyan
Write-Host ''

$revit = Get-Process -Name 'Revit' -ErrorAction SilentlyContinue
if ($revit) {
    Write-Host ("Revit is running (process {0}). Close it and run this again." -f ($revit.Id -join ', ')) -ForegroundColor Red
    exit 1
}

$versions = if ($RevitVersion) { @($RevitVersion) } else { $SupportedVersions }

$addins = @(
    @{ Manifest = 'Cda.Revit.Addin.addin';          Folder = 'Cda' }
    @{ Manifest = 'Dksi.VisionModeler.Addin.addin'; Folder = 'VisionModeler' }
)

$removed = 0
$skippedForRights = $false

foreach ($version in $versions) {
    $scopes = @(
        (Join-Path $env:APPDATA "Autodesk\Revit\Addins\$version"),
        (Join-Path ${env:ProgramW6432} "Autodesk\Revit $version\AddIns")
    )

    foreach ($dir in $scopes) {
        if (-not (Test-Path $dir)) { continue }

        $isProgramFiles = $dir -like "$([regex]::Escape(${env:ProgramW6432}))*"
        if ($isProgramFiles -and -not (Test-Admin)) {
            # Only worth mentioning if something is actually there to remove.
            foreach ($addin in $addins) {
                if (Test-Path (Join-Path $dir $addin.Manifest)) { $script:skippedForRights = $true }
            }
            continue
        }

        foreach ($addin in $addins) {
            $manifest = Join-Path $dir $addin.Manifest
            $folder   = Join-Path $dir $addin.Folder

            if (Test-Path $manifest) {
                Remove-Item $manifest -Force
                Write-Host "  Removed $manifest"
                $removed++
            }

            if (Test-Path $folder) {
                Remove-Item $folder -Recurse -Force
                Write-Host "  Removed $folder"
                $removed++
            }
        }
    }
}

if ($PurgeSettings) {
    foreach ($dir in @("$env:LOCALAPPDATA\Cda", "$env:LOCALAPPDATA\Dksi")) {
        if (Test-Path $dir) {
            Remove-Item $dir -Recurse -Force
            Write-Host "  Removed settings and logs: $dir" -ForegroundColor Yellow
        }
    }
}

Write-Host ''
if ($removed -eq 0) {
    Write-Host '  Nothing to remove - the add-ins were not installed.' -ForegroundColor Yellow
} else {
    Write-Host "  Removed $removed item(s)." -ForegroundColor Green
}

if ($skippedForRights) {
    Write-Host ''
    Write-Host '  An all-users install was found but could not be removed without admin rights.' -ForegroundColor Yellow
    Write-Host '  Right-click Uninstall.cmd and choose "Run as administrator" to finish.' -ForegroundColor Yellow
}

if (-not $PurgeSettings) {
    Write-Host ''
    Write-Host '  Your settings and logs were kept. Add -PurgeSettings to remove those too.'
}

Write-Host ''
exit 0
