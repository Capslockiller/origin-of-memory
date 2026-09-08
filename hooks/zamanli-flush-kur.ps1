<#
.SYNOPSIS
  Registers (or removes) the 8-hourly OdenaOS flush sweep as a Windows
  scheduled task.

.DESCRIPTION
  Master's decision (2026-09-07): "8 saatte bir flush calissin; son flush'tan
  sonra degisiklik yoksa calismasin." The SessionEnd hook is never delivered
  when the app or the machine is killed - session 54 lost 19 hours that way -
  so a periodic sweep runs flush-launch.ps1 -Tara, which walks every transcript
  and lets the per-session turn cursor decide whether any model call is needed
  at all. A quiet machine costs one directory walk.

  The task runs ONLY when the user is logged on: the sweep needs the user's own
  Ollama service and CLI session, neither of which exists in a service context.

  Idempotent: re-running replaces the existing registration.

.PARAMETER Kaldir
  Unregister the task.

.PARAMETER Durum
  Print the task's registration and last/next run times.

.PARAMETER GorevAdi
  Task name (default: OdenaOS-Flush).

.PARAMETER KancaYolu
  Full path to flush-launch.ps1 (default: E:\OdenaOS\.claude\hooks\flush-launch.ps1).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File .\zamanli-flush-kur.ps1
#>
[CmdletBinding()]
param(
  [switch]$Kaldir,
  [switch]$Durum,
  [string]$GorevAdi = 'OdenaOS-Flush',
  [string]$KancaYolu = 'E:\OdenaOS\.claude\hooks\flush-launch.ps1'
)

$ErrorActionPreference = 'Stop'

function Get-BeyinGorev {
  param([string]$Name)
  try { return Get-ScheduledTask -TaskName $Name -ErrorAction Stop } catch { return $null }
}

function Show-BeyinGorev {
  param([string]$Name)
  $task = Get-BeyinGorev -Name $Name
  if (-not $task) {
    Write-Host "[beyin] '$Name' kayitli degil."
    return $false
  }
  Write-Host "[beyin] Gorev      : $($task.TaskName)"
  Write-Host "[beyin] Durum      : $($task.State)"
  foreach ($action in $task.Actions) {
    Write-Host "[beyin] Eylem      : $($action.Execute) $($action.Arguments)"
  }
  foreach ($trigger in $task.Triggers) {
    Write-Host "[beyin] Tetik      : $($trigger.StartBoundary) / tekrar $($trigger.Repetition.Interval)"
  }
  try {
    $info = Get-ScheduledTaskInfo -TaskName $Name -ErrorAction Stop
    Write-Host "[beyin] Son calisma: $($info.LastRunTime) (sonuc $($info.LastTaskResult))"
    Write-Host "[beyin] Sonraki    : $($info.NextRunTime)"
  } catch {}
  return $true
}

if ($Durum) {
  if (Show-BeyinGorev -Name $GorevAdi) { exit 0 } else { exit 1 }
}

if ($Kaldir) {
  if (Get-BeyinGorev -Name $GorevAdi) {
    Unregister-ScheduledTask -TaskName $GorevAdi -Confirm:$false
    Write-Host "[beyin] '$GorevAdi' kaldirildi."
  } else {
    Write-Host "[beyin] '$GorevAdi' zaten kayitli degil."
  }
  exit 0
}

if (-not (Test-Path -LiteralPath $KancaYolu)) {
  Write-Error "[beyin] Kanca bulunamadi: $KancaYolu"
  exit 1
}
$KancaYolu = (Resolve-Path -LiteralPath $KancaYolu).Path

$powershell = (Get-Command powershell.exe -ErrorAction SilentlyContinue).Source
if (-not $powershell) { $powershell = 'powershell.exe' }

$arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Tara' -f $KancaYolu
$action = New-ScheduledTaskAction -Execute $powershell -Argument $arguments `
  -WorkingDirectory (Split-Path -Parent $KancaYolu)

# Next full hour, then every 8 hours forever. No -RepetitionDuration:
# PS 5.1 on Win 11 rejects a zero duration (PT0S); omitting it means indefinitely.
$now = Get-Date
$start = $now.Date.AddHours($now.Hour + 1)
$trigger = New-ScheduledTaskTrigger -Once -At $start `
  -RepetitionInterval ([TimeSpan]::FromHours(8))

# Omitting the duration leaves StopAtDurationEnd = True with an empty duration,
# which some Windows builds read as "stop at the end of a zero-length window" -
# the repetition then never fires again. Say it explicitly instead; the
# property lives on the CIM trigger object and is settable on PS 5.1.
if ($trigger.Repetition) { $trigger.Repetition.StopAtDurationEnd = $false }

# Logged-on only: the sweep needs the user's Ollama and CLI. Interactive means
# no stored password and no service-session surprises.
$principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) `
  -LogonType Interactive -RunLevel Limited

$settings = New-ScheduledTaskSettingsSet `
  -StartWhenAvailable `
  -ExecutionTimeLimit ([TimeSpan]::FromMinutes(30)) `
  -AllowStartIfOnBatteries `
  -DontStopIfGoingOnBatteries `
  -MultipleInstances IgnoreNew `
  -DontStopOnIdleEnd
$settings.Hidden = $true

Register-ScheduledTask -TaskName $GorevAdi -Action $action -Trigger $trigger `
  -Principal $principal -Settings $settings `
  -Description 'OdenaOS: 8 saatte bir flush supurgesi (flush.py --tara). Degisiklik yoksa model cagrilmaz.' `
  -Force | Out-Null

Write-Host "[beyin] '$GorevAdi' kaydedildi."
Write-Host "[beyin] Ilk calisma: $($start.ToString('yyyy-MM-dd HH:mm')) - sonra 8 saatte bir."
Write-Host ''
Write-Host '[beyin] Orkestratorun calistiracagi tam komut:'
Write-Host ('powershell -NoProfile -ExecutionPolicy Bypass -File "{0}\zamanli-flush-kur.ps1"' -f (Split-Path -Parent $KancaYolu))
Write-Host '[beyin] Durum icin  : ... -File "<ayni yol>\zamanli-flush-kur.ps1" -Durum'
Write-Host '[beyin] Kaldirmak   : ... -File "<ayni yol>\zamanli-flush-kur.ps1" -Kaldir'
Write-Host '[beyin] Gorevin kendi eylemi:'
Write-Host ('  {0} {1}' -f $powershell, $arguments)
exit 0
