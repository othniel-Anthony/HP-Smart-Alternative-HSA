. "$PSScriptRoot\lib.ps1"
$results = New-Object System.Collections.ArrayList
function Check([bool]$ok, [string]$what) { [void]$results.Add([pscustomobject]@{ OK = $ok; Check = $what }); Write-Host ("{0}  {1}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $what) }
function Events([string]$out) { $f = "$out\events.log"; if (Test-Path $f) { Get-Content $f } else { @() } }
function Has($root, [string]$text, [int]$sec = 8) { return [bool](Find-El $root $text $sec) }

$data = Join-Path $env:TEMP 'hsa-ui-data'; if (Test-Path $data) { Remove-Item $data -Recurse -Force }
New-Item -ItemType Directory $data | Out-Null
$out = Join-Path $env:TEMP 'hsa-ui-fake'
$fake = Start-Fake 8631 $out
$app = Start-App $data
try {
  $win = Get-AppWindow $app

  # ---------------- add by address
  Click-Named $win 'Add printer by address'
  $box = Find-El $win 'e.g. 192.168.1.50' 5
  Check ([bool]$box) 'Add-by-IP dialog opens with an address box'
  Set-Text $box '127.0.0.1:8631'
  Click-Named $win 'Add'
  Check (Has $win 'Fake HP OfficeJet Pro 9999' 15) 'printer appears in the app after Add by IP (host:port)'
  Start-Sleep 3

  # ---------------- home
  Click-Named $win 'Home'
  Check (Has $win 'Black Cartridge') 'Home lists Black Cartridge supply'
  Check (Has $win 'Cyan Cartridge') 'Home lists Cyan Cartridge supply'
  Check (Has $win '72%') 'Home shows 72% for black'
  Check ([bool](Find-Regex $win '^8%\s+\S')) 'Home flags the low cyan cartridge (8% plus a warning mark)'
  Check (Has $win 'Cyan Cartridge is low (8%)' 3) 'Home shows a low-supply alert'
  Check (Has $win 'Warning: Marker supply low' 3) 'Home shows the printer-reported warning'
  Check (-not (Has $win 'Scan\r\nNo scanner found' 1)) 'Home does not claim there is no scanner'

  # ---------------- Home: "Print test page" prints the bundled colour test page, unchanged
  $jobsBeforeHome = @(Events $out | Where-Object { $_ -match 'IPP PRINT' }).Count
  Check ([bool](Find-Btn $win 'Print test page' 5)) 'Home has a "Print test page" button'
  Click-Btn $win 'Print test page'
  $deadline = (Get-Date).AddSeconds(25)
  while ((Get-Date) -lt $deadline -and @(Events $out | Where-Object { $_ -match 'IPP PRINT' }).Count -le $jobsBeforeHome) { Start-Sleep -Milliseconds 400 }
  $homeJob = Events $out | Where-Object { $_ -match 'IPP PRINT' } | Select-Object -Last 1
  Check (@(Events $out | Where-Object { $_ -match 'IPP PRINT' }).Count -eq $jobsBeforeHome + 1) 'one job reaches the printer'
  Check ($homeJob -match 'name=print-color-test-page-basic-1' -and $homeJob -match 'mime=application/pdf') 'it is the PDF "print-color-test-page-basic-1"'
  Check ($homeJob -match 'color=color') 'printed in colour'
  $sent = Get-ChildItem $out -Filter 'job-*.pdf' | Sort-Object LastWriteTime | Select-Object -Last 1
  $orig = Join-Path $Repo 'src\PrintHub.Core\Resources\print-color-test-page-basic-1.pdf'
  Check ((Get-FileHash $sent.FullName).Hash -eq (Get-FileHash $orig).Hash) 'the printer received exactly the same bytes as the original file'
  Check (Wait-Name $win 'Test page sent to' 8) 'the app confirms the page was sent'
  Save-Shot $app 'ui1-home.png'

  # ---------------- printer page
  Click-Named $win 'Printer & supplies'
  Check (Has $win 'CNFAKE123') 'Printer page shows serial number'
  Check (Has $win 'FAKE_2026.1') 'Printer page shows firmware'
  Check (Has $win 'Network scanning (eSCL)') 'Printer page identifies the eSCL scanner'
  Check ([bool](Find-Regex $win 'Held test job' 8)) 'Printer page lists the job waiting on the printer'
  Save-Shot $app 'ui1-printer.png'

  $before = @(Events $out | Where-Object { $_ -match 'IPP PRINT' }).Count
  Click-Named $win 'Print diagnostic page'
  Start-Sleep 4
  $ev = Events $out | Where-Object { $_ -match 'IPP PRINT' }
  Check ($ev.Count -eq $before + 1) 'Print diagnostic page sends one print job to the printer'
  Check ([bool]($ev | Where-Object { $_ -match 'name=HSA test page' -and $_ -match 'mime=application/pdf' })) 'diagnostic page arrives as a PDF named "HSA test page"'

  Click-Named $win 'Find this printer (flash / beep)'
  Start-Sleep 2
  Check ([bool](Events $out | Where-Object { $_ -match 'Identify-Printer actions=flash\+sound' })) 'Find-my-printer sends Identify-Printer'

  Click-Named $win 'Cancel job'
  Start-Sleep 2
  Check ([bool](Events $out | Where-Object { $_ -match 'Cancel-Job id=9001' })) 'Cancel job sends Cancel-Job for the right job id'

  # ---------------- printer web page (WebView2)
  Click-Named $win 'Printer web page'
  Start-Sleep 8
  $web = Events $out | Where-Object { $_ -match 'HTTP GET / ua=Mozilla' }
  Check ([bool]$web) 'embedded browser requested the printer web page'
  Save-Shot $app 'ui1-web.png'
}
finally { Stop-Mine $app; Stop-Mine $fake }

''
$fail = @($results | Where-Object { -not $_.OK })
"{0} checks, {1} passed, {2} failed" -f $results.Count, ($results.Count - $fail.Count), $fail.Count
