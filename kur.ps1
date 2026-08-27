# yazan: codex · model: gpt-5.6-sol
<#
.SYNOPSIS
Interview-first setup wizard for origin-of-memory.

.DESCRIPTION
PowerShell 5.1 plan contract (also accepted through -Answers):
{
  "preset": "cloud|hybrid|local|lite",
  "vault": "<path>",
  "backend": "claude|antigravity|ollama|openai-compat|none",
  "backend_env": { "BEYIN_*": "<value>" },
  "mcp": true,
  "skills": ["beyin-doktor", "beyin-ice-aktar"],
  "force": false
}

With -Answers, the file is the complete plan: there are no prompts. -DryRun
prints every action and never writes, including setx and MCP registration.
#>
[CmdletBinding()]
param(
  [string]$Answers,
  [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$script:RepoRoot = $PSScriptRoot
$script:ProbeBackend = $false
$script:Interactive = -not $Answers

function Fail-Field([string]$Field, [string]$Message) {
  throw ("field '{0}': {1}" -f $Field, $Message)
}

function Has-Property([object]$Object, [string]$Name) {
  return $null -ne $Object.PSObject.Properties[$Name]
}

function Add-NoteProperty([object]$Object, [string]$Name, [object]$Value) {
  if ($Object.PSObject.Properties.Name -notcontains $Name) {
    $Object | Add-Member -MemberType NoteProperty -Name $Name -Value $Value
  }
}

function Read-Required([string]$Prompt) {
  while ($true) {
    $value = (Read-Host $Prompt).Trim()
    if ($value) { return $value }
    Write-Warning 'A value is required. (Bir değer gerekli.)'
  }
}

function Read-Choice(
  [string]$Title,
  [object[]]$Options,
  [int]$DefaultIndex = 0
) {
  Write-Host ''
  Write-Host $Title
  for ($index = 0; $index -lt $Options.Count; $index++) {
    $defaultMark = if ($index -eq $DefaultIndex) { ' [default / varsayılan]' } else { '' }
    Write-Host ("  {0}. {1}{2}" -f ($index + 1), $Options[$index].Label, $defaultMark)
  }
  while ($true) {
    $raw = (Read-Host 'Choice number (Seçim numarası)').Trim()
    if (-not $raw) { return $Options[$DefaultIndex].Value }
    $number = 0
    if ([int]::TryParse($raw, [ref]$number) -and $number -ge 1 -and $number -le $Options.Count) {
      return $Options[$number - 1].Value
    }
    Write-Warning 'Choose one of the listed numbers. (Listelenen numaralardan birini seç.)'
  }
}

function Read-YesNo([string]$Prompt, [bool]$Default) {
  $suffix = if ($Default) { '[Y/n] ([E/h])' } else { '[y/N] ([e/H])' }
  while ($true) {
    $raw = (Read-Host "$Prompt $suffix").Trim()
    if (-not $raw) { return $Default }
    if ($raw -match '^(?i:y|yes|e|evet)$') { return $true }
    if ($raw -match '^(?i:n|no|h|hayir|hayır)$') { return $false }
    Write-Warning 'Answer yes or no. (Evet veya hayır yanıtla.)'
  }
}

function Resolve-VaultPath([string]$Value) {
  if (-not $Value.Trim()) { Fail-Field 'vault' 'must not be empty' }
  try {
    $expanded = [Environment]::ExpandEnvironmentVariables($Value.Trim())
    $full = [IO.Path]::GetFullPath($expanded)
  } catch {
    Fail-Field 'vault' 'is not a valid path'
  }
  if (Test-Path -LiteralPath $full) {
    if (-not (Test-Path -LiteralPath $full -PathType Container)) {
      Fail-Field 'vault' 'must be a directory'
    }
  }
  $repo = [IO.Path]::GetFullPath($script:RepoRoot).TrimEnd('\', '/')
  $candidate = $full.TrimEnd('\', '/')
  $prefix = $repo + [IO.Path]::DirectorySeparatorChar
  if (
    $candidate.Equals($repo, [StringComparison]::OrdinalIgnoreCase) -or
    $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
  ) {
    Fail-Field 'vault' 'must not be inside the repository'
  }
  if ($full -match '[^\x00-\x7F]') {
    Write-Warning "Vault path contains non-ASCII characters: $full"
  }
  return $full
}

function Get-AvailableSkills {
  return @(
    Get-ChildItem -LiteralPath (Join-Path $script:RepoRoot 'skills') -Directory |
      Sort-Object Name
  )
}

function Get-SkillDescription([IO.DirectoryInfo]$Directory) {
  $skillFile = Join-Path $Directory.FullName 'SKILL.md'
  if (-not (Test-Path -LiteralPath $skillFile -PathType Leaf)) {
    return 'No description. (Açıklama yok.)'
  }
  foreach ($line in Get-Content -LiteralPath $skillFile -Encoding UTF8) {
    if ($line -match '^description:\s*["'']?(.*?)["'']?\s*$') {
      return $Matches[1]
    }
  }
  return 'No description. (Açıklama yok.)'
}

function Convert-BackendEnvironment([object]$Value) {
  if ($null -eq $Value -or $Value -is [string] -or $Value -is [array]) {
    Fail-Field 'backend_env' 'must be a JSON object'
  }
  $environment = [ordered]@{}
  $entries = if ($Value -is [Collections.IDictionary]) {
    @($Value.Keys | ForEach-Object {
      [pscustomobject]@{ Name = [string]$_; Value = $Value[$_] }
    })
  } else {
    @($Value.PSObject.Properties)
  }
  foreach ($entry in $entries) {
    if ($entry.Name -notmatch '^BEYIN_[A-Z0-9_]+$') {
      Fail-Field 'backend_env' ("key '{0}' must start with BEYIN_" -f $entry.Name)
    }
    if ($entry.Value -isnot [string] -or -not $entry.Value) {
      Fail-Field 'backend_env' ("'{0}' must be a non-empty string" -f $entry.Name)
    }
    $environment[$entry.Name] = [string]$entry.Value
  }
  return ,$environment
}

function Validate-Plan([object]$Plan) {
  if ($null -eq $Plan -or $Plan -is [array] -or $Plan -is [string]) {
    Fail-Field 'plan' 'must be a JSON object'
  }
  $required = @('preset', 'vault', 'backend', 'backend_env', 'mcp', 'skills', 'force')
  foreach ($field in $required) {
    if (-not (Has-Property $Plan $field)) { Fail-Field $field 'is required' }
  }
  foreach ($property in $Plan.PSObject.Properties.Name) {
    if ($required -notcontains $property) { Fail-Field $property 'is not part of the plan schema' }
  }

  if ($Plan.preset -isnot [string] -or @('cloud', 'hybrid', 'local', 'lite') -notcontains $Plan.preset) {
    Fail-Field 'preset' 'must be cloud, hybrid, local, or lite'
  }
  if ($Plan.backend -isnot [string] -or @('claude', 'antigravity', 'ollama', 'openai-compat', 'none') -notcontains $Plan.backend) {
    Fail-Field 'backend' 'must be claude, antigravity, ollama, openai-compat, or none'
  }
  $allowedBackends = switch ($Plan.preset) {
    'cloud' { @('claude') }
    'hybrid' { @('antigravity', 'ollama', 'openai-compat') }
    'local' { @('antigravity', 'ollama', 'openai-compat') }
    'lite' { @('none') }
  }
  if ($allowedBackends -notcontains $Plan.backend) {
    Fail-Field 'backend' ("is incompatible with preset '{0}'" -f $Plan.preset)
  }
  if ($Plan.mcp -isnot [bool]) { Fail-Field 'mcp' 'must be true or false' }
  if ($Plan.force -isnot [bool]) { Fail-Field 'force' 'must be true or false' }
  if ($null -eq $Plan.skills -or $Plan.skills -is [string]) {
    Fail-Field 'skills' 'must be a JSON array'
  }

  $availableNames = @(Get-AvailableSkills | ForEach-Object { $_.Name })
  $skills = @()
  foreach ($skill in @($Plan.skills)) {
    if ($skill -isnot [string] -or -not $skill) {
      Fail-Field 'skills' 'entries must be non-empty strings'
    }
    if ($availableNames -notcontains $skill) {
      Fail-Field 'skills' ("unknown skill '{0}'" -f $skill)
    }
    if ($skills -contains $skill) {
      Fail-Field 'skills' ("duplicate skill '{0}'" -f $skill)
    }
    $skills += $skill
  }

  $vault = Resolve-VaultPath ([string]$Plan.vault)
  $backendEnvironment = Convert-BackendEnvironment $Plan.backend_env
  if ($backendEnvironment.Contains('BEYIN_MODEL_BACKEND')) {
    if ($Plan.backend -eq 'none') {
      Fail-Field 'backend_env' 'BEYIN_MODEL_BACKEND is not valid for lite/none'
    }
    if ($backendEnvironment['BEYIN_MODEL_BACKEND'] -ne $Plan.backend) {
      Fail-Field 'backend_env' 'BEYIN_MODEL_BACKEND must match backend'
    }
  }
  if ($Plan.backend -eq 'ollama' -and -not $backendEnvironment.Contains('BEYIN_OLLAMA_MODEL_FAST')) {
    Fail-Field 'backend_env' 'BEYIN_OLLAMA_MODEL_FAST is required for ollama'
  }
  if ($Plan.backend -eq 'openai-compat') {
    if (-not $backendEnvironment.Contains('BEYIN_OPENAI_URL')) {
      Fail-Field 'backend_env' 'BEYIN_OPENAI_URL is required for openai-compat'
    }
    if (-not $backendEnvironment.Contains('BEYIN_OPENAI_MODEL_FAST')) {
      Fail-Field 'backend_env' 'BEYIN_OPENAI_MODEL_FAST is required for openai-compat'
    }
  }

  return [pscustomobject]@{
    Preset = [string]$Plan.preset
    Vault = $vault
    Backend = [string]$Plan.backend
    BackendEnvironment = $backendEnvironment
    Mcp = [bool]$Plan.mcp
    Skills = [string[]]$skills
    Force = [bool]$Plan.force
  }
}

function New-InteractivePlan {
  Write-Host 'origin-of-memory setup wizard (kurulum sihirbazı)'
  Write-Host 'Interview first, build second. (Önce görüşme, sonra kurulum.)'
  $preset = Read-Choice 'Choose a preset (Bir ön ayar seç)' @(
    [pscustomobject]@{ Value = 'cloud'; Label = 'Cloud (Bulut) — full system on Claude; subscription or ANTHROPIC_API_KEY' },
    [pscustomobject]@{ Value = 'hybrid'; Label = 'Hybrid (Hibrit) — Claude hooks/sessions; free background backend' },
    [pscustomobject]@{ Value = 'local'; Label = 'Local (Yerel) — local flush/ingest + MCP + clipboard; claude CLI still required for hooks/compile' },
    [pscustomobject]@{ Value = 'lite'; Label = 'Lite (Hafif) — no Claude Code; ingest + retrieval + MCP + clipboard only' }
  ) 0

  if ($preset -eq 'local') {
    Write-Host 'Local still requires the claude CLI for hooks and compile. (Local, kancalar ve derleme için yine claude CLI ister.)'
  } elseif ($preset -eq 'lite') {
    Write-Host 'Lite has no automatic capture and no compile. Feed memory with export ZIPs; read it through MCP or the clipboard bridge.'
    Write-Host 'Lite otomatik yakalama ve derleme yapmaz. Hafızayı dışa aktarım ZIP dosyalarıyla besle; MCP veya pano köprüsüyle oku.'
  }

  $vault = Resolve-VaultPath (Read-Required 'Vault path (Vault yolu)')
  $backend = 'claude'
  if ($preset -eq 'lite') {
    $backend = 'none'
  } elseif ($preset -in @('hybrid', 'local')) {
    $backend = Read-Choice 'Choose the background backend (Arka plan backendini seç)' @(
      [pscustomobject]@{ Value = 'antigravity'; Label = 'Antigravity — free CLI backend (ücretsiz CLI backendi)' },
      [pscustomobject]@{ Value = 'ollama'; Label = 'Ollama — local server (yerel sunucu)' },
      [pscustomobject]@{ Value = 'openai-compat'; Label = 'OpenAI-compatible — LM Studio, llama.cpp, vLLM' }
    ) 0
  }

  $backendEnvironment = [ordered]@{ BEYIN_VAULT = $vault }
  if ($backend -ne 'none') { $backendEnvironment['BEYIN_MODEL_BACKEND'] = $backend }
  if ($backend -eq 'ollama') {
    $model = (Read-Host 'Fast model slug (Hızlı model kısaltması) [qwen3:8b — suggestion only / yalnızca öneri]').Trim()
    if (-not $model) { $model = 'qwen3:8b' }
    $backendEnvironment['BEYIN_OLLAMA_MODEL_FAST'] = $model
  } elseif ($backend -eq 'openai-compat') {
    $backendEnvironment['BEYIN_OPENAI_URL'] = Read-Required 'OpenAI-compatible base URL (Temel URL)'
    $backendEnvironment['BEYIN_OPENAI_MODEL_FAST'] = Read-Required 'Fast model slug (Hızlı model kısaltması)'
  } elseif ($backend -eq 'antigravity') {
    Write-Host 'Reminder: run agy once interactively and complete login. (Hatırlatma: agy komutunu bir kez etkileşimli çalıştırıp giriş yap.)'
  }

  Write-Host ''
  Write-Host 'User environment variables to persist with setx (setx ile kalıcı olacak kullanıcı değişkenleri):'
  foreach ($name in $backendEnvironment.Keys) {
    Write-Host ("  {0}={1}" -f $name, $backendEnvironment[$name])
  }
  if (-not (Read-YesNo 'Persist these variables? (Bu değişkenler kalıcı olsun mu?)' $true)) {
    throw 'Setup cancelled: environment variables were not approved. (Kurulum iptal edildi.)'
  }

  $mcpDefault = $preset -in @('local', 'lite')
  $mcp = Read-YesNo 'Register the MCP server in Claude Desktop? (MCP sunucusu Claude Desktop uygulamasına kaydedilsin mi?)' $mcpDefault

  Write-Host ''
  Write-Host 'Skills (Beceriler):'
  $skills = @()
  foreach ($directory in Get-AvailableSkills) {
    $description = Get-SkillDescription $directory
    Write-Host ("  {0} — {1}" -f $directory.Name, $description)
    $default = $directory.Name -in @('beyin-doktor', 'beyin-ice-aktar')
    if (Read-YesNo ("Install {0}? (Kurulsun mu?)" -f $directory.Name) $default) {
      $skills += $directory.Name
    }
  }

  $script:ProbeBackend = Read-YesNo 'Run cheap backend checks after install? No model call. (Kurulumdan sonra ucuz backend kontrolleri çalışsın mı? Model çağrısı yok.)' $false
  $plan = [ordered]@{
    preset = $preset
    vault = $vault
    backend = $backend
    backend_env = $backendEnvironment
    mcp = $mcp
    skills = $skills
    force = $false
  }
  Write-Host ''
  Write-Host 'Plan JSON:'
  Write-Host ($plan | ConvertTo-Json -Depth 10)
  return [pscustomobject]$plan
}

function Read-PlanFile([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    Fail-Field 'Answers' 'file does not exist'
  }
  try {
    return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
  } catch {
    Fail-Field 'Answers' ("invalid JSON: {0}" -f $_.Exception.Message)
  }
}

function Show-PlanSummary([object]$Plan) {
  $rows = @(
    [pscustomobject]@{ Setting = 'Preset'; Value = $Plan.Preset },
    [pscustomobject]@{ Setting = 'Vault'; Value = $Plan.Vault },
    [pscustomobject]@{ Setting = 'Backend'; Value = $Plan.Backend },
    [pscustomobject]@{ Setting = 'MCP'; Value = $Plan.Mcp },
    [pscustomobject]@{ Setting = 'Skills'; Value = ($Plan.Skills -join ', ') },
    [pscustomobject]@{ Setting = 'Force'; Value = $Plan.Force },
    [pscustomobject]@{ Setting = 'Mode'; Value = $(if ($DryRun) { 'DRY RUN' } else { 'WRITE' }) }
  )
  Write-Host ''
  Write-Host 'Setup plan (Kurulum planı)'
  Write-Host ($rows | Format-Table -AutoSize | Out-String)
}

function Invoke-InstallCore([object]$Plan) {
  if (-not (Test-Path -LiteralPath $Plan.Vault) -and -not $DryRun) {
    Write-Host ("[CREATE] Vault directory: {0}" -f $Plan.Vault)
    New-Item -ItemType Directory -Path $Plan.Vault -Force | Out-Null
  }
  $powerShell = Get-Command powershell -ErrorAction SilentlyContinue
  if (-not $powerShell) { throw 'powershell executable not found' }
  $arguments = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
    (Join-Path $script:RepoRoot 'install.ps1'),
    '-VaultPath', $Plan.Vault
  )
  if ($DryRun) { $arguments += '-DryRun' }
  if ($Plan.Force) { $arguments += '-Force' }
  if ($Plan.Skills.Count -gt 0) {
    $arguments += @('-SkillFilter', ($Plan.Skills -join ','))
  } else {
    $arguments += '-NoSkills'
  }
  if ($Plan.Preset -eq 'lite') { $arguments += '-SkipHookRegistration' }
  Write-Host ("[RUN] install.ps1 preset={0}" -f $Plan.Preset)
  & $powerShell.Source @arguments
  if ($LASTEXITCODE -ne 0) {
    throw "install.ps1 failed with exit code $LASTEXITCODE"
  }
}

function Set-PlanEnvironment([object]$Plan) {
  foreach ($name in $Plan.BackendEnvironment.Keys) {
    $value = [string]$Plan.BackendEnvironment[$name]
    $shown = if ($name -match '(?i:KEY|TOKEN|SECRET)') { '<redacted>' } else { $value }
    if ($DryRun) {
      Write-Host ("[DRYRUN][SETX] {0}={1}" -f $name, $shown)
      continue
    }
    Write-Host ("[SETX] {0}={1}" -f $name, $shown)
    & setx $name $value | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "setx failed for $name" }
  }
}

function New-McpEntry([string]$Vault) {
  return [pscustomobject]@{
    command = 'python'
    args = @(
      (Join-Path $Vault '.claude\scripts\mcp_server.py'),
      '--vault',
      $Vault
    )
  }
}

function Show-McpSnippet([string]$Vault) {
  $snippet = [ordered]@{
    mcpServers = [ordered]@{
      'origin-of-memory' = New-McpEntry $Vault
    }
  }
  Write-Host 'MCP snippet from docs/mcp.md (docs/mcp.md yapılandırma parçası):'
  Write-Host ($snippet | ConvertTo-Json -Depth 10)
}

function Register-Mcp([object]$Plan) {
  if (-not $Plan.Mcp) {
    Write-Host '[SKIP] MCP registration not selected.'
    return
  }
  $appData = [Environment]::GetEnvironmentVariable('APPDATA')
  if (-not $appData) {
    Write-Warning 'APPDATA is unavailable; MCP registration skipped.'
    Show-McpSnippet $Plan.Vault
    return
  }
  $configPath = Join-Path $appData 'Claude\claude_desktop_config.json'
  if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    Write-Host ("[SKIP] Claude Desktop config not found: {0}" -f $configPath)
    Show-McpSnippet $Plan.Vault
    return
  }
  try {
    $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
  } catch {
    throw "Claude Desktop config is invalid JSON: $configPath"
  }
  if ($config -isnot [pscustomobject]) {
    throw "Claude Desktop config root must be an object: $configPath"
  }
  Add-NoteProperty $config 'mcpServers' ([pscustomobject]@{})
  if ($config.mcpServers -isnot [pscustomobject]) {
    throw "Claude Desktop config field 'mcpServers' must be an object: $configPath"
  }
  if ($null -ne $config.mcpServers.PSObject.Properties['origin-of-memory']) {
    Write-Host '[SKIP] MCP entry already present: origin-of-memory'
    return
  }
  $config.mcpServers | Add-Member -MemberType NoteProperty -Name 'origin-of-memory' -Value (New-McpEntry $Plan.Vault)
  $backup = "$configPath.bak-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
  if ($DryRun) {
    Write-Host ("[DRYRUN][BACKUP] {0} -> {1}" -f $configPath, $backup)
    Write-Host '[DRYRUN][MCP] Merge origin-of-memory under mcpServers.'
    return
  }
  Write-Host ("[BACKUP] {0} -> {1}" -f $configPath, $backup)
  Copy-Item -LiteralPath $configPath -Destination $backup
  $json = $config | ConvertTo-Json -Depth 100
  [IO.File]::WriteAllText(
    $configPath,
    $json + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false)
  )
  Write-Host '[MCP] Registered origin-of-memory.'
}

function Test-TcpEndpoint([string]$Url) {
  try {
    $uri = [Uri]$Url
    $port = $uri.Port
    $client = New-Object Net.Sockets.TcpClient
    try {
      $async = $client.BeginConnect($uri.Host, $port, $null, $null)
      if (-not $async.AsyncWaitHandle.WaitOne(1500, $false)) { return $false }
      $client.EndConnect($async)
      return $true
    } finally {
      $client.Close()
    }
  } catch {
    return $false
  }
}

function Verify-Backend([object]$Plan) {
  if ($Plan.Backend -in @('claude', 'none')) { return }
  if ($DryRun) {
    Write-Host ("[DRYRUN][VERIFY] Cheap backend check: {0}; no model call." -f $Plan.Backend)
    return
  }
  if (-not $script:ProbeBackend) {
    Write-Host '[SKIP] Backend probe was not approved.'
    return
  }
  if ($Plan.Backend -eq 'antigravity') {
    $command = Get-Command agy -ErrorAction SilentlyContinue
    if (-not $command) { Write-Warning 'agy binary not found.'; return }
    & $command.Source --version
    Write-Host 'If this is the first run, launch agy interactively once to log in.'
    return
  }
  if ($Plan.Backend -eq 'ollama') {
    $command = Get-Command ollama -ErrorAction SilentlyContinue
    if ($command) { & $command.Source --version } else { Write-Warning 'ollama binary not found.' }
    $url = if ($Plan.BackendEnvironment.Contains('BEYIN_OLLAMA_URL')) {
      $Plan.BackendEnvironment['BEYIN_OLLAMA_URL']
    } else {
      'http://localhost:11434'
    }
    if (Test-TcpEndpoint $url) { Write-Host "[OK] Ollama TCP endpoint reachable: $url" } else { Write-Warning "Ollama TCP endpoint not reachable: $url" }
    return
  }
  $openAiUrl = $Plan.BackendEnvironment['BEYIN_OPENAI_URL']
  if (Test-TcpEndpoint $openAiUrl) { Write-Host "[OK] OpenAI-compatible TCP endpoint reachable: $openAiUrl" } else { Write-Warning "OpenAI-compatible TCP endpoint not reachable: $openAiUrl" }
}

function Show-NextSteps([object]$Plan) {
  Write-Host ''
  Write-Host 'What happens next (Sonra ne olur)'
  switch ($Plan.Preset) {
    'cloud' {
      Write-Host '  Claude Code captures sessions automatically; flush and nightly compile use Claude.'
      Write-Host '  Claude Code oturumları otomatik yakalar; flush ve gece derlemesi Claude kullanır.'
    }
    'hybrid' {
      Write-Host '  Claude Code hooks capture sessions; background flush/ingest use the selected backend. Compile still needs claude CLI.'
      Write-Host '  Kancalar oturumları yakalar; arka plan flush/içe aktarma seçili backendi kullanır. Derleme yine claude CLI ister.'
    }
    'local' {
      Write-Host '  Local flush/ingest is ready; MCP and the clipboard bridge provide read access. Hooks and compile still need claude CLI.'
      Write-Host '  Yerel flush/içe aktarma hazır; MCP ve pano köprüsü okuma sağlar. Kancalar ve derleme yine claude CLI ister.'
    }
    'lite' {
      Write-Host '  No automatic capture and no compile. Import export ZIPs, build retrieval data, then use MCP or the clipboard bridge.'
      Write-Host '  Otomatik yakalama ve derleme yok. Dışa aktarım ZIP dosyalarını içe aktar, getirme verisini oluştur, sonra MCP veya pano köprüsünü kullan.'
    }
  }
}

try {
  $rawPlan = if ($Answers) { Read-PlanFile $Answers } else { New-InteractivePlan }
  $plan = Validate-Plan $rawPlan
  Show-PlanSummary $plan
  if ($script:Interactive -and -not (Read-YesNo 'Execute this plan? (Bu plan çalıştırılsın mı?)' $false)) {
    throw 'Setup cancelled. (Kurulum iptal edildi.)'
  }
  Invoke-InstallCore $plan
  Set-PlanEnvironment $plan
  Register-Mcp $plan
  Verify-Backend $plan
  Show-NextSteps $plan
  Write-Host ''
  Write-Host ("[DONE] preset={0} mode={1}" -f $plan.Preset, $(if ($DryRun) { 'dry-run' } else { 'write' }))
  exit 0
} catch {
  Write-Error ("[FAILED] {0}" -f $_.Exception.Message)
  exit 1
}
