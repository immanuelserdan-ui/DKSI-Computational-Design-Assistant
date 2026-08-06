# Generates the "Paint Takeoff" ribbon icon: takeoff16.png and takeoff32.png.
#
# House style, same as make-highlight-icon.ps1: flat, outlined, no gradients, white body with
# a blue rim and an orange accent for the action.
#
# WHAT IT HAS TO SAY. The Paint Highlight icon is ONE panel part-marked, because that tool is
# about a single surface. This tool is about ROWS: a room needs one row per painted face, and
# a wall material takeoff gives it fewer rows than the room has faces. So the drawing is three
# stacked bands - a schedule - each with its measured portion marked, which reads at 16 px as
# a striped list rather than as a wall.
#
# Re-run after editing; the output is committed, so nobody needs PowerShell to build.

Add-Type -AssemblyName System.Drawing

$OutDir = Join-Path $PSScriptRoot "..\src\Cda.Revit.Addin\Resources\Icons"
$OutDir = [System.IO.Path]::GetFullPath($OutDir)

$Outline = [System.Drawing.Color]::FromArgb(255,  45,  45,  48)
$Rim     = [System.Drawing.Color]::FromArgb(255,  37, 118, 178)
$Accent  = [System.Drawing.Color]::FromArgb(255, 237, 139,  32)
$Face    = [System.Drawing.Color]::White

function New-Canvas([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap -ArgumentList $size, $size,
        ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    return @($bmp, $g)
}

function Save-TakeoffIcon([int]$size) {
    $c = New-Canvas $size
    $bmp = $c[0]; $g = $c[1]
    $s = $size / 32.0

    $left = 3 * $s
    $width = 26 * $s
    $rowH = 6 * $s
    $gap  = 2 * $s
    $top  = 5 * $s

    # Each row's marked portion is a different width - the point being that every room's
    # share differs, and each one gets its own row instead of being folded into a neighbour's.
    $marked = @(17, 9, 13)

    $faceBrush   = New-Object System.Drawing.SolidBrush -ArgumentList $Face
    $accentBrush = New-Object System.Drawing.SolidBrush -ArgumentList $Accent
    $rimPen      = New-Object System.Drawing.Pen -ArgumentList $Rim, ([float]([Math]::Max(1.4, 2.0 * $s)))

    for ($i = 0; $i -lt 3; $i++) {
        $y = $top + ($i * ($rowH + $gap))

        $g.FillRectangle($faceBrush, [float]$left, [float]$y, [float]$width, [float]$rowH)
        $g.FillRectangle($accentBrush, [float]$left, [float]$y, [float]($marked[$i] * $s), [float]$rowH)
        $g.DrawRectangle($rimPen, [float]$left, [float]$y, [float]$width, [float]$rowH)
    }

    $faceBrush.Dispose()
    $accentBrush.Dispose()
    $rimPen.Dispose()

    $path = Join-Path $OutDir ("takeoff{0}.png" -f $size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)

    $g.Dispose()
    $bmp.Dispose()

    Write-Host "  wrote $path"
}

Write-Host "Generating Paint Takeoff icons into $OutDir"
Save-TakeoffIcon 32
Save-TakeoffIcon 16
Write-Host "Done."
