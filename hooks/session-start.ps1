# v3 session-start (2026-09-02, 48. oturum): v2 enjeksiyonu + ZAMAN blogu + Threads diyeti
# + olcum satiri. v2: Last-Session + Threads + reflection + Kurallar + Journal + indeks + daily.
# ASCII-only kaynak (PS 5.1 BOM'suz dosyayi ANSI okur); Turkce harfler regex'te \uXXXX ile.
# yazan: codex - model: gpt-5.6-sol
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

# Fixed sections are never truncated; knowledge shrinks first, then daily tail.
$fixed = ''
if ($zaman)       { $fixed += "[ZAMAN]`n$zaman`n`n" }
if ($reflection)  { $fixed += $reflection + "`n`n" }
if ($lastSession) { $fixed += "[Memory - Last Session]`n$lastSession`n`n" }
if ($threads)     { $fixed += "[Memory - Active Threads]`n$threads`n`n" }
if ($kurallar)    { $fixed += "[Hafiza - Kurallar]`n$kurallar`n`n" }
if ($journal)     { $fixed += "[Hafiza - Son Journal]`n$journal`n`n" }
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
if ($kota) { $fixed += $kota }

# Harcama defteri (Master karari 2026-08-29): arkada sessiz biriktirir,
# enjeksiyonu bloklamaz; dusen kosum sonraki oturumda telafi olur.
try {
  Start-Process -WindowStyle Hidden -FilePath python `
    -ArgumentList '"E:\OdenaOS\.claude\scripts\harcama_defteri.py"','--topla' `
    -ErrorAction SilentlyContinue
} catch {}

$closing = "[Memory] Continuity is your job. Read '850-Companion/Core.md' for who you are to this user. Hafiza protokolu zorunludur."

$cap = 16000
$note = "`n[not: indeks kirpildi - beyin-doktor calistir]"
$budget = $cap - $fixed.Length - $closing.Length - 80
if ($budget -lt 0) { $budget = 0 }
$kBlock = ''
if ($knowledge) { $kBlock = "[Bilgi Tabani - Indeks]`n$knowledge`n`n" }
$dBlock = ''
if ($dailyTail) { $dBlock = "[Bugunun Logu]`n$dailyTail`n`n" }
$kirpik = 0
if (($kBlock.Length + $dBlock.Length) -gt $budget) {
  $kirpik = 1
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

# Olcum satiri (M2): enjeksiyonun bilesimi hem baglama hem .state/enjeksiyon.jsonl'e yazilir.
$ctx = $fixed + $kBlock + $dBlock + $closing
$olcum = "[enjeksiyon] toplam=" + $ctx.Length + " sabit=" + $fixed.Length + " threads=" + $threads.Length + " lastsession=" + $lastSession.Length + " kurallar=" + $kurallar.Length + " indeks=" + $kBlock.Length + " daily=" + $dBlock.Length + " kirpik=$kirpik"
$ctx = $ctx + "`n" + $olcum
try {
  $rec = @{ ts = $now.ToString('yyyy-MM-ddTHH:mm:sszzz'); toplam = $ctx.Length; sabit = $fixed.Length; zaman = $zaman.Length; threads = $threads.Length; lastsession = $lastSession.Length; kurallar = $kurallar.Length; journal = $journal.Length; indeks = $kBlock.Length; daily = $dBlock.Length; kirpik = $kirpik } | ConvertTo-Json -Compress
  Add-Content -Path (Join-Path $state 'enjeksiyon.jsonl') -Value $rec -Encoding UTF8
} catch {}

$out = @{ hookSpecificOutput = @{ hookEventName = 'SessionStart'; additionalContext = $ctx } } | ConvertTo-Json -Compress -Depth 4
[Console]::Out.WriteLine($out)
exit 0
