# DKSI Revit Tools - one-command dev redeploy.
#
#   powershell -ExecutionPolicy Bypass -File .\tools\redeploy.ps1
#
# Pulls the branch this checkout is on, builds Cda.Revit.Addin, and proves - not assumes -
# that the build actually reached C:\Program Files\Autodesk\Revit\Addins\2027\Cda by
# comparing the deployed DLL's timestamp before and after.
#
# WHY THIS EXISTS
#   Three real failures showed up doing this by hand, each looking identical from the
#   outside ("nothing changed"), each needing a different fix:
#     1. Not elevated. The deploy target writes into Program Files - the copy fails
#        silently (WarnAndContinue) and dotnet build still reports success.
#     2. Revit still running. Same silent failure - the DLL is locked, the copy is skipped.
#     3. The checkout was stale. `dotnet build` recompiles only what changed ON DISK; a
#        checkout that never fetched newer commits rebuilds the exact same old output,
#        and "Deploy verified" is then true but uninteresting - it verified a file that
#        was never different to begin with.
#   This script checks for all three BEFORE building, and proves the fourth thing that
#   actually matters - a build stamp newer than the moment it started - after.
#
# WHAT THIS DOES NOT DO
#   Run this ON THE MACHINE WITH REVIT INSTALLED. There is no remote path from anywhere
#   else to this machine - see the comment on DeployToRevitAddins in Cda.Revit.Addin.csproj
#   for why a build with no local Revit install cannot deploy at all.

$ErrorActionPreference = 'Stop'

function Say([string]$text, [string]$colour = 'Gray') { Write-Host $text -ForegroundColor $colour }
function Section([string]$text) { Say ""; Say $text 'Cyan' }

$RevitAddinRoot = Split-Path -Parent $PSScriptRoot
$DeployedDll    = 'C:\Program Files\Autodesk\Revit\Addins\2027\Cda\Cda.Revit.Addin.dll'
$Project        = Join-Path $RevitAddinRoot 'src\Cda.Revit.Addin\Cda.Revit.Addin.csproj'

Say ""
Say "DKSI Revit Tools - redeploy" 'Cyan'
Say "----------------------------" 'Cyan'

# ---------------------------------------------------------------- pre-flight

Section "1. Elevation"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
    IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Say "NOT elevated." 'Red'
    Say "The deploy copies into Program Files, which silently fails without admin rights." 'Red'
    Say "Close this window and re-open PowerShell with 'Run as administrator'." 'Red'
    exit 1
}
Say "Elevated - OK." 'Green'

Section "2. Revit"
$revit = Get-Process revit -ErrorAction SilentlyContinue
if ($revit) {
    Say "Revit is running (PID $($revit.Id -join ', '))." 'Red'
    Say "It locks the DLL, so the copy silently fails. Close Revit and run this again." 'Red'
    exit 1
}
Say "Not running - OK." 'Green'

Section "3. .NET SDK"
try {
    $dotnetVersion = (dotnet --version)
    Say ".NET SDK $dotnetVersion found - OK." 'Green'
}
catch {
    Say "dotnet was not found on PATH. Install the .NET 10 SDK and try again." 'Red'
    exit 1
}

# ---------------------------------------------------------------- git sync

Section "4. Git sync"

# A non-zero exit from a native command like git does NOT throw a catchable exception in
# PowerShell, whatever $ErrorActionPreference says - that preference is for cmdlets. So
# every git call below is checked via $LASTEXITCODE directly rather than try/catch, which
# would silently treat a failed git command as a success.
git -C $RevitAddinRoot rev-parse --is-inside-work-tree *> $null
if ($LASTEXITCODE -ne 0) {
    Say "'$RevitAddinRoot' is not inside a git repository - cannot sync. Building whatever is on disk." 'Yellow'
}
else {
    $branch = (git -C $RevitAddinRoot rev-parse --abbrev-ref HEAD).Trim()
    Say "Branch: $branch"

    $dirty = git -C $RevitAddinRoot status --porcelain
    if ($dirty) {
        Say ""
        Say "Working tree has uncommitted changes - not touching it automatically:" 'Yellow'
        Say ($dirty | Out-String) 'Yellow'
        Say "Resolve those by hand (commit, stash, or discard), then run this again." 'Yellow'
        exit 1
    }

    Say "Fetching origin/$branch..."
    git -C $RevitAddinRoot fetch origin $branch
    if ($LASTEXITCODE -ne 0) {
        Say "git fetch failed - see the error above. Continuing to build whatever is on disk." 'Yellow'
    }
    else {
        # Safe because the tree is already confirmed clean above: nothing to lose, and this
        # is what guarantees the checkout matches origin exactly rather than merely being
        # "on the right branch name" while sitting behind it - see failure #3 above.
        git -C $RevitAddinRoot checkout $branch
        git -C $RevitAddinRoot reset --hard "origin/$branch"

        $head = (git -C $RevitAddinRoot log -1 --format='%h %s').Trim()
        Say "Now at: $head" 'Green'
    }
}

# ---------------------------------------------------------------- build

Section "5. Build"

$before = if (Test-Path $DeployedDll) { (Get-Item $DeployedDll).LastWriteTime } else { $null }
$started = Get-Date

dotnet build $Project -tl:false -v:n
$buildExitCode = $LASTEXITCODE

# ---------------------------------------------------------------- verify

Section "6. Verify"

if (-not (Test-Path $DeployedDll)) {
    Say "FAILED - no DLL at all was found at:" 'Red'
    Say "  $DeployedDll" 'Red'
    Say "Nothing has ever deployed here. Check the build output above for the real error." 'Red'
    exit 1
}

$after = (Get-Item $DeployedDll).LastWriteTime

Say "Deployed DLL: $DeployedDll"
Say "Before this run: $(if ($before) { $before } else { '(did not exist)' })"
Say "After this run:  $after"

if ($after -ge $started) {
    Say ""
    Say "DEPLOYED. The DLL now on disk was written during this run." 'Green'
    if ($buildExitCode -ne 0) {
        Say "Note: dotnet build reported exit code $buildExitCode despite a fresh deploy -" 'Yellow'
        Say "check the build output above; the deploy target can still have run." 'Yellow'
    }
    Say ""
    Say "Start Revit and confirm the change." 'Green'
    exit 0
}
else {
    Say ""
    Say "DID NOT LAND. The deployed DLL is still older than when this run started," 'Red'
    Say "which means the copy step did not run or was skipped." 'Red'
    Say "Re-read the build output above for a 'DEPLOY DID NOT LAND' or 'No Revit' warning." 'Red'
    exit 1
}
