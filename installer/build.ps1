# Builds the distributable ZipPaster installer.
#
#   .\installer\build.ps1              # publish + build Setup.exe
#   .\installer\build.ps1 -SkipTest    # skip the self-test gate
#
# Produces installer\output\ZipPaster-Setup-<version>.exe

[CmdletBinding()]
param(
  [string]$Version = '1.0.0',
  [switch]$SkipTest
)

$ErrorActionPreference = 'Stop'

$root       = Split-Path -Parent $PSScriptRoot
$project    = Join-Path $root 'src\ZipPaster\ZipPaster.csproj'
$publishDir = Join-Path $root 'src\ZipPaster\bin\Release\net10.0-windows\win-x64\publish'
$exe        = Join-Path $publishDir 'ZipPaster.exe'

function Find-ISCC {
  # winget installs Inno Setup per-user under LOCALAPPDATA by default; the
  # machine-wide installer uses Program Files. Check both.
  $candidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
  )
  foreach ($c in $candidates) { if (Test-Path $c) { return $c } }

  $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }

  return $null
}

# --- 1. the bundled data set must exist -------------------------------------
$data = Join-Path $root 'src\ZipPaster\Resources\us_zipcodes.csv.gz'
if (-not (Test-Path $data)) {
  throw "Missing $data. Run: python tools\build_zipdata.py"
}
Write-Host "ZIP data: $([math]::Round((Get-Item $data).Length / 1KB, 1)) KB" -ForegroundColor Cyan

# --- 2. a running instance would lock the exe -------------------------------
Get-Process -Name ZipPaster -ErrorAction SilentlyContinue | ForEach-Object {
  Write-Host "Stopping running ZipPaster (pid $($_.Id))" -ForegroundColor Yellow
  Stop-Process -Id $_.Id -Force
  Start-Sleep -Milliseconds 500
}

# --- 3. publish a self-contained single file --------------------------------
# Trimming stays off: it breaks WinForms' reflection over control properties.
Write-Host "`nPublishing..." -ForegroundColor Cyan
dotnet publish $project `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishTrimmed=false `
  -p:Version=$Version `
  --nologo -v minimal

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
if (-not (Test-Path $exe)) { throw "Expected $exe was not produced" }

Write-Host "Published: $([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB" -ForegroundColor Green

# --- 4. gate on the self-test -----------------------------------------------
if (-not $SkipTest) {
  Write-Host "`nRunning self-test (takes ~20s and briefly steals focus)..." -ForegroundColor Cyan
  $report = Join-Path $env:TEMP 'zippaster-build-selftest.txt'
  if (Test-Path $report) { Remove-Item $report -Force }

  $proc = Start-Process -FilePath $exe -ArgumentList '--selftest', $report -PassThru -Wait
  if (Test-Path $report) { Get-Content $report | Write-Host }

  if ($proc.ExitCode -ne 0) {
    throw "Self-test failed. Fix before shipping, or pass -SkipTest to override."
  }
  Write-Host "Self-test passed." -ForegroundColor Green
}

# --- 5. build the installer -------------------------------------------------
$iscc = Find-ISCC
if (-not $iscc) {
  Write-Warning @"
Inno Setup 6 was not found, so no Setup.exe was produced.
The portable executable is ready at:
  $exe

To build the installer, install Inno Setup and re-run:
  winget install -e --id JRSoftware.InnoSetup
"@
  return
}

Write-Host "`nBuilding installer with $iscc" -ForegroundColor Cyan
$iss = Join-Path $PSScriptRoot 'ZipPaster.iss'

& $iscc "/DAppVersion=$Version" "/DPublishDir=$publishDir" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$setup = Join-Path $PSScriptRoot "output\ZipPaster-Setup-$Version.exe"
if (Test-Path $setup) {
  Write-Host "`nInstaller: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)" -ForegroundColor Green
} else {
  Write-Warning "ISCC reported success but $setup is missing."
}
