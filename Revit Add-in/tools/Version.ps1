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
# ends with the old add-in still in place and a non-zero exit nobody reads. That is the
# same class of failure this repository has already been bitten by twice: an install that
# reports success and changes nothing.
#
# THE DATE-DERIVED SCHEME IS KEPT, not replaced. MSI version fields are numeric and capped
# (0-65535 for the third field), so 1.0.<yy><day-of-year> remains the right shape: it is
# readable, it sorts, and it cannot overflow. All this adds is a floor - if the calendar
# says a number that has already been used, step past it instead of colliding with it.

function Get-NextPackageVersion {
    param(
        # Folder holding previously built packages.
        [Parameter(Mandatory)][string]$Dist,

        # Filename stem before the version, e.g. 'PaintTakeoff-' or 'DKSI-Revit-Suite-'.
        [Parameter(Mandatory)][string]$Prefix,

        # What the version WOULD be with nothing already built. The date-derived string for
        # the suite; a plain floor like '1.0.1' for a package with no date meaning.
        [Parameter(Mandatory)][string]$Candidate
    )

    $floor = [version]$Candidate

    # Every already-built package sharing this prefix, whatever the extension - .msi and .exe
    # both matter, and the suite writes an MSI and a bundle under different names but the same
    # version. Unparseable names are skipped rather than guessed at.
    $highest = Get-ChildItem -Path $Dist -Filter "$Prefix*" -File -ErrorAction SilentlyContinue |
        ForEach-Object {
            $stem   = [System.IO.Path]::GetFileNameWithoutExtension($_.Name)
            $parsed = $null
            if ([version]::TryParse(($stem -replace ('^' + [regex]::Escape($Prefix)), ''), [ref]$parsed)) { $parsed }
        } |
        Sort-Object -Descending |
        Select-Object -First 1

    if ($null -eq $highest -or $floor -gt $highest) { return $floor.ToString() }

    # Already used. Step the LAST field, which is the one both schemes vary.
    return ('{0}.{1}.{2}' -f $highest.Major, $highest.Minor, ($highest.Build + 1))
}
