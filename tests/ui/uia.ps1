Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$script:AE = [System.Windows.Automation.AutomationElement]
$script:TS = [System.Windows.Automation.TreeScope]
$script:Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:Scratch = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Force $script:Scratch | Out-Null

function Start-Fake([int]$Port, [string]$OutDir) {
  if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
  $exe = "$Repo\tests\FakePrinter\bin\Debug\net8.0-windows10.0.19041.0\FakePrinter.exe"
  $p = Start-Process $exe -ArgumentList "$Port", "`"$OutDir`"" -PassThru -WindowStyle Hidden
  for ($i = 0; $i -lt 40 -and -not (Test-Path "$OutDir\events.log" -ErrorAction SilentlyContinue); $i++) {
    Start-Sleep -Milliseconds 250
    try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', $Port); $c.Close(); break } catch { }
  }
  return $p
}

function Start-App([string]$DataDir, [string]$AppArgs = '') {
  $env:HSA_DATA_DIR = $DataDir
  $exe = "$Repo\src\PrintHub.App\bin\x64\Debug\net8.0-windows10.0.19041.0\HSA.exe"
  if ($AppArgs) { $p = Start-Process $exe -ArgumentList $AppArgs -PassThru } else { $p = Start-Process $exe -PassThru }
  for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 500; $p.Refresh(); if ($p.MainWindowHandle -ne 0) { break } }
  Start-Sleep -Seconds 2
  return $p
}

function Get-AppWindow($proc) {
  $script:AppProc = $proc
  $cond = New-Object System.Windows.Automation.PropertyCondition ($AE::ProcessIdProperty), $proc.Id
  for ($i = 0; $i -lt 20; $i++) {
    $w = $AE::RootElement.FindFirst($TS::Children, $cond)
    if ($w) { return $w }
    Start-Sleep -Milliseconds 300
  }
  throw 'app window not found'
}

function Find-El($root, [string]$name, [int]$TimeoutSec = 10, [string]$Match = 'Exact') {
  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  do {
    if ($Match -eq 'Exact') {
      $cond = New-Object System.Windows.Automation.PropertyCondition ($AE::NameProperty), $name
      $el = $root.FindFirst($TS::Descendants, $cond)
    } else {
      $all = $root.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
      $el = $null
      foreach ($e in $all) { try { if ($e.Current.Name -like "*$name*") { $el = $e; break } } catch { } }
    }
    if ($el) { return $el }
    Start-Sleep -Milliseconds 300
  } while ((Get-Date) -lt $deadline)
  return $null
}


function Find-InApp($proc, [string]$name, [int]$TimeoutSec = 6) {
  $pidCond = New-Object System.Windows.Automation.PropertyCondition ($AE::ProcessIdProperty), $proc.Id
  $nameCond = New-Object System.Windows.Automation.PropertyCondition ($AE::NameProperty), $name
  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  do {
    $tops = $AE::RootElement.FindAll($TS::Children, $pidCond)
    foreach ($top in $tops) {
      $el = $top.FindFirst($TS::Descendants, $nameCond)
      if ($el) { return $el }
    }
    Start-Sleep -Milliseconds 250
  } while ((Get-Date) -lt $deadline)
  return $null
}
function Click-El($el) {
  if (-not $el) { throw 'element is null' }
  $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
  $cur = $el
  for ($up = 0; $up -lt 5 -and $cur; $up++) {
    foreach ($pat in @([System.Windows.Automation.InvokePattern]::Pattern, [System.Windows.Automation.SelectionItemPattern]::Pattern, [System.Windows.Automation.TogglePattern]::Pattern, [System.Windows.Automation.ExpandCollapsePattern]::Pattern)) {
      $o = $null
      if ($cur.TryGetCurrentPattern($pat, [ref]$o)) {
        switch ($pat.ProgrammaticName) {
          'InvokePatternIdentifiers.Pattern' { $o.Invoke() }
          'SelectionItemPatternIdentifiers.Pattern' { $o.Select() }
          'TogglePatternIdentifiers.Pattern' { $o.Toggle() }
          'ExpandCollapsePatternIdentifiers.Pattern' { $o.Expand() }
        }
        return
      }
    }
    $cur = $walker.GetParent($cur)   # the label inside a button is not clickable, its parent is
  }
  throw "no usable pattern on '$($el.Current.Name)'"
}
function Click-Named($root, [string]$name, [int]$TimeoutSec = 10) {
  $el = Find-El $root $name $TimeoutSec
  if (-not $el) { throw "not found: '$name'" }
  Click-El $el
}

function Set-Text($el, [string]$text) {
  $o = $null
  if (-not $el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$o)) { throw 'no value pattern' }
  $o.SetValue($text)
}

function Select-Combo($root, [string]$comboName, [string]$itemText, $proc = $script:AppProc) {
  $combo = Find-El $root $comboName 8
  if (-not $combo) { throw "combo not found: $comboName" }
  $o = $null
  if ($combo.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$o)) { $o.Expand() }
  Start-Sleep -Milliseconds 400
  $item = Find-InApp $proc $itemText 5
  if (-not $item) { throw "combo item not found: $itemText" }
  Click-El $item
  Start-Sleep -Milliseconds 300
}

function Dump-Tree($root, [int]$MaxDepth = 6, [string]$Filter = '') {
  $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
  function Walk($el, $depth) {
    if ($depth -gt $MaxDepth) { return }
    $n = $el.Current.Name; $t = $el.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
    if (-not $Filter -or $n -like "*$Filter*" -or $t -like "*$Filter*") { ('{0}{1} "{2}" id={3}' -f ('  ' * $depth), $t, $n, $el.Current.AutomationId) }
    $c = $walker.GetFirstChild($el)
    while ($c) { Walk $c ($depth + 1); $c = $walker.GetNextSibling($c) }
  }
  Walk $root 0
}

function Stop-Mine($proc) { if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force } }

function Save-Shot($proc, [string]$file) {
  Add-Type -AssemblyName System.Drawing
  if (-not ('Win32S' -as [type])) {
    Add-Type @"
using System; using System.Runtime.InteropServices;
public class Win32S { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; } }
"@
  }
  [Win32S]::SetProcessDPIAware() | Out-Null
  $proc.Refresh()
  $r = New-Object Win32S+RECT; [Win32S]::GetWindowRect($proc.MainWindowHandle, [ref]$r) | Out-Null
  $w = $r.R - $r.L; $h = $r.B - $r.T
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
  [Win32S]::PrintWindow($proc.MainWindowHandle, $hdc, 2) | Out-Null
  $g.ReleaseHdc($hdc); $g.Dispose()
  $bmp.Save((Join-Path $Scratch $file)); $bmp.Dispose()
}


function Find-Regex($root, [string]$pattern, [int]$TimeoutSec = 6) {
  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  do {
    try {
      $all = $root.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
      foreach ($e in $all) { try { if ($e.Current.Name -match $pattern) { return $e } } catch { } }
    } catch { }   # the tree can change under us while the page rebuilds; just look again
    Start-Sleep -Milliseconds 300
  } while ((Get-Date) -lt $deadline)
  return $null
}
