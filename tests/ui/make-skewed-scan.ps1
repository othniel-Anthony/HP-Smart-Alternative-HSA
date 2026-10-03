# Makes %TEMP%\hsa-ui-files\skewed.jpg : a document lying 2.5 degrees crooked on a dark scanner bed.
param([string]$OutDir = (Join-Path $env:TEMP 'hsa-ui-files'))
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null
$bmp = New-Object System.Drawing.Bitmap 1275, 1650
$bmp.SetResolution(150, 150)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAlias'
$g.Clear([System.Drawing.Color]::FromArgb(45, 45, 48))
$g.TranslateTransform(640, 830); $g.RotateTransform(2.5); $g.TranslateTransform(-640, -830)
$g.FillRectangle([System.Drawing.Brushes]::OldLace, 150, 180, 975, 1290)
$title = New-Object System.Drawing.Font('Georgia', 40, [System.Drawing.FontStyle]::Bold)
$body = New-Object System.Drawing.Font('Georgia', 22)
$g.DrawString('Quarterly Report', $title, [System.Drawing.Brushes]::Black, 210, 250)
$y = 360
foreach ($i in 1..16) { $g.DrawString("Line $i - The quick brown fox jumps over the lazy dog.", $body, [System.Drawing.Brushes]::Black, 210, $y); $y += 54 }
$g.Dispose()
$bmp.Save((Join-Path $OutDir 'skewed.jpg'), [System.Drawing.Imaging.ImageFormat]::Jpeg)
$bmp.Dispose()
Write-Output (Join-Path $OutDir 'skewed.jpg')
