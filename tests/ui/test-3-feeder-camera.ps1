. "$PSScriptRoot\lib.ps1"
$env2 = New-TestEnv 8651 'feeder'
$fake = Start-Fake $env2.Port $env2.Out
$app = Start-App $env2.Data '--page scan'
try {
  $win = Get-AppWindow $app
  Check (Wait-Name $win 'Marker supply low' 20) 'printer connected'

  # capabilities from the scanner decide which sources are offered
  Select-Combo $win 'Scan from' 'Document feeder, both sides'
  Check ((Combo-Value $win 'Scan from') -eq 'Document feeder, both sides') 'scanner offers (and the app selects) the duplex feeder'
  Select-Combo $win 'Colour' 'Black and white'
  Select-Combo $win 'Save as' 'JPEG'
  Select-Combo $win 'Resolution (dpi)' '150'
  Check ((Combo-Value $win 'Resolution (dpi)') -eq '150') 'resolution list comes from the scanner (150 available)'

  Click-Btn $win 'Scan'
  Check (Wait-Name $win '^Page 6' 60) 'duplex feeder delivers six pages'
  $ev = Events $env2.Out
  Check ([bool]($ev | Where-Object { $_ -match 'source=Feeder color=Grayscale8 dpi=150 duplex=True' })) 'request: feeder + duplex + grayscale (B&W is applied as a filter) + 150 dpi'
  Check (@($ev | Where-Object { $_ -match 'ESCL job=\d+ page \d/6' }).Count -eq 6) 'scanner delivered 6 pages'
  Check ([bool](Find-Btn $win 'Scan another page' 3)) 'button reads "Scan another page"'
  Save-Shot $app 'ui3-feeder.png'

  # ------------------------------------------------ save as separate JPEGs
  Click-Btn $win 'Save'
  Check (Wait-Name $win '^Saved 6 files' 40) 'Save reports six files'
  $jpgs = @(Get-ChildItem $env2.Save -Filter *.jpg)
  Check ($jpgs.Count -eq 6) 'six JPEG files were written'
  if ($jpgs.Count -gt 0) {
    $bytes = [IO.File]::ReadAllBytes($jpgs[0].FullName)
    Check ($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xD8) 'files are real JPEGs'
    $img = [System.Drawing.Image]::FromFile($jpgs[0].FullName)
    $isBw = $true
    $bmp = New-Object System.Drawing.Bitmap $img
    foreach ($pt in @(@(50, 50), @(200, 300), @(400, 100))) { $c = $bmp.GetPixel($pt[0], $pt[1]); if ([math]::Abs($c.R - $c.G) -gt 8 -or [math]::Abs($c.G - $c.B) -gt 8) { $isBw = $false } }
    Check $isBw 'black & white filter produced a neutral (non-coloured) image'
    $bmp.Dispose(); $img.Dispose()
  }
  # names are numbered per page
  Check ([bool]($jpgs | Where-Object { $_.Name -match ' - 1\.jpg$' }) -and [bool]($jpgs | Where-Object { $_.Name -match ' - 6\.jpg$' })) 'files are numbered " - 1" to " - 6"'

  # ------------------------------------------------ reorder is not scriptable; delete + clear all
  Click-Bar $win 'Delete page'
  Check (Wait-Name $win '^Page 5' 5) 'deleting a page renumbers the rest (5 left)'
  Check (Gone $win '^Page 6' 4) 'page 6 no longer exists'
  Click-Btn $win 'Clear all'
  Check (Wait-Name $win 'Clear all pages' 5) 'Clear all asks for confirmation'
  Click-Last $app 'Clear all'
  Check (Gone $win '^Page 1' 6) 'confirming Clear all empties the filmstrip'
  Check ([bool](Find-Btn $win 'Scan' 3)) 'Scan button returns to "Scan"'

  # ------------------------------------------------ camera
  Click-Btn $win 'Camera'
  Start-Sleep 4
  $noCam = Find-Regex $win 'No camera found|Scan with camera' 6
  Check ([bool]$noCam) 'Camera button opens either the camera dialog or a "No camera found" message'
  $cancelled = $false
  foreach ($n in 'Cancel', 'OK') { try { Click-Last $app $n 3; $cancelled = $true; break } catch { } }
  Check $cancelled 'camera dialog can be dismissed'
  Start-Sleep 1
  Check ($app.HasExited -eq $false) 'app is still running after using the camera dialog'
}
finally { Stop-Mine $app; Stop-Mine $fake }
Summary
