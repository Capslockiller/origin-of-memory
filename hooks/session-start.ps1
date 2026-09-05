# v2 session-start: v1 injection (Last-Session + Threads + reflection debt)
# plus Kurallar, son Journal girdisi, knowledge index ve gunun daily kuyrugu.
# yazan: codex · model: gpt-5.6-sol
$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
if ($env:BEYIN_INVOKED_BY) { exit 0 }
$stdin = [Console]::In.ReadToEnd()
$hook = $null
if ($stdin) { try { $hook = $stdin | ConvertFrom-Json } catch {} }
$sid = if ($hook) { "$($hook.session_id)" } else { '' }
if (($sid -notmatch '^[A-Za-z0-9_.-]{1,128}$') -or ($sid -eq '.') -or ($sid -eq '..')) { $sid = '' }
$vault = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$memDir = (Get-ChildItem -Path $vault -Directory -Filter '*850-Companion' | Select-Object -First 1).FullName
$state = Join-Path $PSScriptRoot '.state'
New-Item -ItemType Directory -Force -Path $state | Out-Null
if ($sid) {
  $sessionState = Join-Path $state ("oturum-{0}" -f $sid)
  New-Item -ItemType Directory -Force -Path $sessionState | Out-Null
  Set-Content -Path (Join-Path $sessionState 'session_start_time') -Value ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) -Encoding Ascii
  Set-Content -Path (Join-Path $sessionState 'prompt_count') -Value '0' -Encoding Ascii
}

$lastSession = ''
if ($memDir) {
  $lsPath = Join-Path $memDir 'Last-Session.md'
  if (Test-Path $lsPath) {
    $buf = @(); $in = $false
    foreach ($l in (Get-Content $lsPath -Encoding UTF8)) {
      if ($l -match '^## Session:') { $in = $true }
      elseif ($l -match '^## Previous') { break }
      if ($in) { $buf += $l; if ($buf.Count -ge 49) { break } }
    }
    $lastSession = ($buf -join "`n")
  }
}

$threads = ''
if ($memDir) {
  $thPath = Join-Path $memDir 'Threads.md'
  if (Test-Path $thPath) {
    $buf = @(); $in = $false
    foreach ($l in (Get-Content $thPath -Encoding UTF8)) {
      if ($l -match '^## Active') { $in = $true; continue }
      if ($l -match '^## Closed') { break }
      if ($in -and ($l -match '^### ' -or $l -match '^\*\*Status:\*\*')) { $buf += $l; if ($buf.Count -ge 12) { break } }
    }
    $threads = ($buf -join "`n")
  }
}

$kurallar = ''
if ($memDir) {
  $kPath = Join-Path $memDir 'Kurallar.md'
  if (Test-Path $kPath) { $kurallar = ((Get-Content $kPath -Encoding UTF8 -TotalCount 60) -join "`n") }
}

$journal = ''
if ($memDir) {
  $jPath = Join-Path $memDir 'Journal.md'
  if (Test-Path $jPath) {
    $jl = @(Get-Content $jPath -Encoding UTF8)
    $idx = -1
    for ($i = $jl.Count - 1; $i -ge 0; $i--) { if ($jl[$i] -match '^## ') { $idx = $i; break } }
    if ($idx -ge 0) {
      $end = [Math]::Min($jl.Count - 1, $idx + 9)
      $journal = (($jl[$idx..$end]) -join "`n")
    }
  }
}

$knowledge = ''
$kiPath = Join-Path $vault 'knowledge\index.md'
if (Test-Path $kiPath) { $knowledge = ((Get-Content $kiPath -Encoding UTF8 -TotalCount 150) -join "`n") }

$dailyTail = ''
$dailyDir = Join-Path $vault 'daily'
if (Test-Path $dailyDir) {
  $dPath = Join-Path $dailyDir ((Get-Date).ToString('yyyy-MM-dd') + '.md')
  if (-not (Test-Path $dPath)) { $dPath = Join-Path $dailyDir ((Get-Date).AddDays(-1).ToString('yyyy-MM-dd') + '.md') }
  if (Test-Path $dPath) {
    $dailyTail = ((@(Get-Content $dPath -Encoding UTF8) | Select-Object -Last 25) -join "`n")
  }
}

$reflection = ''
$rf = Join-Path $state 'needs_reflection'
if (Test-Path $rf) {
  $msg = (Get-Content $rf -Raw -Encoding UTF8).Trim()
  $reflection = "UYARI: Onceki oturum hafiza guncellenmeden bitti: $msg. Anlamli bir sey olduysa 850-Companion dosyalarini simdi guncelle."
  Remove-Item $rf -Force
}

# Fixed sections are never truncated; knowledge shrinks first, then daily tail.
$fixed = ''
if ($reflection)  { $fixed += $reflection + "`n`n" }
if ($lastSession) { $fixed += "[Memory - Last Session]`n$lastSession`n`n" }
if ($threads)     { $fixed += "[Memory - Active Threads]`n$threads`n`n" }
if ($kurallar)    { $fixed += "[Hafiza - Kurallar]`n$kurallar`n`n" }
if ($journal)     { $fixed += "[Hafiza - Son Journal]`n$journal`n`n" }
$closing = "[Memory] Continuity is your job. Read '850-Companion/Core.md' for who you are to this user. Hafiza protokolu zorunludur."

$cap = 16000
$note = "`n[not: indeks kirpildi - beyin-doktor calistir]"
$budget = $cap - $fixed.Length - $closing.Length - 80
if ($budget -lt 0) { $budget = 0 }
$kBlock = ''
if ($knowledge) { $kBlock = "[Bilgi Tabani - Indeks]`n$knowledge`n`n" }
$dBlock = ''
if ($dailyTail) { $dBlock = "[Bugunun Logu]`n$dailyTail`n`n" }
if (($kBlock.Length + $dBlock.Length) -gt $budget) {
  $kAllow = $budget - $dBlock.Length
  if ($kAllow -lt 0) { $kAllow = 0 }
  if ($kBlock.Length -gt $kAllow) {
    if ($kAllow -gt 40) { $kBlock = $kBlock.Substring(0, $kAllow) + $note + "`n`n" } else { $kBlock = '' }
  }
  if (($kBlock.Length + $dBlock.Length) -gt $budget) {
    $dAllow = $budget - $kBlock.Length
    if ($dAllow -gt 40) { $dBlock = $dBlock.Substring(0, $dAllow) + $note + "`n`n" } else { $dBlock = '' }
  }
}

$ctx = $fixed + $kBlock + $dBlock + $closing
$out = @{ hookSpecificOutput = @{ hookEventName = 'SessionStart'; additionalContext = $ctx } } | ConvertTo-Json -Compress -Depth 4
[Console]::Out.WriteLine($out)
exit 0
