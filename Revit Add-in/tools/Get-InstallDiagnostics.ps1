<#
    Collects everything needed to explain why the DKSI Revit Suite did not appear on a machine.

    READ-ONLY. It installs nothing, removes nothing, and changes no setting. Safe to hand to a
    colleague and safe to run twice.

    RUN IT AS THE USER WHO HAS THE PROBLEM, NOT AS ADMIN. Half of what matters lives in that
    user's own profile - their %AppData% add-in folder, their Revit journals, their log files.
    Elevating makes those paths resolve to the administrator's profile, and the report comes
    back describing the wrong person.

    USAGE
        powershell -ExecutionPolicy Bypass -File .\Get-InstallDiagnostics.ps1
        powershell -ExecutionPolicy Bypass -File .\Get-InstallDiagnostics.ps1 -OutFile report.txt

    THE FIRST QUESTION THIS ANSWERS is the one that is easy to get wrong by eye: whether the
    installer FAILED, or whether it SUCCEEDED and produced no visible sign of itself. Those look
    identical to the person running it and have nothing in common as problems.
#>
[CmdletBinding()]
param(
    [string] $RevitVersion = '2027',
    [string] $OutFile,

    # The installer EXE, if it is still on the machine - checked for a download block.
    [string] $Installer
)

$ErrorActionPreference = 'Continue'
$report = [System.Collections.Generic.List[string]]::new()

function Section($t) { $report.Add(""); $report.Add("=== $t ==="); }
function Line($t)    { $report.Add("  $t") }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole(
                [Security.Principal.WindowsBuiltInRole]::Administrator)

$report.Add("DKSI Revit Suite - install diagnostics")
$report.Add("Collected $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') on $env:COMPUTERNAME as $($identity.Name)")
if ($elevated) {
    $report.Add("")
    $report.Add("  *** RUNNING ELEVATED - profile-scoped results below describe the ADMIN account,")
    $report.Add("  *** not the user who has the problem. Re-run without elevation.")
}

# ---------------------------------------------------------------- 1. did it install at all

Section "1. Is it installed? (the question that decides everything else)"

$uninstallKeys = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
)

$products = Get-ChildItem $uninstallKeys -ErrorAction SilentlyContinue |
    ForEach-Object { Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue } |
    Where-Object { $_.DisplayName -match 'DKSI|Painted Material Takeoff' }

if ($products) {
    foreach ($p in $products) {
        $scope = if ($p.PSPath -match 'HKEY_CURRENT_USER') { 'per-user' } else { 'per-machine' }
        Line "INSTALLED  $($p.DisplayName)  $($p.DisplayVersion)  [$scope]"
    }
    Line ""
    Line "If the ribbon is missing but this list is populated, the installer WORKED and the"
    Line "problem is downstream - see sections 3, 4 and 5."
} else {
    Line "NOTHING INSTALLED. No DKSI or Painted Material Takeoff product is registered."
    Line "The installer did not complete. See sections 6 and 7."
}

# ---------------------------------------------------------------- 2. Revit itself

Section "2. Revit"

$revitKeys = Get-ChildItem 'HKLM:\SOFTWARE\Autodesk\Revit' -ErrorAction SilentlyContinue
if ($revitKeys) { foreach ($k in $revitKeys) { Line "registry: $($k.PSChildName)" } }
else { Line "NO Revit registry entries. Is Revit installed on this machine at all?" }

$revitExe = "$env:ProgramFiles\Autodesk\Revit $RevitVersion\Revit.exe"
Line "Revit $RevitVersion exe : $(if (Test-Path $revitExe) { 'present' } else { 'NOT FOUND - the add-in has no host' })"
Line "Revit running now      : $([bool](Get-Process Revit -ErrorAction SilentlyContinue))"

# ---------------------------------------------------------------- 3. files on disk

Section "3. Add-in files on disk"

$locations = [ordered]@{
    'machine-wide (Revit 2027 all-users)' = "$env:ProgramFiles\Autodesk\Revit\Addins\$RevitVersion"
    'per-user'                            = "$env:AppData\Autodesk\Revit\Addins\$RevitVersion"
    'ProgramData (PRE-2027 - Revit 2027 REFUSES these)' = "$env:ProgramData\Autodesk\Revit\Addins\$RevitVersion"
}

$manifestCount = 0
foreach ($name in $locations.Keys) {
    $folder = $locations[$name]
    Line ""
    Line "$name"
    Line "  $folder"
    if (-not (Test-Path $folder)) { Line "    (folder does not exist)"; continue }

    foreach ($m in Get-ChildItem $folder -Filter '*.addin' -ErrorAction SilentlyContinue) {
        Line "    manifest: $($m.Name)"
        if ($m.Name -eq 'Cda.Revit.Addin.addin') { $manifestCount++ }
    }
    foreach ($d in Get-ChildItem $folder -Directory -ErrorAction SilentlyContinue) {
        $dlls = @(Get-ChildItem $d.FullName -Filter '*.dll' -ErrorAction SilentlyContinue)
        Line "    folder:   $($d.Name)  ($($dlls.Count) dll)"
        foreach ($dll in $dlls) {
            Line "        $($dll.Name)  v$($dll.VersionInfo.FileVersion)  $($dll.LastWriteTime)"
        }
    }
}

if ($manifestCount -gt 1) {
    Line ""
    Line "*** DUPLICATE INSTALL: Cda.Revit.Addin.addin found in $manifestCount locations."
    Line "*** Revit loads ONE and ignores the other without saying which. Remove all but one."
}

# ---------------------------------------------------------------- 4. code signing / trust

Section "4. Code signing and trust"

$ptDll = "$env:ProgramFiles\Autodesk\Revit\Addins\$RevitVersion\PaintedMaterialTakeoff\PaintedMaterialTakeoff.dll"
if (Test-Path $ptDll) {
    $sig = Get-AuthenticodeSignature $ptDll
    Line "PaintedMaterialTakeoff.dll : $($sig.Status)"
    Line "  $($sig.StatusMessage)"
} else {
    Line "PaintedMaterialTakeoff.dll : not installed"
}

$thumb = '45973F30D274256D20E8949D0F5E277E38DE1A58'
foreach ($store in 'Root','TrustedPublisher') {
    $n = @(Get-ChildItem "Cert:\LocalMachine\$store" -ErrorAction SilentlyContinue |
           Where-Object { $_.Thumbprint -eq $thumb }).Count
    Line "LocalMachine\$store : $(if ($n -gt 0) { 'certificate TRUSTED' } else { 'NOT trusted - Revit will show Invalid Signature' })"
}

# ---------------------------------------------------------------- 5. what Revit actually did

Section "5. Revit journal - what Revit said about the add-in"

$journals = "$env:LocalAppData\Autodesk\Revit\Autodesk Revit $RevitVersion\Journals"
if (Test-Path $journals) {
    $recent = Get-ChildItem $journals -Filter 'journal*.txt' | Sort-Object LastWriteTime -Descending | Select-Object -First 3
    foreach ($j in $recent) {
        Line ""
        Line "$($j.Name)  $($j.LastWriteTime)"
        $hits = Select-String -Path $j.FullName -Pattern "Cda\.Revit\.Addin|DKSI|PaintedMaterialTakeoff|won't be loaded|AddInLoadFailureMessage" -ErrorAction SilentlyContinue |
                Select-Object -First 12
        if ($hits) { foreach ($h in $hits) { Line "    $($h.Line.Trim().Substring(0, [Math]::Min(200, $h.Line.Trim().Length)))" } }
        else { Line "    (no mention of either add-in - Revit never saw a manifest for them)" }
    }
} else {
    Line "No journals at $journals - has Revit $RevitVersion been started on this machine?"
}

# ---------------------------------------------------------------- 6. installer logs

Section "6. Windows Installer / Burn logs"

$msiEvents = Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='MsiInstaller'} -MaxEvents 60 -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match 'DKSI|Painted Material Takeoff' } | Select-Object -First 12
if ($msiEvents) {
    foreach ($e in $msiEvents) {
        Line "$($e.TimeCreated)  id=$($e.Id)  $(($e.Message -split "`n")[0].Trim())"
    }
} else {
    Line "No MsiInstaller events mentioning these products."
    Line "The MSI never ran - so the bundle was blocked or cancelled before reaching it."
}

$burnLogs = Get-ChildItem $env:TEMP -Filter "*DKSI*_*.log" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 3
if ($burnLogs) {
    foreach ($l in $burnLogs) {
        Line ""
        Line "burn log: $($l.FullName)  $($l.LastWriteTime)"
        Select-String -Path $l.FullName -Pattern "Error|Failed|Exit code|Applied execute" -ErrorAction SilentlyContinue |
            Select-Object -Last 8 | ForEach-Object { Line "    $($_.Line.Trim())" }
    }
} else {
    Line ""
    Line "No bundle log in %TEMP%. Re-run the installer with:  /log %TEMP%\dksi.log"
}

# ---------------------------------------------------------------- 7. was the EXE blocked

Section "7. Was the installer blocked before it ran?"

if ($Installer -and (Test-Path $Installer)) {
    $zone = Get-Content -LiteralPath $Installer -Stream Zone.Identifier -ErrorAction SilentlyContinue
    if ($zone) {
        Line "MARK OF THE WEB PRESENT on $Installer"
        $zone | ForEach-Object { Line "    $_" }
        Line ""
        Line "Windows treats this file as downloaded from another machine. SmartScreen will warn,"
        Line "and some corporate policies block it outright. Right-click > Properties > Unblock,"
        Line "or:  Unblock-File -Path '$Installer'"
    } else {
        Line "No Mark of the Web on $Installer - not blocked as a download."
    }
    Line ""
    Line "Signature: $((Get-AuthenticodeSignature $Installer).Status)"
} else {
    Line "No -Installer path given, so the download block could not be checked."
    Line "Re-run with:  -Installer 'C:\path\to\DKSI-Revit-Suite-<version>.exe'"
}

Line ""
Line "SmartScreen and AppLocker refusals do NOT appear in the Application log."
Line "Check:  Event Viewer > Applications and Services Logs > Microsoft > Windows > AppLocker"

# ---------------------------------------------------------------- 8. runtime

Section "8. .NET runtime (needed by the add-in, not the installer)"

$shared = "$env:ProgramFiles\dotnet\shared\Microsoft.WindowsDesktop.App"
if (Test-Path $shared) {
    foreach ($v in Get-ChildItem $shared -Directory | Sort-Object Name) { Line "WindowsDesktop.App $($v.Name)" }
} else {
    Line "No $shared - but Revit 2027 ships its own .NET 10, so this is not conclusive."
}

# ---------------------------------------------------------------- 9. add-in log

Section "9. DKSI add-in log (only exists if the add-in actually loaded)"

$logDir = "$env:LocalAppData\Cda\RevitAddin\logs"
if (Test-Path $logDir) {
    $latest = Get-ChildItem $logDir -Filter '*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latest) {
        Line "$($latest.FullName)  $($latest.LastWriteTime)"
        Get-Content $latest.FullName -Tail 25 | ForEach-Object { Line "    $_" }
    }
} else {
    Line "No log at $logDir."
    Line "The add-in has never started in this user's session - so either Revit never loaded it,"
    Line "or Revit has not been opened since the install."
}

# ---------------------------------------------------------------- output

$text = $report -join [Environment]::NewLine
Write-Host $text

if ($OutFile) {
    $text | Set-Content -LiteralPath $OutFile -Encoding UTF8
    Write-Host ""
    Write-Host "Written to $OutFile - send that file back." -ForegroundColor Green
}
