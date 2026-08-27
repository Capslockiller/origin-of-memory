# yazan: codex · model: gpt-5.6-sol
[CmdletBinding()]
param(
  [Parameter(Position = 0)]
  [string]$Question
)

$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
if ($env:BEYIN_INVOKED_BY) { exit 0 }
if (-not $Question) { $Question = Read-Host 'Question' }
if (-not $Question) { exit 0 }

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

$script = Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts') 'context_pack.py'
if (-not ($py -and (Test-Path -LiteralPath $script -PathType Leaf))) { exit 0 }
$arguments = @($pyPrefix) + @('-X', 'utf8', $script, $Question, '--clip')
& $py @arguments
if ($LASTEXITCODE -ne 0) { exit 0 }
Write-Output 'Persistent memory context copied to the clipboard.'
exit 0
