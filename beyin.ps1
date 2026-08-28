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
    '/api/action/doctor', '/api/action/compile', '/api/action/index', '/api/action/watcher',
    '/api/action/pull', '/api/action/pull-cancel', '/api/action/backend', '/api/action/try', '/api/quit'
  )
  if ($allowed -cnotcontains $Request.Target) {
    Write-JsonResponse $Request.Stream 404 'Not Found' ([ordered]@{ error = 'not_found' })
    return
  }

  if ($Request.Target -in @('/', '/panel.html')) {
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

  if ($Request.Target -eq '/api/session') {
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

  if ($Request.Target -eq '/api/health') {
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

  if ($Request.Target -eq '/api/today') {
    if ($Request.Method -cne 'GET') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    Write-JsonResponse $Request.Stream 200 'OK' (Get-TodaySummary)
    return
  }

  if ($Request.Target -eq '/api/local-models') {
    if ($Request.Method -cne 'GET') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    Write-JsonResponse $Request.Stream 200 'OK' (Get-LocalModelSummary)
    return
  }

  if ($Request.Target -eq '/api/events') {
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

  if ($Request.Target -eq '/api/action/backend') {
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

  if ($Request.Target -eq '/api/action/pull') {
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

  if ($Request.Target -eq '/api/action/pull-cancel') {
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

  if ($Request.Target -eq '/api/action/try') {
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

  if ($Request.Target -like '/api/action/*') {
    if ($Request.Method -cne 'POST') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    if ($script:ActiveOperation) {
      Write-JsonResponse $Request.Stream 409 'Conflict' ([ordered]@{ error = 'operation_in_progress'; operation = $script:ActiveOperation.Kind })
      return
    }
    $kind = $Request.Target.Substring('/api/action/'.Length)
    try {
      [void](Start-PanelOperation $kind)
      Write-JsonResponse $Request.Stream 202 'Accepted' ([ordered]@{ started = $true; operation = $kind })
    } catch {
      Write-JsonResponse $Request.Stream 503 'Service Unavailable' ([ordered]@{ error = 'operation_unavailable'; message = $_.Exception.Message })
    }
    return
  }

  if ($Request.Target -eq '/api/quit') {
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
  if ($listener) { $listener.Stop() }
  if ($script:ActiveOperation) {
    try {
      $script:ActiveOperation.Process.WaitForExit()
      Update-PanelOperation
    } catch {}
  }
}
