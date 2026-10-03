<#
  Builds the release:  .\build.ps1
  1. runs the unit tests
  2. publishes ONE self-contained file:   dist\HSA-<version>-win-x64.exe   (portable, runs on its own)
  3. zips it with the installer scripts:  dist\HSA-<version>-win-x64.zip
#>
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version
Write-Host "== HP Smart Alternative (HSA) $version =="

if (-not $SkipTests) {
  dotnet test tests/PrintHub.Tests --nologo -v q
  if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

# Only replace what this build produces: earlier releases in dist\ stay (and one may be running right now).
New-Item -ItemType Directory -Force dist | Out-Null
foreach ($old in "dist/publish", "dist/stage", "dist/HSA-$version-win-x64.exe", "dist/HSA-$version-win-x64.zip") {
  if (Test-Path $old) { Remove-Item $old -Recurse -Force }
}
dotnet publish src/PrintHub.App -c Release -r win-x64 -p:Platform=x64 `
  -p:SelfContained=true -p:WindowsAppSDKSelfContained=true -p:PublishReadyToRun=false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true `
  -p:DebugType=none -p:DebugSymbols=false -o dist/publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$exe = "dist/HSA-$version-win-x64.exe"
Copy-Item dist/publish/HSA.exe $exe

$stage = "dist/stage"
New-Item -ItemType Directory $stage -Force | Out-Null
Copy-Item dist/publish/HSA.exe $stage
Copy-Item installer/*.ps1 $stage
Copy-Item README.md, CHANGELOG.md $stage
$zip = "dist/HSA-$version-win-x64.zip"
Compress-Archive -Path "$stage/*" -DestinationPath $zip -Force
Remove-Item $stage -Recurse -Force
Remove-Item dist/publish -Recurse -Force

"{0}  ({1:N0} MB)" -f $exe, ((Get-Item $exe).Length / 1MB)
"{0}  ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)
