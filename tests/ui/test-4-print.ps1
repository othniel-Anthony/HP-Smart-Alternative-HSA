. "$PSScriptRoot\lib.ps1"
$files = Join-Path $env:TEMP 'hsa-ui-files'
$env2 = New-TestEnv 8661 'print'
$fake = Start-Fake $env2.Port $env2.Out
$app = Start-App $env2.Data "--print `"$files\doc3.pdf`" --print `"$files\photo.png`" --print `"$files\note.txt`""
try {
  $win = Get-AppWindow $app
  Check (Wait-Name $win 'Marker supply low' 20) 'printer connected'

  # ---------------- file list + preview
  Check ((Wait-Name $win '^doc3\.pdf$' 8) -and (Wait-Name $win '^photo\.png$' 3) -and (Wait-Name $win '^note\.txt$' 3)) '--print queues all three files on the Print page'
  Check (Wait-Name $win 'Page 1 of 3' 15) 'preview shows the first PDF page ("Page 1 of 3")'
  Click-Btn $win 'Next page'
  Check (Wait-Name $win 'Page 2 of 3' 8) 'Next page moves the preview to page 2'
  Click-Btn $win 'Previous page'
  Check (Wait-Name $win 'Page 1 of 3' 8) 'Previous page goes back'
  Check (Wait-Name $win 'prints directly to the printer' 5) 'route note explains direct printing (no Windows driver)'
  Save-Shot $app 'ui4-print.png'

  # ---------------- remove the txt so the batch is only printable formats
  Click-Btn $win 'Remove note.txt'
  Check (Gone $win '^note\.txt$' 5) 'Remove takes the file off the list'

  # ---------------- options
  Check (Set-Number $win 'Copies' 2) 'Copies box accepts a value'
  Select-Combo $win 'Colour' 'Black and white'
  Select-Combo $win 'Two-sided' 'Flip on long edge'
  Select-Combo $win 'Paper size' 'A4'
  Select-Combo $win 'Fit' 'Fill page (crop edges)'
  Select-Combo $win 'Quality' 'Best'
  Check (Set-Edit-By-Name $win 'Pages' '1-2') 'Pages box accepts a range'
  Save-Shot $app 'ui4-print-opts.png'

  $before = @(Events $env2.Out | Where-Object { $_ -match 'IPP PRINT' }).Count
  Click-Btn $win 'Print'
  Check (Wait-Name $win '^Sent 2 files to Fake HP OfficeJet Pro 9999' 40) 'status says both files were sent'
  $jobs = @(Events $env2.Out | Where-Object { $_ -match 'IPP PRINT' })
  Check ($jobs.Count -eq $before + 2) 'printer received exactly two jobs'
  $pdfJob = $jobs | Where-Object { $_ -match 'name=doc3' } | Select-Object -First 1
  Check ([bool]$pdfJob) 'PDF job arrived under its own file name'
  if ($pdfJob) {
    Check ($pdfJob -match 'mime=application/pdf') 'PDF is sent as application/pdf (original file, not re-rendered)'
    Check ($pdfJob -match 'copies=2') 'copies=2 reached the printer'
    Check ($pdfJob -match 'sides=two-sided-long-edge') 'two-sided long edge reached the printer'
    Check ($pdfJob -match 'color=monochrome') 'black & white reached the printer'
    Check ($pdfJob -match 'media=iso_a4_210x297mm') 'A4 paper reached the printer'
    Check ($pdfJob -match 'scaling=fill') '"Fill page" mapped to print-scaling=fill'
    Check ($pdfJob -match 'quality=5') 'Best quality mapped to print-quality=5'
    Check ($pdfJob -match 'ranges=') 'page range reached the printer'
  }
  $png = $jobs | Where-Object { $_ -match 'name=photo' } | Select-Object -First 1
  Check ([bool]($png -and $png -match 'mime=application/pdf')) 'PNG photo is converted to a PDF page for the printer'
}
finally { Stop-Mine $app; Stop-Mine $fake }

# ---------------- unsupported file type without a Windows driver gives a clear explanation
$env3 = New-TestEnv 8662 'print2'
$fake = Start-Fake $env3.Port $env3.Out
$app = Start-App $env3.Data "--print `"$files\note.txt`""
try {
  $win = Get-AppWindow $app
  Wait-Name $win 'Marker supply low' 20 | Out-Null
  Click-Btn $win 'Print'
  Check (Wait-Name $win "Couldn't print note.txt" 15) 'a .txt file shows a clear "couldn''t print" dialog instead of failing silently'
  Check (Wait-Name $win 'Windows printer driver' 3) 'the explanation mentions the missing Windows printer driver'
  Click-Last $app 'OK'
  Check (@(Events $env3.Out | Where-Object { $_ -match 'IPP PRINT' }).Count -eq 0) 'nothing was sent to the printer'
}
finally { Stop-Mine $app; Stop-Mine $fake }
Summary
