# ALTERNATIVE, NOT CURRENTLY APPLIED.
#
# The shipped "Stamp Review Date" icon is the DKSI logo, produced by logo-to-icon.ps1.
# Run THAT script to regenerate what is in use; this one overwrites it.
#
# Kept because it is the design-convention answer: a company logo says who wrote the
# tool rather than what the button does, and every command in the add-in has an equal
# claim to it. This draws a calendar page with a tick instead - "a date, confirmed" -
# matching the house style of the rest of the set: flat, outlined, no gradients, blue
# body with an orange accent for the action.
#
# Run it if the logo ever needs to move to an About screen and the button needs an
# icon of its own.

Add-Type -AssemblyName System.Drawing

$OutDir = Join-Path $PSScriptRoot "..\src\Cda.Revit.Addin\Resources\Icons"
$OutDir = [System.IO.Path]::GetFullPath($OutDir)

$Outline = [System.Drawing.Color]::FromArgb(255,  45,  45,  48)
$Header  = [System.Drawing.Color]::FromArgb(255,  37, 118, 178)
$Accent  = [System.Drawing.Color]::FromArgb(255, 237, 139,  32)
$Paper   = [System.Drawing.Color]::White

function New-Canvas([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap -ArgumentList $size, $size,
        ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    return @($bmp, $g)
}

# A calendar page with a tick: "a date, confirmed". Reads at 16 px because it is three
# blocks of tone - dark outline, blue band, orange mark - rather than fine detail.
function Save-StampIcon([int]$size) {
    $c = New-Canvas $size
    $bmp = $c[0]; $g = $c[1]
    $s = $size / 32.0

    $pen = New-Object System.Drawing.Pen -ArgumentList $Outline, ([float]([Math]::Max(1.0, 2.0 * $s)))
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    # Binding tabs above the page.
    $tab = New-Object System.Drawing.SolidBrush -ArgumentList $Outline
    $g.FillRectangle($tab, [float](9 * $s), [float](2 * $s), [float](3 * $s), [float](5 * $s))
    $g.FillRectangle($tab, [float](20 * $s), [float](2 * $s), [float](3 * $s), [float](5 * $s))
    $tab.Dispose()

    # Page body.
    $paper = New-Object System.Drawing.SolidBrush -ArgumentList $Paper
    $g.FillRectangle($paper, [float](3 * $s), [float](5 * $s), [float](26 * $s), [float](24 * $s))
    $paper.Dispose()

    # Header band.
    $band = New-Object System.Drawing.SolidBrush -ArgumentList $Header
    $g.FillRectangle($band, [float](3 * $s), [float](5 * $s), [float](26 * $s), [float](7 * $s))
    $band.Dispose()

    $g.DrawRectangle($pen, [float](3 * $s), [float](5 * $s), [float](26 * $s), [float](24 * $s))

    # The tick: the "stamped" half of the idea. Drawn thick so it survives 16 px.
    $tickPen = New-Object System.Drawing.Pen -ArgumentList $Accent, ([float]([Math]::Max(2.0, 3.6 * $s)))
    $tickPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $tickPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $tickPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $pts = @(
        (New-Object System.Drawing.PointF -ArgumentList ([float](8.5 * $s)), ([float](20.5 * $s))),
        (New-Object System.Drawing.PointF -ArgumentList ([float](13.5 * $s)), ([float](25 * $s))),
        (New-Object System.Drawing.PointF -ArgumentList ([float](24 * $s)), ([float](15 * $s)))
    )
    $g.DrawLines($tickPen, [System.Drawing.PointF[]]$pts)
    $tickPen.Dispose()
    $pen.Dispose()

    $g.Dispose()
    $bmp.Save((Join-Path $OutDir "stamp$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    "stamp$size.png"
}

Save-StampIcon 32
Save-StampIcon 16
