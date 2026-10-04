<#
  Installs HP Smart Alternative (HSA) for the current user (no administrator rights needed):
    - copies HSA.exe to %LOCALAPPDATA%\Programs\HP Smart Alternative
    - adds a Start menu shortcut
    - registers an uninstaller in "Apps & features"
  Run:  powershell -ExecutionPolicy Bypass -File .\Install-HSA.ps1
  (HSA.exe also runs fine on its own, without installing.)
#>
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'HSA.exe'
if (-not (Test-Path $source)) { throw "HSA.exe not found next to this script ($PSScriptRoot)." }

$target = Join-Path $env:LOCALAPPDATA 'Programs\HP Smart Alternative'
$version = (Get-Item $source).VersionInfo.ProductVersion

Get-Process HSA -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$target*" } | Stop-Process -Force
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item $source $target -Force
# Windows tags files that came from the internet and asks "are you sure?" before running them. You chose to install this one,
# so clear the tag on the installed copy; the Start menu shortcut then opens it without the prompt.
Unblock-File -LiteralPath (Join-Path $target 'HSA.exe')
Copy-Item (Join-Path $PSScriptRoot 'Uninstall-HSA.ps1') $target -Force

# The first start of a single-file exe unpacks about 160 MB (several seconds). Do that now, once, so the first real launch is quick.
try {
  $warm = Start-Process -FilePath (Join-Path $target 'HSA.exe') -ArgumentList '--prewarm' -PassThru -WindowStyle Hidden
  if (-not $warm.WaitForExit(120000)) { $warm.Kill() }
} catch { }

# Start menu shortcut (the icon is embedded in HSA.exe)
$programs = [Environment]::GetFolderPath('Programs')
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut((Join-Path $programs 'HP Smart Alternative (HSA).lnk'))
$lnk.TargetPath = Join-Path $target 'HSA.exe'
$lnk.WorkingDirectory = $target
$lnk.IconLocation = (Join-Path $target 'HSA.exe') + ',0'
$lnk.Description = 'Print, scan and copy with your printer'
$lnk.Save()

# Apps & features entry (per user)
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HPSmartAlternative'
New-Item -Path $key -Force | Out-Null
$props = @{
  DisplayName     = 'HP Smart Alternative (HSA)'
  DisplayVersion  = $version
  Publisher       = 'HSA'
  InstallLocation = $target
  DisplayIcon     = (Join-Path $target 'HSA.exe')
  UninstallString = "powershell.exe -ExecutionPolicy Bypass -File `"$(Join-Path $target 'Uninstall-HSA.ps1')`""
  NoModify        = 1
  NoRepair        = 1
}
foreach ($k in $props.Keys) { New-ItemProperty -Path $key -Name $k -Value $props[$k] -Force | Out-Null }

Write-Host "HP Smart Alternative (HSA) $version installed to $target"
Write-Host "Start it from the Start menu. Windows Firewall may ask once to allow printer discovery on private networks: choose 'Private'."
