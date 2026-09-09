# Generates the "Time Tracking" ribbon icon: timer16.png and timer32.png.
#
# House style, same as make-highlight-icon.ps1: flat, outlined, no gradients, white body with
# a blue rim and an orange accent for the action. A clock face reads at 16 px because it is
# three blocks of tone - dark ring, white face, orange hand - rather than fine detail.
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

function Save-TimerIcon([int]$size) {
    $c = New-Canvas $size
    $bmp = $c[0]; $g = $c[1]
    $s = $size / 32.0

    # Face.
    $faceBrush = New-Object System.Drawing.SolidBrush -ArgumentList $Face
    $g.FillEllipse($faceBrush, [float](4 * $s), [float](7 * $s), [float](24 * $s), [float](24 * $s))
    $faceBrush.Dispose()

    # Rim, drawn thick so the circle survives the downscale to 16 px.
    $rimPen = New-Object System.Drawing.Pen -ArgumentList $Rim, ([float]([Math]::Max(1.6, 2.6 * $s)))
    $g.DrawEllipse($rimPen, [float](4 * $s), [float](7 * $s), [float](24 * $s), [float](24 * $s))
    $rimPen.Dispose()

    # Crown and stem above the face - what makes it read as a stopwatch rather than a clock,
    # and a stopwatch is the thing that measures an interval.
    $stem = New-Object System.Drawing.Pen -ArgumentList $Outline, ([float]([Math]::Max(1.6, 3.0 * $s)))
    $stem.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $stem.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($stem, [float](16 * $s), [float](3 * $s), [float](16 * $s), [float](7 * $s))
    $g.DrawLine($stem, [float](12 * $s), [float](3 * $s), [float](20 * $s), [float](3 * $s))
    $stem.Dispose()

    # Hands: the hour hand dark, the minute hand orange. One accent, as everywhere else.
    $hour = New-Object System.Drawing.Pen -ArgumentList $Outline, ([float]([Math]::Max(1.4, 2.4 * $s)))
    $hour.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $hour.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($hour, [float](16 * $s), [float](19 * $s), [float](16 * $s), [float](12 * $s))
    $hour.Dispose()

    $minute = New-Object System.Drawing.Pen -ArgumentList $Accent, ([float]([Math]::Max(1.8, 3.0 * $s)))
    $minute.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $minute.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($minute, [float](16 * $s), [float](19 * $s), [float](23 * $s), [float](22 * $s))
    $minute.Dispose()

    $g.Dispose()
    $bmp.Save((Join-Path $OutDir "timer$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    "timer$size.png"
}

Save-TimerIcon 32
Save-TimerIcon 16
