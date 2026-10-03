. "$PSScriptRoot\uia.ps1"
Add-Type -AssemblyName System.Drawing

$script:results = New-Object System.Collections.ArrayList
function Check([bool]$ok, [string]$what) { [void]$script:results.Add([pscustomobject]@{ OK = $ok; Check = $what }); Write-Host ("{0}  {1}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $what) }
function Summary { ''; $fail = @($script:results | Where-Object { -not $_.OK }); "{0} checks, {1} passed, {2} failed" -f $script:results.Count, ($script:results.Count - $fail.Count), $fail.Count; $fail | ForEach-Object { "  FAILED: " + $_.Check } }
function Events([string]$out) { $f = "$out\events.log"; if (Test-Path $f) { Get-Content $f } else { @() } }

# a button (by exact name) - avoids clashing with nav items / titles that share the text
function Find-Btn($root, [string]$name, [int]$TimeoutSec = 8) {
  $c1 = New-Object System.Windows.Automation.PropertyCondition ($AE::NameProperty), $name
  $c2 = New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Button)
  $cond = New-Object System.Windows.Automation.AndCondition $c1, $c2
  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  do {
    foreach ($el in $root.FindAll($TS::Descendants, $cond)) {
      # never pick the window's own caption buttons (Close / Minimize / Maximize)
      if ($el.Current.AutomationId -in 'Close', 'Minimize', 'Maximize') { continue }
      return $el
    }
    Start-Sleep -Milliseconds 250
  } while ((Get-Date) -lt $deadline)
  return $null
}
function Click-Btn($root, [string]$name, [int]$TimeoutSec = 8) { $b = Find-Btn $root $name $TimeoutSec; if (-not $b) { throw "button not found: '$name'" }; Click-El $b }

# toolbar commands may sit in the overflow menu when the window is narrow
function Click-Bar($win, [string]$name) {
  $b = Find-Btn $win $name 2
  if ($b -and -not $b.Current.IsOffscreen) { Click-El $b; return }
  $more = Find-Btn $win 'More options' 3
  if (-not $more) { throw "toolbar command not found: $name" }
  Click-El $more; Start-Sleep -Milliseconds 400
  $item = Find-InApp $script:AppProc $name 3
  if (-not $item) { throw "overflow command not found: $name" }
  Click-El $item
}

function Wait-Name($root, [string]$regex, [int]$sec = 20) { return [bool](Find-Regex $root $regex $sec) }
function Gone($root, [string]$regex, [int]$sec = 4) {
  $deadline = (Get-Date).AddSeconds($sec)
  do { if (-not (Find-Regex $root $regex 1)) { return $true } } while ((Get-Date) -lt $deadline)
  return $false
}

function New-TestEnv([int]$Port, [string]$Tag) {
  $data = Join-Path $env:TEMP "hsa-ui-data-$Tag"; if (Test-Path $data) { Remove-Item $data -Recurse -Force }
  New-Item -ItemType Directory $data | Out-Null
  $save = Join-Path $env:TEMP "hsa-ui-save-$Tag"; if (Test-Path $save) { Remove-Item $save -Recurse -Force }
  New-Item -ItemType Directory $save | Out-Null
  $addr = "127.0.0.1:$Port"
  $settings = @{
    SaveFolder = $save; SelectedPrinterId = $addr; Theme = 'System'
    Printers = @(@{ Id = $addr; Name = 'Fake HP OfficeJet Pro 9999'; Manufacturer = 'HP'; Address = $addr
        IppUri = "http://$addr/ipp/print"; EsclUri = "http://$addr/eSCL/"; WebUri = "http://$addr/" })
  }
  ($settings | ConvertTo-Json -Depth 5) | Set-Content (Join-Path $data 'settings.json') -Encoding UTF8
  [pscustomobject]@{ Data = $data; Save = $save; Out = (Join-Path $env:TEMP "hsa-ui-fake-$Tag"); Port = $Port }
}


# currently selected item of a combo box (by its header name)
function Combo-Value($root, [string]$comboName) {
  $combo = Find-El $root $comboName 6
  if (-not $combo) { return $null }
  $o = $null
  if ($combo.TryGetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern, [ref]$o)) {
    $sel = $o.Current.GetSelection()
    if ($sel.Count -gt 0) { return $sel[0].Current.Name }
  }
  return $null
}


# dialog buttons often share a name with a page button ("Clear all", "Print", "Save"); the dialog's is the last one in the tree
function Click-Last($proc, [string]$name, [int]$TimeoutSec = 6) {
  $pidCond = New-Object System.Windows.Automation.PropertyCondition ($AE::ProcessIdProperty), $proc.Id
  $c1 = New-Object System.Windows.Automation.PropertyCondition ($AE::NameProperty), $name
  $c2 = New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Button)
  $cond = New-Object System.Windows.Automation.AndCondition $c1, $c2
  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  do {
    $found = @()
    foreach ($top in $AE::RootElement.FindAll($TS::Children, $pidCond)) {
      foreach ($el in $top.FindAll($TS::Descendants, $cond)) { if ($el.Current.AutomationId -notin 'Close', 'Minimize', 'Maximize') { $found += $el } }
    }
    if ($found.Count -gt 0) {
      # a ContentDialog's own buttons have these ids; prefer them over a same-named button on the page behind
      $dlg = @($found | Where-Object { $_.Current.AutomationId -in 'PrimaryButton', 'SecondaryButton', 'CloseButton' })
      if ($dlg.Count -gt 0) { Click-El $dlg[0] } else { Click-El $found[-1] }
      return
    }
    Start-Sleep -Milliseconds 250
  } while ((Get-Date) -lt $deadline)
  throw "dialog button not found: $name"
}

function Set-Edit-By-Name($root, [string]$name, [string]$value, [int]$TimeoutSec = 6) {
  $cond = New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Edit)
  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  do {
    try { foreach ($e in $root.FindAll($TS::Descendants, $cond)) { if ($e.Current.Name -eq $name) { Set-Text $e $value; return $true } } } catch { }
    Start-Sleep -Milliseconds 300
  } while ((Get-Date) -lt $deadline)
  return $false
}

function Settings-Json($env2) { Get-Content (Join-Path $env2.Data 'settings.json') -Raw | ConvertFrom-Json }


# WinUI NumberBox shows up as a Spinner with a RangeValue pattern
function Set-Number($root, [string]$name, [double]$value) {
  $cond = New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Spinner)
  foreach ($e in $root.FindAll($TS::Descendants, $cond)) {
    if ($e.Current.Name -eq $name) {
      $o = $null
      if ($e.TryGetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern, [ref]$o)) { $o.SetValue($value); return $true }
    }
  }
  return $false
}
function Get-Number($root, [string]$name) {
  $cond = New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Spinner)
  foreach ($e in $root.FindAll($TS::Descendants, $cond)) {
    if ($e.Current.Name -eq $name) { $o = $null; if ($e.TryGetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern, [ref]$o)) { return $o.Current.Value } }
  }
  return $null
}


function Get-Edit-Value($root, [string]$name) {
  $cond = New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Edit)
  foreach ($e in $root.FindAll($TS::Descendants, $cond)) {
    if ($e.Current.Name -eq $name) { $v = $null; if ($e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$v)) { return $v.Current.Value } }
  }
  return $null
}
