. "$PSScriptRoot\lib.ps1"

function Set-Slider($root, [string]$name, [double]$value) {
  $cond = New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Slider)
  $deadline = (Get-Date).AddSeconds(5)
  do {
    foreach ($e in $root.FindAll($TS::Descendants, $cond)) {
      if ($e.Current.Name -eq $name) { $o = $null; if ($e.TryGetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern, [ref]$o)) { $o.SetValue($value); return $true } }
    }
    Start-Sleep -Milliseconds 250
  } while ((Get-Date) -lt $deadline)
  return $false
}

$img = & "$PSScriptRoot\make-skewed-scan.ps1"
$env2 = New-TestEnv 8691 'edit'
$fake = Start-Fake $env2.Port $env2.Out
$app = Start-App $env2.Data "--import `"$img`""
try {
  $win = Get-AppWindow $app
  Check (Wait-Name $win '^Page 1$' 25) '--import opens the image as page 1 in the Scan editor'
  Save-Shot $app 'ui6-imported.png'

  # ---------------- brightness / contrast sliders (debounced)
  Click-Bar $win 'Adjust'
  Check (Set-Slider $win 'Brightness' 40) 'Brightness slider can be moved'
  Check (Set-Slider $win 'Contrast' -20) 'Contrast slider can be moved'
  Check (Wait-Name $win '^Page 1\s+\S' 8) 'adjusting marks the page as edited'
  Click-Btn $win 'Reset'
  Check (Wait-Name $win '^Page 1$' 8) 'Reset in the Adjust panel returns the page to unedited'
  # close the flyout
  Click-Named $win 'Home'
  Click-Named $win 'Scan'

  # ---------------- straighten + auto-crop on a genuinely crooked page
  Check (Wait-Name $win '^Page 1$' 10) 'page survives navigating away and back'
  Click-Bar $win 'Straighten'
  Check (Wait-Name $win 'Straightened by' 15) 'Straighten detects the tilt and reports the angle'
  Click-Bar $win 'Auto-crop'
  Start-Sleep 2
  Save-Shot $app 'ui6-straight-crop.png'
  Check (Wait-Name $win '^Page 1\s+\S' 5) 'page is marked edited after straighten + auto-crop'

  # ---------------- OCR on the straightened page
  Click-Btn $win 'Copy text'
  Check (Wait-Name $win 'Text on page 1' 30) 'OCR dialog opens for the straightened page'
  $found = $false
  foreach ($e in $win.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
    try { if ($e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit) { $v = $null; if ($e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$v) -and $v.Current.Value -match 'Quarterly') { $found = $true } } } catch { }
  }
  Check $found 'OCR reads "Quarterly" from the crooked, cropped page'
  Click-Last $app 'Close'

  # ---------------- signature dialog (drawing itself needs a real pointer; here: open and cancel without strokes)
  Click-Bar $win 'Sign'
  Check (Wait-Name $win 'Add signature' 6) 'Sign opens the signature dialog'
  Click-Last $app 'Place on page'
  Start-Sleep 1
  Check (Wait-Name $win '^Page 1\s+\S' 3) 'placing an empty signature adds nothing and does not crash'
  Check ($app.HasExited -eq $false) 'app still running'

  # ---------------- undo + save as PNG
  Click-Bar $win 'Undo edits'
  Check (Wait-Name $win '^Page 1$' 8) 'Undo edits restores the original page'
  Select-Combo $win 'Save as' 'PNG'
  Click-Btn $win 'Save'
  Check (Wait-Name $win '^Saved Scan .*\.png' 20) 'Save as PNG reports the file'
  $png = Get-ChildItem $env2.Save -Filter *.png | Select-Object -First 1
  Check ([bool]$png) 'a PNG exists in the save folder'
  if ($png) {
    $b = [IO.File]::ReadAllBytes($png.FullName)
    Check ($b[0] -eq 0x89 -and $b[1] -eq 0x50 -and $b[2] -eq 0x4E -and $b[3] -eq 0x47) 'file is a real PNG'
    $im = [System.Drawing.Image]::FromFile($png.FullName); $size = "$($im.Width)x$($im.Height)"; $im.Dispose()
    Check ($size -eq '1275x1650') "PNG keeps the full original size ($size)"
  }
}
finally { Stop-Mine $app; Stop-Mine $fake }
Summary
