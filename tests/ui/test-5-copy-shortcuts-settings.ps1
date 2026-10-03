. "$PSScriptRoot\lib.ps1"

function Click-In-Card($win, [string]$cardTitle, [string]$buttonName) {
  $title = Find-El $win $cardTitle 6
  if (-not $title) { throw "card not found: $cardTitle" }
  $card = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($title)
  $c1 = New-Object System.Windows.Automation.PropertyCondition ($AE::NameProperty), $buttonName
  $btn = $card.FindFirst($TS::Descendants, $c1)
  if (-not $btn) { throw "no '$buttonName' in card '$cardTitle'" }
  Click-El $btn
}

# ======================================================================= COPY
$env2 = New-TestEnv 8671 'rest'
# shortcuts seeded so that nothing opens a viewer window on the desktop
$settings = Get-Content (Join-Path $env2.Data 'settings.json') -Raw | ConvertFrom-Json
$settings | Add-Member -NotePropertyName Shortcuts -NotePropertyValue @(
  @{ Id = 'a1'; Name = 'Receipt'; Glyph = 'x'; Kind = 'Scan'; Source = 'Flatbed'; Color = 'BlackAndWhite'; Dpi = 150; PaperName = 'Auto (full bed)'; Format = 'Pdf'; AutoCrop = $true; Enhance = $false; Searchable = $false; OpenAfter = $false; PrintCopies = 0; CopyCount = 1; IdCard = $false },
  @{ Id = 'a2'; Name = 'Photo'; Glyph = 'x'; Kind = 'Scan'; Source = 'Flatbed'; Color = 'Color'; Dpi = 150; PaperName = 'Auto (full bed)'; Format = 'Jpeg'; AutoCrop = $true; Enhance = $false; Searchable = $false; OpenAfter = $false; PrintCopies = 1; CopyCount = 1; IdCard = $false },
  @{ Id = 'a3'; Name = 'Quick copy'; Glyph = 'x'; Kind = 'Copy'; Source = 'Flatbed'; Color = 'Grayscale'; Dpi = 150; PaperName = 'Auto (full bed)'; Format = 'Pdf'; AutoCrop = $false; Enhance = $false; Searchable = $false; OpenAfter = $false; PrintCopies = 0; CopyCount = 2; IdCard = $false }
) -Force
($settings | ConvertTo-Json -Depth 6) | Set-Content (Join-Path $env2.Data 'settings.json') -Encoding UTF8

$fake = Start-Fake $env2.Port $env2.Out
$app = Start-App $env2.Data '--page copy'
try {
  $win = Get-AppWindow $app
  Check (Wait-Name $win 'Marker supply low' 20) 'printer connected'
  Check ((Find-Btn $win 'Start copy' 5).Current.IsEnabled) 'Copy is enabled because the printer can scan and print'

  Check (Set-Number $win 'Copies' 3) 'copies set to 3'
  Select-Combo $win 'Colour' 'Black and white'
  Select-Combo $win 'Quality' 'Draft'
  Click-Btn $win 'Start copy'
  $deadline = (Get-Date).AddSeconds(40)
  while ((Get-Date) -lt $deadline -and @(Events $env2.Out | Where-Object { $_ -match 'IPP PRINT' }).Count -lt 1) { Start-Sleep -Milliseconds 500 }
  $ev = Events $env2.Out
  Check ([bool]($ev | Where-Object { $_ -match 'ESCL create .*source=Platen color=Grayscale8 dpi=150' })) 'copy scanned in grayscale at draft resolution (150 dpi)'
  $job = $ev | Where-Object { $_ -match 'IPP PRINT' } | Select-Object -First 1
  Check ([bool]$job) 'copy printed the scan'
  Check ($job -match 'copies=3') 'copy count reached the printer (3)'
  Check ($job -match 'color=monochrome') 'copy printed in black and white'
  Start-Sleep 2

  # ---- ID card copy
  $scansBefore = @(Events $env2.Out | Where-Object { $_ -match 'ESCL create' }).Count
  Click-El (Find-El $win 'Copy an ID card (both sides on one page)' 5)
  Check (Wait-Name $win 'Place the front of the card' 4) 'ID card option shows its instructions'
  Click-Btn $win 'Start copy'
  Check (Wait-Name $win 'Flip the card' 40) 'ID card copy asks you to flip the card'
  Click-Last $app 'OK'
  $deadline = (Get-Date).AddSeconds(40)
  while ((Get-Date) -lt $deadline -and @(Events $env2.Out | Where-Object { $_ -match 'ESCL create' }).Count -lt $scansBefore + 2) { Start-Sleep -Milliseconds 500 }
  Start-Sleep 3
  Check (@(Events $env2.Out | Where-Object { $_ -match 'ESCL create' }).Count -eq $scansBefore + 2) 'both sides of the card were scanned'
  Check (@(Events $env2.Out | Where-Object { $_ -match 'IPP PRINT' }).Count -eq 2) 'the two sides were printed as ONE job'

  # ======================================================================= SHORTCUTS
  Click-Named $win 'Shortcuts'
  Check ((Wait-Name $win '^Receipt$' 8) -and (Wait-Name $win '^Photo$' 3) -and (Wait-Name $win '^Quick copy$' 3)) 'shortcut cards are listed'
  Click-In-Card $win 'Receipt' 'Run'
  Check (Wait-Name $win '^Receipt: saved ' 40) 'running "Receipt" reports the saved file'
  $pdf = Get-ChildItem $env2.Save -Filter 'Receipt*.pdf' | Select-Object -First 1
  Check ([bool]$pdf) 'Receipt saved a PDF in the save folder'
  Click-In-Card $win 'Photo' 'Run'
  $deadline = (Get-Date).AddSeconds(40)
  while ((Get-Date) -lt $deadline -and -not (Get-ChildItem $env2.Save -Filter 'Photo*.jpg')) { Start-Sleep -Milliseconds 500 }
  Check ([bool](Get-ChildItem $env2.Save -Filter 'Photo*.jpg')) 'Photo shortcut saved a JPEG'
  Start-Sleep 3
  Check (@(Events $env2.Out | Where-Object { $_ -match 'IPP PRINT' }).Count -eq 3) 'Photo shortcut also printed its copy'
  $before = @(Events $env2.Out | Where-Object { $_ -match 'IPP PRINT' }).Count
  Click-In-Card $win 'Quick copy' 'Run'
  $deadline = (Get-Date).AddSeconds(40)
  while ((Get-Date) -lt $deadline -and @(Events $env2.Out | Where-Object { $_ -match 'IPP PRINT' }).Count -le $before) { Start-Sleep -Milliseconds 500 }
  Check ((Events $env2.Out | Where-Object { $_ -match 'IPP PRINT' } | Select-Object -Last 1) -match 'copies=2') 'copy shortcut printed 2 copies'

  # create / edit / delete
  Click-Btn $win 'New shortcut'
  Check (Set-Edit-By-Name $win 'Name' 'My test shortcut') 'new shortcut dialog takes a name'
  Click-Last $app 'Save'
  Check (Wait-Name $win '^My test shortcut$' 6) 'new shortcut appears as a card'
  Start-Sleep 1
  Check ([bool]((Settings-Json $env2).Shortcuts | Where-Object { $_.Name -eq 'My test shortcut' })) 'new shortcut was saved to settings.json'
  Click-In-Card $win 'My test shortcut' 'Edit'
  Check (Set-Edit-By-Name $win 'Name' 'Renamed shortcut') 'edit dialog opens with the existing values'
  Click-Last $app 'Save'
  Check (Wait-Name $win '^Renamed shortcut$' 6) 'edited name is shown'
  Click-In-Card $win 'Renamed shortcut' 'Delete shortcut Renamed shortcut'
  Check (Wait-Name $win 'Delete shortcut\?' 5) 'delete asks for confirmation'
  Click-Last $app 'Delete'
  Check (Gone $win '^Renamed shortcut$' 6) 'deleted shortcut disappears'
  Save-Shot $app 'ui5-shortcuts.png'

  # ======================================================================= HOME TILES
  Click-Named $win 'Home'
  Click-Btn $win 'Print'
  Check (Wait-Name $win 'Add documents or photos' 6) 'Home tile "Print" opens the Print page'
  Click-Named $win 'Home'
  Click-Btn $win 'Copy'
  Check (Wait-Name $win 'Start copy' 6) 'Home tile "Copy" opens the Copy page'
  Click-Named $win 'Home'
  Click-Btn $win 'Printer web page'
  Check (Wait-Name $win 'Open in browser' 8) 'Home tile "Printer web page" opens the web page'

  # ======================================================================= SETTINGS
  Click-Named $win 'Settings'
  Check (Wait-Name $win 'HP Smart Alternative \(HSA\) \d+\.\d+\.\d+' 8) 'About shows the app name and version'
  Select-Combo $win 'Theme' 'Dark'
  Start-Sleep 1
  Check ((Settings-Json $env2).Theme -eq 'Dark') 'theme choice is saved'
  Select-Combo $win 'Print using' 'Always directly (IPP)'
  Start-Sleep 1
  Check ((Settings-Json $env2).PrintRoute -eq 'DirectIpp') 'print route choice is saved'
  Click-El (Find-El $win 'Show the file in File Explorer after saving a scan' 5)
  Start-Sleep 1
  Check ((Settings-Json $env2).OpenFolderAfterSave -eq $true) 'checkbox setting is saved'
  $sw = Find-El $win 'Prefer USB when a printer is connected by both USB and network' 5 'Contains'
  Click-El $sw
  Start-Sleep 2
  Check ((Settings-Json $env2).PreferUsb -eq $true) 'Prefer USB switch is saved'
  Click-Btn $win 'Use default'
  Start-Sleep 1
  Check ($null -eq (Settings-Json $env2).SaveFolder) 'Use default clears the custom save folder'
  Check ((Get-Edit-Value $win 'Save scans in') -match 'Documents.HSA Scans$') 'save folder box shows the default folder (Documents\HSA Scans)'
  Check (Wait-Name $win 'Fake HP OfficeJet Pro 9999 +\(127\.0\.0\.1:8671\)' 5) 'printers added by address are listed'
  # USB diagnostics
  $usbBox = $null
  foreach ($ed in $win.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Edit)))) {
    $v = $null; if ($ed.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$v)) { if ($v.Current.Value -match 'USB web interface|03F0|Describe|MI_|connected now') { $usbBox = $v.Current.Value } }
  }
  Check ([bool]$usbBox) 'USB connections panel lists interfaces (or says none were found)'
  Save-Shot $app 'ui5-settings.png'
  Click-Btn $win 'Remove'
  Start-Sleep 2
  Check (@((Settings-Json $env2).Printers).Count -eq 0) 'removing a saved printer updates settings.json'

  # ======================================================================= ADD BY IP FAILURE
  Click-Named $win 'Home'
  Click-Named $win 'Add printer by address'
  Set-Text (Find-El $win 'e.g. 192.168.1.50' 5) '10.254.254.254:9'
  Click-Last $app 'Add'
  Check (Wait-Name $win 'Nothing answered at 10\.254\.254\.254:9' 25) 'a wrong address gives a clear message and does not hang'
}
finally { Stop-Mine $app; Stop-Mine $fake }
Summary
