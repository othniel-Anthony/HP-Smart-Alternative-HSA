# Build-Icon.ps1 — converts icon-source.png into a multi-resolution .ico
# (16, 24, 32, 48, 64, 128, 256) and writes it next to itself as icon.ico.
# Run with: powershell -ExecutionPolicy Bypass -File Build-Icon.ps1
Add-Type -AssemblyName System.Drawing

$here   = Split-Path -Parent $MyInvocation.MyCommand.Path
$src    = Join-Path $here 'icon-source.png'
$dst    = Join-Path $here 'icon.ico'

if (-not (Test-Path $src)) {
    Write-Error "icon-source.png not found in $here"
    exit 1
}

# Required Windows app-icon sizes
$sizes = 16, 24, 32, 48, 64, 128, 256

# Render each size to a PNG byte array
$pngBytes = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality= [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $srcImg = [System.Drawing.Image]::FromFile($src)
    $g.DrawImage($srcImg, 0, 0, $s, $s)
    $g.Dispose()
    $srcImg.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngBytes += , $ms.ToArray()
    $ms.Dispose()
}

# Build the .ico file:
#   ICONDIR (6 bytes)
#   ICONDIRENTRY[count] (16 bytes each)
#   image data (each entry's PNG bytes)
$out = New-Object System.IO.MemoryStream
$bw  = New-Object System.IO.BinaryWriter($out)

# ICONDIR
$bw.Write([UInt16]0)                 # reserved
$bw.Write([UInt16]1)                 # type: 1 = icon
$bw.Write([UInt16]$sizes.Length)     # count

# Reserve space for the ICONDIRENTRYs — we'll fill them in after we know the offsets
$entriesStart = $out.Position
$bw.Write([Byte[]](New-Object byte[] (16 * $sizes.Length)))

# Write the PNG image data and remember the offsets
$offsets = @()
for ($i = 0; $i -lt $pngBytes.Length; $i++) {
    $offsets += $out.Position
    $bw.Write($pngBytes[$i])
}

# Now go back and write the ICONDIRENTRYs
$out.Position = $entriesStart
for ($i = 0; $i -lt $sizes.Length; $i++) {
    $s = $sizes[$i]
    $w = if ($s -ge 256) { 0 } else { [byte]$s }   # 0 in .ico == 256
    $h = if ($s -ge 256) { 0 } else { [byte]$s }
    $bw.Write([byte]$w)                          # width
    $bw.Write([byte]$h)                          # height
    $bw.Write([byte]0)                           # color count (0 = >=8bpp)
    $bw.Write([byte]0)                           # reserved
    $bw.Write([UInt16]1)                         # color planes
    $bw.Write([UInt16]32)                        # bits per pixel
    $bw.Write([UInt32]$pngBytes[$i].Length)      # bytes in this image's resource
    $bw.Write([UInt32]$offsets[$i])              # offset from start of file
}

$bw.Flush()
[byte[]]$icoBytes = $out.ToArray()
$bw.Dispose()
$out.Dispose()

[System.IO.File]::WriteAllBytes($dst, $icoBytes)
Write-Output "Wrote $dst ($($icoBytes.Length) bytes; $($sizes.Length) sizes: $($sizes -join ', '))"
