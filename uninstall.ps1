# yazan: codex · model: gpt-5.6-sol
<#
.SYNOPSIS
Safely reverses origin-of-memory registrations and optionally copied runtime files.

.DESCRIPTION
The -Answers JSON contract is:
{
  "vault": "<path>",
  "remove_scripts": false,
  "remove_hooks": false,
  "remove_skills": false
}

Hook registrations and both Claude Desktop MCP locations are always checked.
The three copied-file groups are removed only after their separate approval.
Every edited or removed file is backed up. Vault memory content is never touched.
#>
[CmdletBinding()]
param(
  [string]$Answers,
  [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
if ($env:BEYIN_INVOKED_BY) { exit 0 }
$script:RepoRoot = $PSScriptRoot
$script:Timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$script:Removed = 0
$script:Planned = 0

function Fail-Field([string]$Field, [string]$Message) {
  throw ("field '{0}': {1}" -f $Field, $Message)
}

function Has-Property([object]$Object, [string]$Name) {
  return $null -ne $Object.PSObject.Properties[$Name]
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
    $full = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Value.Trim()))
  } catch {
    Fail-Field 'vault' 'is not a valid path'
  }
  if (-not (Test-Path -LiteralPath $full -PathType Container)) {
    Fail-Field 'vault' 'must be an existing directory'
  }
  $repo = [IO.Path]::GetFullPath($script:RepoRoot).TrimEnd('\', '/')
  $candidate = $full.TrimEnd('\', '/')
  if (
    $candidate.Equals($repo, [StringComparison]::OrdinalIgnoreCase) -or
    $candidate.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
  ) {
    Fail-Field 'vault' 'must not be inside the repository'
  }
  return $full
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

function New-InteractivePlan {
  Write-Host 'origin-of-memory uninstall (kaldırma)'
  $vault = (Read-Host 'Installed vault path (Kurulu vault yolu)').Trim()
  return [pscustomobject]@{
    vault = $vault
    remove_scripts = Read-YesNo 'Remove copied .claude/scripts files? (Kopyalanan script dosyaları kaldırılsın mı?)' $false
    remove_hooks = Read-YesNo 'Remove copied .claude/hooks files? (Kopyalanan hook dosyaları kaldırılsın mı?)' $false
    remove_skills = Read-YesNo 'Remove this project''s copied skills? (Bu projenin kopyalanan becerileri kaldırılsın mı?)' $false
  }
}

function Validate-Plan([object]$Raw) {
  if ($null -eq $Raw -or $Raw -is [array] -or $Raw -is [string]) {
    Fail-Field 'plan' 'must be a JSON object'
  }
  $fields = @('vault', 'remove_scripts', 'remove_hooks', 'remove_skills')
  foreach ($field in $fields) {
    if (-not (Has-Property $Raw $field)) { Fail-Field $field 'is required' }
  }
  foreach ($property in $Raw.PSObject.Properties.Name) {
    if ($fields -notcontains $property) { Fail-Field $property 'is not part of the plan schema' }
  }
  foreach ($field in @('remove_scripts', 'remove_hooks', 'remove_skills')) {
    if ($Raw.$field -isnot [bool]) { Fail-Field $field 'must be true or false' }
  }
  return [pscustomobject]@{
    Vault = Resolve-VaultPath ([string]$Raw.vault)
    RemoveScripts = [bool]$Raw.remove_scripts
    RemoveHooks = [bool]$Raw.remove_hooks
    RemoveSkills = [bool]$Raw.remove_skills
  }
}

function Get-UserClaudeRoot {
  # USERPROFILE first: GetFolderPath ignores the environment override, which
  # silently points a redirected run at the real profile.
  $profile = $env:USERPROFILE
  if (-not $profile) { $profile = [Environment]::GetFolderPath('UserProfile') }
  if (-not $profile) { throw 'The user profile directory could not be resolved.' }
  return Join-Path $profile '.claude'
}

function Backup-EditedFile([string]$Path) {
  $backup = "$Path.bak-$script:Timestamp"
  if ($DryRun) {
    Write-Host ("[DRYRUN][BACKUP] {0} -> {1}" -f $Path, $backup)
  } else {
    Write-Host ("[BACKUP] {0} -> {1}" -f $Path, $backup)
    Copy-Item -LiteralPath $Path -Destination $backup
  }
  return $backup
}

function Write-JsonFile([string]$Path, [object]$Value) {
  $json = $Value | ConvertTo-Json -Depth 100
  [IO.File]::WriteAllText(
    $Path,
    $json + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false)
  )
}

function Remove-HookRegistrations([string]$Vault, [string]$UserClaude) {
  $settingsPath = Join-Path $UserClaude 'settings.json'
  if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    Write-Host '[SKIP] Claude Code settings.json not found.'
    return
  }
  try {
    $settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
  } catch {
    throw "Claude Code settings.json is invalid JSON: $settingsPath"
  }
  if ($null -eq $settings.PSObject.Properties['hooks']) {
    Write-Host '[SKIP] No hook registrations found.'
    return
  }

  $hooksTarget = Join-Path $Vault '.claude\hooks'
  $registrations = @(
    [pscustomobject]@{ Event = 'SessionStart'; Script = 'session-start.ps1'; Arguments = '' },
    [pscustomobject]@{ Event = 'UserPromptSubmit'; Script = 'prompt-counter.ps1'; Arguments = '' },
    [pscustomobject]@{ Event = 'UserPromptSubmit'; Script = 'memory-retrieve.ps1'; Arguments = '' },
    [pscustomobject]@{ Event = 'SessionEnd'; Script = 'flush-launch.ps1'; Arguments = ' -Reason sessionend' },
    [pscustomobject]@{ Event = 'SessionEnd'; Script = 'session-end.ps1'; Arguments = '' },
    [pscustomobject]@{ Event = 'PreCompact'; Script = 'flush-launch.ps1'; Arguments = ' -Reason precompact' }
  )
  $expected = @{}
  foreach ($registration in $registrations) {
    $hookPath = Join-Path $hooksTarget $registration.Script
    $command = 'powershell -NoProfile -ExecutionPolicy Bypass -File "' + $hookPath + '"' + $registration.Arguments
    $expected["$($registration.Event)|$command"] = $true
  }

  $removed = 0
  foreach ($eventProperty in @($settings.hooks.PSObject.Properties)) {
    $eventName = $eventProperty.Name
    $newGroups = @()
    foreach ($group in @($eventProperty.Value)) {
      $newHooks = @()
      foreach ($hook in @($group.hooks)) {
        if ($expected.ContainsKey("$eventName|$($hook.command)")) {
          Write-Host ("{0} {1}: {2}" -f $(if ($DryRun) { '[DRYRUN][UNREGISTER]' } else { '[UNREGISTER]' }), $eventName, $hook.command)
          $removed++
        } else {
          $newHooks += $hook
        }
      }
      if ($newHooks.Count -gt 0) {
        $group.hooks = $newHooks
        $newGroups += $group
      }
    }
    $settings.hooks.$eventName = $newGroups
  }
  if ($removed -eq 0) {
    Write-Host '[SKIP] None of the six origin-of-memory hook commands were registered.'
    return
  }
  Backup-EditedFile $settingsPath | Out-Null
  if (-not $DryRun) { Write-JsonFile $settingsPath $settings }
  $script:Planned += $removed
  if (-not $DryRun) { $script:Removed += $removed }
}

function Remove-McpEntries {
  $appData = [Environment]::GetEnvironmentVariable('APPDATA')
  $localAppData = [Environment]::GetEnvironmentVariable('LOCALAPPDATA')
  $paths = @()
  if ($appData) { $paths += Join-Path $appData 'Claude\claude_desktop_config.json' }
  if ($localAppData) {
    $paths += Join-Path $localAppData 'Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\claude_desktop_config.json'
  }
  foreach ($path in $paths | Select-Object -Unique) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    try {
      $config = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch {
      throw "Claude Desktop config is invalid JSON: $path"
    }
    if (
      $null -eq $config.PSObject.Properties['mcpServers'] -or
      $null -eq $config.mcpServers.PSObject.Properties['origin-of-memory']
    ) {
      Write-Host ("[SKIP] MCP entry absent: {0}" -f $path)
      continue
    }
    Write-Host ("{0} origin-of-memory from {1}" -f $(if ($DryRun) { '[DRYRUN][MCP-REMOVE]' } else { '[MCP-REMOVE]' }), $path)
    $config.mcpServers.PSObject.Properties.Remove('origin-of-memory')
    Backup-EditedFile $path | Out-Null
    if (-not $DryRun) { Write-JsonFile $path $config; $script:Removed++ }
    $script:Planned++
  }
}

function Assert-TargetUnderRoot([string]$Target, [string]$Root) {
  $fullTarget = [IO.Path]::GetFullPath($Target)
  $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
  if (-not $fullTarget.StartsWith($fullRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing target outside approved root: $fullTarget"
  }
}

function Backup-And-RemoveCopiedFile(
  [string]$Target,
  [string]$ApprovedRoot,
  [string]$BackupRoot,
  [string]$Relative
) {
  Assert-TargetUnderRoot $Target $ApprovedRoot
  if (-not (Test-Path -LiteralPath $Target -PathType Leaf)) { return }
  $backup = Join-Path $BackupRoot $Relative
  if ($DryRun) {
    Write-Host ("[DRYRUN][BACKUP] {0} -> {1}" -f $Target, $backup)
    Write-Host ("[DRYRUN][REMOVE] {0}" -f $Target)
  } else {
    $parent = Split-Path $backup -Parent
    if (-not (Test-Path -LiteralPath $parent)) {
      New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    Copy-Item -LiteralPath $Target -Destination $backup
    Remove-Item -LiteralPath $Target -Force
    Write-Host ("[REMOVE] {0}; backup: {1}" -f $Target, $backup)
    $script:Removed++
  }
  $script:Planned++
}

function Remove-CopiedTree(
  [string]$Source,
  [string]$TargetRoot,
  [string]$BackupRoot,
  [string]$Bucket
) {
  if (-not (Test-Path -LiteralPath $Source -PathType Container)) { return }
  foreach ($item in Get-ChildItem -LiteralPath $Source -File -Recurse) {
    $relative = $item.FullName.Substring($Source.Length).TrimStart('\', '/')
    $segments = $relative -split '[\\/]'
    if (
      $segments -contains '__pycache__' -or
      $segments -contains '.state' -or
      $segments -contains '.stage' -or
      $segments -contains '.import' -or
      $item.Extension -in @('.pyc', '.lock', '.db')
    ) { continue }
    Backup-And-RemoveCopiedFile `
      (Join-Path $TargetRoot $relative) `
      $TargetRoot `
      $BackupRoot `
      (Join-Path $Bucket $relative)
  }
}

function Remove-ApprovedCopies([object]$Plan, [string]$UserClaude) {
  $backupRoot = Join-Path $UserClaude ("origin-of-memory-uninstall-backups\{0}" -f $script:Timestamp)
  if ($Plan.RemoveScripts) {
    $target = Join-Path $Plan.Vault '.claude\scripts'
    Remove-CopiedTree (Join-Path $script:RepoRoot 'scripts') $target $backupRoot 'scripts'
    $hubConfig = Join-Path $target 'hub-config.json'
    Backup-And-RemoveCopiedFile $hubConfig $target $backupRoot 'scripts\hub-config.json'
  } else {
    Write-Host '[SKIP] Copied scripts were not approved for removal.'
  }
  if ($Plan.RemoveHooks) {
    Remove-CopiedTree `
      (Join-Path $script:RepoRoot 'hooks') `
      (Join-Path $Plan.Vault '.claude\hooks') `
      $backupRoot `
      'hooks'
  } else {
    Write-Host '[SKIP] Copied hooks were not approved for removal.'
  }
  if ($Plan.RemoveSkills) {
    $skillsRoot = Join-Path $UserClaude 'skills'
    foreach ($skillDirectory in Get-ChildItem -LiteralPath (Join-Path $script:RepoRoot 'skills') -Directory) {
      Remove-CopiedTree `
        $skillDirectory.FullName `
        (Join-Path $skillsRoot $skillDirectory.Name) `
        $backupRoot `
        (Join-Path 'skills' $skillDirectory.Name)
    }
  } else {
    Write-Host '[SKIP] Copied skills were not approved for removal.'
  }
}

try {
  $rawPlan = if ($Answers) { Read-PlanFile $Answers } else { New-InteractivePlan }
  $plan = Validate-Plan $rawPlan
  $userClaude = Get-UserClaudeRoot
  Write-Host ("Uninstall target: {0}" -f $plan.Vault)
  Write-Host ("Mode: {0}" -f $(if ($DryRun) { 'DRY RUN' } else { 'WRITE' }))
  Remove-HookRegistrations $plan.Vault $userClaude
  Remove-McpEntries
  Remove-ApprovedCopies $plan $userClaude
  Write-Host ''
  Write-Host '[PRESERVED] Vault memory content was not touched: daily/, knowledge/, companion files, and other vault content remain in place.'
  Write-Host ("[DONE] mode={0} planned={1} removed={2}" -f $(if ($DryRun) { 'dry-run' } else { 'write' }), $script:Planned, $script:Removed)
  exit 0
} catch {
  Write-Error ("[FAILED] {0}" -f $_.Exception.Message)
  exit 1
}
