# Generates the "Paint Highlight" ribbon icon: highlight16.png and highlight32.png.
#
# House style, same as make-timer-icon.ps1: flat, outlined, no gradients, white body with
# a blue rim and an orange accent for the action.
#
# WHAT IT HAS TO SAY, IN 16 PIXELS. The button's whole point is "only PART of this surface,
# not the whole element" - so the drawing is a wall panel in outline with one region filled
# orange and hatched. Three blocks of tone: blue rim, white substrate, orange measured area.
# A paint roller or a brush was the obvious first idea and says "painting" rather than
# "the painted area", which is the distinction the tool exists to make.
#
# Re-run after editing; the output is committed, so nobody needs PowerShell to build.

Add-Type -AssemblyName System.Drawing

$OutDir = Join-Path $PSScriptRoot "..\src\Cda.Revit.Addin\Resources\Icons"
$OutDir = [System.IO.Path]::GetFullPath($OutDir)

$Outline = [System.Drawing.Color]::FromArgb(255,  45,  45,  48)
$Rim     = [System.Drawing.Color]::FromArgb(255,  37, 118, 178)
$Accent  = [System.Drawing.Color]::FromArgb(255, 237, 139,  32)
$Hatch   = [System.Drawing.Color]::FromArgb(255, 176,  96,  16)
$Face    = [System.Drawing.Color]::White

function New-Canvas([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap -ArgumentList $size, $size,
        ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    return @($bmp, $g)
}

function Save-HighlightIcon([int]$size) {
    $c = New-Canvas $size
    $bmp = $c[0]; $g = $c[1]
    $s = $size / 32.0

    # The wall panel.
    $panelX = 3 * $s; $panelY = 6 * $s; $panelW = 26 * $s; $panelH = 20 * $s

    $faceBrush = New-Object System.Drawing.SolidBrush -ArgumentList $Face
    $g.FillRectangle($faceBrush, [float]$panelX, [float]$panelY, [float]$panelW, [float]$panelH)
    $faceBrush.Dispose()

    # The measured region: the left portion only. Deliberately NOT the whole panel - that is
    # the entire point of the icon, and of the tool.
    $regionW = 15 * $s

    $accentBrush = New-Object System.Drawing.SolidBrush -ArgumentList $Accent
    $g.FillRectangle($accentBrush, [float]$panelX, [float]$panelY, [float]$regionW, [float]$panelH)
    $accentBrush.Dispose()

    # Hatch lines across the measured region, at 45 degrees, clipped to it. They survive to
    # 16 px as a texture rather than as lines, which is enough to read as "marked out".
    $clip = New-Object System.Drawing.Region -ArgumentList (New-Object System.Drawing.RectangleF -ArgumentList `
        ([float]$panelX, [float]$panelY, [float]$regionW, [float]$panelH))
    $g.Clip = $clip

    $hatchPen = New-Object System.Drawing.Pen -ArgumentList $Hatch, ([float]([Math]::Max(1.0, 1.4 * $s)))
    for ($i = -20; $i -lt 20; $i += 5) {
        $x = $panelX + ($i * $s)
        $g.DrawLine($hatchPen, [float]$x, [float]($panelY + $panelH), [float]($x + $panelH), [float]$panelY)
    }
    $hatchPen.Dispose()
    $g.ResetClip()
    $clip.Dispose()

    # Rim around the whole panel, drawn last so it sits over both tones.
    $rimPen = New-Object System.Drawing.Pen -ArgumentList $Rim, ([float]([Math]::Max(1.6, 2.4 * $s)))
    $g.DrawRectangle($rimPen, [float]$panelX, [float]$panelY, [float]$panelW, [float]$panelH)
    $rimPen.Dispose()

    # The division between measured and unmeasured, in the dark outline colour so it reads as
    # an edge rather than as another hatch line.
    $edgePen = New-Object System.Drawing.Pen -ArgumentList $Outline, ([float]([Math]::Max(1.2, 1.8 * $s)))
    $g.DrawLine($edgePen, [float]($panelX + $regionW), [float]$panelY,
                          [float]($panelX + $regionW), [float]($panelY + $panelH))
    $edgePen.Dispose()

    $path = Join-Path $OutDir ("highlight{0}.png" -f $size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)

    $g.Dispose()
    $bmp.Dispose()

    Write-Host "  wrote $path"
}

Write-Host "Generating Paint Highlight icons into $OutDir"
Save-HighlightIcon 32
Save-HighlightIcon 16
Write-Host "Done."
