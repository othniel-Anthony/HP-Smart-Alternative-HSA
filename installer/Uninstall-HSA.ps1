<#
  Removes HP Smart Alternative (HSA). Your scans (Documents\HSA Scans) and settings (%LOCALAPPDATA%\HP Smart Alternative) are kept
  unless you pass -RemoveData.
#>
param([switch]$RemoveData)
$ErrorActionPreference = 'SilentlyContinue'
$target = Join-Path $env:LOCALAPPDATA 'Programs\HP Smart Alternative'
# only stop the copy installed here (another program called HSA may be running elsewhere)
Get-Process HSA | Where-Object { $_.Path -like "$target*" } | Stop-Process -Force
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'HP Smart Alternative (HSA).lnk') -Force
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HPSmartAlternative' -Recurse -Force
if ($RemoveData) { Remove-Item (Join-Path $env:LOCALAPPDATA 'HP Smart Alternative') -Recurse -Force }

# The uninstaller lives inside the folder it deletes, so remove it from a detached shell.
Start-Process powershell.exe -WindowStyle Hidden -ArgumentList '-NoProfile','-Command',"Start-Sleep 2; Remove-Item -LiteralPath '$target' -Recurse -Force"
Write-Host 'HP Smart Alternative (HSA) was removed.'
