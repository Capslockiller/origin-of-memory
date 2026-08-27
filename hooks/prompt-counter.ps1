if ($env:BEYIN_INVOKED_BY) { exit 0 }
$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$state = Join-Path $PSScriptRoot '.state'
New-Item -ItemType Directory -Force -Path $state | Out-Null
$f = Join-Path $state 'prompt_count'
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
