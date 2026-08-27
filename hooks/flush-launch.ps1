param([string]$Reason = 'sessionend')
# v2 flush launcher: store hook stdin, detach flush.py, return under 1s.
# Wired at USER level so every session on this machine flushes into the vault.
$ErrorActionPreference = 'SilentlyContinue'
if ($env:BEYIN_INVOKED_BY) { exit 0 }

$stdin = [Console]::In.ReadToEnd()
if (-not $stdin) { exit 0 }

$scriptsDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts'
$stateDir = Join-Path $scriptsDir '.state'
New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
$inputPath = Join-Path $stateDir ("hookin-{0}-{1}.json" -f $PID, (Get-Random))
# UTF-8 without BOM: flush.py parses this as strict JSON.
[System.IO.File]::WriteAllText($inputPath, $stdin, [System.Text.UTF8Encoding]::new($false))

$py = $null
$pyPrefix = @()
if ($env:BEYIN_PYTHON) {
  $candidate = Get-Command $env:BEYIN_PYTHON -ErrorAction SilentlyContinue
  if ($candidate) { $py = $candidate.Source }
  elseif (Test-Path -LiteralPath $env:BEYIN_PYTHON -PathType Leaf) { $py = $env:BEYIN_PYTHON }
}
if (-not $py) {
  $candidate = Get-Command python -ErrorAction SilentlyContinue
  if ($candidate) { $py = $candidate.Source }
}
if (-not $py) {
  $candidate = Get-Command py -ErrorAction SilentlyContinue
  if ($candidate) { $py = $candidate.Source; $pyPrefix = @('-3') }
}
$flush = Join-Path $scriptsDir 'flush.py'
if ($py -and (Test-Path $flush)) {
  $flushArgument = '"' + $flush + '"'
  $inputArgument = '"' + $inputPath + '"'
  Start-Process -FilePath $py -WindowStyle Hidden -ArgumentList @(
    $pyPrefix + @('-X', 'utf8', $flushArgument, '--hook-input', $inputArgument, '--reason', $Reason))
}
exit 0
