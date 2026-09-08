param([string]$Reason = 'sessionend', [switch]$Tara)
# Ozetleyici: Haiku birincil (Master karari 2026-09-08, 59. oturum - deney:
# qwen3:8b %14,5 uydurma vs Haiku %5,9; bayrak vakasinda yerel 3/3 basarisiz).
# 2026-08-30 'yerel' karari geri alindi. Yerel 8B yalnizca cevrimdisi/kota-kapali
# YEDEK icin duruyor (otomatik dusme Faz 1 isi; simdilik yedek elle: backend'i
# 'ollama' yap). flush.py claude backend'de model='haiku' ister; claude_runner
# onu claude-haiku-4-5-20251001'e cozer.
$env:BEYIN_MODEL_BACKEND = 'claude'
$env:BEYIN_OLLAMA_MODEL_FAST = 'qwen3:8b'
$env:BEYIN_OLLAMA_MODEL_SMART = 'qwen3:30b-a3b-instruct-2507-q4_K_M'
$env:BEYIN_OLLAMA_NUM_CTX = '16384'   # Ollama sessiz 4k kirpmasi (44. oturum teshisi)
# v2 flush launcher: store hook stdin, detach flush.py, return under 1s.
# Wired at USER level so every session on this machine flushes into the vault.
# yazan: codex - model: gpt-5.6-sol
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

# Hook giris izi (A-borc 2, 2026-09-06): 54. oturumda 19 saatlik transkript
# daily'ye hic dusmedi; teshis "SessionEnd hic ateslemedi" ile "atesledi ama
# python baslamadi" arasinda ayrim yapamadi. Bu satir KANCAYA GIRILDIGINI
# kanitlar: stdin okunmadan once yazilir, hatasi yutulur, dosya 512 KB'i
# gecince .1'e devrilir (bakim.py ayni tavanla ayrica devirir).
function Write-BeyinHookGirdi {
  param(
    [string]$HookName,
    [string]$Reason,
    [string]$SessionId = '',
    [object]$ChildPid = $PID,
    [bool]$Started = $false,
    [string]$Phase = 'entered'
  )
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
      session_id = $SessionId
      pid = $ChildPid
      started = $Started
      phase = $Phase
    } | ConvertTo-Json -Compress
    [System.IO.File]::AppendAllText($path, $record + "`n", [System.Text.UTF8Encoding]::new($false))
  } catch {}
}

# session-end.ps1 dot-sources only these shared interpreter/error helpers.
if ($MyInvocation.InvocationName -eq '.') { return }
if ($env:BEYIN_INVOKED_BY) { exit 0 }
if ($Tara) { $Reason = 'tara' }
Write-BeyinHookGirdi 'flush-launch' $Reason

# Zamanli supurge (Master karari 2026-09-07): SessionEnd kancasi uygulama ya da
# makine oldurulunce hic teslim edilmiyor (54. oturum, 19 saat kayip). Gorev
# zamanlayicisi bu dali 8 saatte bir cagirir; stdin yoktur, cikis onplanda
# beklenir ki zamanlanmis gorev calisma suresini dogru olcsun.
if ($Tara) {
  $scriptsDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts'
  $stateDir = Join-Path $scriptsDir '.state'
  New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
  $python = Resolve-BeyinPython
  if (-not $python) {
    Write-BeyinHookError $stateDir 'flush-launch' 'python-3.12+-missing'
    [Console]::Error.WriteLine('[beyin] Python 3.12+ bulunamadi; tara atlandi.')
    exit 0
  }
  $flush = Join-Path $scriptsDir 'flush.py'
  if (-not (Test-Path -LiteralPath $flush)) {
    Write-BeyinHookError $stateDir 'flush-launch' 'flush-script-missing'
    [Console]::Error.WriteLine('[beyin] flush.py bulunamadi; tara atlandi.')
    exit 0
  }
  $env:BEYIN_FLUSH_STDERR_DIR = $stateDir
  try {
    $process = Start-Process -FilePath $python.Path -WindowStyle Hidden -PassThru `
      -ArgumentList @($python.Prefix + @('-X', 'utf8', ('"' + $flush + '"'), '--tara'))
    Write-BeyinHookGirdi 'flush-launch' $Reason '__tara__' $process.Id $true 'launch'
    $process.WaitForExit()
  } catch {
    Write-BeyinHookGirdi 'flush-launch' $Reason '__tara__' $null $false 'launch'
    Write-BeyinHookError $stateDir 'flush-launch' 'python-launch-failed'
  }
  exit 0
}

$stdin = [Console]::In.ReadToEnd()
if (-not $stdin) { exit 0 }
$sessionId = ''
try {
  if ($stdin -match '"session_id"\s*:\s*"([A-Za-z0-9_.-]{1,128})"') {
    $sessionId = $Matches[1]
  }
} catch {}

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

# PreCompact (48. oturum, 2026-09-02, A8-lite): compaction baglami sildigi icin oturum-ici getirme
# dedup defteri sifirlanir; aksi halde bir kez gosterilen not o oturumda bir daha gelmez.
# Defter yeniden uretilebilir onbellektir (retrieve.py --session), veri degildir.
if ($Reason -eq 'precompact') {
  try {
    if ($stdin -match '"session_id"\s*:\s*"([A-Za-z0-9_.-]{1,128})"') {
      $ledger = Join-Path $stateDir ("retrieve-session-" + $Matches[1] + ".json")
      if (Test-Path -LiteralPath $ledger) { Remove-Item -LiteralPath $ledger -Force -ErrorAction SilentlyContinue }
    }
  } catch {}
}


$py = $python.Path
$pyPrefix = @($python.Prefix)
$flush = Join-Path $scriptsDir 'flush.py'
if ($py -and (Test-Path $flush)) {
  $flushArgument = '"' + $flush + '"'
  $inputArgument = '"' + $inputPath + '"'
  $env:BEYIN_FLUSH_STDERR_DIR = $stateDir
  try {
    $process = Start-Process -FilePath $py -WindowStyle Hidden -PassThru -ArgumentList @(
      $pyPrefix + @('-X', 'utf8', $flushArgument, '--hook-input', $inputArgument, '--reason', $Reason))
    Write-BeyinHookGirdi 'flush-launch' $Reason $sessionId $process.Id $true 'launch'
  } catch {
    Write-BeyinHookGirdi 'flush-launch' $Reason $sessionId $null $false 'launch'
    Write-BeyinHookError $stateDir 'flush-launch' 'python-launch-failed'
  }
} else {
  Write-BeyinHookError $stateDir 'flush-launch' 'flush-script-missing'
  [Console]::Error.WriteLine('[beyin] flush.py bulunamadi; flush atlandi.')
}
exit 0
