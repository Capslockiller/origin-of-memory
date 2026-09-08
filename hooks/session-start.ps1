# v3 session-start (2026-09-02, 48. oturum): v2 enjeksiyonu + ZAMAN blogu + Threads diyeti
# + olcum satiri. v2: Last-Session + Threads + reflection + Kurallar + Journal + indeks + daily.
# ASCII-only kaynak (PS 5.1 BOM'suz dosyayi ANSI okur); Turkce harfler regex'te \uXXXX ile.
# yazan: codex - model: gpt-5.6-sol
$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
if ($env:BEYIN_INVOKED_BY) { exit 0 }
# hook-girdi izi (2026-09-07, 56. oturum): kanca girdi mi sorusu icin, stdin okunmadan once.
function Write-BeyinHookGirdi {
  param([string]$HookName, [string]$Reason)
  try {
    $dir = Join-Path $PSScriptRoot '.state'
    if (-not (Test-Path -LiteralPath $dir)) {
      New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    $path = Join-Path $dir 'hook-girdi.jsonl'
    $var = Get-Item -LiteralPath $path -ErrorAction SilentlyContinue
    if ($var -and $var.Length -gt 524288) {
      Move-Item -LiteralPath $path -Destination ($path + '.1') -Force -ErrorAction SilentlyContinue
    }
    $record = @{
      ts = [DateTimeOffset]::Now.ToString('o')
      hook = $HookName
      reason = $Reason
      pid = $PID
    } | ConvertTo-Json -Compress
    [System.IO.File]::AppendAllText($path, $record + "`n", [System.Text.UTF8Encoding]::new($false))
  } catch {}
}
Write-BeyinHookGirdi 'session-start' 'start'
$stdin = [Console]::In.ReadToEnd()
$hook = $null
if ($stdin) { try { $hook = $stdin | ConvertFrom-Json } catch {} }
$sid = if ($hook) { "$($hook.session_id)" } else { '' }
if (($sid -notmatch '^[A-Za-z0-9_.-]{1,128}$') -or ($sid -eq '.') -or ($sid -eq '..')) { $sid = '' }
$cwd = if ($hook -and $null -ne $hook.cwd) { "$($hook.cwd)" } else { '' }
if ($cwd.Length -gt 2048) { $cwd = '' }
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

$now = Get-Date
$dailyDir = Join-Path $vault 'daily'

# --- ZAMAN blogu (A7): mutlak tarih + gun adi + dilim + son oturum + bugun + yaklasan ---
$zaman = ''
try {
  $gunler = @('Pazar','Pazartesi','Sali','Carsamba','Persembe','Cuma','Cumartesi')
  $gunAdi = $gunler[[int]$now.DayOfWeek]
  $ofs = $now.ToString('zzz')
  $tzName = [System.TimeZoneInfo]::Local.Id
  $zaman = "Bugun: " + $now.ToString('yyyy-MM-dd') + " $gunAdi " + $now.ToString('HH:mm') + " (UTC$ofs, $tzName)"

  # son oturum: Last-Session.md ve en yeni daily dosyasinin mtime'i (hangisi daha yeni)
  $son = $null
  if ($memDir) {
    $lsf = Get-Item (Join-Path $memDir 'Last-Session.md') -ErrorAction SilentlyContinue
    if ($lsf) { $son = $lsf.LastWriteTime }
  }
  if (Test-Path $dailyDir) {
    $nd = Get-ChildItem -Path $dailyDir -Filter '*.md' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($nd -and ((-not $son) -or ($nd.LastWriteTime -gt $son))) { $son = $nd.LastWriteTime }
  }
  if ($son) {
    $fark = $now - $son
    if ($fark.TotalMinutes -lt 90) { $sonStr = [int]$fark.TotalMinutes; $sonStr = "$sonStr dk once" }
    elseif ($fark.TotalHours -lt 36) { $sonStr = [int]$fark.TotalHours; $sonStr = "$sonStr saat once" }
    else { $sonStr = [int]$fark.TotalDays; $sonStr = "$sonStr gun once" }
    $zaman += "`nSon hafiza izi: $sonStr (" + $son.ToString('yyyy-MM-dd HH:mm') + ")"
  }

  # bugunku oturum sayisi (daily'deki '### Oturum' bloklari)
  $dToday = Join-Path $dailyDir ($now.ToString('yyyy-MM-dd') + '.md')
  if (Test-Path $dToday) {
    $n = @(Get-Content $dToday -Encoding UTF8 | Where-Object { $_ -match '^### Oturum' }).Count
    $zaman += "`nBugun daily'ye dusen oturum: $n"
  } else { $zaman += "`nBugun daily'ye dusen oturum: 0 (dosya yok)" }
} catch { }

# --- Threads: Active bolgesindeki HER thread; baslik + Status satiri (220 kr'a kirpik) ---
$threads = ''
$thLines = @()
if ($memDir) {
  $thPath = Join-Path $memDir 'Threads.md'
  if (Test-Path $thPath) {
    $thLines = @(Get-Content $thPath -Encoding UTF8)
    $buf = @(); $in = $false; $bekle = $false
    foreach ($l in $thLines) {
      if ($l -match '^## Active') { $in = $true; continue }
      if ($l -match '^## Closed') { break }
      if (-not $in) { continue }
      if ($l -match '^### ') { $buf += $l; $bekle = $true; continue }
      if ($bekle -and ($l -match '^\*\*Status:\*\*')) {
        $s = $l
        if ($s.Length -gt 220) {
          $cut = $s.LastIndexOf(' ', 220)
          if ($cut -lt 120) { $cut = 220 }
          $s = $s.Substring(0, $cut) + ' ...'
        }
        $buf += $s; $bekle = $false
      }
    }
    if ($buf.Count -gt 0) {
      $threads = ($buf -join "`n") + "`n(Status satirlari kirpik; thread govdesi icin Threads.md'yi oku ya da memory_search kullan.)"
    }
  }
}

# --- Yaklasan tarihler (A7): Active thread metninden 1-21 gun icindeki tarihler (bugunun kayit damgalari haric) ---
$yaklasan = ''
try {
  if ($thLines.Count -gt 0) {
    $aylar = @{ 'oca'=1; 'sub'=2; 'mar'=3; 'nis'=4; 'may'=5; 'haz'=6; 'tem'=7; 'agu'=8; 'eyl'=9; 'eki'=10; 'kas'=11; 'ara'=12 }
    # Turkce harfler kacisla: \u015E (S-cedilla), \u015F (s-cedilla), \u011F (g-breve)
    $rxAy = '(?<g>\d{1,2})\s+(?<ay>Oca|[S\u015E][u\u00FC]b|Mar|Nis|May|Haz|Tem|A[g\u011F]u|Eyl|Eki|Kas|Ara)\b'
    $rxIso = '(?<y>20\d{2})-(?<m>\d{2})-(?<d>\d{2})'
    $bul = @{}
    $cur = ''; $in = $false
    foreach ($l in $thLines) {
      if ($l -match '^## Active') { $in = $true; continue }
      if ($l -match '^## Closed') { break }
      if (-not $in) { continue }
      if ($l -match '^### Thread: (.+)$') { $cur = $Matches[1]; if ($cur.Length -gt 32) { $c2 = $cur.LastIndexOf(' ', 32); if ($c2 -lt 12) { $c2 = 32 }; $cur = $cur.Substring(0, $c2) }; continue }
      foreach ($m in [regex]::Matches($l, $rxAy)) {
        $key = $m.Groups['ay'].Value.ToLowerInvariant()
        $key = $key.Replace([string][char]0x015F, 's').Replace([string][char]0x00FC, 'u').Replace([string][char]0x011F, 'g')
        if (-not $aylar.ContainsKey($key)) { continue }
        $g = [int]$m.Groups['g'].Value; $a = $aylar[$key]
        try { $dt = Get-Date -Year $now.Year -Month $a -Day $g -Hour 0 -Minute 0 -Second 0 } catch { continue }
        $kalan = ($dt.Date - $now.Date).Days
        if ($kalan -lt 1 -or $kalan -gt 21) { continue }
        $st = [Math]::Max(0, $m.Index - 40); $ln = [Math]::Min($l.Length - $st, 110)
        $ctx = $l.Substring($st, $ln).Replace('**','').Replace('`','')
        $k = $dt.ToString('yyyy-MM-dd') + '|' + $cur
        if (-not $bul.ContainsKey($k)) { $bul[$k] = "  " + $dt.ToString('dd.MM') + " ($kalan gun) [$cur] ..$ctx.." }
      }
      foreach ($m in [regex]::Matches($l, $rxIso)) {
        try { $dt = Get-Date -Year ([int]$m.Groups['y'].Value) -Month ([int]$m.Groups['m'].Value) -Day ([int]$m.Groups['d'].Value) -Hour 0 -Minute 0 -Second 0 } catch { continue }
        $kalan = ($dt.Date - $now.Date).Days
        if ($kalan -lt 1 -or $kalan -gt 21) { continue }
        $st = [Math]::Max(0, $m.Index - 40); $ln = [Math]::Min($l.Length - $st, 110)
        $ctx = $l.Substring($st, $ln).Replace('**','').Replace('`','')
        $k = $dt.ToString('yyyy-MM-dd') + '|' + $cur
        if (-not $bul.ContainsKey($k)) { $bul[$k] = "  " + $dt.ToString('dd.MM') + " ($kalan gun) [$cur] ..$ctx.." }
      }
    }
    if ($bul.Count -gt 0) {
      $lines = @($bul.Keys | Sort-Object | ForEach-Object { $bul[$_] } | Select-Object -First 6)
      $yaklasan = "Yaklasan (21 gun, Threads'ten):`n" + ($lines -join "`n")
    }
  }
} catch { }
if ($yaklasan) { $zaman += "`n$yaklasan" }

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
if (Test-Path $dailyDir) {
  $dPath = Join-Path $dailyDir ($now.ToString('yyyy-MM-dd') + '.md')
  if (-not (Test-Path $dPath)) { $dPath = Join-Path $dailyDir ($now.AddDays(-1).ToString('yyyy-MM-dd') + '.md') }
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

# Kota satiri (Master karari 2026-08-29): kota.py --hizli tek satir doner;
# dusen/yavas kalirsa sessizce atlanir (fail-quiet: kota bilgisi kritik degil).
$kota = ''
try {
  $kotaJob = Start-Job -ScriptBlock {
    & python "E:\OdenaOS\.claude\scripts\kota.py" --hizli 2>$null
  }
  if (Wait-Job $kotaJob -Timeout 8) {
    $kotaOut = (Receive-Job $kotaJob | Select-Object -First 1)
    if ($kotaOut -and $kotaOut.StartsWith('[kota]')) { $kota = "$kotaOut`n`n" }
  }
  Remove-Job $kotaJob -Force -ErrorAction SilentlyContinue
} catch {}
# Harcama defteri (Master karari 2026-08-29): arkada sessiz biriktirir,
# enjeksiyonu bloklamaz; dusen kosum sonraki oturumda telafi olur.
try {
  Start-Process -WindowStyle Hidden -FilePath python `
    -ArgumentList '"E:\OdenaOS\.claude\scripts\harcama_defteri.py"','--topla' `
    -ErrorAction SilentlyContinue
} catch {}

$closing = "[Memory] Continuity is your job. Read '850-Companion/Core.md' for who you are to this user. Hafiza protokolu zorunludur."

# A6-2: Current material is protected before the root map or daily tail shrink.
# Floors name content characters, not section headers.  Invalid values fall back
# to the safe default so a malformed environment cannot silently remove them.
function Get-BeyinFloor {
  param([string]$Name)
  $raw = [Environment]::GetEnvironmentVariable($Name)
  $value = 800
  if ($raw -match '^\d{1,6}$') {
    try { $value = [int]$raw } catch { $value = 800 }
  }
  return $value
}
function Limit-BeyinBlock {
  param([string]$Block, [int]$Allow, [bool]$KeepTail, [string]$Name)
  if ($Block.Length -le $Allow) { return $Block }
  if ($Allow -le 0) { return '' }
  $suffix = "`n[not: $Name kirpildi - beyin-doktor calistir]`n`n"
  $keep = $Allow - $suffix.Length
  if ($keep -le 0) { return '' }
  if ($KeepTail) { return $suffix + $Block.Substring($Block.Length - $keep, $keep) }
  return $Block.Substring(0, $keep) + $suffix
}
function Test-CiftAcilis {
  param([string]$Path, [string]$Stamp, [string]$CurrentCwd)
  if (-not $CurrentCwd -or -not (Test-Path -LiteralPath $Path)) { return $false }
  try {
    foreach ($line in @(Get-Content -LiteralPath $Path -Encoding UTF8 -Tail 32)) {
      if (-not $line) { continue }
      try {
        $old = $line | ConvertFrom-Json
        if ("$($old.ts)" -eq $Stamp -and "$($old.cwd)" -eq $CurrentCwd) { return $true }
      } catch {}
    }
  } catch {}
  return $false
}

$stamp = $now.ToString('yyyy-MM-ddTHH:mm:sszzz')
$ledgerPath = Join-Path $state 'enjeksiyon.jsonl'
# Desktop helper starts have no documented payload discriminator.  A same-second,
# same-cwd record is nevertheless deterministic and cheap to suppress.
if (Test-CiftAcilis $ledgerPath $stamp $cwd) {
  $ciftCtx = "[ZAMAN]`n$zaman`n(cift acilis - tam paket ilk acilisa basildi)"
  try {
    $ciftRec = @{ ts = $stamp; cwd = $cwd; session_id = $sid; toplam = $ciftCtx.Length; sabit = 0; zaman = $zaman.Length; threads = 0; lastsession = 0; kurallar = 0; journal = 0; indeks = 0; daily = 0; kirpik = 0; kirpildi = @(); cift = $true } | ConvertTo-Json -Compress
    Add-Content -Path $ledgerPath -Value $ciftRec -Encoding UTF8
  } catch {}
  $out = @{ hookSpecificOutput = @{ hookEventName = 'SessionStart'; additionalContext = $ciftCtx } } | ConvertTo-Json -Compress -Depth 4
  [Console]::Out.WriteLine($out)
  exit 0
}

$dailyFloor = Get-BeyinFloor 'BEYIN_ACILIS_DAILY_TABAN'
$indexFloor = Get-BeyinFloor 'BEYIN_ACILIS_INDEKS_TABAN'
$cap = 16000
$fixed = ''
# Reflection stays first when present.  Time and quota deliberately come last:
# their values are volatile, while the prefix is stable across identical starts.
if ($reflection)  { $fixed += $reflection + "`n`n" }
if ($lastSession) { $fixed += "[Memory - Last Session]`n$lastSession`n`n" }
if ($threads)     { $fixed += "[Memory - Active Threads]`n$threads`n`n" }
if ($kurallar)    { $fixed += "[Hafiza - Kurallar]`n$kurallar`n`n" }
$jBlock = ''
if ($journal) { $jBlock = "[Hafiza - Son Journal]`n$journal`n`n" }
# The current reader only emits thread headings/status lines; they are protected.
# Keep a separate elastic block so any future emitted thread body follows Journal.
$tBodyBlock = ''
$kBlock = ''
if ($knowledge) { $kBlock = "[Bilgi Tabani - Indeks]`n$knowledge`n`n" }
$dBlock = ''
if ($dailyTail) { $dBlock = "[Bugunun Logu]`n$dailyTail`n`n" }
$kirpik = 0
$kirpildi = @()
$zamanBlock = if ($zaman) { "[ZAMAN]`n$zaman`n`n" } else { '' }
$kotaBlock = $kota

# Reserve enough room for the measurement line, then trim elastic content in
# priority order.  Journal and a future Threads body yield before protected
# current material.  The final loop accounts for the exact measurement length.
$reserve = 512
$available = $cap - $fixed.Length - $closing.Length - $zamanBlock.Length - $kotaBlock.Length - $reserve
if ($available -lt 0) { $available = 0 }
foreach ($item in @('journal', 'threads')) {
  $totalElastic = $jBlock.Length + $tBodyBlock.Length + $kBlock.Length + $dBlock.Length
  if ($totalElastic -le $available) { break }
  if ($item -eq 'journal' -and $jBlock) {
    $allow = [Math]::Max(0, $jBlock.Length - ($totalElastic - $available))
    $jBlock = Limit-BeyinBlock $jBlock $allow $false 'journal'
    $kirpik = 1; $kirpildi += 'journal'
  }
  if ($item -eq 'threads' -and $tBodyBlock) {
    $totalElastic = $jBlock.Length + $tBodyBlock.Length + $kBlock.Length + $dBlock.Length
    $allow = [Math]::Max(0, $tBodyBlock.Length - ($totalElastic - $available))
    $tBodyBlock = Limit-BeyinBlock $tBodyBlock $allow $false 'threads'
    $kirpik = 1; $kirpildi += 'threads'
  }
}
foreach ($item in @('indeks', 'daily')) {
  $totalElastic = $jBlock.Length + $tBodyBlock.Length + $kBlock.Length + $dBlock.Length
  if ($totalElastic -le $available) { break }
  if ($item -eq 'indeks' -and $kBlock) {
    $minimum = [Math]::Min($kBlock.Length, $indexFloor + "[Bilgi Tabani - Indeks]`n`n".Length + "`n[not: indeks kirpildi - beyin-doktor calistir]`n`n".Length)
    $allow = [Math]::Max($minimum, $kBlock.Length - ($totalElastic - $available))
    $kBlock = Limit-BeyinBlock $kBlock $allow $false 'indeks'
    $kirpik = 1; $kirpildi += 'indeks'
  }
  if ($item -eq 'daily' -and $dBlock) {
    $totalElastic = $jBlock.Length + $tBodyBlock.Length + $kBlock.Length + $dBlock.Length
    $minimum = [Math]::Min($dBlock.Length, $dailyFloor + "[Bugunun Logu]`n`n".Length + "`n[not: daily kirpildi - beyin-doktor calistir]`n`n".Length)
    $allow = [Math]::Max($minimum, $dBlock.Length - ($totalElastic - $available))
    $dBlock = Limit-BeyinBlock $dBlock $allow $true 'daily'
    $kirpik = 1; $kirpildi += 'daily'
  }
}

# Olcum satiri (M2): enjeksiyonun bilesimi hem baglama hem .state/enjeksiyon.jsonl'e yazilir.
$ctxBase = $fixed + $jBlock + $tBodyBlock + $kBlock + $dBlock + $closing
$olcum = "[enjeksiyon] toplam=" + $ctxBase.Length + " sabit=" + $fixed.Length + " threads=" + $threads.Length + " lastsession=" + $lastSession.Length + " kurallar=" + $kurallar.Length + " indeks=" + $kBlock.Length + " daily=" + $dBlock.Length + " kirpik=$kirpik"
$ctx = $ctxBase + "`n" + $olcum + "`n" + $zamanBlock + $kotaBlock
while ($ctx.Length -gt $cap -and ($jBlock -or $tBodyBlock)) {
  if ($jBlock) { $jBlock = ''; if ($kirpildi -notcontains 'journal') { $kirpildi += 'journal' }; $kirpik = 1 }
  elseif ($tBodyBlock) { $tBodyBlock = ''; if ($kirpildi -notcontains 'threads') { $kirpildi += 'threads' }; $kirpik = 1 }
  $ctxBase = $fixed + $jBlock + $tBodyBlock + $kBlock + $dBlock + $closing
  $olcum = "[enjeksiyon] toplam=" + $ctxBase.Length + " sabit=" + $fixed.Length + " threads=" + $threads.Length + " lastsession=" + $lastSession.Length + " kurallar=" + $kurallar.Length + " indeks=" + $kBlock.Length + " daily=" + $dBlock.Length + " kirpik=$kirpik"
  $ctx = $ctxBase + "`n" + $olcum + "`n" + $zamanBlock + $kotaBlock
}
try {
  $rec = @{ ts = $stamp; cwd = $cwd; session_id = $sid; toplam = $ctx.Length; sabit = $fixed.Length; zaman = $zaman.Length; threads = $threads.Length; lastsession = $lastSession.Length; kurallar = $kurallar.Length; journal = $jBlock.Length; indeks = $kBlock.Length; daily = $dBlock.Length; kirpik = $kirpik; kirpildi = @($kirpildi); cift = $false } | ConvertTo-Json -Compress
  Add-Content -Path $ledgerPath -Value $rec -Encoding UTF8
} catch {}

$out = @{ hookSpecificOutput = @{ hookEventName = 'SessionStart'; additionalContext = $ctx } } | ConvertTo-Json -Compress -Depth 4
[Console]::Out.WriteLine($out)
exit 0
