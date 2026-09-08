<#
    Removes the PER-USER copy of DKSI Revit Tools, so the machine-wide one can take over.

    RUN THIS AS THE USER, NOT AS ADMIN. That is the whole point of it.

    The per-user package installs into %AppData%, which is per-profile. A machine-wide rollout
    from SCCM, Intune or GPO runs as SYSTEM or a local admin and cannot see - let alone remove -
    what is in twenty other people's profiles. Elevating this script makes it inspect the
    ADMIN's profile and cheerfully report nothing to do, while every real user still has two
    manifests. So: no elevation, run in each user's own session, e.g. from a logon script.

    WHY THE DUPLICATE MATTERS
        Both packages carry the same add-in ClientId. Revit does not support reading one add-in
        from two manifests: it loads one, ignores the other, and does not say which. A user can
        therefore run months-old code with the current version sitting on disk beside it, and
        every symptom of that looks like "the update did not work".

    WHAT IT TOUCHES, AND NOTHING ELSE
        %AppData%\Autodesk\Revit\Addins\<version>\Cda.Revit.Addin.addin
        %AppData%\Autodesk\Revit\Addins\<version>\Cda\

    It does NOT touch the machine-wide install, the user's settings and logs in
    %LocalAppData%\Cda\RevitAddin, or any other vendor's add-in. Settings are deliberately left:
    they are the user's own preferences and the machine-wide install reads the same files.

    USAGE
        powershell -ExecutionPolicy Bypass -File .\Remove-PerUserInstall.ps1            # report only
        powershell -ExecutionPolicy Bypass -File .\Remove-PerUserInstall.ps1 -Apply     # remove
#>
[CmdletBinding()]
param(
    [string] $RevitVersion = '2027',

    # Off by default. A script that deletes files on a hundred workstations should have to be
    # asked twice, and the first run is worth reading before the second.
    [switch] $Apply
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole(
                [Security.Principal.WindowsBuiltInRole]::Administrator)

if ($elevated) {
    Write-Warning @"
Running ELEVATED. This inspects `$env:AppData, which under elevation is the ADMINISTRATOR's
profile - not the profile of the person who uses Revit on this machine. Anything it reports
is about the wrong user.

Re-run it without elevation, in the session of the user whose copy you mean to remove.
"@
}

$addins   = Join-Path $env:AppData "Autodesk\Revit\Addins\$RevitVersion"
$manifest = Join-Path $addins 'Cda.Revit.Addin.addin'
$payload  = Join-Path $addins 'Cda'

Write-Host ""
Write-Host "Per-user DKSI Revit Tools - $($identity.Name)" -ForegroundColor Cyan
Write-Host ""

$targets = @()
if (Test-Path $manifest) { $targets += $manifest }
if (Test-Path $payload)  { $targets += $payload }

if ($targets.Count -eq 0) {
    Write-Host "  Nothing to remove - no per-user copy in $addins" -ForegroundColor Green
    Write-Host ""
    exit 0
}

foreach ($t in $targets) { Write-Host "  found  $t" }

# The machine-wide copy, reported so the user is not left with neither. Removing the per-user
# copy when nothing replaces it takes the add-in away entirely.
$machineWide = Join-Path $env:ProgramFiles "Autodesk\Revit\Addins\$RevitVersion\Cda.Revit.Addin.addin"
$haveMachineWide = Test-Path $machineWide

Write-Host ""
if ($haveMachineWide) {
    Write-Host "  Machine-wide copy present: $machineWide" -ForegroundColor Green
    Write-Host "  Removing the per-user copy leaves that one in charge."
} else {
    Write-Warning "NO machine-wide copy found at $machineWide"
    Write-Host "  Removing the per-user copy would leave this machine with NO DKSI Revit Tools."
    Write-Host "  Install the machine-wide package first, then run this."
}

if (-not $Apply) {
    Write-Host ""
    Write-Host "  Report only. Re-run with -Apply to remove." -ForegroundColor Yellow
    Write-Host ""
    exit 0
}

if (-not $haveMachineWide) {
    Write-Host ""
    Write-Error "Refusing to remove the only copy on this machine. Install the machine-wide package first."
    exit 1
}

# Revit reads manifests at startup and holds the DLL while running, so removing either under a
# live session leaves a half-state that survives until the next restart.
if (Get-Process Revit -ErrorAction SilentlyContinue) {
    Write-Host ""
    Write-Error "Revit is running. Close it and run this again - it holds the add-in DLL open."
    exit 1
}

Write-Host ""
foreach ($t in $targets) {
    try {
        Remove-Item -LiteralPath $t -Recurse -Force
        Write-Host "  removed  $t" -ForegroundColor Green
    }
    catch {
        Write-Warning "could not remove $t : $($_.Exception.Message)"
    }
}

# The per-user MSI's own registration, if it was installed that way rather than hand-copied.
# HKCU because a per-user install registers there; a machine-wide one never appears here.
$product = Get-ChildItem 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
    ForEach-Object { Get-ItemProperty $_.PSPath } |
    Where-Object { $_.DisplayName -eq 'DKSI Revit Tools' } |
    Select-Object -First 1

Write-Host ""
if ($product) {
    Write-Host "  Apps & features still lists 'DKSI Revit Tools' $($product.DisplayVersion) for this user." -ForegroundColor Yellow
    Write-Host "  Remove that entry too:"
    Write-Host "      msiexec /x $($product.PSChildName) /qn"
} else {
    Write-Host "  No per-user product registration left behind." -ForegroundColor Green
}

Write-Host ""
Write-Host "Done. Start Revit - the DKSI tab now comes from the machine-wide install." -ForegroundColor Green
Write-Host ""
