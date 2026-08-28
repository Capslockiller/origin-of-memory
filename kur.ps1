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
  "force": false,
  "install_runtime": false,
  "pull_models": ["qwen3:8b"]
}

With -Answers, the file is the complete plan: there are no prompts. -DryRun
prints every action and never writes, including environment and MCP registration.
With -Recommended, detection builds the plan and installation proceeds without
prompts; pair it with -DryRun for an agent-reviewable confirmation screen.
#>
[CmdletBinding()]
param(
  [string]$Answers,
  [switch]$Recommended,
  [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$script:RepoRoot = $PSScriptRoot
$script:ProbeBackend = $false
$script:Interactive = -not $Answers -and -not $Recommended
$script:AllowEmptyOpenAiModel = $false
$script:PlanAlreadyConfirmed = $false
$script:OllamaModels = [ordered]@{
  'qwen3:4b' = 2.5
  'qwen3:8b' = 5.2
  'qwen3:14b' = 9.3
  'qwen3:30b' = 19.0
  'gemma3:4b' = 3.3
  'gemma3:12b' = 8.1
  'gemma3:27b' = 17.0
}

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

function Read-Default([string]$Prompt, [string]$DefaultValue) {
  $raw = (Read-Host ("{0} [{1}]" -f $Prompt, $(if ($DefaultValue) { $DefaultValue } else { 'not detected / sonra ayarla' }))).Trim()
  if ($raw) { return $raw }
  return $DefaultValue
}

function Read-Choice(
  [string]$Title,
  [object[]]$Options,
  [int]$DefaultIndex = 0
) {
  Write-Host ''
  Write-Host $Title
  for ($index = 0; $index -lt $Options.Count; $index++) {
    $defaultMark = if ($index -eq $DefaultIndex) { ' [default] [varsayılan]' } else { '' }
    Write-Host ("  [{0}] {1}{2}" -f ($index + 1), $Options[$index].Label, $defaultMark)
  }
  while ($true) {
    $raw = (Read-Host ("Choice number [{0}] (Seçim numarası)" -f ($DefaultIndex + 1))).Trim()
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

function Find-PythonCommand {
  $command = Get-Command python -ErrorAction SilentlyContinue
  if ($command) {
    return [pscustomobject]@{ Exe = $command.Source; Prefix = @() }
  }
  $command = Get-Command py -ErrorAction SilentlyContinue
  if ($command) {
    return [pscustomobject]@{ Exe = $command.Source; Prefix = @('-3') }
  }
  throw 'Python was not found; the hardware probe requires Python 3.12+.'
}

function Invoke-PythonJson([string]$ScriptName, [string[]]$Arguments) {
  $python = Find-PythonCommand
  $scriptPath = Join-Path $script:RepoRoot ("scripts\{0}" -f $ScriptName)
  $output = & $python.Exe @($python.Prefix) -B $scriptPath @Arguments 2>&1 | Out-String
  if ($LASTEXITCODE -ne 0) {
    throw ("{0} failed: {1}" -f $ScriptName, $output.Trim())
  }
  try {
    return $output | ConvertFrom-Json
  } catch {
    throw ("{0} returned invalid JSON: {1}" -f $ScriptName, $_.Exception.Message)
  }
}

function Show-HardwareSummary([object]$Probe) {
  $gpuText = if (@($Probe.gpus).Count -gt 0) {
    @($Probe.gpus | ForEach-Object {
      "{0} / {1:N2} GB ({2})" -f $_.name, [double]$_.vram_gb, $_.source
    }) -join '; '
  } else {
    'unknown / bilinmiyor'
  }
  Write-Host ''
  Write-Host 'Local model hardware summary (Yerel model donanım özeti)'
  Write-Host ("  RAM: {0}" -f $(if ($null -eq $Probe.ram_gb) { 'unknown' } else { "{0:N2} GB" -f [double]$Probe.ram_gb }))
  Write-Host ("  CPU: {0}; physical/logical cores: {1}/{2}" -f $Probe.cpu.name, $Probe.cpu.physical_cores, $Probe.cpu.logical_cores)
  Write-Host ("  GPU: {0}" -f $gpuText)
  Write-Host ("  Model-store free disk: {0}" -f $(if ($null -eq $Probe.free_disk_gb) { 'unknown' } else { "{0:N2} GB" -f [double]$Probe.free_disk_gb }))
}

function Read-VerifiedModel([string]$Prompt, [string]$DefaultTag) {
  while ($true) {
    $raw = (Read-Host ("{0} [{1}]" -f $Prompt, $DefaultTag)).Trim()
    $tag = if ($raw) { $raw } else { $DefaultTag }
    if ($script:OllamaModels.Contains($tag)) { return $tag }
    Write-Warning ("Use one of the verified tags: {0}" -f ($script:OllamaModels.Keys -join ', '))
  }
}

function Get-OllamaInteractiveChoices {
  if ($DryRun) {
    return [pscustomobject]@{
      FastModel = 'qwen3:8b'
      SmartModel = $null
      InstallRuntime = $false
      PullModels = @()
    }
  }

  $probe = Invoke-PythonJson 'donanim.py' @('--json')
  Show-HardwareSummary $probe
  $probeJson = $probe | ConvertTo-Json -Depth 100 -Compress
  $recommendations = @(Invoke-PythonJson 'model_oneri.py' @('--json', '--probe-json', $probeJson))
  Write-Host ''
  Write-Host 'Ranked model fit estimates (Sıralı model uyum tahminleri)'
  $rows = @($recommendations | ForEach-Object {
    [pscustomobject]@{
      Tag = $_.tag
      SizeGB = $_.size_gb
      Label = $_.label
      Role = $_.role
      Why = $_.why
    }
  })
  Write-Host ($rows | Format-Table -AutoSize -Wrap | Out-String)
  Write-Host 'These are memory-fit estimates, not speed promises. (Bunlar bellek uyum tahminidir, hız vaadi değildir.)'

  $fastCandidate = @($recommendations | Where-Object { $_.role -eq 'fast' }) | Select-Object -First 1
  if (-not $fastCandidate) { $fastCandidate = $recommendations[0] }
  $fastModel = Read-VerifiedModel 'Fast model tag (Hızlı model etiketi)' $fastCandidate.tag

  $installRuntime = $false
  if (-not (Get-Command ollama -ErrorAction SilentlyContinue)) {
    Write-Host ''
    Write-Host 'Ollama is not on PATH. The installer may trigger Windows SmartScreen; review that prompt and do not bypass it.'
    Write-Host 'Ollama PATH üzerinde değil. Kurucu Windows SmartScreen uyarısı gösterebilir; uyarıyı inceleyin, atlatmayın.'
    Write-Host 'Preferred exact command:'
    Write-Host '  winget install --id Ollama.Ollama -e --accept-source-agreements --accept-package-agreements'
    Write-Host 'Fallback exact commands when winget is absent:'
    Write-Host '  Invoke-WebRequest https://ollama.com/download/OllamaSetup.exe -OutFile "$env:TEMP\OllamaSetup.exe"'
    Write-Host '  Start-Process "$env:TEMP\OllamaSetup.exe" -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART" -Wait'
    $installRuntime = Read-YesNo 'Install Ollama after final plan approval? (Son plan onayından sonra Ollama kurulsun mu?)' $false
  }

  $pullModels = @()
  $fastSize = [double]$script:OllamaModels[$fastModel]
  $fastDiskNeed = [Math]::Round($fastSize * 1.5, 2)
  Write-Host ("Model {0}: {1:N2} GB download; disk preflight requires {2:N2} GB. Pulls are resumable." -f $fastModel, $fastSize, $fastDiskNeed)
  if ($null -ne $probe.free_disk_gb -and [double]$probe.free_disk_gb -lt $fastDiskNeed) {
    Write-Warning ("Not enough model-store disk: {0:N2} GB free, {1:N2} GB required. Pull will not be offered." -f [double]$probe.free_disk_gb, $fastDiskNeed)
  } elseif (Read-YesNo ("Pull {0} after final plan approval? (Son plan onayından sonra indirilsin mi?)" -f $fastModel) $false) {
    $pullModels += $fastModel
  }

  $smartModel = $null
  $smartCandidate = @($recommendations | Where-Object { $_.role -eq 'smart' -and $_.label -ne 'no-fit' }) | Select-Object -First 1
  if ($pullModels.Count -gt 0 -and $smartCandidate -and $smartCandidate.tag -ne $fastModel) {
    $smartSize = [double]$script:OllamaModels[$smartCandidate.tag]
    $combinedNeed = [Math]::Round(($fastSize + $smartSize) * 1.5, 2)
    Write-Host ("Optional smart model {0}: {1:N2} GB; combined disk preflight requires {2:N2} GB." -f $smartCandidate.tag, $smartSize, $combinedNeed)
    if (
      ($null -eq $probe.free_disk_gb -or [double]$probe.free_disk_gb -ge $combinedNeed) -and
      (Read-YesNo ("Also pull {0} as the smart model? (Akıllı model olarak bu da indirilsin mi?)" -f $smartCandidate.tag) $false)
    ) {
      $smartModel = $smartCandidate.tag
      $pullModels += $smartModel
    }
  }

  return [pscustomobject]@{
    FastModel = $fastModel
    SmartModel = $smartModel
    InstallRuntime = $installRuntime
    PullModels = [string[]]$pullModels
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
    $emptyOpenAiModel = (
      $script:AllowEmptyOpenAiModel -and
      $entry.Name -eq 'BEYIN_OPENAI_MODEL_FAST' -and
      $entry.Value -is [string]
    )
    if (($entry.Value -isnot [string] -or -not $entry.Value) -and -not $emptyOpenAiModel) {
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
  $optional = @('install_runtime', 'pull_models')
  foreach ($field in $required) {
    if (-not (Has-Property $Plan $field)) { Fail-Field $field 'is required' }
  }
  Add-NoteProperty $Plan 'install_runtime' $false
  Add-NoteProperty $Plan 'pull_models' @()
  foreach ($property in $Plan.PSObject.Properties.Name) {
    if ($required -notcontains $property -and $optional -notcontains $property) {
      Fail-Field $property 'is not part of the plan schema'
    }
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
    'lite' { @('none', 'ollama', 'openai-compat') }
  }
  if ($allowedBackends -notcontains $Plan.backend) {
    Fail-Field 'backend' ("is incompatible with preset '{0}'" -f $Plan.preset)
  }
  if ($Plan.mcp -isnot [bool]) { Fail-Field 'mcp' 'must be true or false' }
  if ($Plan.force -isnot [bool]) { Fail-Field 'force' 'must be true or false' }
  if ($Plan.install_runtime -isnot [bool]) {
    Fail-Field 'install_runtime' 'must be true or false'
  }
  if ($null -eq $Plan.skills -or $Plan.skills -is [string]) {
    Fail-Field 'skills' 'must be a JSON array'
  }
  if ($null -eq $Plan.pull_models -or $Plan.pull_models -is [string]) {
    Fail-Field 'pull_models' 'must be a JSON array'
  }

  $pullModels = @()
  foreach ($tag in @($Plan.pull_models)) {
    if ($tag -isnot [string] -or -not $script:OllamaModels.Contains($tag)) {
      Fail-Field 'pull_models' ("unknown verified Ollama tag '{0}'" -f $tag)
    }
    if ($pullModels -contains $tag) {
      Fail-Field 'pull_models' ("duplicate tag '{0}'" -f $tag)
    }
    $pullModels += $tag
  }
  if ($pullModels.Count -gt 2) {
    Fail-Field 'pull_models' 'accepts at most a fast and a smart model'
  }
  if (($Plan.install_runtime -or $pullModels.Count -gt 0) -and $Plan.backend -ne 'ollama') {
    Fail-Field 'install_runtime' 'runtime install and model pulls require backend ollama'
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
  if ($Plan.backend -eq 'ollama') {
    if ($pullModels.Count -gt 0) {
      if (
        $backendEnvironment.Contains('BEYIN_OLLAMA_MODEL_FAST') -and
        $backendEnvironment['BEYIN_OLLAMA_MODEL_FAST'] -ne $pullModels[0]
      ) {
        Fail-Field 'pull_models' 'first tag must match BEYIN_OLLAMA_MODEL_FAST'
      }
      $backendEnvironment['BEYIN_OLLAMA_MODEL_FAST'] = $pullModels[0]
      if ($pullModels.Count -gt 1) {
        if (
          $backendEnvironment.Contains('BEYIN_OLLAMA_MODEL_SMART') -and
          $backendEnvironment['BEYIN_OLLAMA_MODEL_SMART'] -ne $pullModels[1]
        ) {
          Fail-Field 'pull_models' 'second tag must match BEYIN_OLLAMA_MODEL_SMART'
        }
        $backendEnvironment['BEYIN_OLLAMA_MODEL_SMART'] = $pullModels[1]
      }
    }
    if (-not $backendEnvironment.Contains('BEYIN_OLLAMA_MODEL_FAST')) {
      Fail-Field 'backend_env' 'BEYIN_OLLAMA_MODEL_FAST is required for ollama'
    }
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
    InstallRuntime = [bool]$Plan.install_runtime
    PullModels = [string[]]$pullModels
  }
}

function Get-McpConfigState {
  $appData = [Environment]::GetEnvironmentVariable('APPDATA')
  $localAppData = [Environment]::GetEnvironmentVariable('LOCALAPPDATA')
  $standard = if ($appData) { Join-Path $appData 'Claude\claude_desktop_config.json' } else { $null }
  $virtual = if ($localAppData) { Join-Path $localAppData 'Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\claude_desktop_config.json' } else { $null }
  $paths = @($standard, $virtual | Where-Object { $_ })
  return [pscustomobject]@{
    Exists = [bool](@($paths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }).Count -gt 0)
    Paths = [string[]]$paths
  }
}

function Get-DecisionContext {
  $probe = Invoke-PythonJson 'donanim.py' @('--json')
  $recommendations = @(Invoke-PythonJson 'model_oneri.py' @('--json'))
  $userProfile = [Environment]::GetFolderPath('UserProfile')
  if (-not $userProfile) { $userProfile = [Environment]::GetEnvironmentVariable('USERPROFILE') }
  $documents = [Environment]::GetFolderPath('MyDocuments')
  if (-not $documents) { $documents = Join-Path $userProfile 'Documents' }
  $mcpState = Get-McpConfigState
  return [pscustomobject]@{
    probe = $probe
    recommendations = $recommendations
    user_profile = $userProfile
    documents_path = $documents
    mcp_config_exists = $mcpState.Exists
  }
}

function Get-AutoDecision {
  $context = Get-DecisionContext
  $json = $context | ConvertTo-Json -Depth 100 -Compress
  $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
  $decision = Invoke-PythonJson 'kurulum_plani.py' @('--context-base64', $encoded)
  return [pscustomobject]@{ Context = $context; Decision = $decision }
}

function Show-Detection([object]$Bundle) {
  $context = $Bundle.Context
  Write-Host ''
  Write-Host 'Detected environment (Algılanan ortam)'
  Write-Host ("  Claude Code: {0}" -f $(if ($context.probe.commands.claude) { 'detected' } else { 'not detected' }))
  $runtimes = @($context.probe.runtimes)
  if ($runtimes.Count -eq 0) {
    Write-Host '  Local runtimes: none detected'
  } else {
    foreach ($runtime in $runtimes) {
      $state = if ($runtime.detected_by) { $runtime.detected_by } else { 'not detected' }
      Write-Host ("  {0}: {1}; endpoint={2}; backend={3}" -f $runtime.name, $state, $runtime.endpoint, $runtime.backend)
    }
  }
  Show-HardwareSummary $context.probe
}

function Show-RecommendedConfirmation([object]$Decision, [bool]$PromptForAction) {
  $plan = $Decision.plan
  Write-Host ''
  Write-Host 'Step 2/2 — Recommended setup confirmation (Önerilen kurulum onayı)'
  Write-Host ("  Preset: {0} — {1}" -f $plan.preset, $Decision.reasons.preset)
  Write-Host ("  Vault: {0} — {1}" -f $plan.vault, $Decision.reasons.vault)
  Write-Host ("  Backend: {0} — {1}" -f $plan.backend, $Decision.reasons.backend)
  Write-Host ("  Model: {0} — {1}" -f $(if ($plan.backend -eq 'ollama') { $plan.backend_env.BEYIN_OLLAMA_MODEL_FAST } elseif ($plan.backend -eq 'openai-compat') { 'set later' } else { 'not needed' }), $Decision.reasons.model)
  Write-Host ("  MCP: {0} — {1}" -f $plan.mcp, $Decision.reasons.mcp)
  Write-Host ("  Skills: {0} — {1}" -f ($plan.skills -join ', '), $Decision.reasons.skills)
  foreach ($note in @($Decision.notes)) { Write-Host ("  NOTE: {0}" -f $note) }
  foreach ($todo in @($Decision.todos)) { Write-Host ("  [TODO] {0}" -f $todo) }
  Write-Host ("  Mode: {0}" -f $(if ($DryRun) { 'DRY RUN (writes nothing)' } else { 'WRITE' }))
  Write-Host ("  Plan JSON: {0}" -f ($plan | ConvertTo-Json -Depth 10 -Compress))
  if (-not $PromptForAction) { return 'install' }
  while ($true) {
    $raw = (Read-Host '[Enter] Install / [c] Change something / [q] Quit').Trim().ToLowerInvariant()
    if (-not $raw) { return 'install' }
    if ($raw -eq 'c') { return 'change' }
    if ($raw -eq 'q') { return 'quit' }
    Write-Warning 'Press Enter, c, or q. (Enter, c veya q kullan.)'
  }
}

function Get-OpenAiDefaultUrl([object]$Runtime) {
  $name = ([string]$Runtime.name).ToLowerInvariant()
  $url = ([string]$Runtime.endpoint).TrimEnd('/')
  if (-not $url) {
    if ($name -eq 'lm studio') { $url = 'http://127.0.0.1:1234/v1' }
    elseif ($name -eq 'llama.cpp') { $url = 'http://127.0.0.1:8080/v1' }
    else { $url = 'http://127.0.0.1:8000/v1' }
  } elseif (-not $url.EndsWith('/v1')) {
    $url += '/v1'
  }
  return $url
}

function Read-RuntimeChoice([object]$Bundle, [string]$Preset) {
  $detected = @($Bundle.Decision.detected_runtimes)
  if ($detected.Count -gt 0) {
    $options = @()
    foreach ($runtime in $detected) {
      $status = if ($runtime.detected_by -in @('port', 'both')) { 'running' } else { 'installed' }
      $runtimeNote = if (([string]$runtime.name) -match '^(?i:vllm)$') { '; probably WSL/remote, no installer' } else { '' }
      $options += [pscustomobject]@{
        Value = $runtime
        Label = ("{0} — {1}; {2}{3}" -f $runtime.name, $status, $runtime.endpoint, $runtimeNote)
      }
    }
    $skipBackend = if ($Preset -eq 'lite') { 'none' } else { 'antigravity' }
    $options += [pscustomobject]@{
      Value = [pscustomobject]@{ name = 'Skip local models'; backend = $skipBackend; endpoint = ''; detected_by = $null }
      Label = $(if ($skipBackend -eq 'none') { 'Skip local models' } else { 'Skip local models — use Antigravity' })
    }
    return Read-Choice 'Detected local runtimes — running is preferred (Algılanan yerel çalışma zamanları)' $options 0
  }

  $choice = Read-Choice 'No local runtime detected (Yerel çalışma zamanı algılanmadı)' @(
    [pscustomobject]@{ Value = 'install-ollama'; Label = 'Install Ollama (easiest, CLI; model download size is shown separately)' },
    [pscustomobject]@{ Value = 'lm-studio'; Label = "I'll use LM Studio (GUI; no CLI needed)" },
    [pscustomobject]@{ Value = 'skip'; Label = 'Skip local models' }
  ) 0
  if ($choice -eq 'install-ollama') {
    return [pscustomobject]@{ name = 'Ollama'; backend = 'ollama'; endpoint = 'http://127.0.0.1:11434'; detected_by = $null; install = $true }
  }
  if ($choice -eq 'lm-studio') {
    Write-Host '  Download: https://lmstudio.ai/download'
    Write-Host '  Install the GUI, load a model, then enable the local server in the Developer/Server tab. This wizard does not download the installer.'
    return [pscustomobject]@{ name = 'LM Studio'; backend = 'openai-compat'; endpoint = 'http://127.0.0.1:1234/v1'; detected_by = $null; install = $false }
  }
  $fallback = if ($Preset -eq 'lite') { 'none' } else { 'antigravity' }
  return [pscustomobject]@{ name = 'Skip local models'; backend = $fallback; endpoint = ''; detected_by = $null; install = $false }
}

function New-CustomPlan([object]$Bundle) {
  $defaults = $Bundle.Decision.plan
  Write-Host ''
  Write-Host 'Step 1/7 — Preset'
  $presetOptions = @(
    [pscustomobject]@{ Value = 'cloud'; Label = 'Cloud — Claude handles capture and model work' },
    [pscustomobject]@{ Value = 'hybrid'; Label = 'Hybrid — Claude capture plus a local/background backend' },
    [pscustomobject]@{ Value = 'local'; Label = 'Local — local model work; Claude still needed for hooks/compile' },
    [pscustomobject]@{ Value = 'lite'; Label = 'Lite — no Claude Code; no automatic capture or nightly compile' }
  )
  $presetDefault = [Array]::IndexOf(@('cloud', 'hybrid', 'local', 'lite'), [string]$defaults.preset)
  if ($presetDefault -lt 0) { $presetDefault = 0 }
  $preset = Read-Choice 'Choose a preset (Ön ayar seç)' $presetOptions $presetDefault

  Write-Host ''
  Write-Host 'Step 2/7 — Vault'
  $vault = Resolve-VaultPath (Read-Default 'Vault path (Vault yolu)' ([string]$defaults.vault))

  Write-Host ''
  Write-Host 'Step 3/7 — Runtime/backend'
  $runtime = $null
  $backend = 'claude'
  $installRuntime = $false
  if ($preset -in @('hybrid', 'local', 'lite')) {
    $runtime = Read-RuntimeChoice $Bundle $preset
    $backend = [string]$runtime.backend
    $installRuntime = [bool]$runtime.install
  } else {
    Write-Host '  Claude backend [detected default / algılanan varsayılan]'
  }

  Write-Host ''
  Write-Host 'Step 4/7 — Model'
  $backendEnvironment = [ordered]@{ BEYIN_VAULT = $vault }
  $pullModels = @()
  if ($backend -ne 'none') { $backendEnvironment['BEYIN_MODEL_BACKEND'] = $backend }
  if ($backend -eq 'ollama') {
    $candidate = @($Bundle.Context.recommendations | Where-Object { $_.label -in @('fits-gpu', 'cpu-ok') }) | Select-Object -First 1
    if (-not $candidate) { $candidate = @($Bundle.Context.recommendations) | Select-Object -First 1 }
    $defaultTag = if ($candidate) { [string]$candidate.tag } else { 'qwen3:4b' }
    $fastModel = Read-VerifiedModel 'Fast Ollama model tag (Hızlı Ollama modeli)' $defaultTag
    $backendEnvironment['BEYIN_OLLAMA_MODEL_FAST'] = $fastModel
    if ($installRuntime) {
      $size = [double]$script:OllamaModels[$fastModel]
      Write-Host ("  {0} download is about {1:N1} GB; pull remains opt-in." -f $fastModel, $size)
      if (Read-YesNo 'Pull this model after installing Ollama? (Model Ollama kurulduktan sonra indirilsin mi?)' $false) { $pullModels += $fastModel }
    }
  } elseif ($backend -eq 'openai-compat') {
    $backendEnvironment['BEYIN_OPENAI_URL'] = Get-OpenAiDefaultUrl $runtime
    Write-Host '  LM Studio shows the model identifier in its Server tab; llama.cpp/vLLM use the served model name.'
    Write-Host '  Model listing needs an HTTP request and is never performed automatically or in dry-run.'
    $knownModel = if ($runtime.PSObject.Properties['model']) { [string]$runtime.model } else { '' }
    $backendEnvironment['BEYIN_OPENAI_MODEL_FAST'] = Read-Default 'Fast model name (Hızlı model adı)' $knownModel
    $script:AllowEmptyOpenAiModel = $true
  } elseif ($backend -eq 'antigravity') {
    Write-Host '  Antigravity selected [detected/default]; run agy once interactively to log in.'
  } else {
    Write-Host '  No model backend [default for lite without a runtime].'
  }

  Write-Host ''
  Write-Host 'Step 5/7 — Integrations'
  $mcp = Read-YesNo 'Register MCP in Claude Desktop?' ([bool]$defaults.mcp)
  $skillMode = Read-Choice 'Skills to install (Kurulacak beceriler)' @(
    [pscustomobject]@{ Value = 'core'; Label = 'beyin-doktor + beyin-ice-aktar' },
    [pscustomobject]@{ Value = 'all'; Label = 'All available skills (Tüm beceriler)' },
    [pscustomobject]@{ Value = 'none'; Label = 'No skills (Beceri yok)' }
  ) 0
  $skills = if ($skillMode -eq 'core') { @('beyin-doktor', 'beyin-ice-aktar') } elseif ($skillMode -eq 'all') { @(Get-AvailableSkills | ForEach-Object { $_.Name }) } else { @() }

  Write-Host ''
  Write-Host 'Step 6/7 — Verification'
  $script:ProbeBackend = Read-YesNo 'Run a cheap TCP/binary check after install? No model call.' $false

  $plan = [ordered]@{
    preset = $preset
    vault = $vault
    backend = $backend
    backend_env = $backendEnvironment
    mcp = $mcp
    skills = $skills
    force = $false
    install_runtime = $installRuntime
    pull_models = $pullModels
  }
  Write-Host ''
  Write-Host 'Step 7/7 — Plan confirmation'
  Write-Host 'Plan JSON:'
  Write-Host ($plan | ConvertTo-Json -Depth 10)
  return [pscustomobject]$plan
}

function New-InteractivePlan {
  Write-Host 'origin-of-memory setup wizard (kurulum sihirbazı)'
  Write-Host 'Step 1/2 — Choose a path (Yol seç)'
  $mode = Read-Choice 'First question decides the flow (İlk soru akışı belirler)' @(
    [pscustomobject]@{ Value = 'recommended'; Label = 'Recommended setup (auto-detect)' },
    [pscustomobject]@{ Value = 'custom'; Label = 'Custom' },
    [pscustomobject]@{ Value = 'detect'; Label = 'Show me what you detected first' }
  ) 0
  $bundle = Get-AutoDecision
  if ($mode -eq 'detect') {
    Show-Detection $bundle
    return New-CustomPlan $bundle
  }
  if ($mode -eq 'custom') { return New-CustomPlan $bundle }
  $action = Show-RecommendedConfirmation $bundle.Decision $true
  if ($action -eq 'quit') { return $null }
  if ($action -eq 'change') { return New-CustomPlan $bundle }
  $script:AllowEmptyOpenAiModel = $true
  $script:PlanAlreadyConfirmed = $true
  return $bundle.Decision.plan
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
    [pscustomobject]@{ Setting = 'Install Ollama'; Value = $Plan.InstallRuntime },
    [pscustomobject]@{ Setting = 'Pull models'; Value = ($Plan.PullModels -join ', ') },
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
    if ($name -eq 'BEYIN_OPENAI_MODEL_FAST' -and -not $value) {
      Write-Host '[TODO] BEYIN_OPENAI_MODEL_FAST was not detected; set it to the model identifier shown by the local runtime.'
      continue
    }
    $shown = if ($name -match '(?i:KEY|TOKEN|SECRET)') { '<redacted>' } else { $value }
    if ($DryRun) {
      Write-Host ("[DRYRUN][SETX] {0}={1}" -f $name, $shown)
      continue
    }
    Write-Host ("[SETX] {0}={1}" -f $name, $shown)
    [Environment]::SetEnvironmentVariable($name, $value, 'User')
    Set-Item -Path ("Env:{0}" -f $name) -Value $value
  }
}

function Refresh-ProcessPath {
  $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
  $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
  $env:Path = @($machinePath, $userPath) -join ';'
}

function Install-OllamaRuntime {
  $existing = Get-Command ollama -ErrorAction SilentlyContinue
  if ($existing) {
    Write-Host ("[SKIP] Ollama already found: {0}" -f $existing.Source)
    & $existing.Source --version
    if ($LASTEXITCODE -ne 0) { throw 'ollama --version failed' }
    return
  }

  $winget = Get-Command winget -ErrorAction SilentlyContinue
  if ($winget) {
    Write-Host '[RUN] winget install --id Ollama.Ollama -e --accept-source-agreements --accept-package-agreements'
    & $winget.Source install --id Ollama.Ollama -e --accept-source-agreements --accept-package-agreements
    if ($LASTEXITCODE -ne 0) { throw "winget Ollama install failed with exit code $LASTEXITCODE" }
  } else {
    $installer = Join-Path ([IO.Path]::GetTempPath()) 'OllamaSetup.exe'
    Write-Warning 'winget is absent. The official installer fallback uses commonly reported InnoSetup silent flags that Ollama does not officially document.'
    Write-Warning 'Windows SmartScreen may prompt. Review it; this wizard never bypasses SmartScreen.'
    Write-Host ("[DOWNLOAD] https://ollama.com/download/OllamaSetup.exe -> {0}" -f $installer)
    $previousProgressPreference = $ProgressPreference
    try {
      # Windows PowerShell 5.1 renders progress slowly during large downloads.
      $ProgressPreference = 'SilentlyContinue'
      Invoke-WebRequest -Uri 'https://ollama.com/download/OllamaSetup.exe' -OutFile $installer -UseBasicParsing
    } finally {
      $ProgressPreference = $previousProgressPreference
    }
    Write-Host ("[RUN] {0} /VERYSILENT /SUPPRESSMSGBOXES /NORESTART" -f $installer)
    $process = Start-Process `
      -FilePath $installer `
      -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' `
      -Wait `
      -PassThru
    if ($process.ExitCode -ne 0) {
      throw "Ollama installer failed with exit code $($process.ExitCode)"
    }
  }

  Refresh-ProcessPath
  $command = Get-Command ollama -ErrorAction SilentlyContinue
  if (-not $command) {
    throw 'Ollama installation completed but ollama is still not on refreshed PATH.'
  }
  & $command.Source --version
  if ($LASTEXITCODE -ne 0) { throw 'ollama --version failed after installation' }
  Write-Host '[OK] Ollama installed and verified.'
}

function Invoke-OllamaSetup([object]$Plan) {
  if ($Plan.Backend -ne 'ollama' -or $Plan.Preset -notin @('hybrid', 'local', 'lite')) {
    return
  }
  if ($DryRun) {
    Write-Host '[SKIP] Ollama guided setup disabled in dry-run; no probe, install, or pull.'
    return
  }
  if (-not $Plan.InstallRuntime -and $Plan.PullModels.Count -eq 0) {
    Write-Host '[SKIP] No Ollama runtime install or model pull requested.'
    return
  }

  if ($Plan.InstallRuntime) { Install-OllamaRuntime }
  $ollama = Get-Command ollama -ErrorAction SilentlyContinue
  if (-not $ollama -and $Plan.PullModels.Count -gt 0) {
    throw 'Model pull requested but ollama is not on PATH; set install_runtime=true or install Ollama first.'
  }
  if ($Plan.PullModels.Count -eq 0) { return }

  $probe = Invoke-PythonJson 'donanim.py' @('--json')
  Show-HardwareSummary $probe
  $totalSize = 0.0
  foreach ($tag in $Plan.PullModels) {
    $totalSize += [double]$script:OllamaModels[$tag]
  }
  $diskNeed = [Math]::Round($totalSize * 1.5, 2)
  if ($null -eq $probe.free_disk_gb) {
    throw ("Model pull refused: free disk for {0} could not be determined." -f $probe.model_store)
  }
  if ([double]$probe.free_disk_gb -lt $diskNeed) {
    throw ("Model pull refused: {0:N2} GB free, {1:N2} GB required for {2}." -f [double]$probe.free_disk_gb, $diskNeed, ($Plan.PullModels -join ', '))
  }
  Write-Host ("[OK] Disk preflight: {0:N2} GB free; {1:N2} GB required. Pulls are resumable." -f [double]$probe.free_disk_gb, $diskNeed)
  foreach ($tag in $Plan.PullModels) {
    $size = [double]$script:OllamaModels[$tag]
    Write-Host ("[PULL] ollama pull {0} ({1:N2} GB catalogue size; output follows)" -f $tag, $size)
    & $ollama.Source pull $tag
    if ($LASTEXITCODE -ne 0) { throw "ollama pull $tag failed with exit code $LASTEXITCODE" }
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
  $localAppData = [Environment]::GetEnvironmentVariable('LOCALAPPDATA')
  $standardPath = if ($appData) {
    Join-Path $appData 'Claude\claude_desktop_config.json'
  } else { $null }
  $virtualPath = if ($localAppData) {
    Join-Path $localAppData 'Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\claude_desktop_config.json'
  } else { $null }
  $standardExists = $standardPath -and (Test-Path -LiteralPath $standardPath -PathType Leaf)
  $virtualExists = $virtualPath -and (Test-Path -LiteralPath $virtualPath -PathType Leaf)

  if (-not $standardPath -and -not $virtualExists) {
    Write-Warning 'APPDATA is unavailable and no virtualised Claude Desktop config was found; MCP registration skipped.'
    Show-McpSnippet $Plan.Vault
    return
  }

  $sourcePath = if ($standardExists) { $standardPath } elseif ($virtualExists) { $virtualPath } else { $null }
  if ($sourcePath) {
    try {
      $config = Get-Content -LiteralPath $sourcePath -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch {
      throw "Claude Desktop config is invalid JSON: $sourcePath"
    }
    if ($config -isnot [pscustomobject]) {
      throw "Claude Desktop config root must be an object: $sourcePath"
    }
    if ($standardExists -and $virtualExists) {
      try {
        $secondary = Get-Content -LiteralPath $virtualPath -Raw -Encoding UTF8 | ConvertFrom-Json
      } catch {
        throw "Claude Desktop config is invalid JSON: $virtualPath"
      }
      if ($secondary -isnot [pscustomobject]) {
        throw "Claude Desktop config root must be an object: $virtualPath"
      }
      foreach ($property in $secondary.PSObject.Properties) {
        if ($property.Name -eq 'mcpServers') { continue }
        if ($null -eq $config.PSObject.Properties[$property.Name]) {
          $config | Add-Member -MemberType NoteProperty -Name $property.Name -Value $property.Value
        }
      }
      if ($null -ne $secondary.PSObject.Properties['mcpServers']) {
        Add-NoteProperty $config 'mcpServers' ([pscustomobject]@{})
        if ($secondary.mcpServers -isnot [pscustomobject] -or $config.mcpServers -isnot [pscustomobject]) {
          throw "Claude Desktop config field 'mcpServers' must be an object in both config paths"
        }
        foreach ($server in $secondary.mcpServers.PSObject.Properties) {
          if ($null -eq $config.mcpServers.PSObject.Properties[$server.Name]) {
            $config.mcpServers | Add-Member -MemberType NoteProperty -Name $server.Name -Value $server.Value
          }
        }
      }
    }
  } else {
    $config = [pscustomobject]@{}
    Write-Warning ("Claude Desktop config not found. Creating the standard config; MSIX may instead read: {0}" -f $virtualPath)
  }

  Add-NoteProperty $config 'mcpServers' ([pscustomobject]@{})
  if ($config.mcpServers -isnot [pscustomobject]) {
    throw "Claude Desktop config field 'mcpServers' must be an object: $sourcePath"
  }
  if ($null -eq $config.mcpServers.PSObject.Properties['origin-of-memory']) {
    $config.mcpServers | Add-Member -MemberType NoteProperty -Name 'origin-of-memory' -Value (New-McpEntry $Plan.Vault)
  }

  $targets = @()
  if ($standardExists -or (-not $virtualExists)) { $targets += $standardPath }
  if ($virtualExists) { $targets += $virtualPath }
  $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
  if ($DryRun) {
    foreach ($target in $targets) {
      if (Test-Path -LiteralPath $target -PathType Leaf) {
        Write-Host ("[DRYRUN][BACKUP] {0} -> {0}.bak-{1}" -f $target, $timestamp)
      }
      $verb = if (Test-Path -LiteralPath $target -PathType Leaf) { 'Merge' } else { 'Create' }
      Write-Host ("[DRYRUN][MCP] {0} origin-of-memory in {1}." -f $verb, $target)
    }
    return
  }
  $json = $config | ConvertTo-Json -Depth 100
  foreach ($target in $targets) {
    if (Test-Path -LiteralPath $target -PathType Leaf) {
      $backup = "$target.bak-$timestamp"
      Write-Host ("[BACKUP] {0} -> {1}" -f $target, $backup)
      Copy-Item -LiteralPath $target -Destination $backup
    } else {
      $parent = Split-Path $target -Parent
      if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
      }
    }
    [IO.File]::WriteAllText(
      $target,
      $json + [Environment]::NewLine,
      [Text.UTF8Encoding]::new($false)
    )
    Write-Host ("[MCP] Registered origin-of-memory: {0}" -f $target)
  }
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
      Write-Host '  No automatic capture and no compile. A selected local backend can process imports; use MCP or the clipboard bridge for retrieval.'
      Write-Host '  Otomatik yakalama ve derleme yok. Seçili yerel backend içe aktarımları işleyebilir; erişim için MCP veya pano köprüsünü kullan.'
    }
  }
}

try {
  if ($Answers -and $Recommended) {
    throw '-Answers and -Recommended are mutually exclusive.'
  }
  $rawPlan = if ($Answers) {
    Read-PlanFile $Answers
  } elseif ($Recommended) {
    $bundle = Get-AutoDecision
    $script:AllowEmptyOpenAiModel = $true
    Show-RecommendedConfirmation $bundle.Decision $false | Out-Null
    $script:PlanAlreadyConfirmed = $true
    $bundle.Decision.plan
  } else {
    New-InteractivePlan
  }
  if ($null -eq $rawPlan) {
    Write-Host '[QUIT] No changes were made. (Değişiklik yapılmadı.)'
    exit 0
  }
  $plan = Validate-Plan $rawPlan
  if ($Answers -or -not $script:PlanAlreadyConfirmed) { Show-PlanSummary $plan }
  if ($script:Interactive -and -not $script:PlanAlreadyConfirmed -and -not (Read-YesNo 'Execute this plan? (Bu plan çalıştırılsın mı?)' $true)) {
    throw 'Setup cancelled. (Kurulum iptal edildi.)'
  }
  Invoke-InstallCore $plan
  Invoke-OllamaSetup $plan
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
