# yazan: codex
# model: gpt-5.6-sol
<#
.SYNOPSIS
Starts the zero-install browser setup wizard on a loopback-only HTTP server.

.DESCRIPTION
Phase one exposes a read-only system check. The future installer child-process
route is wired but is intentionally not called by the current page.
#>
[CmdletBinding()]
param(
  [switch]$NoBrowser,
  [ValidateRange(2, 600)]
  [int]$GraceSeconds = 30
)

$ErrorActionPreference = 'Stop'
if ([Environment]::GetEnvironmentVariable('BEYIN_INVOKED_BY')) { exit 0 }

$script:RepoRoot = $PSScriptRoot
$script:Utf8 = New-Object Text.UTF8Encoding($false)
$script:Crlf = [string][char]13 + [char]10
$script:Events = New-Object Collections.ArrayList
$script:NextSequence = 1
$script:ActiveOperation = $null
$script:DetectionResultSeen = $false
$script:TokenUsed = $false
$script:QuitRequested = $false
# Armed at startup, not only after an operation. Left null, a wizard that never
# completed anything never reached its own exit test: opening the page, doing
# nothing and closing the browser left the process holding a loopback port
# indefinitely. Measured - one run outlived a 600 second grace by hours.
$script:ShutdownAt = $null
$script:IdleSeconds = 0
$script:GraceSeconds = $GraceSeconds

function New-RandomToken {
  $bytes = New-Object byte[] 32
  $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
  try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
  return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Add-GuiEvent([string]$Type, [Collections.IDictionary]$Payload) {
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

function Get-DetectionRows([object]$Context) {
  $commands = $Context.probe.commands
  $pythonPresent = [bool]$commands.python
  $claudePresent = [bool]$commands.claude
  $ollamaPresent = [bool]$commands.ollama
  $documentsPresent = [bool]($Context.documents_path -and (Test-Path -LiteralPath $Context.documents_path -PathType Container))
  $hardwarePresent = [bool]($Context.probe -and ($null -ne $Context.probe.ram_gb -or $Context.probe.cpu.name -or @($Context.probe.gpus).Count -gt 0))
  $recommendationPresent = [bool](@($Context.recommendations).Count -gt 0)

  return @(
    [pscustomobject]@{
      name = 'Python 3'
      state = $(if ($pythonPresent) { 'Present' } else { 'Required' })
      consequence = $(if ($pythonPresent) { 'Hardware and model checks can run.' } else { 'Setup must install Python before the memory tools can run.' })
    },
    [pscustomobject]@{
      name = 'Claude Code'
      state = $(if ($claudePresent) { 'Present' } else { 'Optional' })
      consequence = $(if ($claudePresent) { 'Automatic session capture and compilation are available.' } else { 'Local-only modes remain possible, but capture and compilation need Claude Code.' })
    },
    [pscustomobject]@{
      name = 'Ollama'
      state = $(if ($ollamaPresent) { 'Present' } else { 'Optional' })
      consequence = $(if ($ollamaPresent) { 'A local-model backend can be configured.' } else { 'Cloud setup still works; local models would need a runtime later.' })
    },
    [pscustomobject]@{
      name = 'Documents folder'
      state = $(if ($documentsPresent) { 'Present' } else { 'Unavailable' })
      consequence = $(if ($documentsPresent) { "Default memory location: $($Context.documents_path)" } else { 'The default memory location could not be resolved.' })
    },
    [pscustomobject]@{
      name = 'Claude Desktop config'
      state = $(if ($Context.mcp_config_exists) { 'Present' } else { 'Optional' })
      consequence = $(if ($Context.mcp_config_exists) { 'Desktop memory access can be registered.' } else { 'Desktop integration will stay off unless configured later.' })
    },
    [pscustomobject]@{
      name = 'Hardware probe'
      state = $(if ($hardwarePresent) { 'Present' } else { 'Unavailable' })
      consequence = $(if ($hardwarePresent) { 'RAM, CPU, GPU, and model-store disk were inspected.' } else { 'Local-model fit cannot be estimated on this computer.' })
    },
    [pscustomobject]@{
      name = 'Local model recommendation'
      state = $(if ($recommendationPresent) { 'Present' } else { 'Unavailable' })
      consequence = $(if ($recommendationPresent) { 'At least one verified model candidate was evaluated.' } else { 'No verified local model candidate could be recommended.' })
    }
  )
}

function Get-PythonMissingRows {
  $blocked = 'Install Python first, then run the existing system detection again.'
  return @(
    [pscustomobject]@{ name = 'Python 3'; state = 'Required'; consequence = 'The memory tools and detailed system detection require Python 3.12 or newer.' },
    [pscustomobject]@{ name = 'Claude Code'; state = 'Unavailable'; consequence = $blocked },
    [pscustomobject]@{ name = 'Ollama'; state = 'Unavailable'; consequence = $blocked },
    [pscustomobject]@{ name = 'Documents folder'; state = 'Unavailable'; consequence = $blocked },
    [pscustomobject]@{ name = 'Claude Desktop config'; state = 'Unavailable'; consequence = $blocked },
    [pscustomobject]@{ name = 'Hardware probe'; state = 'Unavailable'; consequence = $blocked },
    [pscustomobject]@{ name = 'Local model recommendation'; state = 'Unavailable'; consequence = $blocked }
  )
}

function New-DetectionCommand {
  $wizard = (Join-Path $script:RepoRoot 'kur.ps1').Replace("'", "''")
  $repo = $script:RepoRoot.Replace("'", "''")
  return @(
    '$ErrorActionPreference = ''Stop'''
    '$ProgressPreference = ''SilentlyContinue'''
    ('$source = [IO.File]::ReadAllText(''{0}'')' -f $wizard)
    '$entry = [regex]::Match($source, ''(?m)^try \{\r?\n  if \(\$Answers -and \$Recommended\)'')'
    'if (-not $entry.Success) { throw ''kur.ps1 entry point was not found'' }'
    '$definitions = $source.Substring(0, $entry.Index)'
    'Invoke-Expression $definitions'
    ('$script:RepoRoot = ''{0}''' -f $repo)
    'try { [void](Find-PythonCommand) } catch { Write-Output ''__PYTHON_MISSING__''; exit 0 }'
    'try {'
    '  $context = Get-DecisionContext'
    '  Write-Output (''__DETECTION__'' + ($context | ConvertTo-Json -Depth 100 -Compress))'
    '} catch {'
    '  [Console]::Error.WriteLine($_.Exception.Message)'
    '  exit 1'
    '}'
  ) -join [Environment]::NewLine
}

function Start-GuiOperation([string]$Kind, [string]$Command) {
  if ($script:ActiveOperation -and -not $script:ActiveOperation.Process.HasExited) { return $false }
  $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Command))
  $info = New-Object Diagnostics.ProcessStartInfo
  $info.FileName = Join-Path $PSHOME 'powershell.exe'
  $info.Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded"
  $info.UseShellExecute = $false
  $info.CreateNoWindow = $true
  $info.RedirectStandardOutput = $true
  $info.RedirectStandardError = $true
  $process = New-Object Diagnostics.Process
  $process.StartInfo = $info
  if (-not $process.Start()) { throw "Could not start $Kind process." }
  $outSource = "oom-gui-out-$($process.Id)-$([Guid]::NewGuid().ToString('N'))"
  $errSource = "oom-gui-err-$($process.Id)-$([Guid]::NewGuid().ToString('N'))"
  Register-ObjectEvent -InputObject $process -EventName OutputDataReceived -SourceIdentifier $outSource | Out-Null
  Register-ObjectEvent -InputObject $process -EventName ErrorDataReceived -SourceIdentifier $errSource | Out-Null
  $process.BeginOutputReadLine()
  $process.BeginErrorReadLine()
  $script:ActiveOperation = [pscustomobject]@{
    Kind = $Kind
    Process = $process
    OutSource = $outSource
    ErrSource = $errSource
  }
  Add-GuiEvent "$Kind-started" ([ordered]@{ process_id = $process.Id })
  return $true
}

function Read-OperationEvent([string]$Source, [bool]$IsError) {
  while ($true) {
    $eventRecord = Get-Event -SourceIdentifier $Source -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $eventRecord) { break }
    Remove-Event -EventIdentifier $eventRecord.EventIdentifier -ErrorAction SilentlyContinue
    $line = $eventRecord.SourceEventArgs.Data
    if ($null -eq $line -or $line -eq '') { continue }
    if (-not $IsError -and $line -eq '__PYTHON_MISSING__') {
      $script:DetectionResultSeen = $true
      Add-GuiEvent 'detection-result' ([ordered]@{ rows = @(Get-PythonMissingRows) })
    } elseif (-not $IsError -and $line.StartsWith('__DETECTION__')) {
      try {
        $context = $line.Substring('__DETECTION__'.Length) | ConvertFrom-Json
        $rows = @(Get-DetectionRows $context)
        $script:DetectionResultSeen = $true
        Add-GuiEvent 'detection-result' ([ordered]@{ rows = $rows })
      } catch {
        Add-GuiEvent 'operation-failed' ([ordered]@{ message = "Detection result was invalid: $($_.Exception.Message)" })
      }
    } else {
      Add-GuiEvent 'operation-output' ([ordered]@{
        operation = $script:ActiveOperation.Kind
        stream = $(if ($IsError) { 'stderr' } else { 'stdout' })
        line = $line
      })
    }
  }
}

function Update-GuiOperation {
  if (-not $script:ActiveOperation) { return }
  Read-OperationEvent $script:ActiveOperation.OutSource $false
  Read-OperationEvent $script:ActiveOperation.ErrSource $true
  if (-not $script:ActiveOperation.Process.HasExited) { return }

  $operation = $script:ActiveOperation
  $operation.Process.WaitForExit()
  Read-OperationEvent $operation.OutSource $false
  Read-OperationEvent $operation.ErrSource $true
  $exitCode = $operation.Process.ExitCode
  if ($exitCode -eq 0 -and ($operation.Kind -ne 'detection' -or $script:DetectionResultSeen)) {
    Add-GuiEvent "$($operation.Kind)-completed" ([ordered]@{ exit_code = $exitCode })
  } else {
    Add-GuiEvent 'operation-failed' ([ordered]@{
      operation = $operation.Kind
      exit_code = $exitCode
      message = "$($operation.Kind) failed with exit code $exitCode."
    })
  }
  Unregister-Event -SourceIdentifier $operation.OutSource -ErrorAction SilentlyContinue
  Unregister-Event -SourceIdentifier $operation.ErrSource -ErrorAction SilentlyContinue
  $operation.Process.Dispose()
  $script:ActiveOperation = $null
  $script:ShutdownAt = [DateTime]::UtcNow.AddSeconds($script:GraceSeconds)
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
  $body = if ($contentLength -gt 0) {
    $script:Utf8.GetString($allBytes, $headerEnd + 4, $contentLength)
  } else { '' }
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
  Write-HttpResponse $Stream $Status $Reason 'application/json; charset=utf-8' ($Value | ConvertTo-Json -Depth 20 -Compress) $Headers
}

function Test-ApiEnvelope([object]$Request, [bool]$RequireCookie) {
  if ($Request.Headers['host'] -cne $script:ExpectedHost) { return 403 }

  # A browser omits Origin on a same-origin GET and sends no Content-Type on a
  # bodyless request. Demanding both on every route rejected the page's own
  # fetch() and EventSource calls with 403 and 415. The Python tests could not
  # see this: they send whatever the test hands them, while a real browser
  # decides these headers itself.
  $origin = [string]$Request.Headers['origin']
  if ($origin) {
    if ($origin -cne $script:ExpectedOrigin) { return 403 }
  } else {
    # No Origin means same-origin by construction. Chromium still states it in
    # Sec-Fetch-Site; when that header exists and disagrees, refuse.
    $site = [string]$Request.Headers['sec-fetch-site']
    if ($site -and $site -cne 'same-origin') { return 403 }
  }

  # Only a request that carries a body has to declare its type.
  $hasBody = [string]$Request.Body
  if ($hasBody) {
    $mediaType = @($Request.Headers['content-type'] -split ';', 2)[0].Trim().ToLowerInvariant()
    if ($mediaType -ne 'application/json') { return 415 }
  }
  if ($RequireCookie) {
    $cookie = [string]$Request.Headers['cookie']
    $escaped = [regex]::Escape($script:SessionToken)
    if ($cookie -notmatch "(?:^|;\s*)oom_session=$escaped(?:;|$)") { return 401 }
  }
  return 0
}

function Invoke-HttpRequest([object]$Request) {
  $allowed = @('/', '/kur.html', '/api/session', '/api/detect', '/api/events', '/api/install', '/api/quit')
  if ($allowed -cnotcontains $Request.Target) {
    Write-JsonResponse $Request.Stream 404 'Not Found' ([ordered]@{ error = 'not_found' })
    return
  }

  if ($Request.Target -in @('/', '/kur.html')) {
    if ($Request.Method -cne 'GET') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    if ($Request.Headers['host'] -cne $script:ExpectedHost) {
      Write-JsonResponse $Request.Stream 403 'Forbidden' ([ordered]@{ error = 'invalid_host' })
      return
    }
    $html = [IO.File]::ReadAllText((Join-Path $script:RepoRoot 'gui\kur.html'), $script:Utf8)
    $securityHeaders = [ordered]@{
      # connect-src is load-bearing: with default-src 'none' and no connect-src,
      # the browser blocks the page's fetch() to its OWN api routes. The Python
      # tests drive the server directly and never see this - only a real browser
      # enforces CSP - so the omission survived until the page was opened.
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
    $cookieHeaders = [ordered]@{ 'Set-Cookie' = "oom_session=$($script:SessionToken); Path=/; HttpOnly; SameSite=Strict" }
    Write-JsonResponse $Request.Stream 200 'OK' ([ordered]@{ ok = $true }) $cookieHeaders
    return
  }

  $guard = Test-ApiEnvelope $Request $true
  if ($guard -ne 0) {
    $reason = if ($guard -eq 401) { 'Unauthorized' } elseif ($guard -eq 415) { 'Unsupported Media Type' } else { 'Forbidden' }
    Write-JsonResponse $Request.Stream $guard $reason ([ordered]@{ error = 'request_rejected' })
    return
  }

  if ($Request.Target -eq '/api/detect') {
    if ($Request.Method -cne 'GET') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    if ($script:ActiveOperation) {
      Write-JsonResponse $Request.Stream 409 'Conflict' ([ordered]@{ error = 'operation_in_progress' })
      return
    }
    $script:DetectionResultSeen = $false
    [void](Start-GuiOperation 'detection' (New-DetectionCommand))
    Write-JsonResponse $Request.Stream 202 'Accepted' ([ordered]@{ started = $true })
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

  if ($Request.Target -eq '/api/install') {
    if ($Request.Method -cne 'POST') {
      Write-JsonResponse $Request.Stream 405 'Method Not Allowed' ([ordered]@{ error = 'method_not_allowed' })
      return
    }
    if ($script:ActiveOperation) {
      Write-JsonResponse $Request.Stream 409 'Conflict' ([ordered]@{ error = 'operation_in_progress' })
      return
    }
    try { $body = $Request.Body | ConvertFrom-Json } catch { $body = $null }
    if (-not $body -or -not $body.answers) {
      Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'answers_path_required' })
      return
    }
    $answers = [IO.Path]::GetFullPath([string]$body.answers)
    if (-not (Test-Path -LiteralPath $answers -PathType Leaf)) {
      Write-JsonResponse $Request.Stream 400 'Bad Request' ([ordered]@{ error = 'answers_file_not_found' })
      return
    }
    $kur = (Join-Path $script:RepoRoot 'kur.ps1').Replace("'", "''")
    $answersLiteral = $answers.Replace("'", "''")
    $command = '$ProgressPreference = ''SilentlyContinue''; & ''{0}'' -Answers ''{1}''' -f $kur, $answersLiteral
    [void](Start-GuiOperation 'install' $command)
    Write-JsonResponse $Request.Stream 202 'Accepted' ([ordered]@{ started = $true; ui_driven = $false })
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

function Open-GuiBrowser([string]$Url) {
  if ($NoBrowser) {
    Write-Output "GUI_READY $Url"
    return
  }
  $edge = Get-Command msedge.exe -ErrorAction SilentlyContinue
  $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
  $candidates = @(
    $(if ($edge) { $edge.Source } else { $null }),
    $(if ($programFilesX86) { Join-Path $programFilesX86 'Microsoft\Edge\Application\msedge.exe' } else { $null }),
    $(if ($env:ProgramFiles) { Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe' } else { $null })
  ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -Unique
  foreach ($candidate in $candidates) {
    try {
      Start-Process -FilePath $candidate -ArgumentList "--app=$Url" -ErrorAction Stop | Out-Null
      Write-Host 'Opened the setup wizard in Microsoft Edge app mode.'
      return
    } catch {
      Write-Warning "Microsoft Edge could not be launched: $($_.Exception.Message)"
    }
  }
  try {
    Start-Process $Url -ErrorAction Stop | Out-Null
    Write-Host 'Microsoft Edge was unavailable; opened the setup wizard in the default browser.'
  } catch {
    Write-Warning "No browser could be launched: $($_.Exception.Message)"
    Write-Host "Open this URL manually: $Url"
  }
}

$listener = $null
try {
  $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
  $listener.Start()
  $endpoint = [Net.IPEndPoint]$listener.LocalEndpoint
  $port = $endpoint.Port
  $script:ExpectedHost = "127.0.0.1:$port"
  $script:ExpectedOrigin = "http://$($script:ExpectedHost)"
  $script:LaunchToken = New-RandomToken
  $script:SessionToken = New-RandomToken
  $url = "$($script:ExpectedOrigin)/#$($script:LaunchToken)"
  Write-Host "GUI_LISTENING $($endpoint.Address):$port"
  $script:IdleSeconds = $script:GraceSeconds
  $script:ShutdownAt = [DateTime]::UtcNow.AddSeconds($script:IdleSeconds)
  Open-GuiBrowser $url

  while ($true) {
    Update-GuiOperation
    if ($script:QuitRequested -and -not $script:ActiveOperation) { break }
    if ($script:ShutdownAt -and [DateTime]::UtcNow -ge $script:ShutdownAt -and -not $script:ActiveOperation) { break }
    if ($listener.Pending()) {
      $client = $listener.AcceptTcpClient()
      try {
        $request = Receive-HttpRequest $client
        # Someone is using it: push the idle deadline out.
        $script:ShutdownAt = [DateTime]::UtcNow.AddSeconds($script:IdleSeconds)
        Invoke-HttpRequest $request
      } catch {
        try {
          Write-JsonResponse $client.GetStream() 400 'Bad Request' ([ordered]@{ error = 'bad_request' })
        } catch {}
      } finally {
        $client.Dispose()
      }
    } else {
      Start-Sleep -Milliseconds 40
    }
  }
} catch {
  Write-Error "The graphical setup wizard failed: $($_.Exception.Message)"
  exit 1
} finally {
  if ($listener) { $listener.Stop() }
  if ($script:ActiveOperation) {
    try {
      $script:ActiveOperation.Process.WaitForExit()
      Update-GuiOperation
    } catch {}
  }
}
