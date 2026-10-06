<#
  Builds the release:  .\build.ps1
  1. runs the unit tests (only if a tests folder is present)
  2. publishes ONE self-contained file:   dist\HSA-<version>-win-x64.exe   (portable, runs on its own)
  3. zips it with the installer scripts:  dist\HSA-<version>-win-x64.zip

  Code signing (optional). Without a certificate Windows shows its "publisher could not be verified" prompt on first
  launch. When you have a code-signing certificate, sign the exe in the same build:

      .\build.ps1 -PfxPath C:\certs\hsa.pfx -PfxPassword "..."          # certificate in a .pfx file
      .\build.ps1 -CertThumbprint 0123ABCD...                            # certificate already installed in Windows
      .\build.ps1 -PfxPath hsa.pfx -PfxPassword "..." -TimestampUrl ""   # skip the timestamp (testing only)

  See docs/code-signing.md for where to get a certificate (including a free one for open-source projects).
#>
param(
  [switch]$SkipTests,
  [switch]$NoDatabase,   # leave the Epson model database out of the exe even if Resources\epson-database.json exists (use this for public releases)
  [string]$PfxPath,
  [string]$PfxPassword,
  [string]$CertThumbprint,
  [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version
Write-Host "== HP Smart Alternative (HSA) $version =="

# The unit tests are not part of the public repository; when the folder is present (a developer checkout) run them first.
if ($SkipTests) { Write-Host 'Skipping tests.' }
elseif (Test-Path tests/PrintHub.Tests) {
  dotnet test tests/PrintHub.Tests --nologo -v q
  if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
else { Write-Host 'No tests folder here: building without running tests.' }

# Only replace what this build produces: earlier releases in dist\ stay (and one may be running right now).
New-Item -ItemType Directory -Force dist | Out-Null
foreach ($old in "dist/publish", "dist/stage", "dist/HSA-$version-win-x64.exe", "dist/HSA-$version-win-x64.zip") {
  if (Test-Path $old) { Remove-Item $old -Recurse -Force }
}
$epsonDb = 'src/PrintHub.Core/Resources/epson-database.json'
if ($NoDatabase) { Write-Host 'Epson model database: LEFT OUT of this build (-NoDatabase).' -ForegroundColor Green }
elseif (Test-Path $epsonDb) { Write-Host "Epson model database: BUILT IN from $epsonDb. It is the EWR database (Apache-2.0, RxNaison): THIRD-PARTY-NOTICES.md and docs/licenses/EWR-LICENSE.txt must ship with the build, and the zip includes them." -ForegroundColor Yellow }
else { Write-Host 'Epson model database: not built in (the counter reset will look for epson-database.json next to HSA.exe or in its data folder).' }
$dbProp = if ($NoDatabase) { '-p:EmbedEpsonDatabase=false' } else { '-p:EmbedEpsonDatabase=true' }
dotnet publish src/PrintHub.App -c Release -r win-x64 -p:Platform=x64 $dbProp `
  -p:SelfContained=true -p:WindowsAppSDKSelfContained=true -p:PublishReadyToRun=false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true `
  -p:DebugType=none -p:DebugSymbols=false -o dist/publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# ---------------------------------------------------------------- optional code signing
$signed = $false
if ($PfxPath -or $CertThumbprint) {
  $signtool = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName | Select-Object -Last 1 -ExpandProperty FullName
  if (-not $signtool) { $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName | Select-Object -Last 1 -ExpandProperty FullName }
  if (-not $signtool) { throw 'signtool.exe was not found. Build the app once (dotnet build restores it) or install the Windows SDK.' }

  $signArgs = @('sign', '/fd', 'SHA256', '/d', 'HP Smart Alternative (HSA)')
  if ($PfxPath) { if (-not (Test-Path $PfxPath)) { throw "Certificate file not found: $PfxPath" }; $signArgs += @('/f', $PfxPath); if ($PfxPassword) { $signArgs += @('/p', $PfxPassword) } }
  else { $signArgs += @('/sha1', $CertThumbprint) }
  if ($TimestampUrl) { $signArgs += @('/tr', $TimestampUrl, '/td', 'SHA256') }
  else { Write-Warning 'Signing without a timestamp: the signature stops being valid when the certificate expires.' }
  $signArgs += 'dist/publish/HSA.exe'

  & $signtool @signArgs
  if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
  $sig = Get-AuthenticodeSignature dist/publish/HSA.exe
  Write-Host ("Signed by: {0}  (status: {1})" -f $sig.SignerCertificate.Subject, $sig.Status)
  $signed = $true
} else {
  Write-Host 'Not signed (no certificate given). Windows will ask "are you sure?" the first time it is run from a download.'
}

$exe = "dist/HSA-$version-win-x64.exe"
Copy-Item dist/publish/HSA.exe $exe

$stage = "dist/stage"
New-Item -ItemType Directory $stage -Force | Out-Null
Copy-Item dist/publish/HSA.exe $stage
Copy-Item installer/*.ps1 $stage
Copy-Item README.md, CHANGELOG.md, THIRD-PARTY-NOTICES.md $stage
New-Item -ItemType Directory "$stage/licenses" -Force | Out-Null
Copy-Item docs/licenses/* "$stage/licenses"
$zip = "dist/HSA-$version-win-x64.zip"
Compress-Archive -Path "$stage/*" -DestinationPath $zip -Force
Remove-Item $stage -Recurse -Force
Remove-Item dist/publish -Recurse -Force

# SHA256SUMS.txt must be uploaded with the release: the in-app updater refuses an update it cannot check against it.
$sums = foreach ($f in $exe, $zip) { "{0} *{1}" -f (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $f -Leaf) }
Set-Content -Path dist/SHA256SUMS.txt -Value $sums -Encoding ascii

"{0}  ({1:N0} MB){2}" -f $exe, ((Get-Item $exe).Length / 1MB), $(if ($signed) { '  [signed]' } else { '' })
"{0}  ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)
