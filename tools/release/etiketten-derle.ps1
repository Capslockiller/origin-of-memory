[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$Tag = "v3.1.0"
)

# F9-1: the release exe is built only from a clean tree whose HEAD is exactly at the tag,
# and only reported as built when its own `oom --version` names that commit.

$ErrorActionPreference = "Stop"
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

function Stop-Derleme {
    param([string]$Message)
    [Console]::Error.WriteLine("etiketten-derle: $Message")
    exit 1
}

function Get-TamYol {
    param([string]$Path)
    $full = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine((Get-Location -PSProvider FileSystem).ProviderPath, $Path))
    $root = [System.IO.Path]::GetPathRoot($full)
    if ($full.Length -gt $root.Length) { $full = $full.TrimEnd('\', '/') }
    return $full
}

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Join-Path $PSScriptRoot "../.."
}
$RepoRoot = Get-TamYol $RepoRoot

# git must answer for THIS directory, not for a repository somewhere above it.
$topLevel = & git -C $RepoRoot rev-parse --show-toplevel
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($topLevel)) {
    Stop-Derleme "git deposu değil: $RepoRoot"
}
if (-not [string]::Equals((Get-TamYol $topLevel.Trim()), $RepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    Stop-Derleme "git deposunun kökü değil: $RepoRoot (kök: $($topLevel.Trim()))"
}

$status = & git -C $RepoRoot status --porcelain
if ($LASTEXITCODE -ne 0) { Stop-Derleme "git status başarısız." }
if (-not [string]::IsNullOrWhiteSpace(($status -join ""))) {
    Stop-Derleme "ağaç temiz değil (git status --porcelain boş değil):`n$($status -join "`n")"
}

$head = & git -C $RepoRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) { Stop-Derleme "HEAD okunamadı." }
$tagCommit = & git -C $RepoRoot rev-parse --verify --quiet "refs/tags/$Tag^{commit}"
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($tagCommit)) { Stop-Derleme "$Tag etiketi yok." }
$head = $head.Trim()
$tagCommit = $tagCommit.Trim()
if ($head -ne $tagCommit) {
    Stop-Derleme "HEAD ($head) $Tag etiketinde ($tagCommit) değil."
}

$project = Join-Path $RepoRoot "src/Oom/Oom.csproj"

# The tree passed "git status --porcelain" clean above, but bin/ and obj/ are gitignored —
# an incremental build into them can still carry stale DLLs or runtimes/ entries from an
# earlier, dirty build. kur-3.1.0.ps1 copies this output wholesale into the live bin, so the
# build must start from nothing rather than reuse whatever is already there.
foreach ($stale in @((Join-Path $RepoRoot "src/Oom/bin"), (Join-Path $RepoRoot "src/Oom/obj"))) {
    if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -Recurse -Force }
}

& dotnet build $project -c Release --no-incremental --disable-build-servers
if ($LASTEXITCODE -ne 0) { Stop-Derleme "dotnet build başarısız (çıkış $LASTEXITCODE)." }

$exe = Join-Path $RepoRoot "src/Oom/bin/Release/net9.0/oom.exe"
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { Stop-Derleme "derleme oom.exe üretmedi: $exe" }

$version = (& $exe --version) -join "`n"
if ($LASTEXITCODE -ne 0) { Stop-Derleme "oom --version başarısız (çıkış $LASTEXITCODE)." }
$match = [System.Text.RegularExpressions.Regex]::Match($version, '\+([0-9a-fA-F]{7,40})\b')
if (-not $match.Success) {
    Stop-Derleme "oom --version bir commit taşımıyor (git rev-parse derlemede başarısız)."
}
$builtCommit = $match.Groups[1].Value.ToLowerInvariant()
if (-not $head.StartsWith($builtCommit)) {
    Stop-Derleme "oom --version commit'i ($builtCommit) HEAD ($head) değil."
}

$status = & git -C $RepoRoot status --porcelain
if ($LASTEXITCODE -ne 0 -or -not [string]::IsNullOrWhiteSpace(($status -join ""))) {
    Stop-Derleme "derleme ağacı kirletti:`n$($status -join "`n")"
}

Write-Output "etiketten-derle: $Tag ($head) temiz ağaçtan derlendi."
Write-Output "exe: $exe"
Write-Output "sürüm: $version"
exit 0
