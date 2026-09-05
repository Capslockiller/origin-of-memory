param([string]$Reason = 'sessionend')
# v2 flush launcher: store hook stdin, detach flush.py, return under 1s.
# Wired at USER level so every session on this machine flushes into the vault.
# yazan: codex · model: gpt-5.6-sol
$ErrorActionPreference = 'SilentlyContinue'

function Test-BeyinPython {
  param([string]$Path, [string[]]$Prefix = @())
  if (-not $Path) { return $false }
  & $Path @Prefix -c 'import sys; raise SystemExit(0 if sys.version_info >= (3, 12) else 1)' 2>$null
  return ($LASTEXITCODE -eq 0)
}

function Resolve-BeyinPython {
  $choices = @()
  if ($env:BEYIN_PYTHON) {
    $candidate = Get-Command $env:BEYIN_PYTHON -ErrorAction SilentlyContinue
    if ($candidate) { $choices += ,@($candidate.Source, @()) }
    elseif (Test-Path -LiteralPath $env:BEYIN_PYTHON -PathType Leaf) {
      $choices += ,@($env:BEYIN_PYTHON, @())
    }
  }
  $candidate = Get-Command py -ErrorAction SilentlyContinue
  if ($candidate) { $choices += ,@($candidate.Source, @('-3.12')) }
  $candidate = Get-Command python3 -ErrorAction SilentlyContinue
  if ($candidate) { $choices += ,@($candidate.Source, @()) }
  $candidate = Get-Command python -ErrorAction SilentlyContinue
  if ($candidate) { $choices += ,@($candidate.Source, @()) }
  foreach ($choice in $choices) {
    if (Test-BeyinPython -Path $choice[0] -Prefix $choice[1]) {
      return [pscustomobject]@{ Path = $choice[0]; Prefix = @($choice[1]) }
    }
  }
  return $null
}

function Write-BeyinHookError {
  param([string]$StateDir, [string]$HookName, [string]$ErrorName)
  try {
    New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
    $record = @{
      ts = [DateTimeOffset]::Now.ToString('o')
      hook = $HookName
      error = $ErrorName
    } | ConvertTo-Json -Compress
    $path = Join-Path $StateDir 'hook-hatalari.jsonl'
    [System.IO.File]::AppendAllText($path, $record + "`n", [System.Text.UTF8Encoding]::new($false))
  } catch {}
}

# session-end.ps1 dot-sources only these shared interpreter/error helpers.
if ($MyInvocation.InvocationName -eq '.') { return }
if ($env:BEYIN_INVOKED_BY) { exit 0 }

$stdin = [Console]::In.ReadToEnd()
if (-not $stdin) { exit 0 }

$scriptsDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts'
$stateDir = Join-Path $scriptsDir '.state'
New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
$python = Resolve-BeyinPython
if (-not $python) {
  Write-BeyinHookError $stateDir 'flush-launch' 'python-3.12+-missing'
  [Console]::Error.WriteLine('[beyin] Python 3.12+ bulunamadi; flush atlandi.')
  exit 0
}

$inputPath = Join-Path $stateDir ("hookin-{0}-{1}.json" -f $PID, (Get-Random))
# UTF-8 without BOM: flush.py parses this as strict JSON.
[System.IO.File]::WriteAllText($inputPath, $stdin, [System.Text.UTF8Encoding]::new($false))

$py = $python.Path
$pyPrefix = @($python.Prefix)
$flush = Join-Path $scriptsDir 'flush.py'
if ($py -and (Test-Path $flush)) {
  $flushArgument = '"' + $flush + '"'
  $inputArgument = '"' + $inputPath + '"'
  Start-Process -FilePath $py -WindowStyle Hidden -ArgumentList @(
    $pyPrefix + @('-X', 'utf8', $flushArgument, '--hook-input', $inputArgument, '--reason', $Reason))
} else {
  Write-BeyinHookError $stateDir 'flush-launch' 'flush-script-missing'
  [Console]::Error.WriteLine('[beyin] flush.py bulunamadi; flush atlandi.')
}
exit 0
