<#
  End-to-end UI tests: drives the real app with Windows UI Automation against a fake network printer
  (tests/FakePrinter: IPP + eSCL + web page + Bonjour). Uses a throw-away data folder (HSA_DATA_DIR), never your settings.
  Build first:  dotnet build src/PrintHub.App -p:Platform=x64 ; dotnet build tests/FakePrinter
  Run:          powershell -ExecutionPolicy Bypass -File tests\ui\run-all.ps1
  Needs: Python 3 (only to generate sample files) and a desktop session (the app window must be able to open).
#>
$ErrorActionPreference = 'Continue'
python "$PSScriptRoot\make-test-files.py" | Out-Null
$total = 0; $failed = 0
foreach ($t in Get-ChildItem "$PSScriptRoot\test-*.ps1" | Sort-Object Name) {
  Write-Host "`n=== $($t.BaseName) ===" -ForegroundColor Cyan
  $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $t.FullName 2>&1
  $out | ForEach-Object { Write-Host $_ }
  $line = $out | Select-String -Pattern '(\d+) checks, (\d+) passed, (\d+) failed' | Select-Object -Last 1
  if ($line) { $total += [int]$line.Matches[0].Groups[1].Value; $failed += [int]$line.Matches[0].Groups[3].Value } else { $failed++ ; Write-Host 'no summary line (script error)' -ForegroundColor Red }
}
Write-Host "`nALL UI TESTS: $total checks, $failed failed" -ForegroundColor $(if ($failed) { 'Red' } else { 'Green' })
exit $failed
