# Turns the DKSI logo into the "Stamp Review Date" ribbon icon pair.
#
# Two things have to happen, and skipping either is what makes a pasted logo look wrong
# on a ribbon:
#
#   1. The white background must become transparent. The source PNG is opaque white and
#      Revit's ribbon is grey, so dropped in as-is the logo arrives inside a visible
#      white tile. The background is removed by flood-filling inward from the border,
#      NOT by keying every white pixel - that distinction is what keeps the white gaps
#      between the swirl arms, which are part of the mark.
#
#   2. It must be resampled with a feathered alpha edge. The rim where the flood fill
#      stops is still near-white and fully opaque; scaled straight down to 16 px that
#      rim averages into a pale halo around the logo.
#
# The fill runs at WorkingSize rather than the source's 650 px. A flood fill over
# 420,000 pixels in PowerShell takes minutes; at 192 px it is a couple of seconds and
# the extra precision would be thrown away by the downscale to 32 and 16 anyway.

Add-Type -AssemblyName System.Drawing

$Source = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\assets\dksi-logo.png"))
$OutDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\src\Cda.Revit.Addin\Resources\Icons"))

$WorkingSize = 192
$BackgroundLevel = 228   # every channel at least this bright counts as background
$FeatherFloor = 170      # rim pixels lighter than this get their alpha scaled down

if (-not (Test-Path $Source)) { throw "Logo not found at $Source" }

$fmt = [System.Drawing.Imaging.PixelFormat]::Format32bppArgb
$original = [System.Drawing.Image]::FromFile($Source)
$work = New-Object System.Drawing.Bitmap -ArgumentList $WorkingSize, $WorkingSize, $fmt

$g = [System.Drawing.Graphics]::FromImage($work)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.DrawImage($original, 0, 0, $WorkingSize, $WorkingSize)
$g.Dispose()
$original.Dispose()

# --- pull the pixels out as a byte array (B,G,R,A per pixel) ---------------------
$rect = New-Object System.Drawing.Rectangle -ArgumentList 0, 0, $WorkingSize, $WorkingSize
$data = $work.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadWrite, $fmt)
$stride = $data.Stride
$buffer = New-Object byte[] ($stride * $WorkingSize)
[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buffer, 0, $buffer.Length)

function Test-Background([int]$x, [int]$y) {
    $o = ($y * $script:stride) + ($x * 4)
    return ($script:buffer[$o] -ge $script:BackgroundLevel) -and
           ($script:buffer[$o + 1] -ge $script:BackgroundLevel) -and
           ($script:buffer[$o + 2] -ge $script:BackgroundLevel)
}

# --- flood fill inward from every border pixel -----------------------------------
$seen = New-Object bool[] ($WorkingSize * $WorkingSize)
$queue = New-Object 'System.Collections.Generic.Queue[int]'

function Push-Pixel([int]$x, [int]$y) {
    if ($x -lt 0 -or $y -lt 0 -or $x -ge $script:WorkingSize -or $y -ge $script:WorkingSize) { return }
    $i = ($y * $script:WorkingSize) + $x
    if ($script:seen[$i]) { return }
    if (-not (Test-Background $x $y)) { return }
    $script:seen[$i] = $true
    $script:queue.Enqueue($i)
}

for ($x = 0; $x -lt $WorkingSize; $x++) {
    Push-Pixel $x 0
    Push-Pixel $x ($WorkingSize - 1)
}
for ($y = 0; $y -lt $WorkingSize; $y++) {
    Push-Pixel 0 $y
    Push-Pixel ($WorkingSize - 1) $y
}

while ($queue.Count -gt 0) {
    $i = $queue.Dequeue()
    $x = $i % $WorkingSize
    $y = [int][Math]::Floor($i / $WorkingSize)
    $buffer[($y * $stride) + ($x * 4) + 3] = 0

    Push-Pixel ($x - 1) $y
    Push-Pixel ($x + 1) $y
    Push-Pixel $x ($y - 1)
    Push-Pixel $x ($y + 1)
}

# --- feather the rim the fill stopped at ------------------------------------------
$before = $buffer.Clone()
for ($y = 1; $y -lt ($WorkingSize - 1); $y++) {
    for ($x = 1; $x -lt ($WorkingSize - 1); $x++) {
        $o = ($y * $stride) + ($x * 4)
        if ($before[$o + 3] -eq 0) { continue }

        $touchesGap = ($before[$o - 4 + 3] -eq 0) -or ($before[$o + 4 + 3] -eq 0) -or
                      ($before[$o - $stride + 3] -eq 0) -or ($before[$o + $stride + 3] -eq 0)
        if (-not $touchesGap) { continue }

        $lightest = [Math]::Min($before[$o], [Math]::Min($before[$o + 1], $before[$o + 2]))
        if ($lightest -lt $FeatherFloor) { continue }

        $buffer[$o + 3] = [byte][Math]::Max(0, [Math]::Min(255, (255 - $lightest) * 3))
    }
}

[System.Runtime.InteropServices.Marshal]::Copy($buffer, 0, $data.Scan0, $buffer.Length)
$work.UnlockBits($data)

# --- emit the two ribbon sizes -----------------------------------------------------
foreach ($size in @(32, 16)) {
    $target = New-Object System.Drawing.Bitmap -ArgumentList $size, $size, $fmt
    $tg = [System.Drawing.Graphics]::FromImage($target)
    $tg.Clear([System.Drawing.Color]::Transparent)
    $tg.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $tg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $tg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $tg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    # A hair of padding: a mark drawn hard to the icon edge looks cramped against the
    # buttons either side of it.
    $pad = [float]($size / 16.0)
    $tg.DrawImage($work, $pad, $pad, [float]($size - (2 * $pad)), [float]($size - (2 * $pad)))
    $tg.Dispose()

    $target.Save((Join-Path $OutDir "stamp$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $target.Dispose()
    "stamp$size.png"
}

$work.Dispose()
