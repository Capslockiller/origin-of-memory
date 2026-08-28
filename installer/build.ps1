# yazan: codex · model: gpt-5.6-sol
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:BEYIN_INVOKED_BY) { exit 0 }

$scriptPath = Join-Path $PSScriptRoot 'origin-of-memory.iss'
if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
  throw "Inno Setup script not found: $scriptPath"
}

$candidates = @()
$onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if ($onPath) { $candidates += $onPath.Source }

if (${env:ProgramFiles(x86)}) {
  $candidates += Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
}
if ($env:ProgramFiles) {
  $candidates += Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'
}
if ($env:LOCALAPPDATA) {
  $candidates += Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
}

$compiler = @(
  $candidates |
    Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } |
    Select-Object -Unique
) | Select-Object -First 1

if (-not $compiler) {
  throw @'
ISCC.exe was not found on PATH or in the usual Inno Setup 6 locations.
Install it yourself, then rerun this script:
  winget install --id JRSoftware.InnoSetup -e --accept-source-agreements --accept-package-agreements
This build script does not install tools or request elevation.
'@
}

Write-Host "[RUN] $compiler $scriptPath"
& $compiler $scriptPath
if ($LASTEXITCODE -ne 0) {
  throw "ISCC.exe failed with exit code $LASTEXITCODE"
}

$output = Join-Path $PSScriptRoot 'output\Setup.exe'
if (-not (Test-Path -LiteralPath $output -PathType Leaf)) {
  throw "ISCC.exe returned success but the expected output was not created: $output"
}
Write-Host "[DONE] $output"
