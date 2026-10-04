# Installs the packaged zip for the current user, starts the installed copy, uninstalls, and checks nothing is left behind.
# Touches only: %LOCALAPPDATA%\Programs\HP Smart Alternative, one Start-menu shortcut, one HKCU Uninstall key.
param([string]$Zip = (Get-ChildItem (Join-Path $PSScriptRoot '..\..\dist') -Filter 'HSA-*-win-x64.zip' | Sort-Object LastWriteTime | Select-Object -Last 1).FullName)
$ErrorActionPreference = 'Stop'
$pass = 0; $fail = 0
function Check([bool]$ok, [string]$what) { if ($ok) { $script:pass++ } else { $script:fail++ }; Write-Host ("{0}  {1}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $what) }

$target = Join-Path $env:LOCALAPPDATA 'Programs\HP Smart Alternative'
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'HP Smart Alternative (HSA).lnk'
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HPSmartAlternative'
if (Test-Path $target) { throw "Refusing to run: $target already exists (an installed copy)." }

$others = @(Get-Process HSA -ErrorAction SilentlyContinue | Where-Object { $_.Path -notlike "$target*" } | ForEach-Object Id)   # e.g. another program that is also called HSA

$work = Join-Path $env:TEMP 'hsa-installer-test'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
Expand-Archive $Zip $work
Check ((Test-Path "$work\HSA.exe") -and (Test-Path "$work\Install-HSA.ps1") -and (Test-Path "$work\Uninstall-HSA.ps1")) 'zip contains HSA.exe and both scripts'
# make the extracted exe look like a browser download (Mark of the Web), which is what triggers Windows' "are you sure?" prompt
Set-Content -LiteralPath "$work\HSA.exe" -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"
Check ($null -ne (Get-Content -LiteralPath "$work\HSA.exe" -Stream Zone.Identifier -ErrorAction SilentlyContinue)) 'test setup: the source exe is marked as downloaded from the internet'

# ---------------------------------------------------------------- install
& powershell -NoProfile -ExecutionPolicy Bypass -File "$work\Install-HSA.ps1" | Out-Null
Check (Test-Path "$target\HSA.exe") 'HSA.exe is copied to Programs\HP Smart Alternative'
Check (Test-Path "$target\Uninstall-HSA.ps1") 'uninstaller is installed next to it'
Check (Test-Path $lnk) 'Start-menu shortcut exists'
Check ($null -eq (Get-Content -LiteralPath "$target\HSA.exe" -Stream Zone.Identifier -ErrorAction SilentlyContinue)) 'installed exe is no longer marked as downloaded, so it starts without the "are you sure?" prompt'
$ws = New-Object -ComObject WScript.Shell
$sc = $ws.CreateShortcut($lnk)
Check ($sc.TargetPath -eq "$target\HSA.exe") 'shortcut points at the installed exe'
Check ($sc.IconLocation -like '*HSA.exe*') 'shortcut uses the icon embedded in the exe'
$reg = Get-ItemProperty $key -ErrorAction SilentlyContinue
Check ([bool]$reg -and $reg.DisplayName -eq 'HP Smart Alternative (HSA)') 'listed in Apps & features'
$ver = (Get-Item "$work\HSA.exe").VersionInfo.ProductVersion
Check ($reg.DisplayVersion -eq $ver) "Apps & features shows the version ($ver)"

# ---------------------------------------------------------------- run the installed copy
$env:HSA_DATA_DIR = Join-Path $env:TEMP 'hsa-installer-data'
$p = Start-Process "$target\HSA.exe" -PassThru
$up = $false
for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 500; $p.Refresh(); if ($p.HasExited) { break }; if ($p.MainWindowHandle -ne 0) { $up = $true; break } }
Check $up 'installed app starts and shows its window'
Check ($p.MainWindowTitle -eq 'HP Smart Alternative (HSA)') 'window title is the product name'
Start-Sleep 3

# ---------------------------------------------------------------- uninstall (app still running: it must be stopped)
& powershell -NoProfile -ExecutionPolicy Bypass -File "$target\Uninstall-HSA.ps1" | Out-Null
for ($i = 0; $i -lt 20 -and (Test-Path $target); $i++) { Start-Sleep -Milliseconds 500 }
Check (-not (Test-Path $target)) 'install folder is removed'
Check (-not (Test-Path $lnk)) 'Start-menu shortcut is removed'
Check (-not (Test-Path $key)) 'Apps & features entry is removed'
$p.Refresh(); Check $p.HasExited 'the running installed copy was stopped by the uninstaller'
$stillThere = @(Get-Process HSA -ErrorAction SilentlyContinue | ForEach-Object Id)
Check (-not ($others | Where-Object { $_ -notin $stillThere })) 'a different program named HSA that was already running was NOT touched'

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $env:HSA_DATA_DIR -Recurse -Force -ErrorAction SilentlyContinue
"{0} checks, {1} passed, {2} failed" -f ($pass + $fail), $pass, $fail
