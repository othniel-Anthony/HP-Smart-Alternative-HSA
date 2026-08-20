# Generate-PrinterIcons.ps1
# Creates six procedural icons for the most common HP printer families.
# Drop a real product photo in this folder named "<normalized-model>.png" to
# override the procedural icon for a specific model.
# Run with:  powershell -ExecutionPolicy Bypass -File Generate-PrinterIcons.ps1
Add-Type -AssemblyName System.Drawing

$outDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$size = 128

function New-Icon {
    param(
        [System.Drawing.Color]$Bg,
        [string]$Label,
        [System.Drawing.Color]$Fg
    )

    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias

    # Rounded-square background
    $r = 24
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r, $r, 180, 90)
    $path.AddArc($size - $r, 0, $r, $r, 270, 90)
    $path.AddArc($size - $r, $size - $r, $r, $r, 0, 90)
    $path.AddArc(0, $size - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $bgBrush = New-Object System.Drawing.SolidBrush($Bg)
    $g.FillPath($bgBrush, $path)

    # White printer silhouette
    $white = New-Object System.Drawing.SolidBrush($Fg)
    # Paper coming out the top
    $g.FillRectangle($white, 52, 24, 24, 8)
    # Top output slot
    $g.FillRectangle($white, 40, 32, 48, 8)
    # Body
    $g.FillRectangle($white, 30, 40, 68, 40)
    # Bottom paper tray
    $g.FillRectangle($white, 36, 80, 56, 6)
    # Two tiny status lights
    $lightBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(220, $Fg))
    $g.FillEllipse($lightBrush, 82, 48, 6, 6)
    $g.FillEllipse($lightBrush, 82, 60, 6, 6)

    # Family label
    $font = New-Object System.Drawing.Font('Segoe UI', 16, [System.Drawing.FontStyle]::Bold)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF(0, 92, $size, 30)
    $g.DrawString($Label, $font, $white, $rect, $sf)

    $g.Dispose()
    $bgBrush.Dispose(); $white.Dispose(); $lightBrush.Dispose(); $font.Dispose(); $sf.Dispose()
    return $bmp
}

# 6 family buckets, each with a distinctive colour and 2-3 char label
$icons = @(
    @{ Name = 'laserjet-mono.png';   R = 32;  G = 32;  B = 36;  Label = 'LJ'  }
    @{ Name = 'laserjet-color.png';  R = 60;  G = 90;  B = 140; Label = 'CLJ' }
    @{ Name = 'officejet.png';       R = 0;   G = 90;  B = 170; Label = 'OJ'  }
    @{ Name = 'envy.png';            R = 30;  G = 130; B = 145; Label = 'EN'  }
    @{ Name = 'smart-tank.png';      R = 50;  G = 130; B = 80;  Label = 'ST'  }
    @{ Name = 'generic.png';         R = 100; G = 100; B = 100; Label = 'HP'  }
)

foreach ($i in $icons) {
    $bg = [System.Drawing.Color]::FromArgb(255, $i.R, $i.G, $i.B)
    $bmp = New-Icon -Bg $bg -Label $i.Label -Fg ([System.Drawing.Color]::White)
    $path = Join-Path $outDir $i.Name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output ("wrote: {0}" -f $path)
}
