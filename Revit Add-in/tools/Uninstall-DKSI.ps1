<#
    Removes the DKSI Revit Suite and everything it leaves behind.

    YOU MAY NOT NEED THIS. The bundle uninstalls itself - Apps & features > "DKSI Revit Suite",
    or DKSI-Revit-Suite-<version>.exe /uninstall /quiet - and that removes both chained products
    properly. This script exists for what that does NOT remove:

        - a per-user copy in somebody's %AppData%, which is a different product in a different
          scope and which the per-machine uninstall cannot see
        - the self-signed root certificate the PaintTakeoff trust step installs, which outlives
          the software and is the one piece of real security residue
        - user settings and the TIME TRACKING LOG in %LocalAppData%\Cda\RevitAddin
        - a stale PaintTakeoff 1.0.1 install under C:\ProgramData, from before the folder fix
        - hand-copied files that no installer ever knew about

    IT IS REPORT-ONLY UNTIL YOU PASS -Apply. Read the first run before authorising the second.

    RUN AS ADMINISTRATOR for the machine-wide parts. Note the same split that governs
    installation: the per-user copy lives in a profile an elevated process cannot see, so
    removing it is a separate job for tools\Remove-PerUserInstall.ps1, run in that user's own
    session. This script reports the per-user copy it can see and refuses to pretend otherwise.

    USAGE
        .\Uninstall-DKSI.ps1                                    # report only
        .\Uninstall-DKSI.ps1 -Apply                             # remove the software
        .\Uninstall-DKSI.ps1 -Apply -RemoveCertificate          # ... and the trust anchor
        .\Uninstall-DKSI.ps1 -Apply -RemoveSettings             # ... and user data (SEE BELOW)
#>
[CmdletBinding()]
param(
    [string] $RevitVersion = '2027',

    [switch] $Apply,

    # Removes the PaintTakeoff signing certificate from LocalMachine\Root and
    # LocalMachine\TrustedPublisher. OFF BY DEFAULT because it is shared state, not ours: if
    # anything else on this machine was signed with the same key, this breaks it too. It is
    # still the right thing to do when the software is going away for good - a self-signed root
    # left behind after its software is uninstalled is a trust anchor nobody owns any more.
    [switch] $RemoveCertificate,

    # DESTROYS USER DATA, INCLUDING THE TIME TRACKING LOG. That log is somebody's record of
    # hours worked and may never have been exported anywhere else. Off by default, and the
    # script prints what it is about to delete before it does.
    [switch] $RemoveSettings
)

$ErrorActionPreference = 'Continue'

$BundleUpgradeCode      = '{9A5F27E3-6C81-4D40-BB92-3E074F1C8A56}'
$DksiAllUsersUpgrade    = '{C4E81F6A-5D73-4A92-B0E8-71F39C2A4D6B}'
$DksiPerUserUpgrade     = '{7B2F4D18-93AC-4E26-8F51-C0A7D6E93B45}'
$PaintTakeoffUpgrade    = '{8E1D4C7A-3B62-4F09-9D57-2A6C8B0E41F3}'
$SigningThumbprint      = '45973F30D274256D20E8949D0F5E277E38DE1A58'

function Say([string]$t, [string]$c = 'Gray') { Write-Host $t -ForegroundColor $c }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole(
                [Security.Principal.WindowsBuiltInRole]::Administrator)

Say ""
Say "DKSI Revit Suite - uninstall" 'Cyan'
Say "  $env:COMPUTERNAME as $($identity.Name)$(if ($elevated) { ' (elevated)' } else { ' (NOT elevated)' })"
Say ""

if (-not $elevated) {
    Write-Warning "Not elevated. The machine-wide products cannot be removed without administrator rights."
}

# ---------------------------------------------------------------- find what is installed
#
# SEARCHED BY UpgradeCode, NEVER BY ProductCode. A ProductCode is regenerated on every build -
# the 1.0.26223 and 1.0.26224 packages have different ones - so a script pinned to a
# ProductCode silently stops finding the thing it was written to remove. The UpgradeCode is the
# permanent identity and is the whole reason it exists.

$uninstallRoots = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
)

$entries = Get-ChildItem $uninstallRoots -ErrorAction SilentlyContinue |
    ForEach-Object { Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue }

function ByUpgrade($code) {
    $entries | Where-Object {
        ($_.BundleUpgradeCode -contains $code) -or ($_.BundleUpgradeCode -eq $code)
    }
}

$bundle       = ByUpgrade $BundleUpgradeCode
$dksiMachine  = $entries | Where-Object { $_.DisplayName -eq 'DKSI Revit Tools (all users)' }
$dksiPerUser  = $entries | Where-Object { $_.DisplayName -eq 'DKSI Revit Tools' }
$paintTakeoff = $entries | Where-Object { $_.DisplayName -eq 'Painted Material Takeoff for Revit' }

Say "INSTALLED" 'White'
$found = $false
foreach ($pair in @(
    @{ n = 'DKSI Revit Suite (bundle)';        e = $bundle },
    @{ n = 'DKSI Revit Tools (all users)';     e = $dksiMachine },
    @{ n = 'DKSI Revit Tools (per-user)';      e = $dksiPerUser },
    @{ n = 'Painted Material Takeoff';         e = $paintTakeoff })) {
    if ($pair.e) {
        $found = $true
        foreach ($x in @($pair.e)) { Say ("  {0,-34} {1}" -f $pair.n, $x.DisplayVersion) }
    }
}
if (-not $found) { Say "  nothing registered" 'Green' }

# ---------------------------------------------------------------- leftovers on disk

Say ""
Say "FILES ON DISK" 'White'

$fileTargets = [ordered]@{
    "machine-wide add-in"       = "$env:ProgramFiles\Autodesk\Revit\Addins\$RevitVersion\Cda"
    "machine-wide manifest"     = "$env:ProgramFiles\Autodesk\Revit\Addins\$RevitVersion\Cda.Revit.Addin.addin"
    "PaintTakeoff payload"      = "$env:ProgramFiles\Autodesk\Revit\Addins\$RevitVersion\PaintedMaterialTakeoff"
    "PaintTakeoff manifest"     = "$env:ProgramFiles\Autodesk\Revit\Addins\$RevitVersion\PaintedMaterialTakeoff.addin"
    "PaintTakeoff 1.0.1 (stale)" = "$env:ProgramData\Autodesk\Revit\Addins\$RevitVersion\PaintedMaterialTakeoff"
    "per-user add-in"           = "$env:AppData\Autodesk\Revit\Addins\$RevitVersion\Cda"
    "per-user manifest"         = "$env:AppData\Autodesk\Revit\Addins\$RevitVersion\Cda.Revit.Addin.addin"
}

$present = @()
foreach ($k in $fileTargets.Keys) {
    if (Test-Path $fileTargets[$k]) { $present += $fileTargets[$k]; Say ("  {0,-28} {1}" -f $k, $fileTargets[$k]) }
}
if ($present.Count -eq 0) { Say "  none" 'Green' }

# ---------------------------------------------------------------- certificate

Say ""
Say "CERTIFICATE" 'White'
$certStores = @()
foreach ($store in 'Root','TrustedPublisher') {
    $hit = Get-ChildItem "Cert:\LocalMachine\$store" -ErrorAction SilentlyContinue |
           Where-Object { $_.Thumbprint -eq $SigningThumbprint }
    if ($hit) { $certStores += $store; Say "  LocalMachine\$store  PaintedMaterialTakeoff dev signing key" }
}
if ($certStores.Count -eq 0) { Say "  not present" 'Green' }

# ---------------------------------------------------------------- user data

Say ""
Say "USER DATA" 'White'
$dataDir = "$env:LocalAppData\Cda\RevitAddin"
if (Test-Path $dataDir) {
    Say "  $dataDir"
    foreach ($f in Get-ChildItem $dataDir -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 10) {
        Say "      $($f.Name)  $([math]::Round($f.Length/1KB,1)) KB"
    }
    $timeLog = Get-ChildItem $dataDir -Recurse -Filter '*time*' -ErrorAction SilentlyContinue
    if ($timeLog) { Say "  *** CONTAINS TIME TRACKING RECORDS - hours worked. Export before deleting." 'Yellow' }
} else {
    Say "  none" 'Green'
}

# ---------------------------------------------------------------- act

if (-not $Apply) {
    Say ""
    Say "Report only. Re-run with -Apply to remove." 'Yellow'
    Say "  -RemoveCertificate  also removes the trust anchor"
    Say "  -RemoveSettings     also removes user data INCLUDING TIME RECORDS"
    Say ""
    exit 0
}

if (Get-Process Revit -ErrorAction SilentlyContinue) {
    Say ""
    Write-Error "Revit is running. Close it first - it holds the add-in DLL open."
    exit 1
}

Say ""
Say "Removing..." 'Cyan'

# THE BUNDLE FIRST, and by its own uninstall string. It knows which packages it installed and
# removes them as a set; picking the MSIs off individually leaves the bundle registered in Apps
# & features pointing at products that no longer exist.
if ($bundle) {
    foreach ($b in @($bundle)) {
        $cmd = if ($b.QuietUninstallString) { $b.QuietUninstallString } else { $b.UninstallString }
        if (-not $cmd) { continue }
        Say "  bundle: $cmd"
        try {
            $exe  = [regex]::Match($cmd, '^"([^"]+)"').Groups[1].Value
            $args = $cmd.Substring($cmd.IndexOf('"', 1) + 1).Trim()
            if (-not $exe) { $exe = ($cmd -split ' ')[0] }
            $p = Start-Process $exe -ArgumentList "$args /quiet /norestart" -Wait -PassThru
            Say "    exit $($p.ExitCode)" $(if ($p.ExitCode -eq 0) { 'Green' } else { 'Yellow' })
        } catch { Write-Warning "    bundle uninstall failed: $($_.Exception.Message)" }
    }
}

# Anything the bundle did not take - a package installed directly, or one whose bundle
# registration has already gone.
foreach ($pair in @(
    @{ n = 'DKSI Revit Tools (all users)'; e = $dksiMachine },
    @{ n = 'Painted Material Takeoff';     e = $paintTakeoff })) {

    foreach ($x in @($pair.e)) {
        if (-not $x -or -not $x.PSChildName) { continue }
        if ($x.PSChildName -notmatch '^\{[0-9A-Fa-f-]{36}\}$') { continue }

        # Re-read: the bundle may already have removed it a moment ago.
        $stillThere = Get-ChildItem $uninstallRoots -ErrorAction SilentlyContinue |
            Where-Object { $_.PSChildName -eq $x.PSChildName }
        if (-not $stillThere) { Say "  $($pair.n): already removed by the bundle" 'Green'; continue }

        Say "  msiexec /x $($x.PSChildName)"
        $p = Start-Process msiexec.exe -ArgumentList "/x $($x.PSChildName) /qn /norestart" -Wait -PassThru
        Say "    exit $($p.ExitCode)" $(if ($p.ExitCode -eq 0) { 'Green' } else { 'Yellow' })
    }
}

# Files the installers did not own - a stale 1.0.1 ProgramData copy, or hand-copied leftovers.
# The per-user paths are reported but NOT removed here: under elevation they resolve to the
# admin's profile, so deleting them would hit the wrong account.
Say ""
foreach ($path in $present) {
    if ($path -like "$env:AppData*") { continue }
    if (-not (Test-Path $path)) { continue }
    try {
        Remove-Item -LiteralPath $path -Recurse -Force
        Say "  removed  $path" 'Green'
    } catch {
        Write-Warning "  could not remove $path : $($_.Exception.Message)"
    }
}

if ($dksiPerUser -or (Test-Path "$env:AppData\Autodesk\Revit\Addins\$RevitVersion\Cda.Revit.Addin.addin")) {
    Say ""
    Say "  A PER-USER copy is still present." 'Yellow'
    Say "  It lives in a profile this process cannot reliably reach. Run, as that user:"
    Say "      tools\Remove-PerUserInstall.ps1 -Apply"
}

# ---------------------------------------------------------------- certificate

if ($RemoveCertificate -and $certStores.Count -gt 0) {
    Say ""
    Say "Removing the signing certificate..." 'Cyan'
    Say "  Anything else signed with this key stops being trusted on this machine." 'Yellow'
    foreach ($store in $certStores) {
        try {
            $s = New-Object Security.Cryptography.X509Certificates.X509Store($store, 'LocalMachine')
            $s.Open('ReadWrite')
            foreach ($c in @($s.Certificates | Where-Object { $_.Thumbprint -eq $SigningThumbprint })) {
                $s.Remove($c)
                Say "  removed from LocalMachine\$store" 'Green'
            }
            $s.Close()
        } catch { Write-Warning "  could not update LocalMachine\$store : $($_.Exception.Message)" }
    }
} elseif ($certStores.Count -gt 0) {
    Say ""
    Say "  Certificate LEFT IN PLACE. Re-run with -RemoveCertificate to remove it." 'Yellow'
}

# ---------------------------------------------------------------- user data

if ($RemoveSettings -and (Test-Path $dataDir)) {
    Say ""
    Say "Removing user data - this includes time tracking records." 'Yellow'
    try {
        Remove-Item -LiteralPath $dataDir -Recurse -Force
        Say "  removed  $dataDir" 'Green'
    } catch { Write-Warning "  could not remove $dataDir : $($_.Exception.Message)" }
} elseif (Test-Path $dataDir) {
    Say ""
    Say "  User data LEFT IN PLACE at $dataDir" 'Green'
    Say "  Settings and time records survive, so a reinstall picks up where you left off."
}

Say ""
Say "Done. Start Revit and confirm the DKSI tab is gone." 'Green'
Say ""
