# Shared version helper for the packaging scripts. Dot-source it:
#
#     . (Join-Path $PSScriptRoot 'Version.ps1')
#
# WHY THIS EXISTS: every package here is versioned by a DEFAULT baked into the build script,
# and both defaults had drifted BELOW what was already built and shipped -
#
#     build-painttakeoff.ps1   default 1.0.3       newest in dist  1.0.18
#     build-suite.ps1          default 1.0.<yy><doy>, i.e. today's date; newest in dist
#                              1.0.26261, hand-bumped eleven times in one day and so
#                              eight days ahead of the calendar it is derived from
#
# Running either script exactly as its own header documents therefore produced a package
# that Windows Installer treats as a DOWNGRADE. MajorUpgrade's DowngradeErrorMessage catches
# that in an interactive install, but a silent one (`/quiet`, which is how IT deploys this)
# ends with the old add-in still in place and a non-zero exit nobody reads.
#
# TWO FLOORS, NOT ONE, AND THE SECOND IS THE IMPORTANT ONE. The first version of this file
# looked only at dist\. That is not good enough, and it failed for real within the hour:
# dist\ is gitignored scratch space, something cleaned roughly a hundred stale artefacts out
# of it, and the very next build fell straight back to the date-derived 1.0.26253 - below the
# 1.0.26262 already INSTALLED on the machine. Deleting a disposable build folder must never
# be able to resurrect the downgrade bug, so the installed product's own DisplayVersion is
# consulted too. That one cannot be cleaned away, because it is the thing being upgraded.

function Get-InstalledProductVersion {
    param([Parameter(Mandatory)][string]$DisplayNamePattern)

    $keys = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    Get-ItemProperty $keys -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like $DisplayNamePattern -and $_.DisplayVersion } |
        ForEach-Object {
            $parsed = $null
            if ([version]::TryParse($_.DisplayVersion, [ref]$parsed)) { $parsed }
        } |
        Sort-Object -Descending |
        Select-Object -First 1
}

function Get-NextPackageVersion {
    param(
        # Folder holding previously built packages.
        [Parameter(Mandatory)][string]$Dist,

        # Filename stem before the version, e.g. 'PaintTakeoff-' or 'DKSI-Revit-Suite-'.
        [Parameter(Mandatory)][string]$Prefix,

        # What the version WOULD be with nothing already built or installed.
        [Parameter(Mandatory)][string]$Candidate,

        # ARP DisplayName of the installed product this package upgrades, e.g.
        # 'DKSI Revit Suite'. Wildcards allowed. Omit only for a package that never installs.
        [string]$InstalledName
    )

    $floor = [version]$Candidate

    # Highest already built. Unparseable names are skipped rather than guessed at.
    $built = Get-ChildItem -Path $Dist -Filter "$Prefix*" -File -ErrorAction SilentlyContinue |
        ForEach-Object {
            $stem   = [System.IO.Path]::GetFileNameWithoutExtension($_.Name)
            $parsed = $null
            if ([version]::TryParse(($stem -replace ('^' + [regex]::Escape($Prefix)), ''), [ref]$parsed)) { $parsed }
        } |
        Sort-Object -Descending |
        Select-Object -First 1

    # Highest already installed - survives dist\ being emptied.
    $installed = if ($InstalledName) { Get-InstalledProductVersion -DisplayNamePattern $InstalledName } else { $null }

    $highest = @($built, $installed) | Where-Object { $_ } | Sort-Object -Descending | Select-Object -First 1

    if ($null -eq $highest -or $floor -gt $highest) { return $floor.ToString() }

    # Already used. Step the LAST field, which is the one both schemes vary.
    return ('{0}.{1}.{2}' -f $highest.Major, $highest.Minor, ($highest.Build + 1))
}
