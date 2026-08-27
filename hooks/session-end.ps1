$ErrorActionPreference = 'SilentlyContinue'
if ($env:BEYIN_INVOKED_BY) { exit 0 }
$vault = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$memDir = (Get-ChildItem -Path $vault -Directory -Filter '*850-Companion' | Select-Object -First 1).FullName
$state = Join-Path $PSScriptRoot '.state'
New-Item -ItemType Directory -Force -Path $state | Out-Null
$sf = Join-Path $state 'session_start_time'
$pf = Join-Path $state 'prompt_count'
$start = 0
if (Test-Path $sf) { $start = ((Get-Content $sf | Select-Object -First 1) -as [long]); if (-not $start) { $start = 0 } }
$prompts = 0
if (Test-Path $pf) { $prompts = ((Get-Content $pf | Select-Object -First 1) -as [int]); if (-not $prompts) { $prompts = 0 } }
$modified = $false
if ($memDir) {
  $ls = Join-Path $memDir 'Last-Session.md'
  if (Test-Path $ls) {
    $m = [DateTimeOffset]::new((Get-Item $ls).LastWriteTimeUtc, [TimeSpan]::Zero).ToUnixTimeSeconds()
    if ($m -gt $start) { $modified = $true }
  }
}
if (($prompts -ge 5) -and (-not $modified)) {
  $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm'
  Set-Content -Path (Join-Path $state 'needs_reflection') -Value "Oturum hafiza guncellenmeden bitti. Prompt: $prompts. $stamp" -Encoding UTF8
}
Remove-Item -Force -ErrorAction SilentlyContinue $sf, $pf
exit 0
