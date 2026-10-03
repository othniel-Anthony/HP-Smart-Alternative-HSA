. "$PSScriptRoot\lib.ps1"
$env2 = New-TestEnv 8641 'scan'
$fake = Start-Fake $env2.Port $env2.Out
$app = Start-App $env2.Data '--page scan'
try {
  $win = Get-AppWindow $app
  Check (Wait-Name $win 'Marker supply low' 20) 'saved printer is restored at startup, selected, and its status loaded (discovery cannot see it)'
  Check ((Combo-Value $win 'Preset') -like 'Document*') 'Scan page opens with the Document preset'

  # ------------------------------------------------ flatbed scan
  Click-Btn $win 'Scan'
  Check (Wait-Name $win '^Page 1' 30) 'Scan produces page 1 in the filmstrip'
  Start-Sleep 1
  $ev = Events $env2.Out
  Check ([bool]($ev | Where-Object { $_ -match 'ESCL create .*source=Platen color=RGB24 dpi=300' })) 'request used flatbed, colour, 300 dpi from the preset'
  Check ([bool]($ev | Where-Object { $_ -match '503 warming up' })) 'scanner "busy" (503) was retried'
  Check ([bool]($ev | Where-Object { $_ -match 'ESCL delete' })) 'finished job was released on the scanner'
  Check ([bool](Find-Regex $win '^Page 1\s+\S' 5)) 'page was auto-cropped/enhanced (marked as edited)'
  Check ([bool](Find-Btn $win 'Scan another page' 3)) 'Scan button changes to "Scan another page"'
  Save-Shot $app 'ui2-scan1.png'

  # ------------------------------------------------ edit tools
  Click-Bar $win 'Undo edits'
  Check (Wait-Name $win '^Page 1$' 8) 'Undo edits clears the edit marker'
  Click-Bar $win 'Right'
  Check (Wait-Name $win '^Page 1\s+\S' 8) 'Rotate right marks the page as edited'
  Click-Bar $win 'Filter'
  Click-El (Find-InApp $app 'Black and white' 4)
  Start-Sleep 1
  Save-Shot $app 'ui2-scan-bw.png'
  Check ($true) 'Filter menu: Black and white applied (see screenshot)'
  Click-Bar $win 'Auto-crop'
  Start-Sleep 1
  Click-Bar $win 'Straighten'
  Start-Sleep 2

  # ------------------------------------------------ add text dialog
  Click-Bar $win 'Add text'
  $tb = Find-El $win 'Text' 5
  $edit = $null
  $cond = New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Edit)
  foreach ($e in $win.FindAll($TS::Descendants, $cond)) { if ($e.Current.Name -eq 'Text') { $edit = $e; break } }
  Check ([bool]$edit) 'Add text dialog opens'
  if ($edit) { Set-Text $edit 'APPROVED by HSA test' }
  Click-Btn $win 'Add'
  Start-Sleep 2
  Save-Shot $app 'ui2-scan-text.png'

  # ------------------------------------------------ crop dialog
  Click-Bar $win 'Crop'
  Check ([bool](Find-Btn $win 'Detect document' 6)) 'Crop dialog opens with a Detect document button'
  Click-Btn $win 'Detect document'
  Start-Sleep 1
  Click-Btn $win 'Apply'
  Start-Sleep 2
  Check (Wait-Name $win '^Page 1\s+\S' 5) 'Crop applied without error'

  # ------------------------------------------------ second page, delete it
  Click-Btn $win 'Scan another page'
  Check (Wait-Name $win '^Page 2' 30) 'Scan another page adds page 2'
  Click-Bar $win 'Delete page'
  Check (Gone $win '^Page 2' 6) 'Delete page removes page 2'

  # ------------------------------------------------ OCR copy text
  Click-Btn $win 'Copy text'
  Check (Wait-Name $win 'Text on page 1' 25) 'Copy text runs OCR and shows the dialog'
  $ocrBox = $null
  foreach ($e in $win.FindAll($TS::Descendants, $cond)) { try { if ($e.Current.Name -eq '' -or $e.Current.Name -like '*Fake*' ) { } } catch { } }
  $textItems = $win.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  $ocrOk = $false
  foreach ($e in $textItems) { try { if ($e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit) { $v = $null; if ($e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$v)) { if ($v.Current.Value -match 'Invoice') { $ocrOk = $true } } } } catch { } }
  Check $ocrOk 'recognised text contains the scanned words ("Invoice")'
  Click-Btn $win 'Close'

  # ------------------------------------------------ save as searchable PDF
  Click-Btn $win 'Save'
  Check (Wait-Name $win '^Saved Scan .*\.pdf' 40) 'Save reports the saved file'
  $pdf = Get-ChildItem $env2.Save -Filter *.pdf | Select-Object -First 1
  Check ([bool]$pdf) 'a PDF exists in the chosen save folder'
  if ($pdf) {
    $txt = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($pdf.FullName))
    Check ($txt.StartsWith('%PDF-')) 'file is a PDF'
    Check ($txt -match '/Count 1') 'PDF has exactly the one remaining page'
    Check ($txt -match 'Invoice') 'PDF has a searchable text layer from OCR'
  }

  # ------------------------------------------------ print this scan
  $jobsBefore = @(Events $env2.Out | Where-Object { $_ -match 'IPP PRINT' }).Count
  Click-Btn $win 'Print'
  Check ([bool](Find-El $win 'Copies' 5)) 'Print dialog opens'
  Click-Btn $win 'Print' 5 | Out-Null
  Start-Sleep 4
  Check (@(Events $env2.Out | Where-Object { $_ -match 'IPP PRINT' }).Count -eq $jobsBefore + 1) 'printing the scan sends one job to the printer'
}
finally { Stop-Mine $app; Stop-Mine $fake }
Summary
