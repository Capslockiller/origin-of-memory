# yazan: codex
# model: gpt-5.6-sol
<#
.SYNOPSIS
Starts the Origin of Memory operations panel on a loopback-only HTTP server.

.DESCRIPTION
Serves one self-contained page, reports health, today's activity, and local
model evidence, and runs explicit confirmed operations. The panel has no
destructive route.
#>
[CmdletBinding()]
param(
  [switch]$NoBrowser,
  [ValidateRange(2, 3600)]
  [int]$GraceSeconds = 300
)

$ErrorActionPreference = 'Stop'
if ([Environment]::GetEnvironmentVariable('BEYIN_INVOKED_BY')) { exit 0 }

$script:RepoRoot = $PSScriptRoot
$script:Utf8 = New-Object Text.UTF8Encoding($false)
$script:Crlf = [string][char]13 + [char]10
$script:Events = New-Object Collections.ArrayList
$script:NextSequence = 1
$script:ActiveOperation = $null
$script:TokenUsed = $false
$script:QuitRequested = $false
$script:ShutdownAt = $null
$script:IdleSeconds = $GraceSeconds
$script:AllowedBackends = @('claude', 'antigravity', 'ollama', 'openai-compat')
$script:PasaportIzleyiciProcess = $null
# F5 "Kokpit" part 2 — kule (the tower) lifecycle mirrors the pasaport
# listener above: born with the panel (Start-Kule), dies with it (Stop-Kule
# in the single shutdown path). $script:VsCodeInfo caches Find-VsCode's
# result for the whole panel session; the marker matches kule.py's own
# RESULT_MARKER exactly (see scripts/kule.py).
$script:KuleProcess = $null
$script:VsCodeInfo = $null
$script:KuleResultMarker = 'KULE-SONUC '

function New-RandomToken {
  $bytes = New-Object byte[] 32
  $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
  try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
  return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Read-JsonObject([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return @{} }
  try {
    $value = [IO.File]::ReadAllText($Path, $script:Utf8) | ConvertFrom-Json
    if ($null -eq $value) { return @{} }
    return $value
  } catch {
    return @{}
  }
}

function Resolve-PanelPaths {
  $configured = [Environment]::GetEnvironmentVariable('BEYIN_VAULT')
  $vault = if ($configured) {
    [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($configured))
  } else {
    $script:RepoRoot
  }
  $installedScripts = Join-Path $vault '.claude\scripts'
  $sourceScripts = Join-Path $script:RepoRoot 'scripts'
  $scripts = if (Test-Path -LiteralPath (Join-Path $installedScripts 'durum.py') -PathType Leaf) {
    $installedScripts
  } else {
    $sourceScripts
  }
  return [pscustomobject]@{
    Vault = $vault
    Scripts = $scripts
    State = Join-Path $scripts '.state'
  }
}

function Find-PythonCommand {
  $candidate = $null
  if ($env:BEYIN_PYTHON) {
    $command = Get-Command $env:BEYIN_PYTHON -ErrorAction SilentlyContinue
    if ($command) { $candidate = $command.Source }
    elseif (Test-Path -LiteralPath $env:BEYIN_PYTHON -PathType Leaf) { $candidate = $env:BEYIN_PYTHON }
  }
  if (-not $candidate) {
    $command = Get-Command python -ErrorAction SilentlyContinue
    if ($command) { $candidate = $command.Source }
  }
  if ($candidate) { return [pscustomobject]@{ Exe = $candidate; Prefix = @() } }
  $command = Get-Command py -ErrorAction SilentlyContinue
  if ($command) { return [pscustomobject]@{ Exe = $command.Source; Prefix = @('-3') } }
  return $null
}

function ConvertTo-PowerShellLiteral([string]$Value) {
  return "'" + $Value.Replace("'", "''") + "'"
}

function New-PythonCommand([string[]]$Arguments) {
  if (-not $script:Python) { throw 'Python was not found. Set BEYIN_PYTHON to the interpreter path.' }
  $parts = New-Object Collections.Generic.List[string]
  $parts.Add('& ' + (ConvertTo-PowerShellLiteral $script:Python.Exe))
  foreach ($item in @($script:Python.Prefix) + @($Arguments)) {
    $parts.Add((ConvertTo-PowerShellLiteral ([string]$item)))
  }
  return "`$env:PYTHONUTF8 = '1'; " + ($parts -join ' ') + '; exit $LASTEXITCODE'
}

function New-PythonStdinCommand([string]$Code, [string[]]$Arguments) {
  if (-not $script:Python) { throw 'Python was not found. Set BEYIN_PYTHON to the interpreter path.' }
  $encodedCode = [Convert]::ToBase64String($script:Utf8.GetBytes($Code))
  $parts = New-Object Collections.Generic.List[string]
  $parts.Add('& ' + (ConvertTo-PowerShellLiteral $script:Python.Exe))
  foreach ($item in @($script:Python.Prefix) + @('-u', '-') + @($Arguments)) {
    $parts.Add((ConvertTo-PowerShellLiteral ([string]$item)))
  }
  return (
    "`$env:PYTHONUTF8 = '1'; " +
    "`$code = (New-Object Text.UTF8Encoding(`$false)).GetString([Convert]::FromBase64String('$encodedCode')); " +
    "`$code | " + ($parts -join ' ') + '; exit $LASTEXITCODE'
  )
}

function Add-PanelEvent([string]$Type, [Collections.IDictionary]$Payload) {
  $sequence = $script:NextSequence
  $script:NextSequence++
  $data = [ordered]@{
    sequence = $sequence
    type = $Type
    time = [DateTime]::UtcNow.ToString('o')
  }
  if ($Payload) {
    foreach ($key in $Payload.Keys) { $data[$key] = $Payload[$key] }
  }
  [void]$script:Events.Add([pscustomobject]@{
    Sequence = $sequence
    Type = $Type
    Json = ($data | ConvertTo-Json -Depth 30 -Compress)
  })
  while ($script:Events.Count -gt 500) { $script:Events.RemoveAt(0) }
}

function Get-OperationCommand([string]$Kind) {
  $scriptPath = switch ($Kind) {
    'doctor' { Join-Path $script:PanelPaths.Scripts 'durum.py' }
    'compile' { Join-Path $script:PanelPaths.Scripts 'compile.py' }
    'index' { Join-Path $script:PanelPaths.Scripts 'retrieve.py' }
    'watcher' { Join-Path $script:PanelPaths.Scripts 'watcher.py' }
    default { throw "Unknown operation: $Kind" }
  }
  if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
    throw "Operation script was not found: $scriptPath"
  }
  $arguments = switch ($Kind) {
    'doctor' { @($scriptPath, '--json', '--state-dir', $script:PanelPaths.State) }
    'compile' { @($scriptPath) }
    'index' { @($scriptPath, 'build', '--vault-root', $script:PanelPaths.Vault, '--state-dir', $script:PanelPaths.State) }
    'watcher' { @($scriptPath, '--once') }
  }
  return New-PythonCommand $arguments
}

function Start-PanelCommand([string]$Kind, [string]$Command, [Collections.IDictionary]$Metadata = @{}) {
  if ($script:ActiveOperation -and -not $script:ActiveOperation.Process.HasExited) { return $false }
  $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Command))
  $info = New-Object Diagnostics.ProcessStartInfo
  $info.FileName = Join-Path $PSHOME 'powershell.exe'
  $info.Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded"
  $info.WorkingDirectory = $script:PanelPaths.Vault
  $info.UseShellExecute = $false
  $info.CreateNoWindow = $true
  $info.RedirectStandardOutput = $true
  $info.RedirectStandardError = $true
  $process = New-Object Diagnostics.Process
  $process.StartInfo = $info
  if (-not $process.Start()) { throw "Could not start $Kind." }
  $outSource = "oom-panel-out-$($process.Id)-$([Guid]::NewGuid().ToString('N'))"
  $errSource = "oom-panel-err-$($process.Id)-$([Guid]::NewGuid().ToString('N'))"
  Register-ObjectEvent -InputObject $process -EventName OutputDataReceived -SourceIdentifier $outSource | Out-Null
  Register-ObjectEvent -InputObject $process -EventName ErrorDataReceived -SourceIdentifier $errSource | Out-Null
  $process.BeginOutputReadLine()
  $process.BeginErrorReadLine()
  $script:ActiveOperation = [pscustomobject]@{
    Kind = $Kind
    Process = $process
    OutSource = $outSource
    ErrSource = $errSource
    CancelRequested = $false
    Metadata = $Metadata
  }
  $started = [ordered]@{ operation = $Kind; process_id = $process.Id }
  foreach ($key in @($Metadata.Keys)) { $started[$key] = $Metadata[$key] }
  Add-PanelEvent 'operation-started' $started
  return $true
}

function Start-PanelOperation([string]$Kind) {
  return Start-PanelCommand $Kind (Get-OperationCommand $Kind)
}

function Read-OperationEvent([string]$Source, [bool]$IsError) {
  while ($true) {
    $eventRecord = Get-Event -SourceIdentifier $Source -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $eventRecord) { break }
    Remove-Event -EventIdentifier $eventRecord.EventIdentifier -ErrorAction SilentlyContinue
    $line = $eventRecord.SourceEventArgs.Data
    if ($null -eq $line -or $line -eq '') { continue }
    Add-PanelEvent 'operation-output' ([ordered]@{
      operation = $script:ActiveOperation.Kind
      stream = $(if ($IsError) { 'stderr' } else { 'stdout' })
      line = $line
    })
  }
}

function Update-PanelOperation {
  if (-not $script:ActiveOperation) { return }
  Read-OperationEvent $script:ActiveOperation.OutSource $false
  Read-OperationEvent $script:ActiveOperation.ErrSource $true
  if (-not $script:ActiveOperation.Process.HasExited) { return }
  $operation = $script:ActiveOperation
  $operation.Process.WaitForExit()
  Read-OperationEvent $operation.OutSource $false
  Read-OperationEvent $operation.ErrSource $true
  $exitCode = $operation.Process.ExitCode
  if ($operation.CancelRequested) {
    Add-PanelEvent 'operation-cancelled' ([ordered]@{
      operation = $operation.Kind
      exit_code = $exitCode
      message = 'The model pull was cancelled. Ollama can resume its partial download.'
    })
  } elseif ($exitCode -eq 0) {
    Add-PanelEvent 'operation-completed' ([ordered]@{ operation = $operation.Kind; exit_code = $exitCode })
  } else {
    Add-PanelEvent 'operation-failed' ([ordered]@{
      operation = $operation.Kind
      exit_code = $exitCode
      message = "$($operation.Kind) failed with exit code $exitCode."
    })
  }
  Unregister-Event -SourceIdentifier $operation.OutSource -ErrorAction SilentlyContinue
  Unregister-Event -SourceIdentifier $operation.ErrSource -ErrorAction SilentlyContinue
  $operation.Process.Dispose()
  $script:ActiveOperation = $null
  $script:ShutdownAt = [DateTime]::UtcNow.AddSeconds($script:IdleSeconds)
}

function Invoke-HealthSummary {
  if (-not $script:Python) { throw 'Python was not found. Set BEYIN_PYTHON to the interpreter path.' }
  $arguments = @($script:Python.Prefix) + @(
    (Join-Path $script:PanelPaths.Scripts 'durum.py'),
    '--json',
    '--state-dir',
    $script:PanelPaths.State
  )
  $oldPreference = $ErrorActionPreference
  try {
    $ErrorActionPreference = 'Continue'
    $lines = @(& $script:Python.Exe @arguments 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = $LASTEXITCODE
  } finally {
    $ErrorActionPreference = $oldPreference
  }
  if ($exitCode -ne 0 -or $lines.Count -eq 0) { throw 'The health command produced no usable JSON.' }
  return (($lines -join [Environment]::NewLine) | ConvertFrom-Json)
}

function Invoke-PanelPythonJson([string[]]$Arguments, [string]$FailureMessage, [string]$StandardInput = $null) {
  if (-not $script:Python) { throw 'Python was not found. Set BEYIN_PYTHON to the interpreter path.' }
  $allArguments = @($script:Python.Prefix) + @($Arguments)
  $oldPreference = $ErrorActionPreference
  try {
    $ErrorActionPreference = 'Continue'
    $lines = if ($null -ne $StandardInput) {
      @($StandardInput | & $script:Python.Exe @allArguments 2>&1 | ForEach-Object { $_.ToString() })
    } else {
      @(& $script:Python.Exe @allArguments 2>&1 | ForEach-Object { $_.ToString() })
    }
    $exitCode = $LASTEXITCODE
  } finally {
    $ErrorActionPreference = $oldPreference
  }
  if ($exitCode -ne 0 -or $lines.Count -eq 0) { throw $FailureMessage }
  try { return (($lines -join [Environment]::NewLine) | ConvertFrom-Json) } catch { throw $FailureMessage }
}

function Invoke-HardwareProbe {
  $scriptPath = Join-Path $script:PanelPaths.Scripts 'donanim.py'
  if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { throw 'donanim.py was not found.' }
  return Invoke-PanelPythonJson @($scriptPath, '--json') 'The hardware probe produced no usable JSON.'
}

function Invoke-ModelRecommendations([object]$Probe) {
  $scriptPath = Join-Path $script:PanelPaths.Scripts 'model_oneri.py'
  if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { throw 'model_oneri.py was not found.' }
  $probeJson = $Probe | ConvertTo-Json -Depth 100 -Compress
  $encodedProbe = [Convert]::ToBase64String($script:Utf8.GetBytes($probeJson))
  $code = @'
import base64
import os
import runpy
import sys

script = sys.argv[1]
probe = base64.b64decode(sys.argv[2]).decode("utf-8")
sys.path.insert(0, os.path.dirname(os.path.abspath(script)))
sys.argv = [script, "--json", "--probe-json", probe]
runpy.run_path(script, run_name="__main__")
'@
  return @(Invoke-PanelPythonJson @('-', $scriptPath, $encodedProbe) 'The model recommendation command produced no usable JSON.' $code)
}

function Get-BackendSummary {
  $code = @'
import json
import os
import sys

sys.path.insert(0, sys.argv[1])
import claude_runner

backend, warning = claude_runner.resolve_backend()
compile_name, compile_warning = claude_runner.compile_backend()
print(json.dumps({
    "backend": backend,
    "configured_value": os.environ.get("BEYIN_MODEL_BACKEND", ""),
    "warning": warning,
    "compile_backend": compile_name,
    "compile_warning": compile_warning,
    "models": {
        "fast": claude_runner._resolved_slug(backend, "haiku") or None,
        "smart": claude_runner._resolved_slug(backend, "sonnet") or None,
    },
}, ensure_ascii=False))
'@
  return Invoke-PanelPythonJson @('-', $script:PanelPaths.Scripts) 'The active backend could not be resolved through claude_runner.' $code
}

function Get-OllamaBaseUrl {
  $configured = [Environment]::GetEnvironmentVariable('BEYIN_OLLAMA_URL')
  $raw = if ($configured) { $configured.Trim() } else { 'http://localhost:11434' }
  try { $uri = [Uri]$raw } catch { throw 'BEYIN_OLLAMA_URL is not a valid absolute URL.' }
  if (-not $uri.IsAbsoluteUri -or $uri.Scheme -cne 'http') {
    throw 'BEYIN_OLLAMA_URL must be an absolute http URL.'
  }
  $isLoopback = $uri.Host -ceq 'localhost'
  $address = $null
  if ([Net.IPAddress]::TryParse($uri.Host, [ref]$address)) {
    $isLoopback = [Net.IPAddress]::IsLoopback($address)
  }
  if (-not $isLoopback) { throw 'BEYIN_OLLAMA_URL must name a loopback host.' }
  return $raw.TrimEnd('/')
}

function Invoke-OllamaRequest([string]$Path) {
  $baseUrl = Get-OllamaBaseUrl
  $request = [Net.HttpWebRequest]::Create($baseUrl + $Path)
  $request.Method = 'GET'
  $request.Timeout = 1500
  $request.ReadWriteTimeout = 1500
  $request.AllowAutoRedirect = $false
  $request.Proxy = $null
  $response = $null
  $reader = $null
  try {
    $response = $request.GetResponse()
    if ([int]$response.StatusCode -ne 200) { throw "Ollama returned HTTP $([int]$response.StatusCode)." }
    $reader = New-Object IO.StreamReader($response.GetResponseStream(), $script:Utf8)
    $text = $reader.ReadToEnd()
    return $text | ConvertFrom-Json
  } finally {
    if ($reader) { $reader.Dispose() }
    if ($response) { $response.Dispose() }
  }
}

function Get-OllamaInventory {
  $commandPresent = [bool](Get-Command ollama -ErrorAction SilentlyContinue)
  try { $baseUrl = Get-OllamaBaseUrl } catch {
    return [ordered]@{
      status = 'invalid-config'
      message = 'Ollama inventory is unavailable because BEYIN_OLLAMA_URL is not a valid loopback HTTP URL.'
      endpoint = $null
      command_present = $commandPresent
      models = $null
      detail = $_.Exception.Message
    }
  }
  try {
    $payload = Invoke-OllamaRequest '/api/tags'
    $models = @($payload.models | ForEach-Object {
      [ordered]@{
        name = $(if ($_.name) { [string]$_.name } else { [string]$_.model })
        model = [string]$_.model
        size_bytes = $(if ($null -ne $_.size) { [long]$_.size } else { $null })
        modified_at = $(if ($_.modified_at) { [string]$_.modified_at } else { $null })
        digest = $(if ($_.digest) { [string]$_.digest } else { $null })
      }
    })
    return [ordered]@{
      status = 'running'
      message = $(if ($commandPresent) { 'Ollama is installed and running.' } else { 'Ollama is running, but its command is not on PATH.' })
      endpoint = $baseUrl
      command_present = $commandPresent
      models = $models
    }
  } catch {
    $message = if ($commandPresent) {
      'Ollama is installed but is not running at its configured loopback endpoint.'
    } else {
      'Ollama is not installed (or is not on PATH), and its loopback API is unreachable.'
    }
    return [ordered]@{
      status = $(if ($commandPresent) { 'not-running' } else { 'not-installed' })
      message = $message
      endpoint = $baseUrl
      command_present = $commandPresent
      models = $null
      detail = $_.Exception.Message
    }
  }
}

function Invoke-NezaketDurum {
  $scriptPath = Join-Path $script:PanelPaths.Scripts 'nezaket.py'
  if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { throw 'nezaket.py was not found.' }
  $raw = Invoke-PanelPythonJson @($scriptPath, '--state-dir', $script:PanelPaths.State, 'durum', '--json') 'The politeness gate produced no usable JSON.'
  # ConvertFrom-Json collapses a single-element JSON array into a bare object,
  # which a one-record queue would otherwise trip on the round trip back out
  # through ConvertTo-Json below (same pattern Get-OllamaInventory uses).
  $kayitlar = @()
  if ($raw.kuyruk -and $raw.kuyruk.kayitlar) { $kayitlar = @($raw.kuyruk.kayitlar) }
  return [ordered]@{
    mesgul = [bool]$raw.mesgul
    neden = [string]$raw.neden
    bilinmiyor = [bool]$raw.bilinmiyor
    okuma = $raw.okuma
    kuyruk = [ordered]@{
      adet = $(if ($raw.kuyruk) { [int]$raw.kuyruk.adet } else { 0 })
      en_eski_yas_sn = $(if ($raw.kuyruk) { $raw.kuyruk.en_eski_yas_sn } else { $null })
      kayitlar = $kayitlar
    }
    izin_hatasi = $raw.izin_hatasi
  }
}

function Get-NezaketOperationCommand([object]$Kayit) {
  $tur = [string]$Kayit.tur
  $scriptName = switch ($tur) {
    'compile' { 'compile.py' }
    'watcher' { 'watcher.py' }
    'ingest' { 'ingest.py' }
    default { throw "Cannot replay a deferred '$tur' operation from the panel." }
  }
  $scriptPath = Join-Path $script:PanelPaths.Scripts $scriptName
  if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
    throw "Operation script was not found: $scriptPath"
  }
  $argv = @()
  if ($Kayit.argv) { $argv = @($Kayit.argv | ForEach-Object { [string]$_ }) }
  # The release itself IS the explicit approval; --nezaket-del stops this one
  # replay from being re-queued if the machine is somehow still busy.
  # --nezaket-del MUST come BEFORE $argv, not after: for ingest, $argv starts
  # with the subcommand (e.g. "claude"), and --nezaket-del is only defined on
  # ingest.py's top-level parser, not on its subparsers — put after the
  # subcommand token it is an "unrecognized arguments" argparse error (exit
  # 2), which the caller then reads as a silent invalid-arguments no-op.
  $arguments = @($scriptPath) + @('--nezaket-del') + $argv
  return New-PythonCommand $arguments
}

function Invoke-PasaportDurum {
  $kapiScript = Join-Path $script:PanelPaths.Scripts 'pasaport_kapi.py'
  $defterScript = Join-Path $script:PanelPaths.Scripts 'pasaport_defteri.py'
  $bekleyen = $null
  if (Test-Path -LiteralPath $kapiScript -PathType Leaf) {
    $bekleyen = Invoke-PanelPythonJson (@($kapiScript, '--state-dir', $script:PanelPaths.State, '--vault-root', $script:PanelPaths.Vault, 'bekleyen', '--json')) 'The pending pasaport candidate produced no usable JSON.'
  }
  $sonPaketler = @()
  $istekler = @()
  if (Test-Path -LiteralPath $defterScript -PathType Leaf) {
    $sonPaketler = @(Invoke-PanelPythonJson (@($defterScript, '--state-dir', $script:PanelPaths.State, 'durum', '--json')) 'The pasaport ledger produced no usable JSON.' | Select-Object -First 5)
    $istekler = @(Invoke-PanelPythonJson (@($defterScript, '--state-dir', $script:PanelPaths.State, 'istekler', '--json')) 'The pasaport blind-spot map produced no usable JSON.')
  }
  $heartbeat = Read-JsonObject (Join-Path $script:PanelPaths.State 'pano-izleyici.json')
  $calisiyor = [bool]($script:PasaportIzleyiciProcess -and -not $script:PasaportIzleyiciProcess.HasExited)
  return [ordered]@{
    bekleyen = $bekleyen
    son_paketler = $sonPaketler
    istekler = $istekler
    izleyici = [ordered]@{
      calisiyor = $calisiyor
      heartbeat = $heartbeat
    }
  }
}

function New-PasaportOnaylaCommand([string]$RawHash) {
  if (-not $script:Python) { throw 'Python was not found. Set BEYIN_PYTHON to the interpreter path.' }
  $scriptPath = Join-Path $script:PanelPaths.Scripts 'pasaport_kapi.py'
  if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { throw 'pasaport_kapi.py was not found.' }
  $arguments = @(
    $scriptPath, '--state-dir', $script:PanelPaths.State, '--vault-root', $script:PanelPaths.Vault,
    'onayla', $RawHash
  )
  return New-PythonCommand $arguments
}

function New-PasaportPanodanCommand {
  if (-not $script:Python) { throw 'Python was not found. Set BEYIN_PYTHON to the interpreter path.' }
  $scriptPath = Join-Path $script:PanelPaths.Scripts 'pano_izleyici.py'
  if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { throw 'pano_izleyici.py was not found.' }
  $arguments = @(
    $scriptPath, '--state-dir', $script:PanelPaths.State, '--vault-root', $script:PanelPaths.Vault, '--once'
  )
  return New-PythonCommand $arguments
}

function Start-PasaportIzleyici {
  # Off-switch checked before anything else: BEYIN_PASAPORT_IZLEYICI=off means
  # the listener is never spawned at all, not spawned-then-killed.
  $off = [Environment]::GetEnvironmentVariable('BEYIN_PASAPORT_IZLEYICI')
  if ($off -and $off.Trim().ToLowerInvariant() -eq 'off') { return }
  if (-not $script:Python) { return }
  $scriptPath = Join-Path $script:PanelPaths.Scripts 'pano_izleyici.py'
  if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { return }
  $arguments = New-Object Collections.Generic.List[string]
  foreach ($item in @($script:Python.Prefix)) { $arguments.Add([string]$item) }
  $arguments.Add($scriptPath)
  $arguments.Add('--state-dir')
  $arguments.Add($script:PanelPaths.State)
  $arguments.Add('--vault-root')
  $arguments.Add($script:PanelPaths.Vault)
  try {
    $script:PasaportIzleyiciProcess = Start-Process -FilePath $script:Python.Exe -ArgumentList $arguments.ToArray() -WindowStyle Hidden -PassThru -ErrorAction Stop
  } catch {
    $script:PasaportIzleyiciProcess = $null
  }
}

function Stop-PasaportIzleyici {
  if (-not $script:PasaportIzleyiciProcess) { return }
  try {
    if (-not $script:PasaportIzleyiciProcess.HasExited) {
      # The listener is a message-only window (no visible main window), so
      # CloseMainWindow() can legitimately return false — that is not a
      # failure, only proof the graceful path was tried before the kill.
      [void]$script:PasaportIzleyiciProcess.CloseMainWindow()
      if (-not $script:PasaportIzleyiciProcess.WaitForExit(2000)) {
        $script:PasaportIzleyiciProcess.Kill()
      }
    }
  } catch {
  } finally {
    $script:PasaportIzleyiciProcess = $null
  }
}

# --------------------------------------------------------------------------
# F5 "Kokpit" part 2 — kule (the tower): lifecycle, routes' backing calls.
# The panel never computes here either: every count/status shown comes
# straight out of kule/durum.json or a job record kule.py itself wrote.
# --------------------------------------------------------------------------

function Get-QueryParams([string]$QueryString) {
  $result = @{}
  if (-not $QueryString) { return $result }
  foreach ($pair in ($QueryString -split '&')) {
    if (-not $pair) { continue }
    $eq = $pair.IndexOf('=')
    if ($eq -lt 0) {
      $result[[Uri]::UnescapeDataString($pair)] = ''
    } else {
      $key = [Uri]::UnescapeDataString($pair.Substring(0, $eq))
      $value = [Uri]::UnescapeDataString($pair.Substring($eq + 1))
      $result[$key] = $value
    }
  }
  return $result
}

function ConvertTo-OrderedFromJsonObject([object]$Value) {
  # ConvertFrom-Json hands back a Hashtable for an empty/missing file (see
  # Read-JsonObject) and a PSCustomObject for real JSON content — merge
  # either shape's keys into a plain [ordered] so callers can add fields to
  # it before it goes back out through ConvertTo-Json.
  $result = [ordered]@{}
  if ($Value -is [Collections.IDictionary]) {
    foreach ($key in $Value.Keys) { $result[$key] = $Value[$key] }
  } elseif ($Value) {
    foreach ($prop in @($Value.PSObject.Properties)) { $result[$prop.Name] = $prop.Value }
  }
  return $result
}

function Find-VsCode {
  # Cached for the panel session — probed at most once, in this fixed order.
  if ($script:VsCodeInfo) { return $script:VsCodeInfo }
  $command = Get-Command code -ErrorAction SilentlyContinue
  if ($command) {
    $script:VsCodeInfo = [ordered]@{ bulundu = $true; yol = $command.Source }
    return $script:VsCodeInfo
  }
  $localAppData = [Environment]::GetEnvironmentVariable('LOCALAPPDATA')
  $programFiles = [Environment]::GetEnvironmentVariable('ProgramFiles')
  $candidates = @(
    $(if ($localAppData) { Join-Path $localAppData 'Programs\Microsoft VS Code\bin\code.cmd' } else { $null }),
    $(if ($programFiles) { Join-Path $programFiles 'Microsoft VS Code\bin\code.cmd' } else { $null })
  ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }
  $found = $candidates | Select-Object -First 1
  $script:VsCodeInfo = [ordered]@{ bulundu = [bool]$found; yol = $(if ($found) { $found } else { $null }) }
  return $script:VsCodeInfo
}

function Invoke-KuleDurum {
  $durumPath = Join-Path (Join-Path $script:PanelPaths.State 'kule') 'durum.json'
  $result = ConvertTo-OrderedFromJsonObject (Read-JsonObject $durumPath)
  # ConvertFrom-Json collapses a single-element (or empty) JSON array into a
  # bare object/$null — the same round-trip pitfall Invoke-NezaketDurum and
  # Invoke-PasaportDurum already guard against — so re-wrap both array
  # fields explicitly before this goes back out through ConvertTo-Json.
  if ($result.Contains('son_isler')) { $result['son_isler'] = @($result['son_isler']) }
  if ($result.Contains('reaper_eylemleri')) { $result['reaper_eylemleri'] = @($result['reaper_eylemleri']) }
  $result['vscode'] = Find-VsCode
  $result['calisiyor'] = [bool]($script:KuleProcess -and -not $script:KuleProcess.HasExited)
  return $result
}

function Repair-KuleJobArrays([object]$Job) {
  # Same single-element-array collapse pitfall, this time on one job
  # record's own array fields (`diffler` in particular — a job with exactly
  # one watched file is the common case, and that is exactly the size
  # ConvertFrom-Json collapses).
  if ($Job -isnot [Collections.IDictionary]) {
    foreach ($field in @('izlenen_dosyalar', 'artefaktlar', 'diffler', 'uyarilar')) {
      if (@($Job.PSObject.Properties.Name) -contains $field) {
        $Job.$field = @($Job.$field)
      }
    }
  }
  return $Job
}

function Get-KuleJobLocation([string]$JobId) {
  $jobsDir = Join-Path (Join-Path $script:PanelPaths.State 'kule') 'jobs'
  $activePath = Join-Path $jobsDir "$JobId.json"
  if (Test-Path -LiteralPath $activePath -PathType Leaf) {
    return [pscustomobject]@{ Dir = $jobsDir; Path = $activePath; Konum = 'active' }
  }
  $arsivDir = Join-Path (Join-Path $script:PanelPaths.State 'kule') 'arsiv'
  $arsivPath = Join-Path $arsivDir "$JobId.json"
  if (Test-Path -LiteralPath $arsivPath -PathType Leaf) {
    return [pscustomobject]@{ Dir = $arsivDir; Path = $arsivPath; Konum = 'arsiv' }
  }
  return $null
}

function Resolve-KuleGoreliYol([string]$Konum, [string]$JobId, [string]$StoredRel) {
  # Resolves a once/sonra/diff path recorded by kule.py's `_kule_relative`
  # (relative to `<state>/kule/`, e.g. `jobs/<id>/diff/0.diff`) against the
  # job's CURRENT location — `active` (`jobs/`) or `arsiv` once kule.py's
  # own archive step has moved the whole `jobs/<id>` directory in one
  # piece. Only the tail after the `jobs/<id>`-or-`arsiv/<id>` prefix is
  # trusted for the shape; the base directory always comes from `$Konum`,
  # which the caller just found the job record in — not from the stored
  # string. Returns `$null` for anything that does not resolve inside
  # `<state>/kule/`, including a tampered absolute path; callers refuse
  # with `kule_yol_disi` in that case.
  if (-not $StoredRel) { return $null }
  $kuleDir = Join-Path $script:PanelPaths.State 'kule'
  $kuleDirFull = [IO.Path]::GetFullPath($kuleDir)
  $parts = @($StoredRel -split '[\\/]' | Where-Object { $_ -ne '' })
  if ([IO.Path]::IsPathRooted($StoredRel)) {
    $candidate = $StoredRel
  } elseif ($parts.Count -ge 2 -and ($parts[0] -eq 'jobs' -or $parts[0] -eq 'arsiv') -and $parts[1] -eq $JobId) {
    $baseDir = if ($Konum -eq 'active') { Join-Path $kuleDir 'jobs' } else { Join-Path $kuleDir 'arsiv' }
    $candidate = Join-Path $baseDir $JobId
    for ($i = 2; $i -lt $parts.Count; $i++) { $candidate = Join-Path $candidate $parts[$i] }
  } else {
    $candidate = Join-Path $kuleDir $StoredRel
  }
  $candidateFull = [IO.Path]::GetFullPath($candidate)
  $kuleDirPrefix = $kuleDirFull.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
  if ($candidateFull -ne $kuleDirFull -and -not $candidateFull.StartsWith($kuleDirPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    return $null
  }
  return $candidateFull
}

function Read-KuleLogTail([string]$LogPath, [int]$MaxBayt = 65536) {
  # Reads at most the log's last $MaxBayt bytes via a seek instead of
  # loading the whole file — a long-running job's `.log` can otherwise
  # grow well past what any tail view needs (kule.py itself caps it at
  # BEYIN_KULE_LOG_MAX_BAYT with its own rotation, but this stays cheap
  # regardless of that cap).
  if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf)) { return @() }
  try {
    $stream = [IO.File]::Open($LogPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
      $start = [Math]::Max(0, $stream.Length - $MaxBayt)
      $stream.Seek($start, [IO.SeekOrigin]::Begin) | Out-Null
      $buffer = New-Object byte[] ($stream.Length - $start)
      if ($buffer.Length -gt 0) { $stream.Read($buffer, 0, $buffer.Length) | Out-Null }
    } finally {
      $stream.Close()
    }
  } catch {
    return @()
  }
  $text = $script:Utf8.GetString($buffer)
  $lines = @($text -split "`r?`n")
  # A seek that lands mid-line leaves a partial first line — drop it
  # rather than showing a truncated fragment, unless the whole file fit.
  if ($start -gt 0 -and $lines.Count -gt 1) { $lines = $lines[1..($lines.Count - 1)] }
  return @($lines | Where-Object { $_ -ne '' } | Select-Object -Last 60)
}

function Get-KuleJobDetail([string]$JobId) {
  # Reads jobs/<id>.json (or arsiv/<id>.json) and the matching .log file
  # directly — no python spawn, same "just read files" posture as
  # Get-TodaySummary above.
  $location = Get-KuleJobLocation $JobId
  if (-not $location) { return $null }
  $job = Repair-KuleJobArrays (Read-JsonObject $location.Path)
  $logPath = Join-Path $location.Dir "$JobId.log"
  $logLines = Read-KuleLogTail $logPath
  return [ordered]@{ is = $job; log_kuyruk = $logLines }
}

function Get-KuleDiffText([string]$JobId, [int]$N) {
  # The diff path is NEVER assembled from the request's id/n directly — it
  # is read out of the job record's own `diffler[n].diff_yol` field, which
  # only kule.py itself ever writes. `id`/`n` are only used to select which
  # already-trusted record and array index to read. `diff_yol` is relative
  # to `<state>/kule/` (kule.py's own `_kule_relative`) and re-resolved
  # here against the job's CURRENT location — Resolve-KuleGoreliYol
  # refuses anything that would land outside `<state>/kule/`.
  $location = Get-KuleJobLocation $JobId
  if (-not $location) { return [pscustomobject]@{ Metin = $null; Hata = 'kule_diff_yok' } }
  $detail = Get-KuleJobDetail $JobId
  if (-not $detail -or -not $detail.is) { return [pscustomobject]@{ Metin = $null; Hata = 'kule_diff_yok' } }
  $diffler = @($detail.is.diffler)
  if ($N -lt 0 -or $N -ge $diffler.Count) { return [pscustomobject]@{ Metin = $null; Hata = 'kule_diff_yok' } }
  $diffRel = [string]$diffler[$N].diff_yol
  $diffPath = Resolve-KuleGoreliYol $location.Konum $JobId $diffRel
  if (-not $diffPath) { return [pscustomobject]@{ Metin = $null; Hata = 'kule_yol_disi' } }
  if (-not (Test-Path -LiteralPath $diffPath -PathType Leaf)) {
    return [pscustomobject]@{ Metin = $null; Hata = 'kule_diff_yok' }
  }
  try {
    return [pscustomobject]@{ Metin = [IO.File]::ReadAllText($diffPath, $script:Utf8); Hata = $null }
  } catch {
    return [pscustomobject]@{ Metin = $null; Hata = 'kule_diff_yok' }
  }
}

function Invoke-KuleIsVer(
  [string]$Tur,
  [string]$Model,
  [string]$Prompt,
  [string]$Cwd,
  [string[]]$Izlenen,
  [Collections.IDictionary]$Izin
) {
  if (-not $script:Python) { throw 'Python was not found. Set BEYIN_PYTHON to the interpreter path.' }
  $scriptPath = Join-Path $script:PanelPaths.Scripts 'kule.py'
  if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { throw 'kule.py was not found.' }
  $arguments = New-Object Collections.Generic.List[string]
  $arguments.Add($scriptPath)
  $arguments.Add('--state-dir'); $arguments.Add($script:PanelPaths.State)
  $arguments.Add('--vault-root'); $arguments.Add($script:PanelPaths.Vault)
  $arguments.Add('is-ver')
  $arguments.Add('--tur'); $arguments.Add($Tur)
  $arguments.Add('--model'); $arguments.Add($Model)
  $arguments.Add('--cwd'); $arguments.Add($Cwd)
  $arguments.Add('--kaynak'); $arguments.Add('panel')
  $arguments.Add('--stdin')
  $arguments.Add('--json')
  foreach ($path in $Izlenen) { $arguments.Add('--izlenen'); $arguments.Add($path) }
  foreach ($key in @($Izin.Keys)) { $arguments.Add('--izin'); $arguments.Add("$key=$($Izin[$key])") }
  $allArguments = @($script:Python.Prefix) + @($arguments.ToArray())
  $oldPreference = $ErrorActionPreference
  $oldOutputEncoding = $OutputEncoding
  $oldConsoleEncoding = [Console]::OutputEncoding
  try {
    $ErrorActionPreference = 'Continue'
    # Same PS 5.1 native-pipe fix New-KaydetCommand applies: without forcing
    # UTF-8-no-BOM here, $OutputEncoding's OEM/ANSI default would mangle a
    # Turkish prompt before kule.py ever sees a byte of it.
    $OutputEncoding = [Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
    $lines = @($Prompt | & $script:Python.Exe @allArguments 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = $LASTEXITCODE
  } finally {
    $ErrorActionPreference = $oldPreference
    $OutputEncoding = $oldOutputEncoding
    [Console]::OutputEncoding = $oldConsoleEncoding
  }
  $resultLine = @($lines | Where-Object { $_.StartsWith($script:KuleResultMarker) }) | Select-Object -Last 1
  if (-not $resultLine) {
    throw "Kule is-ver produced no usable result (exit $exitCode): $($lines -join ' | ')"
  }
  $parsed = $resultLine.Substring($script:KuleResultMarker.Length) | ConvertFrom-Json
  # A job created with exactly one `izlenen` path is the same
  # single-element-array collapse case Repair-KuleJobArrays guards against
  # on the read side — guard it here too, on the freshly-created record.
  if ($parsed -and $parsed.is) { $parsed.is = Repair-KuleJobArrays $parsed.is }
  return $parsed
}

function Invoke-KuleGecis([string]$Command, [string]$JobId) {
  # onayla/reddet/iptal print plain JSON on success, or a bare slug on
  # stderr with a non-zero exit on refusal (kule-is-yok, kule-gecis-gecersiz)
  # — unlike is-ver's marker line, so this is deliberately not routed
  # through Invoke-PanelPythonJson (which would only surface a generic
  # failure message and lose the actual slug).
  if (-not $script:Python) { throw 'Python was not found. Set BEYIN_PYTHON to the interpreter path.' }
  $scriptPath = Join-Path $script:PanelPaths.Scripts 'kule.py'
  if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { throw 'kule.py was not found.' }
  $arguments = @($script:Python.Prefix) + @(
    $scriptPath, '--state-dir', $script:PanelPaths.State, '--vault-root', $script:PanelPaths.Vault, $Command, $JobId
  )
  $oldPreference = $ErrorActionPreference
  try {
    $ErrorActionPreference = 'Continue'
    $lines = @(& $script:Python.Exe @arguments 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = $LASTEXITCODE
  } finally {
    $ErrorActionPreference = $oldPreference
  }
  $text = ($lines -join [Environment]::NewLine).Trim()
  if ($exitCode -ne 0) {
    return [ordered]@{ basarili = $false; hata = $(if ($text) { $text } else { 'kule-beklenmedik' }) }
  }
  try {
    return [ordered]@{ basarili = $true; is = (Repair-KuleJobArrays ($text | ConvertFrom-Json)) }
  } catch {
    return [ordered]@{ basarili = $false; hata = 'kule-cikti-gecersiz' }
  }
}

function Start-Kule {
  # Off-switch checked before anything else, same posture as
  # Start-PasaportIzleyici's BEYIN_PASAPORT_IZLEYICI=off.
  $off = [Environment]::GetEnvironmentVariable('BEYIN_KULE')
  if ($off -and $off.Trim().ToLowerInvariant() -eq 'off') { return }
  if (-not $script:Python) { return }
  $scriptPath = Join-Path $script:PanelPaths.Scripts 'kule.py'
  if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { return }
  # Belt and braces: kule.calis() itself now clears a leftover `dur` at its
  # own startup (a stale marker from an earlier Stop-Kule whose process has
  # since exited would otherwise make it return immediately, forever), but
  # moving it out of the way here too means a freshly-spawned process never
  # even sees it. Moved, not deleted — same "never wipe, only relocate"
  # posture as kule.py's own archive step.
  try {
    $kuleDur = Join-Path (Join-Path $script:PanelPaths.State 'kule') 'dur'
    if (Test-Path -LiteralPath $kuleDur -PathType Leaf) {
      Move-Item -LiteralPath $kuleDur -Destination "$kuleDur.onceki" -Force
    }
  } catch {}
  $arguments = New-Object Collections.Generic.List[string]
  foreach ($item in @($script:Python.Prefix)) { $arguments.Add([string]$item) }
  $arguments.Add($scriptPath)
  $arguments.Add('--state-dir')
  $arguments.Add($script:PanelPaths.State)
  $arguments.Add('--vault-root')
  $arguments.Add($script:PanelPaths.Vault)
  $arguments.Add('calis')
  try {
    $script:KuleProcess = Start-Process -FilePath $script:Python.Exe -ArgumentList $arguments.ToArray() -WindowStyle Hidden -PassThru -ErrorAction Stop
  } catch {
    $script:KuleProcess = $null
  }
}

function Stop-Kule {
  if (-not $script:KuleProcess) { return }
  try {
    if (-not $script:KuleProcess.HasExited) {
      # Graceful stop first: write the `dur` marker kule.calis()'s own loop
      # checks at the top of every pass, then give it up to 5s to notice and
      # return on its own before falling back to a hard kill. Any
      # `claude`/`codex` child kule already spawned is intentionally left
      # running either way — see docs/kokpit.md "Orphan / reaper semantics".
      try {
        $kuleDir = Join-Path $script:PanelPaths.State 'kule'
        if (-not (Test-Path -LiteralPath $kuleDir -PathType Container)) {
          New-Item -ItemType Directory -Path $kuleDir -Force | Out-Null
        }
        [IO.File]::WriteAllText((Join-Path $kuleDir 'dur'), '', $script:Utf8)
      } catch {}
      if (-not $script:KuleProcess.WaitForExit(5000)) {
        $script:KuleProcess.Kill()
      }
    }
  } catch {
  } finally {
    $script:KuleProcess = $null
  }
}

function Get-LocalModelSummary {
  $result = [ordered]@{
    python_available = [bool]$script:Python
    python_message = $null
    computer = $null
    recommendations = $null
    ollama = Get-OllamaInventory
    active_backend = $null
    backend_storage = 'Windows user environment (HKCU\Environment)'
  }
  if (-not $script:Python) {
    $result.python_message = 'Python was not found. Hardware, fit recommendations, backend resolution, and model smoke tests cannot be determined.'
    return $result
  }
  try {
    $probe = Invoke-HardwareProbe
    $result.computer = $probe
  } catch {
    $result.python_message = $_.Exception.Message
  }
  if ($result.computer) {
    try { $result.recommendations = @(Invoke-ModelRecommendations $result.computer) } catch { $result.python_message = $_.Exception.Message }
  }
  try { $result.active_backend = Get-BackendSummary } catch {
    if (-not $result.python_message) { $result.python_message = $_.Exception.Message }
  }
  return $result
}

function Get-PullCommand([string]$Model, [string]$BaseUrl) {
  $endpoint = $BaseUrl.TrimEnd('/') + '/api/pull'
  $body = [Text.Encoding]::UTF8.GetBytes((([ordered]@{ model = $Model; stream = $true }) | ConvertTo-Json -Compress))
  $bodyBase64 = [Convert]::ToBase64String($body)
  $command = @"
`$ErrorActionPreference = 'Stop'
`$body = [Convert]::FromBase64String('$bodyBase64')
`$request = [Net.HttpWebRequest]::Create($(ConvertTo-PowerShellLiteral $endpoint))
`$request.Method = 'POST'
`$request.ContentType = 'application/json'
`$request.ContentLength = `$body.Length
`$request.Timeout = 10000
`$request.ReadWriteTimeout = 300000
`$request.AllowAutoRedirect = `$false
`$request.Proxy = `$null
`$requestStream = `$request.GetRequestStream()
try { `$requestStream.Write(`$body, 0, `$body.Length) } finally { `$requestStream.Dispose() }
`$response = `$request.GetResponse()
if ([int]`$response.StatusCode -ne 200) { throw "Ollama returned HTTP `$([int]`$response.StatusCode)." }
`$reader = New-Object IO.StreamReader(`$response.GetResponseStream(), (New-Object Text.UTF8Encoding(`$false)))
try {
  while (-not `$reader.EndOfStream) {
    `$line = `$reader.ReadLine()
    if (`$line) { [Console]::Out.WriteLine(`$line); [Console]::Out.Flush() }
  }
} finally { `$reader.Dispose(); `$response.Dispose() }
exit 0
"@
  return $command
}

function Get-SmokeTestCommand([string]$Backend, [string]$Tier) {
  $code = @'
import json
from pathlib import Path
import sys
import time

scripts = Path(sys.argv[1])
vault = Path(sys.argv[2])
state = Path(sys.argv[3])
backend = sys.argv[4]
tier = sys.argv[5]
sys.path.insert(0, str(scripts))
import claude_runner

slug = claude_runner._resolved_slug(backend, tier) or None
started = time.monotonic()
answer, error = claude_runner.run_claude(
    "Reply with one short sentence confirming this smoke test.",
    model=tier,
    tools="",
    timeout=120,
    cwd=vault,
    vault_root=vault,
    backend=backend,
    component="panel-smoke",
    state_dir=state,
)
payload = {
    "backend": backend,
    "model": slug,
    "latency_ms": int((time.monotonic() - started) * 1000),
    "answer": answer,
    "error": error,
}
print(json.dumps(payload, ensure_ascii=False), flush=True)
raise SystemExit(0 if error is None else 1)
'@
  return New-PythonStdinCommand $code @($script:PanelPaths.Scripts, $script:PanelPaths.Vault, $script:PanelPaths.State, $Backend, $Tier)
}

function New-KaydetCommand([string]$Metin, [string]$Baslik) {
  if (-not $script:Python) { throw 'Python was not found. Set BEYIN_PYTHON to the interpreter path.' }
  $scriptPath = Join-Path $script:PanelPaths.Scripts 'kaydet.py'
  if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { throw 'kaydet.py was not found.' }
  # The note text goes over stdin, never argv — argv can end up in process
  # listings and shell history, stdin does not. It is base64-encoded here
  # only so it survives the EncodedCommand hop into the spawned PowerShell
  # child untouched; kaydet.py itself reads plain UTF-8 text from stdin.
  $encodedText = [Convert]::ToBase64String($script:Utf8.GetBytes($Metin))
  $arguments = New-Object Collections.Generic.List[string]
  $arguments.Add($scriptPath)
  $arguments.Add('--stdin')
  $arguments.Add('--vault-root')
  $arguments.Add($script:PanelPaths.Vault)
  $arguments.Add('--state-dir')
  $arguments.Add($script:PanelPaths.State)
  $arguments.Add('--json')
  if ($Baslik) {
    $arguments.Add('--baslik')
    $arguments.Add($Baslik)
  }
  $parts = New-Object Collections.Generic.List[string]
  $parts.Add('& ' + (ConvertTo-PowerShellLiteral $script:Python.Exe))
  foreach ($item in @($script:Python.Prefix) + @($arguments)) {
    $parts.Add((ConvertTo-PowerShellLiteral ([string]$item)))
  }
  return (
    "`$env:PYTHONUTF8 = '1'; " +
    "`$metin = (New-Object Text.UTF8Encoding(`$false)).GetString([Convert]::FromBase64String('$encodedText')); " +
    # PowerShell 5.1 encodes text piped to a NATIVE (non-PowerShell) child
    # process using $OutputEncoding, which defaults to the console's OEM/
    # ANSI codepage — not UTF-8. Left unset, Turkish text piped to kaydet.py
    # would be mangled before Python ever sees a byte of it. Setting both
    # $OutputEncoding (governs the pipe) and [Console]::OutputEncoding
    # (governs what a native child reads back) to UTF-8-no-BOM here, right
    # before the pipe, keeps the bytes correct end to end.
    "`$OutputEncoding = [Console]::OutputEncoding = New-Object Text.UTF8Encoding(`$false); " +
    "`$metin | " + ($parts -join ' ') + '; exit $LASTEXITCODE'
  )
}

function Convert-StateTime([object]$Value) {
  if ($Value -is [ValueType] -and $Value -isnot [bool]) {
    try { return [DateTimeOffset]::FromUnixTimeSeconds([long]$Value).ToLocalTime().ToString('o') } catch {}
  }
  if ($Value -is [string] -and $Value) { return $Value }
  return 'unknown'
}

function Get-TodaySummary {
  $today = [DateTime]::Now.ToString('yyyy-MM-dd')
  $dailyName = "$today.md"
  $dailyPath = Join-Path (Join-Path $script:PanelPaths.Vault 'daily') $dailyName
  $dailyText = ''
  if (Test-Path -LiteralPath $dailyPath -PathType Leaf) {
    try { $dailyText = [IO.File]::ReadAllText($dailyPath, $script:Utf8) } catch { $dailyText = '' }
  }

  $ingestState = Read-JsonObject (Join-Path $script:PanelPaths.State 'ingest-state.json')
  $todaySources = New-Object Collections.Generic.HashSet[string]([StringComparer]::OrdinalIgnoreCase)
  if ($ingestState.sources) {
    foreach ($sourceProperty in @($ingestState.sources.PSObject.Properties)) {
      foreach ($doneProperty in @($sourceProperty.Value.done.PSObject.Properties)) {
        if ([string]$doneProperty.Value.daily -ceq $dailyName) { [void]$todaySources.Add($sourceProperty.Name) }
      }
    }
  }

  $sessions = New-Object Collections.ArrayList
  $matches = [regex]::Matches($dailyText, '(?m)^### Oturum \((?<time>\d{2}:\d{2})\)(?<suffix>[^\r\n]*)$')
  for ($index = 0; $index -lt $matches.Count; $index++) {
    $match = $matches[$index]
    $end = if ($index + 1 -lt $matches.Count) { $matches[$index + 1].Index } else { $dailyText.Length }
    $block = $dailyText.Substring($match.Index, $end - $match.Index)
    $agent = 'unknown'
    if ($block -match '<!--\s*session:\S+\s+ts:\S+\s+source:(?<source>[a-z]+)\s*-->') {
      $agent = $Matches['source']
    }
    $suffix = $match.Groups['suffix'].Value.Trim()
    if ($suffix -match '^[—-]\s*(?<writer>[^·]+)') {
      $writer = $Matches['writer'].Trim()
      if ($writer) { $agent = $writer }
    }
    if ($agent -eq 'unknown' -and $todaySources.Count -eq 1) {
      $agent = @($todaySources)[0]
    }
    [void]$sessions.Add([ordered]@{ time = $match.Groups['time'].Value; agent = $agent })
  }

  $lastFlushState = Read-JsonObject (Join-Path $script:PanelPaths.State 'last-flush.json')
  $compileState = Read-JsonObject (Join-Path $script:PanelPaths.State 'compile-state.json')
  $lastCompileRun = $null
  foreach ($run in @($compileState.runs)) {
    if ([string]$run.daily_file -ceq $dailyName) { $lastCompileRun = $run }
  }

  $sessionIds = @([regex]::Matches($dailyText, '<!--\s*session:(?<id>\S+)\s+ts:') | ForEach-Object { $_.Groups['id'].Value } | Select-Object -Unique)
  $conceptCount = 0
  if ($sessionIds.Count -gt 0 -and $lastCompileRun) {
    $conceptRoot = Join-Path $script:PanelPaths.Vault 'knowledge\concepts'
    if (Test-Path -LiteralPath $conceptRoot -PathType Container) {
      foreach ($concept in @(Get-ChildItem -LiteralPath $conceptRoot -Filter '*.md' -File -ErrorAction SilentlyContinue)) {
        try { $conceptText = [IO.File]::ReadAllText($concept.FullName, $script:Utf8) } catch { continue }
        $traced = $false
        foreach ($sessionId in $sessionIds) {
          if ($conceptText -match ('<!--\s*session:' + [regex]::Escape($sessionId) + '\s')) { $traced = $true; break }
        }
        if ($traced) { $conceptCount++ }
      }
    }
  }

  $compileStatus = if ($lastCompileRun) { [string]$lastCompileRun.status } elseif ($compileState.last_status) { [string]$compileState.last_status } else { 'unknown' }
  $compileAt = if ($lastCompileRun) { Convert-StateTime $lastCompileRun.ts } else { Convert-StateTime $compileState.last_run }
  $ingestLast = $ingestState.last_run
  return [ordered]@{
    date = $today
    daily_present = [bool]$dailyText
    sessions = @($sessions)
    last_flush = [ordered]@{
      status = $(if ($lastFlushState.status) { [string]$lastFlushState.status } else { 'unknown' })
      at = Convert-StateTime $lastFlushState.ts
      detail = $(if ($lastFlushState.detail) { [string]$lastFlushState.detail } else { '' })
    }
    last_compile = [ordered]@{ status = $compileStatus; at = $compileAt }
    concepts_count = $conceptCount
    ingest = [ordered]@{
      source = $(if ($ingestLast.source) { [string]$ingestLast.source } else { 'unknown' })
      at = Convert-StateTime $ingestLast.ts
      sources_for_today = @($todaySources)
    }
  }
}

function Receive-HttpRequest([Net.Sockets.TcpClient]$Client) {
  $stream = $Client.GetStream()
  $stream.ReadTimeout = 5000
  $memory = New-Object IO.MemoryStream
  $buffer = New-Object byte[] 4096
  $separator = $script:Crlf + $script:Crlf
  $headerEnd = -1
  $contentLength = 0
  while ($memory.Length -lt 65536) {
    $read = $stream.Read($buffer, 0, $buffer.Length)
    if ($read -le 0) { break }
    $memory.Write($buffer, 0, $read)
    $bytes = $memory.ToArray()
    $text = [Text.Encoding]::ASCII.GetString($bytes)
    if ($headerEnd -lt 0) {
      $headerEnd = $text.IndexOf($separator, [StringComparison]::Ordinal)
      if ($headerEnd -ge 0) {
        $headerText = $text.Substring(0, $headerEnd)
        foreach ($line in @($headerText -split $script:Crlf | Select-Object -Skip 1)) {
          if ($line -match '^(?i:Content-Length):\s*(\d+)\s*$') { $contentLength = [int]$Matches[1] }
        }
        if ($contentLength -gt 32768) { throw 'Request body is too large.' }
      }
    }
    if ($headerEnd -ge 0 -and $memory.Length -ge ($headerEnd + 4 + $contentLength)) { break }
  }
  if ($headerEnd -lt 0) { throw 'Incomplete HTTP headers.' }
  $allBytes = $memory.ToArray()
  $headerText = [Text.Encoding]::ASCII.GetString($allBytes, 0, $headerEnd)
  $lines = @($headerText -split $script:Crlf)
  if ($lines[0] -notmatch '^([A-Z]+) ([^ ]+) HTTP/1\.[01]$') { throw 'Invalid request line.' }
  $method = $Matches[1]
  $target = $Matches[2]
  $headers = @{}
  foreach ($line in @($lines | Select-Object -Skip 1)) {
    $colon = $line.IndexOf(':')
    if ($colon -le 0) { throw 'Invalid HTTP header.' }
    $name = $line.Substring(0, $colon).Trim().ToLowerInvariant()
    if ($headers.ContainsKey($name)) { throw 'Duplicate HTTP header.' }
    $headers[$name] = $line.Substring($colon + 1).Trim()
  }
  if ($headers.ContainsKey('transfer-encoding')) { throw 'Transfer-Encoding is not supported.' }
  $body = if ($contentLength -gt 0) { $script:Utf8.GetString($allBytes, $headerEnd + 4, $contentLength) } else { '' }
  return [pscustomobject]@{ Method = $method; Target = $target; Headers = $headers; Body = $body; Stream = $stream }
}

function Write-HttpResponse(
  [IO.Stream]$Stream,
  [int]$Status,
  [string]$Reason,
  [string]$ContentType,
  [string]$Body,
  [Collections.IDictionary]$ExtraHeaders
) {
  $bodyBytes = $script:Utf8.GetBytes($Body)
  $lines = New-Object Collections.Generic.List[string]
  $lines.Add("HTTP/1.1 $Status $Reason")
  $lines.Add("Content-Type: $ContentType")
  $lines.Add("Content-Length: $($bodyBytes.Length)")
  $lines.Add('Connection: close')
  $lines.Add('Cache-Control: no-store')
  $lines.Add('X-Content-Type-Options: nosniff')
  foreach ($key in @($ExtraHeaders.Keys)) { $lines.Add("$($key): $($ExtraHeaders[$key])") }
  $headerText = ($lines -join $script:Crlf) + $script:Crlf + $script:Crlf
  $headerBytes = [Text.Encoding]::ASCII.GetBytes($headerText)
  $Stream.Write($headerBytes, 0, $headerBytes.Length)
  if ($bodyBytes.Length -gt 0) { $Stream.Write($bodyBytes, 0, $bodyBytes.Length) }
  $Stream.Flush()
}

function Write-JsonResponse([IO.Stream]$Stream, [int]$Status, [string]$Reason, [object]$Value, [Collections.IDictionary]$Headers = @{}) {
  Write-HttpResponse $Stream $Status $Reason 'application/json; charset=utf-8' ($Value | ConvertTo-Json -Depth 30 -Compress) $Headers
}

function Test-ApiEnvelope([object]$Request, [bool]$RequireCookie) {
  if ($Request.Headers['host'] -cne $script:ExpectedHost) { return 403 }
  $origin = [string]$Request.Headers['origin']
  if ($origin) {
    if ($origin -cne $script:ExpectedOrigin) { return 403 }
  } else {
    $site = [string]$Request.Headers['sec-fetch-site']
    if ($site -and $site -cne 'same-origin') { return 403 }
  }
  if ([string]$Request.Body) {
    $mediaType = @($Request.Headers['content-type'] -split ';', 2)[0].Trim().ToLowerInvariant()
    if ($mediaType -ne 'application/json') { return 415 }
  }
  if ($RequireCookie) {
    $cookie = [string]$Request.Headers['cookie']
    $escaped = [regex]::Escape($script:SessionToken)
    if ($cookie -notmatch "(?:^|;\s*)oom_panel_session=$escaped(?:;|$)") { return 401 }
  }
  return 0
}

function Invoke-HttpRequest([object]$Request) {
  $allowed = @(
    '/', '/panel.html', '/api/session', '/api/health', '/api/today', '/api/local-models', '/api/events',
    '/api/nezaket', '/api/pasaport', '/api/kule', '/api/kule/is', '/api/kule/diff',
    '/api/action/doctor', '/api/action/compile', '/api/action/index', '/api/action/watcher',
    '/api/action/pull', '/api/action/pull-cancel', '/api/action/backend', '/api/action/try',
    '/api/action/kaydet', '/api/action/nezaket-serbest',
    '/api/action/pasaport-onayla', '/api/action/pasaport-reddet', '/api/action/pasaport-panodan',
    '/api/action/kule-is-ver', '/api/action/kule-onayla', '/api/action/kule-reddet',
    '/api/action/kule-iptal', '/api/action/kule-vscode',
    '/api/quit'
  )
  # /api/kule/is and /api/kule/diff carry a query string (?id=...[&n=...]),
  # so routing (and the allowlist check above) matches on the path alone —
  # $requestQuery is parsed separately, only by the two handlers that need it.
  $requestPath = $Request.Target
  $requestQuery = ''
  $queryIndex = $Request.Target.IndexOf('?')
  if ($queryIndex -ge 0) {
    $requestPath = $Request.Target.Substring(0, $queryIndex)
    $requestQuery = $Request.Target.Substring($queryIndex + 1)
  }
  if ($allowed -cnotcontains $requestPath) {
    Write-JsonResponse $Request.Stream 404 'Not Found' ([ordered]@{ error = 'not_found' })
    return
  }

  if ($requestPath -in @('/', '/panel.html')) {
    if ($Request.Method -cne 'GET') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    if ($Request.Headers['host'] -cne $script:ExpectedHost) {
      Write-JsonResponse $Request.Stream 403 'Forbidden' ([ordered]@{ error = 'invalid_host' })
      return
    }
    $html = [IO.File]::ReadAllText((Join-Path $script:RepoRoot 'gui\panel.html'), $script:Utf8)
    $securityHeaders = [ordered]@{
      'Content-Security-Policy' = "default-src 'none'; connect-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'"
      'Referrer-Policy' = 'no-referrer'
      'X-Frame-Options' = 'DENY'
    }
    Write-HttpResponse $Request.Stream 200 'OK' 'text/html; charset=utf-8' $html $securityHeaders
    return
  }

  if ($requestPath -eq '/api/session') {
    if ($Request.Method -cne 'POST') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    $guard = Test-ApiEnvelope $Request $false
    if ($guard -ne 0) {
      Write-JsonResponse $Request.Stream $guard $(if ($guard -eq 415) { 'Unsupported Media Type' } else { 'Forbidden' }) ([ordered]@{ error = 'request_rejected' })
      return
    }
    try { $body = $Request.Body | ConvertFrom-Json } catch { $body = $null }
    if ($script:TokenUsed -or -not $body -or [string]$body.token -cne $script:LaunchToken) {
      Write-JsonResponse $Request.Stream 401 'Unauthorized' ([ordered]@{ error = 'invalid_or_used_token' })
      return
    }
    $script:TokenUsed = $true
    $cookieHeaders = [ordered]@{ 'Set-Cookie' = "oom_panel_session=$($script:SessionToken); Path=/; HttpOnly; SameSite=Strict" }
    Write-JsonResponse $Request.Stream 200 'OK' ([ordered]@{ ok = $true }) $cookieHeaders
    return
  }

  $guard = Test-ApiEnvelope $Request $true
  if ($guard -ne 0) {
    $reason = if ($guard -eq 401) { 'Unauthorized' } elseif ($guard -eq 415) { 'Unsupported Media Type' } else { 'Forbidden' }
    Write-JsonResponse $Request.Stream $guard $reason ([ordered]@{ error = 'request_rejected' })
    return
  }

  if ($requestPath -eq '/api/health') {
    if ($Request.Method -cne 'GET') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    try {
      Write-JsonResponse $Request.Stream 200 'OK' (Invoke-HealthSummary)
    } catch {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'health_unavailable'; message = $_.Exception.Message })
    }
    return
  }

  if ($requestPath -eq '/api/today') {
    if ($Request.Method -cne 'GET') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    Write-JsonResponse $Request.Stream 200 'OK' (Get-TodaySummary)
    return
  }

  if ($requestPath -eq '/api/local-models') {
    if ($Request.Method -cne 'GET') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    Write-JsonResponse $Request.Stream 200 'OK' (Get-LocalModelSummary)
    return
  }

  if ($requestPath -eq '/api/nezaket') {
    if ($Request.Method -cne 'GET') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    if (-not $script:Python) {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'python_missing'; message = 'The politeness gate requires Python.' })
      return
    }
    try {
      Write-JsonResponse $Request.Stream 200 'OK' (Invoke-NezaketDurum)
    } catch {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'nezaket_unavailable'; message = $_.Exception.Message })
    }
    return
  }

  if ($requestPath -eq '/api/pasaport') {
    if ($Request.Method -cne 'GET') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    if (-not $script:Python) {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'python_missing'; message = 'Pasaport requires Python.' })
      return
    }
    try {
      Write-JsonResponse $Request.Stream 200 'OK' (Invoke-PasaportDurum)
    } catch {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'pasaport_unavailable'; message = $_.Exception.Message })
    }
    return
  }

  if ($requestPath -eq '/api/kule') {
    if ($Request.Method -cne 'GET') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    if (-not $script:Python) {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'python_missing'; message = 'Kule requires Python.' })
      return
    }
    try {
      Write-JsonResponse $Request.Stream 200 'OK' (Invoke-KuleDurum)
    } catch {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'kule_unavailable'; message = $_.Exception.Message })
    }
    return
  }

  if ($requestPath -eq '/api/kule/is') {
    if ($Request.Method -cne 'GET') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    $queryParams = Get-QueryParams $requestQuery
    $jobId = [string]$queryParams['id']
    if ($jobId -cnotmatch '^[0-9a-f]{8,32}$') {
      Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'kule_id_gecersiz' })
      return
    }
    try {
      $detail = Get-KuleJobDetail $jobId
      if (-not $detail) {
        Write-JsonResponse $Request.Stream 404 'Not Found' ([ordered]@{ error = 'kule_is_yok' })
        return
      }
      Write-JsonResponse $Request.Stream 200 'OK' $detail
    } catch {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'kule_is_unavailable'; message = $_.Exception.Message })
    }
    return
  }

  if ($requestPath -eq '/api/kule/diff') {
    if ($Request.Method -cne 'GET') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    $queryParams = Get-QueryParams $requestQuery
    $jobId = [string]$queryParams['id']
    if ($jobId -cnotmatch '^[0-9a-f]{8,32}$') {
      Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'kule_id_gecersiz' })
      return
    }
    $n = -1
    if (-not [int]::TryParse([string]$queryParams['n'], [ref]$n) -or $n -lt 0) {
      Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'kule_n_gecersiz' })
      return
    }
    try {
      $diffResult = Get-KuleDiffText $jobId $n
      if ($diffResult.Hata) {
        # A resolved-outside-<state>/kule/ path (a tampered diff_yol) is a
        # 400, distinct from the ordinary "no such diff" 404 — everything
        # else Get-KuleDiffText can report folds into the latter.
        if ($diffResult.Hata -eq 'kule_yol_disi') {
          Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = $diffResult.Hata })
        } else {
          Write-JsonResponse $Request.Stream 404 'Not Found' ([ordered]@{ error = $diffResult.Hata })
        }
        return
      }
      Write-HttpResponse $Request.Stream 200 'OK' 'text/plain; charset=utf-8' $diffResult.Metin @{}
    } catch {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'kule_diff_unavailable'; message = $_.Exception.Message })
    }
    return
  }

  if ($requestPath -eq '/api/events') {
    if ($Request.Method -cne 'GET') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    $lastId = 0
    [void][int]::TryParse([string]$Request.Headers['last-event-id'], [ref]$lastId)
    $frames = New-Object Text.StringBuilder
    foreach ($item in @($script:Events | Where-Object { $_.Sequence -gt $lastId })) {
      [void]$frames.Append("id: $($item.Sequence)$($script:Crlf)")
      [void]$frames.Append("event: $($item.Type)$($script:Crlf)")
      [void]$frames.Append("data: $($item.Json)$($script:Crlf)$($script:Crlf)")
    }
    if ($frames.Length -eq 0) { [void]$frames.Append(": keepalive$($script:Crlf)$($script:Crlf)") }
    Write-HttpResponse $Request.Stream 200 'OK' 'text/event-stream; charset=utf-8' $frames.ToString() ([ordered]@{ 'X-Accel-Buffering' = 'no' })
    return
  }

  if ($requestPath -eq '/api/action/backend') {
    if ($Request.Method -cne 'POST') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    try { $body = $Request.Body | ConvertFrom-Json } catch { $body = $null }
    $backend = if ($body) { ([string]$body.backend).Trim().ToLowerInvariant() } else { '' }
    if ($script:AllowedBackends -cnotcontains $backend) {
      Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{
        error = 'invalid_backend'
        allowed = $script:AllowedBackends
      })
      return
    }
    $confirmation = "Set BEYIN_MODEL_BACKEND=$backend in Windows user environment (HKCU\Environment)"
    if ([string]$body.confirmation -cne $confirmation) {
      Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{
        error = 'confirmation_required'
        confirmation = $confirmation
      })
      return
    }
    if (-not $script:Python) {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'python_missing'; message = 'Python is required to verify the resolved backend after the change.' })
      return
    }
    if ($script:ActiveOperation) {
      Write-JsonResponse $Request.Stream 409 'Conflict' ([ordered]@{ error = 'operation_in_progress'; operation = $script:ActiveOperation.Kind })
      return
    }
    try { [void](Get-BackendSummary) } catch {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'backend_resolution_unavailable'; message = $_.Exception.Message })
      return
    }
    Add-PanelEvent 'operation-started' ([ordered]@{ operation = 'backend'; value = $backend; storage = 'Windows user environment (HKCU\Environment)' })
    $settingWritten = $false
    try {
      [Environment]::SetEnvironmentVariable('BEYIN_MODEL_BACKEND', $backend, 'User')
      $settingWritten = $true
      $env:BEYIN_MODEL_BACKEND = $backend
      $resolved = Get-BackendSummary
      Add-PanelEvent 'operation-completed' ([ordered]@{
        operation = 'backend'
        exit_code = 0
        value = $backend
        storage = 'Windows user environment (HKCU\Environment)'
        resolved_backend = $resolved.backend
      })
      Write-JsonResponse $Request.Stream 200 'OK' ([ordered]@{
        changed = $true
        setting = 'BEYIN_MODEL_BACKEND'
        value = $backend
        storage = 'Windows user environment (HKCU\Environment)'
        active_backend = $resolved
      })
    } catch {
      $failure = if ($settingWritten) { "BEYIN_MODEL_BACKEND=$backend was written to the Windows user environment, but verification failed: $($_.Exception.Message)" } else { $_.Exception.Message }
      Add-PanelEvent 'operation-failed' ([ordered]@{ operation = 'backend'; exit_code = 1; message = $failure; changed = $settingWritten; value = $backend; storage = 'Windows user environment (HKCU\Environment)' })
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'backend_switch_failed'; message = $failure; changed = $settingWritten; value = $backend; storage = 'Windows user environment (HKCU\Environment)' })
    }
    return
  }

  if ($requestPath -eq '/api/action/pull') {
    if ($Request.Method -cne 'POST') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    if ($script:ActiveOperation) {
      Write-JsonResponse $Request.Stream 409 'Conflict' ([ordered]@{ error = 'operation_in_progress'; operation = $script:ActiveOperation.Kind })
      return
    }
    if (-not $script:Python) {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'python_missing'; message = 'Model pull refused because free model-store disk cannot be determined without Python.' })
      return
    }
    try { $body = $Request.Body | ConvertFrom-Json } catch { $body = $null }
    $model = if ($body) { ([string]$body.model).Trim() } else { '' }
    try {
      $probe = Invoke-HardwareProbe
      $recommendations = @(Invoke-ModelRecommendations $probe)
      $candidate = @($recommendations | Where-Object { [string]$_.tag -ceq $model }) | Select-Object -First 1
      if (-not $candidate) {
        Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'unknown_model'; message = 'Only a model returned by model_oneri.py may be pulled.' })
        return
      }
      if ($null -eq $probe.free_disk_gb) {
        Write-JsonResponse $Request.Stream 409 'Conflict' ([ordered]@{ error = 'disk_unknown'; message = "Model pull refused: free disk for $($probe.model_store) could not be determined." })
        return
      }
      $size = [double]$candidate.size_gb
      $required = [Math]::Round($size * 1.5, 2)
      $free = [double]$probe.free_disk_gb
      if ($free -lt $required) {
        Write-JsonResponse $Request.Stream 409 'Conflict' ([ordered]@{
          error = 'insufficient_disk'
          message = ('Model pull refused: {0:N2} GB free, {1:N2} GB required for {2}.' -f $free, $required, $model)
          free_disk_gb = $free
          required_disk_gb = $required
          model = $model
        })
        return
      }
      $inventory = Get-OllamaInventory
      if ($inventory.status -cne 'running') {
        Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'ollama_unavailable'; message = $inventory.message })
        return
      }
      $command = Get-PullCommand $model $inventory.endpoint
      [void](Start-PanelCommand 'pull' $command ([ordered]@{ model = $model; size_gb = $size; required_disk_gb = $required; free_disk_gb = $free }))
      Write-JsonResponse $Request.Stream 202 'Accepted' ([ordered]@{
        started = $true
        operation = 'pull'
        model = $model
        size_gb = $size
        free_disk_gb = $free
        required_disk_gb = $required
      })
    } catch {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'pull_unavailable'; message = $_.Exception.Message })
    }
    return
  }

  if ($requestPath -eq '/api/action/pull-cancel') {
    if ($Request.Method -cne 'POST') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    if (-not $script:ActiveOperation -or $script:ActiveOperation.Kind -cne 'pull') {
      Write-JsonResponse $Request.Stream 409 'Conflict' ([ordered]@{ error = 'no_pull_in_progress' })
      return
    }
    try {
      $script:ActiveOperation.CancelRequested = $true
      $script:ActiveOperation.Process.Kill()
      Write-JsonResponse $Request.Stream 202 'Accepted' ([ordered]@{ cancelling = $true; operation = 'pull'; resumable = $true })
    } catch {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'cancel_failed'; message = $_.Exception.Message })
    }
    return
  }

  if ($requestPath -eq '/api/action/try') {
    if ($Request.Method -cne 'POST') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    if ($script:ActiveOperation) {
      Write-JsonResponse $Request.Stream 409 'Conflict' ([ordered]@{ error = 'operation_in_progress'; operation = $script:ActiveOperation.Kind })
      return
    }
    if (-not $script:Python) {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'python_missing'; message = 'The runner smoke test requires Python.' })
      return
    }
    try { $body = $Request.Body | ConvertFrom-Json } catch { $body = $null }
    $backend = if ($body) { ([string]$body.backend).Trim().ToLowerInvariant() } else { '' }
    $tier = if ($body) { ([string]$body.tier).Trim().ToLowerInvariant() } else { '' }
    if ($script:AllowedBackends -cnotcontains $backend) {
      Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'invalid_backend'; allowed = $script:AllowedBackends })
      return
    }
    if ($tier -cnotin @('haiku', 'sonnet')) {
      Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'invalid_model_tier'; allowed = @('haiku', 'sonnet') })
      return
    }
    try {
      [void](Start-PanelCommand 'try' (Get-SmokeTestCommand $backend $tier) ([ordered]@{ backend = $backend; tier = $tier }))
      Write-JsonResponse $Request.Stream 202 'Accepted' ([ordered]@{ started = $true; operation = 'try'; backend = $backend; tier = $tier })
    } catch {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'try_unavailable'; message = $_.Exception.Message })
    }
    return
  }

  if ($requestPath -eq '/api/action/kaydet') {
    if ($Request.Method -cne 'POST') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    if ($script:ActiveOperation) {
      Write-JsonResponse $Request.Stream 409 'Conflict' ([ordered]@{ error = 'operation_in_progress'; operation = $script:ActiveOperation.Kind })
      return
    }
    if (-not $script:Python) {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'python_missing'; message = 'Kaydet requires Python.' })
      return
    }
    try { $body = $Request.Body | ConvertFrom-Json } catch { $body = $null }
    $metin = if ($body -and $null -ne $body.metin) { [string]$body.metin } else { '' }
    if (-not $metin.Trim()) {
      Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'missing_metin' })
      return
    }
    $baslik = if ($body -and $body.baslik) { [string]$body.baslik } else { '' }
    try {
      [void](Start-PanelCommand 'kaydet' (New-KaydetCommand $metin $baslik) ([ordered]@{}))
      Write-JsonResponse $Request.Stream 202 'Accepted' ([ordered]@{ started = $true; operation = 'kaydet' })
    } catch {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'kaydet_unavailable'; message = $_.Exception.Message })
    }
    return
  }

  if ($requestPath -eq '/api/action/nezaket-serbest') {
    if ($Request.Method -cne 'POST') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    # Checked BEFORE anything is popped from the queue: this route must never
    # drop work. If another operation is already running there is nothing
    # safe to start right now, so refuse up front and leave every selected id
    # exactly where it was in the queue.
    if ($script:ActiveOperation) {
      Write-JsonResponse $Request.Stream 409 'Conflict' ([ordered]@{ error = 'operation_in_progress'; operation = $script:ActiveOperation.Kind })
      return
    }
    if (-not $script:Python) {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'python_missing'; message = 'Releasing queued operations requires Python.' })
      return
    }
    try { $body = $Request.Body | ConvertFrom-Json } catch { $body = $null }
    $ids = @()
    if ($body -and $body.ids) { $ids = @($body.ids | ForEach-Object { [string]$_ } | Where-Object { $_ }) }
    if ($ids.Count -eq 0) {
      Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'missing_ids' })
      return
    }
    # Only the first selected id ever leaves the queue — the rest stay
    # queued, untouched. Popping every selected id up front (the previous
    # behaviour) meant that once only the first could actually be started,
    # every id after it was already gone from the queue with nothing ever
    # started for it: work silently dropped. One id popped, one operation
    # started (or the pop simply doesn't happen), every single time.
    $firstId = [string]$ids[0]
    $nezaketScript = Join-Path $script:PanelPaths.Scripts 'nezaket.py'
    try {
      $released = @(Invoke-PanelPythonJson (@($nezaketScript, '--state-dir', $script:PanelPaths.State, 'serbest', $firstId)) 'Releasing the queued operation produced no usable JSON.')
    } catch {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'nezaket_release_failed'; message = $_.Exception.Message })
      return
    }
    $started = $null
    if ($released.Count -gt 0 -and $released[0]) {
      try {
        $command = Get-NezaketOperationCommand $released[0]
        if (Start-PanelCommand ([string]$released[0].tur) $command ([ordered]@{ nezaket_id = [string]$released[0].id })) {
          $started = $released[0]
        }
      } catch {
        Add-PanelEvent 'operation-failed' ([ordered]@{ operation = [string]$released[0].tur; exit_code = 1; message = $_.Exception.Message })
      }
    }
    Write-JsonResponse $Request.Stream 202 'Accepted' ([ordered]@{
      started = $started
      remaining_selected = [Math]::Max(0, $ids.Count - 1)
    })
    return
  }

  if ($requestPath -eq '/api/action/pasaport-onayla') {
    if ($Request.Method -cne 'POST') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    if ($script:ActiveOperation) {
      Write-JsonResponse $Request.Stream 409 'Conflict' ([ordered]@{ error = 'operation_in_progress'; operation = $script:ActiveOperation.Kind })
      return
    }
    if (-not $script:Python) {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'python_missing'; message = 'Approval requires Python.' })
      return
    }
    try { $body = $Request.Body | ConvertFrom-Json } catch { $body = $null }
    $rawHash = if ($body -and $body.raw_hash) { [string]$body.raw_hash } else { '' }
    if (-not $rawHash) {
      Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'missing_raw_hash' })
      return
    }
    if ($rawHash -cnotmatch '^[0-9a-f]{64}$') {
      Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'bad_raw_hash' })
      return
    }
    try {
      # Approval writes to the daily log and spawns compile — same
      # streamed-through-SSE shape as Kaydet, so the panel sees the write
      # and the compile outcome as they happen rather than after the fact.
      [void](Start-PanelCommand 'pasaport-onayla' (New-PasaportOnaylaCommand $rawHash) ([ordered]@{ raw_hash = $rawHash }))
      Write-JsonResponse $Request.Stream 202 'Accepted' ([ordered]@{ started = $true; operation = 'pasaport-onayla' })
    } catch {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'pasaport_onayla_unavailable'; message = $_.Exception.Message })
    }
    return
  }

  if ($requestPath -eq '/api/action/pasaport-reddet') {
    if ($Request.Method -cne 'POST') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    if (-not $script:Python) {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'python_missing'; message = 'Rejection requires Python.' })
      return
    }
    try { $body = $Request.Body | ConvertFrom-Json } catch { $body = $null }
    $rawHash = if ($body -and $body.raw_hash) { [string]$body.raw_hash } else { '' }
    if (-not $rawHash) {
      Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'missing_raw_hash' })
      return
    }
    if ($rawHash -cnotmatch '^[0-9a-f]{64}$') {
      Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'bad_raw_hash' })
      return
    }
    # Rejection never writes to the daily log and never spawns compile, so
    # unlike approval it runs synchronously — no operation slot, no SSE.
    $scriptPath = Join-Path $script:PanelPaths.Scripts 'pasaport_kapi.py'
    try {
      $result = Invoke-PanelPythonJson (@($scriptPath, '--state-dir', $script:PanelPaths.State, '--vault-root', $script:PanelPaths.Vault, 'reddet', $rawHash)) 'The rejection produced no usable JSON.'
      Write-JsonResponse $Request.Stream 200 'OK' $result
    } catch {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'pasaport_reddet_failed'; message = $_.Exception.Message })
    }
    return
  }

  if ($requestPath -eq '/api/action/pasaport-panodan') {
    if ($Request.Method -cne 'POST') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    if ($script:ActiveOperation) {
      Write-JsonResponse $Request.Stream 409 'Conflict' ([ordered]@{ error = 'operation_in_progress'; operation = $script:ActiveOperation.Kind })
      return
    }
    if (-not $script:Python) {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'python_missing'; message = 'Reading the clipboard requires Python.' })
      return
    }
    try {
      [void](Start-PanelCommand 'pasaport-panodan' (New-PasaportPanodanCommand) ([ordered]@{}))
      Write-JsonResponse $Request.Stream 202 'Accepted' ([ordered]@{ started = $true; operation = 'pasaport-panodan' })
    } catch {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'pasaport_panodan_unavailable'; message = $_.Exception.Message })
    }
    return
  }

  # F5 "Kokpit" part 2 — kule actions. Every one of these is a quick file
  # write/read, run synchronously right here — NOT through Start-PanelCommand
  # / SSE, and NOT gated by $script:ActiveOperation: kule has its own
  # multi-lane worker process doing the actual streaming into job logs, so
  # the panel's single-operation-at-a-time slot (used by doctor/compile/
  # index/watcher/pull/try/kaydet/pasaport-onayla) is left completely
  # untouched by these routes.
  if ($requestPath -eq '/api/action/kule-is-ver') {
    if ($Request.Method -cne 'POST') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    if (-not $script:Python) {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'python_missing'; message = 'Kule requires Python.' })
      return
    }
    try { $body = $Request.Body | ConvertFrom-Json } catch { $body = $null }
    $tur = if ($body -and $body.tur) { [string]$body.tur } else { '' }
    if ($tur -cnotin @('claude', 'codex')) {
      Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'kule_tur_gecersiz'; allowed = @('claude', 'codex') })
      return
    }
    $model = if ($body -and $body.model) { [string]$body.model } else { '' }
    if (-not $model -or $model.Length -gt 64 -or $model -cnotmatch '^[A-Za-z0-9._:-]+$') {
      Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'kule_model_gecersiz' })
      return
    }
    $prompt = if ($body -and $null -ne $body.prompt) { [string]$body.prompt } else { '' }
    if (-not $prompt.Trim()) {
      Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'kule_prompt_eksik' })
      return
    }
    $cwd = if ($body -and $body.cwd) { [string]$body.cwd } else { [string]$script:PanelPaths.Vault }
    if (-not [IO.Path]::IsPathRooted($cwd)) {
      Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'kule_cwd_gecersiz' })
      return
    }
    $izlenen = @()
    if ($body -and $body.izlenen) { $izlenen = @($body.izlenen | ForEach-Object { [string]$_ } | Where-Object { $_ }) }
    # Belt-and-braces: kule.py's own create_job is the authoritative check
    # (symlink-safe, via Path.resolve()) — this is a fast, textual
    # pre-check so a malformed request gets a proper 400 without a
    # subprocess round-trip. Same two caps kule.py enforces.
    if ($izlenen.Count -gt 50) {
      Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'kule_izlenen_cok_buyuk' })
      return
    }
    $cwdFull = [IO.Path]::GetFullPath($cwd)
    $cwdPrefix = $cwdFull.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    foreach ($item in $izlenen) {
      $itemFull = if ([IO.Path]::IsPathRooted($item)) { [IO.Path]::GetFullPath($item) } else { [IO.Path]::GetFullPath((Join-Path $cwdFull $item)) }
      if ($itemFull -ne $cwdFull -and -not $itemFull.StartsWith($cwdPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'kule_izlenen_cwd_disi' })
        return
      }
      if ((Test-Path -LiteralPath $itemFull -PathType Leaf) -and (Get-Item -LiteralPath $itemFull).Length -gt 5MB) {
        Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'kule_izlenen_cok_buyuk' })
        return
      }
    }
    $izin = @{}
    if ($body -and $body.izin) {
      foreach ($prop in @($body.izin.PSObject.Properties)) { $izin[$prop.Name] = [string]$prop.Value }
    }
    # Same allowlist kule.py's own _validate_izin enforces — permission_mode
    # / sandbox each a closed set, allowed_tools a bounded character class.
    # `bypassPermissions` and `danger-full-access` fall through as unknown
    # values, refused like anything else outside the allowed sets.
    $izinPermissionModes = @('default', 'acceptEdits', 'plan')
    $izinSandboxModes = @('read-only', 'workspace-write')
    foreach ($izinKey in @($izin.Keys)) {
      $izinValue = $izin[$izinKey]
      $izinGecerli = $false
      if ($izinKey -eq 'permission_mode') {
        $izinGecerli = $izinPermissionModes -ccontains $izinValue
      } elseif ($izinKey -eq 'sandbox') {
        $izinGecerli = $izinSandboxModes -ccontains $izinValue
      } elseif ($izinKey -eq 'allowed_tools') {
        $izinGecerli = ($izinValue.Length -le 200) -and ($izinValue -cmatch '^[A-Za-z0-9_,() *-]*$')
      }
      if (-not $izinGecerli) {
        Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'kule_izin_gecersiz' })
        return
      }
    }
    try {
      $result = Invoke-KuleIsVer -Tur $tur -Model $model -Prompt $prompt -Cwd $cwd -Izlenen $izlenen -Izin $izin
      Write-JsonResponse $Request.Stream 200 'OK' $result
    } catch {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'kule_is_ver_failed'; message = $_.Exception.Message })
    }
    return
  }

  if ($requestPath -in @('/api/action/kule-onayla', '/api/action/kule-reddet', '/api/action/kule-iptal')) {
    if ($Request.Method -cne 'POST') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    if (-not $script:Python) {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'python_missing'; message = 'Kule requires Python.' })
      return
    }
    try { $body = $Request.Body | ConvertFrom-Json } catch { $body = $null }
    $jobId = if ($body -and $body.id) { [string]$body.id } else { '' }
    if ($jobId -cnotmatch '^[0-9a-f]{8,32}$') {
      Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'kule_id_gecersiz' })
      return
    }
    $command = @{ '/api/action/kule-onayla' = 'onayla'; '/api/action/kule-reddet' = 'reddet'; '/api/action/kule-iptal' = 'iptal' }[$requestPath]
    try {
      $result = Invoke-KuleGecis $command $jobId
      if ($result.basarili) {
        Write-JsonResponse $Request.Stream 200 'OK' $result
      } else {
        Write-JsonResponse $Request.Stream 409 'Conflict' $result
      }
    } catch {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'kule_gecis_failed'; message = $_.Exception.Message })
    }
    return
  }

  if ($requestPath -eq '/api/action/kule-vscode') {
    if ($Request.Method -cne 'POST') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    try { $body = $Request.Body | ConvertFrom-Json } catch { $body = $null }
    $jobId = if ($body -and $body.id) { [string]$body.id } else { '' }
    if ($jobId -cnotmatch '^[0-9a-f]{8,32}$') {
      Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'kule_id_gecersiz' })
      return
    }
    $n = -1
    if (-not ($body -and [int]::TryParse([string]$body.n, [ref]$n)) -or $n -lt 0) {
      Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'kule_n_gecersiz' })
      return
    }
    $vscode = Find-VsCode
    if (-not $vscode.bulundu) {
      Write-JsonResponse $Request.Stream 409 'Conflict' ([ordered]@{ error = 'vscode_yok' })
      return
    }
    try {
      $location = Get-KuleJobLocation $jobId
      $detail = Get-KuleJobDetail $jobId
      if (-not $location -or -not $detail -or -not $detail.is) {
        Write-JsonResponse $Request.Stream 404 'Not Found' ([ordered]@{ error = 'kule_is_yok' })
        return
      }
      $diffler = @($detail.is.diffler)
      if ($n -ge $diffler.Count) {
        Write-JsonResponse $Request.Stream 404 'Not Found' ([ordered]@{ error = 'kule_diff_yok' })
        return
      }
      # Both paths come from the job record kule.py itself wrote — never
      # from the request body — so this can only ever open files kule
      # actually snapshotted for this job. Each is relative to
      # `<state>/kule/` and re-resolved (and re-validated as staying
      # under it) against the job's CURRENT location before anything
      # opens it.
      $onceRel = [string]$diffler[$n].once_yol
      $sonraRel = [string]$diffler[$n].sonra_yol
      $oncePath = Resolve-KuleGoreliYol $location.Konum $jobId $onceRel
      $sonraPath = Resolve-KuleGoreliYol $location.Konum $jobId $sonraRel
      if (-not $oncePath -or -not $sonraPath) {
        Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'kule_yol_disi' })
        return
      }
      Start-Process -FilePath $vscode.yol -ArgumentList @('--diff', $oncePath, $sonraPath) -ErrorAction Stop | Out-Null
      Write-JsonResponse $Request.Stream 200 'OK' ([ordered]@{ acildi = $true })
    } catch {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'kule_vscode_failed'; message = $_.Exception.Message })
    }
    return
  }

  if ($requestPath -like '/api/action/*') {
    if ($Request.Method -cne 'POST') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    if ($script:ActiveOperation) {
      Write-JsonResponse $Request.Stream 409 'Conflict' ([ordered]@{ error = 'operation_in_progress'; operation = $script:ActiveOperation.Kind })
      return
    }
    $kind = $requestPath.Substring('/api/action/'.Length)
    try {
      [void](Start-PanelOperation $kind)
      Write-JsonResponse $Request.Stream 202 'Accepted' ([ordered]@{ started = $true; operation = $kind })
    } catch {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'operation_unavailable'; message = $_.Exception.Message })
    }
    return
  }

  if ($requestPath -eq '/api/quit') {
    if ($Request.Method -cne 'POST') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    $script:QuitRequested = $true
    Write-JsonResponse $Request.Stream 200 'OK' ([ordered]@{ stopping = $true; operation_continues = [bool]$script:ActiveOperation })
  }
}

function Open-PanelBrowser([string]$Url) {
  if ($NoBrowser) {
    Write-Output "PANEL_READY $Url"
    return
  }
  $edge = Get-Command msedge.exe -ErrorAction SilentlyContinue
  $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
  $programFiles = [Environment]::GetEnvironmentVariable('ProgramFiles')
  $candidates = @(
    $(if ($edge) { $edge.Source } else { $null }),
    $(if ($programFilesX86) { Join-Path $programFilesX86 'Microsoft\Edge\Application\msedge.exe' } else { $null }),
    $(if ($programFiles) { Join-Path $programFiles 'Microsoft\Edge\Application\msedge.exe' } else { $null })
  ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -Unique
  foreach ($candidate in $candidates) {
    try {
      Start-Process -FilePath $candidate -ArgumentList "--app=$Url" -ErrorAction Stop | Out-Null
      return
    } catch {
      Write-Warning "Microsoft Edge could not be launched: $($_.Exception.Message)"
    }
  }
  try {
    Start-Process $Url -ErrorAction Stop | Out-Null
  } catch {
    Write-Warning "No browser could be launched: $($_.Exception.Message)"
    Write-Host "Open this URL manually: $Url"
  }
}

$listener = $null
try {
  $script:PanelPaths = Resolve-PanelPaths
  $script:Python = Find-PythonCommand
  $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
  $listener.Start()
  $endpoint = [Net.IPEndPoint]$listener.LocalEndpoint
  $port = $endpoint.Port
  $script:ExpectedHost = "127.0.0.1:$port"
  $script:ExpectedOrigin = "http://$($script:ExpectedHost)"
  $script:LaunchToken = New-RandomToken
  $script:SessionToken = New-RandomToken
  $url = "$($script:ExpectedOrigin)/#$($script:LaunchToken)"
  Write-Host "PANEL_LISTENING $($endpoint.Address):$port"
  $script:ShutdownAt = [DateTime]::UtcNow.AddSeconds($script:IdleSeconds)
  # The listener and the tower are both born with the panel and die with
  # it — spawned once here, stopped once in the `finally` below (quit and
  # idle-shutdown both funnel through the same loop exit into that block).
  Start-PasaportIzleyici
  Start-Kule
  Open-PanelBrowser $url

  while ($true) {
    Update-PanelOperation
    if ($script:QuitRequested -and -not $script:ActiveOperation) { break }
    if ($script:ShutdownAt -and [DateTime]::UtcNow -ge $script:ShutdownAt -and -not $script:ActiveOperation) { break }
    if ($listener.Pending()) {
      $client = $listener.AcceptTcpClient()
      try {
        $request = Receive-HttpRequest $client
        $script:ShutdownAt = [DateTime]::UtcNow.AddSeconds($script:IdleSeconds)
        Invoke-HttpRequest $request
      } catch {
        try { Write-JsonResponse $client.GetStream() 400 'Bad Request' ([ordered]@{ error = 'bad_request' }) } catch {}
      } finally {
        $client.Dispose()
      }
    } else {
      Start-Sleep -Milliseconds 40
    }
  }
} catch {
  Write-Error "The operations panel failed: $($_.Exception.Message)"
  exit 1
} finally {
  Stop-Kule
  Stop-PasaportIzleyici
  if ($listener) { $listener.Stop() }
  if ($script:ActiveOperation) {
    try {
      $script:ActiveOperation.Process.WaitForExit()
      Update-PanelOperation
    } catch {}
  }
}
