<#
    Compares two Finish Surface Area CSV exports and reports where the numbers moved.

    WHY THIS EXISTS
        RoomFinishCalculator's opening and casework deductions were changed from "scan every
        instance for every room" to an index built once per phase. The change is
        behaviour-preserving by construction and the edge cases are documented in the code -
        but this solution has NO TESTS, and the output of that engine is areas that end up on
        drawings. "Behaviour-preserving by construction" is a claim; a diff is evidence.

        Run it before shipping the installer to anyone.

    PROCEDURE
        1. git stash push "Revit Add-in/src/Cda.Revit.Addin/Finishes/RoomFinishCalculator.cs"
        2. Build, run Finish Surface Area on a real model, save the CSV as before.csv
        3. git stash pop
        4. Build, run it again on the SAME model, unchanged, save as after.csv
        5. .\Compare-FinishCsv.ps1 before.csv after.csv

        The model must not change between runs - not even a selection that alters what the
        command scopes to. Any difference this reports is either a real regression or a
        difference in what you measured, and it cannot tell you which.

    WHAT "IDENTICAL" MEANS HERE
        Row-for-row equality on the key columns, and area equality within a tolerance. The
        tolerance is not slack for the optimisation - that change cannot alter a number by any
        amount. It is for floating-point summation order, which CAN differ when values are
        accumulated in a different sequence, and which is not a regression.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Before,
    [Parameter(Mandatory)] [string] $After,

    # Square metres. Anything above this is a real difference, not summation order.
    [double] $Tolerance = 0.0005
)

$ErrorActionPreference = 'Stop'

foreach ($p in @($Before, $After)) {
    if (-not (Test-Path $p)) { Write-Error "Not found: $p"; exit 1 }
}

$b = Import-Csv -Path $Before
$a = Import-Csv -Path $After

Write-Host ""
Write-Host "Comparing finish exports" -ForegroundColor Cyan
Write-Host "  before : $Before  ($($b.Count) rows)"
Write-Host "  after  : $After  ($($a.Count) rows)"
Write-Host ""

if ($b.Count -eq 0) { Write-Error "The 'before' export has no rows - nothing to compare."; exit 1 }

# Columns are taken from the file rather than hardcoded: the exporter's shape has changed
# before and a script that assumes it would fail silently on the wrong column.
$columns = $b[0].PSObject.Properties.Name
$areaColumns = @($columns | Where-Object { $_ -match 'Area|Areal|SqM' })
$keyColumns  = @($columns | Where-Object { $areaColumns -notcontains $_ })

if ($areaColumns.Count -eq 0) {
    Write-Warning "No area-looking column found. Falling back to whole-row comparison."
    $keyColumns = $columns
}

Write-Host "  key columns  : $($keyColumns -join ', ')"
Write-Host "  area columns : $($areaColumns -join ', ')"
Write-Host ""

function Key($row) { ($keyColumns | ForEach-Object { $row.$_ }) -join '|' }

$beforeByKey = @{}
foreach ($row in $b) { $beforeByKey[(Key $row)] = $row }

$afterByKey = @{}
foreach ($row in $a) { $afterByKey[(Key $row)] = $row }

$missing = @($beforeByKey.Keys | Where-Object { -not $afterByKey.ContainsKey($_) })
$added   = @($afterByKey.Keys  | Where-Object { -not $beforeByKey.ContainsKey($_) })

$changed = @()
foreach ($key in $beforeByKey.Keys) {
    if (-not $afterByKey.ContainsKey($key)) { continue }

    foreach ($col in $areaColumns) {
        $x = 0.0; $y = 0.0
        $okX = [double]::TryParse($beforeByKey[$key].$col, [ref]$x)
        $okY = [double]::TryParse($afterByKey[$key].$col,  [ref]$y)

        if (-not $okX -or -not $okY) {
            if ($beforeByKey[$key].$col -ne $afterByKey[$key].$col) {
                $changed += [pscustomobject]@{ Key = $key; Column = $col
                                               Before = $beforeByKey[$key].$col
                                               After = $afterByKey[$key].$col; Delta = 'n/a' }
            }
            continue
        }

        $delta = [math]::Abs($x - $y)
        if ($delta -gt $Tolerance) {
            $changed += [pscustomobject]@{ Key = $key; Column = $col
                                           Before = $x; After = $y; Delta = $delta }
        }
    }
}

$clean = ($missing.Count -eq 0) -and ($added.Count -eq 0) -and ($changed.Count -eq 0)

if ($clean) {
    Write-Host "IDENTICAL." -ForegroundColor Green
    Write-Host "  $($b.Count) rows matched, every area within $Tolerance m2."
    Write-Host "  The optimisation changed no number in this model. Safe to ship."
    Write-Host ""
    exit 0
}

Write-Host "DIFFERENCES FOUND - do not ship until these are explained." -ForegroundColor Red
Write-Host ""

if ($missing.Count -gt 0) {
    Write-Host "  $($missing.Count) row(s) present BEFORE and gone AFTER:" -ForegroundColor Red
    $missing | Select-Object -First 20 | ForEach-Object { Write-Host "      $_" }
    if ($missing.Count -gt 20) { Write-Host "      ... and $($missing.Count - 20) more" }
    Write-Host ""
}

if ($added.Count -gt 0) {
    Write-Host "  $($added.Count) row(s) new AFTER:" -ForegroundColor Red
    $added | Select-Object -First 20 | ForEach-Object { Write-Host "      $_" }
    if ($added.Count -gt 20) { Write-Host "      ... and $($added.Count - 20) more" }
    Write-Host ""
}

if ($changed.Count -gt 0) {
    Write-Host "  $($changed.Count) area(s) changed by more than $Tolerance m2:" -ForegroundColor Red
    $changed | Sort-Object -Property Delta -Descending | Select-Object -First 25 | Format-Table -AutoSize
}

Write-Host ""
Write-Host "A row that appears or disappears points at the deduction index - an opening or" -ForegroundColor Yellow
Write-Host "casework instance bucketed to the wrong room, or to none. That is the change to" -ForegroundColor Yellow
Write-Host "look at first: RoomFinishCalculator.OpeningsIn / CaseworkIn." -ForegroundColor Yellow
Write-Host ""
exit 1
