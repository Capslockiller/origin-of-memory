# yazan: codex · model: gpt-5.6-sol
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$VaultPath,
  [switch]$DryRun,
  [switch]$Force,
  [string[]]$SkillFilter,
  [switch]$NoSkills,
  [switch]$SkipHookRegistration
)

$ErrorActionPreference = 'Stop'
$script:Planned = 0
$script:Written = 0
$script:Skipped = 0

function Write-Action([string]$Kind, [string]$Message) {
  $script:Planned++
  Write-Host ("[{0}] {1}" -f $Kind, $Message)
}

function Ensure-Directory([string]$Path) {
  if (Test-Path -LiteralPath $Path) { return }
  Write-Action 'CREATE' $Path
  if (-not $DryRun) {
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    $script:Written++
  }
}

function Copy-Tree(
  [string]$Source,
  [string]$Destination,
  [bool]$AllowOverwrite
) {
  foreach ($item in Get-ChildItem -LiteralPath $Source -File -Recurse) {
    $relative = $item.FullName.Substring($Source.Length).TrimStart('\', '/')
    $segments = $relative -split '[\\/]'
    if (
      $segments -contains '__pycache__' -or
      $segments -contains '.state' -or
      $segments -contains '.stage' -or
      $segments -contains '.import' -or
      $item.Extension -in @('.pyc', '.lock', '.db')
    ) {
      continue
    }
    $target = Join-Path $Destination $relative
    $exists = Test-Path -LiteralPath $target
    if ($exists -and -not $AllowOverwrite) {
      Write-Host ("[SKIP] Exists: {0}" -f $target)
      $script:Skipped++
      continue
    }
    $verb = if ($exists) { 'OVERWRITE' } else { 'COPY' }
    Write-Action $verb ("{0} -> {1}" -f $item.FullName, $target)
    if (-not $DryRun) {
      $parent = Split-Path $target -Parent
      if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
      }
      Copy-Item -LiteralPath $item.FullName -Destination $target -Force
      $script:Written++
    }
  }
}

function Add-NoteProperty([object]$Object, [string]$Name, [object]$Value) {
  if ($Object.PSObject.Properties.Name -notcontains $Name) {
    $Object | Add-Member -MemberType NoteProperty -Name $Name -Value $Value
  }
}

function Find-Python {
  $candidate = $null
  if ($env:BEYIN_PYTHON) {
    $command = Get-Command $env:BEYIN_PYTHON -ErrorAction SilentlyContinue
    if ($command) { $candidate = $command.Source }
    elseif (Test-Path -LiteralPath $env:BEYIN_PYTHON -PathType Leaf) {
      $candidate = $env:BEYIN_PYTHON
    }
  }
  if (-not $candidate) {
    $command = Get-Command python -ErrorAction SilentlyContinue
    if ($command) { $candidate = $command.Source }
  }
  if ($candidate) {
    return [pscustomobject]@{ Exe = $candidate; Prefix = @() }
  }
  $command = Get-Command py -ErrorAction SilentlyContinue
  if ($command) {
    return [pscustomobject]@{ Exe = $command.Source; Prefix = @('-3') }
  }
  return $null
}

$repoRoot = $PSScriptRoot
$expandedVault = [Environment]::ExpandEnvironmentVariables($VaultPath)
$vault = [IO.Path]::GetFullPath($expandedVault)
$userRoot = [Environment]::GetFolderPath('UserProfile')
if (-not $userRoot) { $userRoot = $env:USERPROFILE }
if (-not $userRoot) { throw 'The user profile directory could not be resolved.' }
$userClaude = Join-Path $userRoot '.claude'
$settingsPath = Join-Path $userClaude 'settings.json'

if (Test-Path -LiteralPath $vault) {
  if (-not (Test-Path -LiteralPath $vault -PathType Container)) {
    throw "VaultPath is not a directory: $vault"
  }
  Write-Host "[OK] Vault exists: $vault"
} else {
  if (-not $DryRun -and -not $Force) {
    $answer = Read-Host "Vault does not exist. Create '$vault'? [y/N]"
    if ($answer -notmatch '^(?i:y|yes)$') {
      throw 'Installation cancelled.'
    }
  }
  Ensure-Directory $vault
}

$vaultClaude = Join-Path $vault '.claude'
$scriptsTarget = Join-Path $vaultClaude 'scripts'
$hooksTarget = Join-Path $vaultClaude 'hooks'
$skillsTarget = Join-Path $userClaude 'skills'

Copy-Tree (Join-Path $repoRoot 'scripts') $scriptsTarget ([bool]$Force)
Copy-Tree (Join-Path $repoRoot 'hooks') $hooksTarget ([bool]$Force)
$skillsSource = Join-Path $repoRoot 'skills'
if ($NoSkills) {
  Write-Host '[SKIP] No skills selected.'
  $script:Skipped++
} elseif ($null -eq $SkillFilter -or $SkillFilter.Count -eq 0) {
  Copy-Tree $skillsSource $skillsTarget ([bool]$Force)
} else {
  $selectedSkills = @(
    $SkillFilter |
      ForEach-Object { $_ -split ',' } |
      ForEach-Object { $_.Trim() } |
      Where-Object { $_ }
  )
  $availableSkills = @(
    Get-ChildItem -LiteralPath $skillsSource -Directory |
      ForEach-Object { $_.Name }
  )
  foreach ($skillName in $selectedSkills) {
    if ($availableSkills -notcontains $skillName) {
      throw "Unknown SkillFilter entry: $skillName"
    }
  }
  foreach ($skillName in $selectedSkills | Select-Object -Unique) {
    Copy-Tree `
      (Join-Path $skillsSource $skillName) `
      (Join-Path $skillsTarget $skillName) `
      ([bool]$Force)
  }
}
Copy-Tree (Join-Path $repoRoot 'template\vault') $vault $false

$hubSource = Join-Path $repoRoot 'template\hub-config.example.json'
$hubTarget = Join-Path $scriptsTarget 'hub-config.json'
if (Test-Path -LiteralPath $hubTarget) {
  Write-Host "[SKIP] Exists: $hubTarget"
  $script:Skipped++
} else {
  Write-Action 'COPY' ("{0} -> {1}" -f $hubSource, $hubTarget)
  if (-not $DryRun) {
    Ensure-Directory (Split-Path $hubTarget -Parent)
    Copy-Item -LiteralPath $hubSource -Destination $hubTarget
    $script:Written++
  }
}

if ($SkipHookRegistration) {
  Write-Host '[SKIP] Hook registration disabled.'
  $script:Skipped++
} else {
  $settingsExisted = Test-Path -LiteralPath $settingsPath
  if ($settingsExisted) {
    try {
      $settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch {
      throw "Existing settings.json is invalid JSON: $settingsPath"
    }
  } else {
    $settings = [pscustomobject]@{}
  }
  Add-NoteProperty $settings 'hooks' ([pscustomobject]@{})

  $registrations = @(
    [pscustomobject]@{ Event = 'SessionStart'; Script = 'session-start.ps1'; Arguments = ''; Timeout = 15 },
    [pscustomobject]@{ Event = 'UserPromptSubmit'; Script = 'prompt-counter.ps1'; Arguments = ''; Timeout = 5 },
    [pscustomobject]@{ Event = 'UserPromptSubmit'; Script = 'memory-retrieve.ps1'; Arguments = ''; Timeout = 5 },
    [pscustomobject]@{ Event = 'SessionEnd'; Script = 'flush-launch.ps1'; Arguments = ' -Reason sessionend'; Timeout = 15 },
    [pscustomobject]@{ Event = 'SessionEnd'; Script = 'session-end.ps1'; Arguments = ''; Timeout = 10 },
    [pscustomobject]@{ Event = 'PreCompact'; Script = 'flush-launch.ps1'; Arguments = ' -Reason precompact'; Timeout = 15 }
  )
  $addedHooks = 0
  foreach ($registration in $registrations) {
    $eventName = $registration.Event
    Add-NoteProperty $settings.hooks $eventName @()
    $hookPath = Join-Path $hooksTarget $registration.Script
    $commandText = 'powershell -NoProfile -ExecutionPolicy Bypass -File "' + $hookPath + '"' + $registration.Arguments
    $found = $false
    foreach ($group in @($settings.hooks.$eventName)) {
      foreach ($hook in @($group.hooks)) {
        if ($hook.command -eq $commandText) { $found = $true }
      }
    }
    if ($found) {
      Write-Host ("[SKIP] Hook already registered: {0} / {1}" -f $eventName, $registration.Script)
      $script:Skipped++
      continue
    }
    $hookObject = [pscustomobject]@{
      type = 'command'
      command = $commandText
      timeout = $registration.Timeout
    }
    $groupObject = [pscustomobject]@{ hooks = @($hookObject) }
    $settings.hooks.$eventName = @($settings.hooks.$eventName) + @($groupObject)
    Write-Action 'REGISTER' ("{0}: {1}" -f $eventName, $commandText)
    $addedHooks++
  }

  if ($addedHooks -gt 0 -and -not $DryRun) {
    Ensure-Directory $userClaude
    if ($settingsExisted) {
      $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
      $backup = "$settingsPath.bak-$timestamp"
      Write-Action 'BACKUP' ("{0} -> {1}" -f $settingsPath, $backup)
      Copy-Item -LiteralPath $settingsPath -Destination $backup
      $script:Written++
    }
    $json = $settings | ConvertTo-Json -Depth 100
    [IO.File]::WriteAllText($settingsPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    $script:Written++
  } elseif ($addedHooks -gt 0 -and $DryRun -and $settingsExisted) {
    Write-Action 'BACKUP' ("{0} -> {0}.bak-<timestamp>" -f $settingsPath)
  }
}

$python = Find-Python
if (-not $python) {
  Write-Warning 'Python was not found. Set BEYIN_PYTHON or install Python 3.12+.'
} else {
  $versionOutput = & $python.Exe @($python.Prefix) --version 2>&1 | Out-String
  if ($versionOutput -match 'Python\s+(\d+)\.(\d+)') {
    $major = [int]$Matches[1]
    $minor = [int]$Matches[2]
    if ($major -lt 3 -or ($major -eq 3 -and $minor -lt 12)) {
      Write-Warning "Python 3.12+ is required; found $($versionOutput.Trim())."
    } else {
      Write-Host "[OK] $($versionOutput.Trim())"
    }
  } else {
    Write-Warning 'Python version could not be determined.'
  }
}

if ($SkipHookRegistration) {
  Write-Host '[SKIP] claude CLI check disabled with hook registration.'
} elseif (Get-Command claude -ErrorAction SilentlyContinue) {
  Write-Host '[OK] claude CLI found on PATH.'
} else {
  Write-Warning 'claude CLI was not found on PATH.'
}

Write-Host ''
Write-Host 'Installation summary'
Write-Host ("  Vault: {0}" -f $vault)
Write-Host ("  Mode: {0}" -f $(if ($DryRun) { 'DRY RUN' } else { 'WRITE' }))
Write-Host ("  Planned actions: {0}" -f $script:Planned)
Write-Host ("  Writes completed: {0}" -f $script:Written)
Write-Host ("  Existing items skipped: {0}" -f $script:Skipped)
exit 0
