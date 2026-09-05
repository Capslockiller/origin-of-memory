if ($env:BEYIN_INVOKED_BY) { exit 0 }
# yazan: codex · model: gpt-5.6-sol
$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$stdin = [Console]::In.ReadToEnd()
if (-not $stdin) { exit 0 }
try { $hook = $stdin | ConvertFrom-Json } catch { exit 0 }
$sid = "$($hook.session_id)"
if (($sid -notmatch '^[A-Za-z0-9_.-]{1,128}$') -or ($sid -eq '.') -or ($sid -eq '..')) { exit 0 }
$state = Join-Path $PSScriptRoot '.state'
New-Item -ItemType Directory -Force -Path $state | Out-Null
$sessionState = Join-Path $state ("oturum-{0}" -f $sid)
New-Item -ItemType Directory -Force -Path $sessionState | Out-Null
$f = Join-Path $sessionState 'prompt_count'
$count = 0
if (Test-Path $f) { $count = ((Get-Content $f | Select-Object -First 1) -as [int]); if (-not $count) { $count = 0 } }
$count++
Set-Content -Path $f -Value $count -Encoding Ascii
if (($count % 15) -eq 0) {
  $msg = "[Hafiza] $count. mesaj. Oturum sonunda 850-Companion/Last-Session.md ve Threads.md guncellemeyi unutma."
  $out = @{ hookSpecificOutput = @{ hookEventName = 'UserPromptSubmit'; additionalContext = $msg } } | ConvertTo-Json -Compress -Depth 4
  [Console]::Out.WriteLine($out)
}
exit 0
