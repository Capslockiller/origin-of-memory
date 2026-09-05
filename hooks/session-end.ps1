# yazan: codex · model: gpt-5.6-sol
$ErrorActionPreference = 'SilentlyContinue'
if ($env:BEYIN_INVOKED_BY) { exit 0 }
. (Join-Path $PSScriptRoot 'flush-launch.ps1')
$stdin = [Console]::In.ReadToEnd()
$hook = $null
if ($stdin) { try { $hook = $stdin | ConvertFrom-Json } catch {} }
$sid = if ($hook) { "$($hook.session_id)" } else { '' }
if (($sid -notmatch '^[A-Za-z0-9_.-]{1,128}$') -or ($sid -eq '.') -or ($sid -eq '..')) { $sid = '' }
$vault = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$memDir = (Get-ChildItem -Path $vault -Directory -Filter '*850-Companion' | Select-Object -First 1).FullName
$state = Join-Path $PSScriptRoot '.state'
New-Item -ItemType Directory -Force -Path $state | Out-Null
$sessionState = if ($sid) { Join-Path $state ("oturum-{0}" -f $sid) } else { $null }
$sf = if ($sessionState) { Join-Path $sessionState 'session_start_time' } else { $null }
$pf = if ($sessionState) { Join-Path $sessionState 'prompt_count' } else { $null }
$start = 0
if ($sf -and (Test-Path $sf)) { $start = ((Get-Content $sf | Select-Object -First 1) -as [long]); if (-not $start) { $start = 0 } }
$prompts = 0
if ($pf -and (Test-Path $pf)) { $prompts = ((Get-Content $pf | Select-Object -First 1) -as [int]); if (-not $prompts) { $prompts = 0 } }
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
if ($sessionState) { Remove-Item -LiteralPath $sessionState -Recurse -Force -ErrorAction SilentlyContinue }

$bakimSetting = [Environment]::GetEnvironmentVariable('BEYIN_BAKIM')
$bakimEnabled = (-not $bakimSetting) -or ($bakimSetting -eq '1')
if ($bakimEnabled) {
  $scriptsDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts'
  $scriptsState = Join-Path $scriptsDir '.state'
  $bakim = Join-Path $scriptsDir 'bakim.py'
  $python = Resolve-BeyinPython
  if (-not $python) {
    Write-BeyinHookError $scriptsState 'session-end' 'python-3.12+-missing'
    [Console]::Error.WriteLine('[beyin] Python 3.12+ bulunamadi; bakim sayimi atlandi.')
  } elseif (-not (Test-Path -LiteralPath $bakim -PathType Leaf)) {
    Write-BeyinHookError $scriptsState 'session-end' 'bakim-script-missing'
    [Console]::Error.WriteLine('[beyin] bakim.py bulunamadi; bakim sayimi atlandi.')
  } else {
    try {
      $arguments = @($python.Prefix) + @('-X', 'utf8', $bakim, '--dry-run', '--state-dir', $scriptsState, '--hook-state-dir', $state)
      $quotedArguments = @($arguments | ForEach-Object { '"' + ("$_" -replace '"', '\"') + '"' })
      $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
      $startInfo.FileName = $python.Path
      $startInfo.Arguments = $quotedArguments -join ' '
      $startInfo.UseShellExecute = $false
      $startInfo.CreateNoWindow = $true
      $startInfo.RedirectStandardOutput = $true
      $startInfo.RedirectStandardError = $true
      $process = [System.Diagnostics.Process]::Start($startInfo)
      if ($process -and (-not $process.WaitForExit(1000))) { $process.Kill() }
      if ($process) { $process.Dispose() }
    } catch {}
  }
}
exit 0
