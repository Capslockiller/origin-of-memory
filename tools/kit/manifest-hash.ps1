[CmdletBinding()]
param(
    [string]$KitRoot
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($KitRoot)) {
    $KitRoot = Join-Path $PSScriptRoot "../../kit"
}

function Test-ReparsePoint {
    param([System.IO.FileSystemInfo]$Item)

    return ($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
}

function Assert-NoLinkedPath {
    param(
        [string]$Path,
        [string]$Root
    )

    $current = $Path
    while ($true) {
        $item = Get-Item -LiteralPath $current -Force
        if (Test-ReparsePoint $item) {
            throw "Bağlantı izlenmez: $current"
        }

        if ([string]::Equals($current, $Root, [System.StringComparison]::OrdinalIgnoreCase)) {
            return
        }

        $parent = [System.IO.Directory]::GetParent($current)
        if ($null -eq $parent) {
            throw "Kit köküne ulaşılamadı: $Path"
        }
        $current = $parent.FullName
    }
}

function Get-PathRelativeToRoot {
    param(
        [string]$Root,
        [string]$Path
    )

    if ([string]::Equals($Path, $Root, [System.StringComparison]::OrdinalIgnoreCase)) {
        return "."
    }

    $trimChars = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $prefix = $Root.TrimEnd($trimChars) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $Path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Yol kit kökünün dışında: $Path"
    }

    return $Path.Substring($prefix.Length)
}

function Get-FilesNoLinks {
    param([string]$Root)

    $relativePaths = [System.Collections.Generic.List[string]]::new()
    $pending = [System.Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)

    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
            if (Test-ReparsePoint $entry) {
                throw "Bağlantı izlenmez: $($entry.FullName)"
            }

            if ($entry.PSIsContainer) {
                $pending.Push($entry.FullName)
            }
            else {
                $relative = (Get-PathRelativeToRoot $Root $entry.FullName).Replace("\", "/")
                $relativePaths.Add($relative)
            }
        }
    }

    $result = $relativePaths.ToArray()
    [System.Array]::Sort($result, [System.StringComparer]::Ordinal)
    return $result
}

function Resolve-ComponentPath {
    param(
        [string]$Root,
        [string]$RelativePath
    )

    if ([System.IO.Path]::IsPathRooted($RelativePath)) {
        throw "Mutlak bileşen yolu: $RelativePath"
    }

    $fullPath = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($Root, $RelativePath))
    Get-PathRelativeToRoot $Root $fullPath | Out-Null

    return $fullPath
}

function Format-Json {
    param([string]$Json)

    $builder = [System.Text.StringBuilder]::new()
    $indent = 0
    $inString = $false
    $escaped = $false

    for ($index = 0; $index -lt $Json.Length; $index++) {
        $character = $Json[$index]
        if ($inString) {
            [void]$builder.Append($character)
            if ($escaped) {
                $escaped = $false
            }
            elseif ($character -eq "\") {
                $escaped = $true
            }
            elseif ($character -eq '"') {
                $inString = $false
            }
            continue
        }

        switch ($character) {
            '"' {
                $inString = $true
                [void]$builder.Append($character)
            }
            { $_ -eq '{' -or $_ -eq '[' } {
                [void]$builder.Append($character)
                $closing = if ($character -eq '{') { '}' } else { ']' }
                if ($index + 1 -lt $Json.Length -and $Json[$index + 1] -ne $closing) {
                    $indent++
                    [void]$builder.Append("`r`n")
                    [void]$builder.Append(' ' * ($indent * 2))
                }
            }
            { $_ -eq '}' -or $_ -eq ']' } {
                $opening = if ($character -eq '}') { '{' } else { '[' }
                if ($index -gt 0 -and $Json[$index - 1] -ne $opening) {
                    $indent--
                    [void]$builder.Append("`r`n")
                    [void]$builder.Append(' ' * ($indent * 2))
                }
                [void]$builder.Append($character)
            }
            ',' {
                [void]$builder.Append(",`r`n")
                [void]$builder.Append(' ' * ($indent * 2))
            }
            ':' {
                [void]$builder.Append(": ")
            }
            default {
                if (-not [char]::IsWhiteSpace($character)) {
                    [void]$builder.Append($character)
                }
            }
        }
    }

    return $builder.ToString()
}

$resolvedKitRoot = (Resolve-Path -LiteralPath $KitRoot).Path
$manifestPath = Join-Path $resolvedKitRoot "manifest.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json

foreach ($component in $manifest.components) {
    $source = Resolve-ComponentPath $resolvedKitRoot ([string]$component.path)

    if ($component.kind -eq "skill") {
        if (-not (Test-Path -LiteralPath $source -PathType Container)) {
            throw "Skill kaynağı bulunamadı: $($component.path)"
        }

        Assert-NoLinkedPath $source $resolvedKitRoot

        $hashes = [ordered]@{}
        foreach ($relative in Get-FilesNoLinks $source) {
            $fullPath = Join-Path $source ($relative.Replace("/", [System.IO.Path]::DirectorySeparatorChar))
            $hashes[$relative] = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        $sha256 = [pscustomobject]$hashes
    }
    else {
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Dosya kaynağı bulunamadı: $($component.path)"
        }

        Assert-NoLinkedPath $source $resolvedKitRoot

        $sha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    $component | Add-Member -NotePropertyName "sha256" -NotePropertyValue $sha256 -Force
}

$json = Format-Json ($manifest | ConvertTo-Json -Depth 100 -Compress)
[System.IO.File]::WriteAllText(
    $manifestPath,
    $json + [System.Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))
