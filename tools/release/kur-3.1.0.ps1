[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Vault,
    [Parameter(Mandatory = $true)][string]$Source,
    [Alias("Home")][string]$HomeDir,
    [switch]$Canli
)

# F9-3/F9-4/B5: copies a built bin into <vault>/.oom/bin (staged beside it, then swapped),
# keeps exactly one previous bin as bin.onceki, and never writes hooks, oom.json or
# scheduled tasks. Hook files under -Home are only read, to report which exe they call.

$ErrorActionPreference = "Stop"
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

function Stop-Kurulum {
    param([string]$Message)
    [Console]::Error.WriteLine("kur-3.1.0: $Message")
    exit 1
}

function Get-TamYol {
    param([string]$Path)
    if ($Path.StartsWith('\\?\')) { $Path = $Path.Substring(4) }
    $full = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine((Get-Location -PSProvider FileSystem).ProviderPath, $Path))
    $root = [System.IO.Path]::GetPathRoot($full)
    if ($full.Length -gt $root.Length) { $full = $full.TrimEnd('\', '/') }
    return $full
}

function Test-CanliKasa {
    param([string]$Path)
    try {
        $guards = [System.Collections.Generic.List[string]]::new()
        $guards.Add((Get-TamYol "E:\OdenaOS"))
        if (-not [string]::IsNullOrWhiteSpace($env:OOM_RELEASE_GUARD_VAULT)) {
            $guards.Add((Get-TamYol $env:OOM_RELEASE_GUARD_VAULT))
        }
        $candidates = [System.Collections.Generic.List[string]]::new()
        $candidates.Add($Path)
        $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and $null -ne $item.Target) {
            foreach ($target in @($item.Target)) {
                if ([System.IO.Path]::IsPathRooted($target)) {
                    $candidates.Add((Get-TamYol $target))
                }
                else {
                    $candidates.Add((Get-TamYol ([System.IO.Path]::Combine((Split-Path -Path $Path -Parent), $target))))
                }
            }
        }
        foreach ($candidate in $candidates) {
            foreach ($guard in $guards) {
                if ([string]::Equals($candidate, $guard, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
            }
        }
        return $false
    }
    catch {
        Stop-Kurulum "canlı-kasa yolu çözülemedi, güvenli taraf seçildi (kurulum reddedildi): $Path ($($_.Exception.Message))"
    }
}

function Assert-Kilitsiz {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    try {
        $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        $stream.Dispose()
    }
    catch {
        Stop-Kurulum "oom.exe kilitli (çalışan bir oom süreci olabilir), hiçbir şey değişmedi: $Path"
    }
}

function Copy-Dizin {
    param([string]$From, [string]$To)
    [System.IO.Directory]::CreateDirectory($To) | Out-Null
    foreach ($dir in [System.IO.Directory]::GetDirectories($From)) {
        Copy-Dizin $dir (Join-Path $To ([System.IO.Path]::GetFileName($dir)))
    }
    foreach ($file in [System.IO.Directory]::GetFiles($From)) {
        [System.IO.File]::Copy($file, (Join-Path $To ([System.IO.Path]::GetFileName($file))), $false)
    }
}

# SHA-256 through .NET, not Get-FileHash: Windows PowerShell started from PowerShell 7
# (as on CI runners) inherits a PSModulePath that hides Microsoft.PowerShell.Utility.
function Get-Sha256 {
    param([string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try { return ([System.BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
}

function Get-DizinOzeti {
    param([string]$Root)
    Get-ChildItem -LiteralPath $Root -Recurse -File -Force |
        ForEach-Object { $_.FullName.Substring($Root.Length).TrimStart('\') + ":" + (Get-Sha256 -Path $_.FullName) } |
        Sort-Object
}

function Get-KancaExeleri {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return @() }
    $json = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -eq $json.hooks) { return @() }
    $exes = @()
    foreach ($hookEvent in $json.hooks.PSObject.Properties) {
        foreach ($entry in @($hookEvent.Value)) {
            foreach ($hook in @($entry.hooks)) {
                $command = [string]$hook.command
                # HookTemplates quotes "<exe> --vault <vault>" together when a path has a space.
                $match = [System.Text.RegularExpressions.Regex]::Match($command, '^"?(.+?oom\.exe)(?=[\s"]|$)', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
                if ($match.Success) { $exes += $match.Groups[1].Value }
            }
        }
    }
    return $exes
}

$Vault = Get-TamYol $Vault
$Source = Get-TamYol $Source
if ([string]::IsNullOrWhiteSpace($HomeDir)) { $HomeDir = $env:USERPROFILE }

if ((Test-CanliKasa $Vault) -and -not $Canli) {
    Stop-Kurulum "hedef canlı kasa ($Vault); canlı kurulum yalnız -Canli ile yapılır. Hiçbir şey yazılmadı."
}

$oomDir = Join-Path $Vault ".oom"
$bin = Join-Path $oomDir "bin"
$onceki = Join-Path $oomDir "bin.onceki"
$yeni = Join-Path $oomDir "bin.yeni"
$eski = Join-Path $oomDir "bin.eski"

if (-not (Test-Path -LiteralPath $oomDir -PathType Container)) { Stop-Kurulum "OoM kasası değil (.oom yok): $Vault" }
if (-not (Test-Path -LiteralPath (Join-Path $Source "oom.exe") -PathType Leaf)) { Stop-Kurulum "kaynakta oom.exe yok: $Source" }
foreach ($reserved in @($bin, $onceki, $yeni, $eski)) {
    if ([string]::Equals($Source, $reserved, [System.StringComparison]::OrdinalIgnoreCase)) { Stop-Kurulum "kaynak kurulum dizinlerinden biri olamaz: $Source" }
}
if (Test-Path -LiteralPath $eski) { Stop-Kurulum "yarım kalmış bir kurulum var, önce elle incele: $eski" }

# F9-4, before any write: hooks that already point at a DIFFERENT oom.exe are the safety
# property this installer exists to protect — reject up front, nothing written, rather than
# swap the bins and only warn afterwards.
$expectedExe = Join-Path $bin "oom.exe"
foreach ($hookFile in @((Join-Path $HomeDir ".claude\settings.json"), (Join-Path $HomeDir ".codex\hooks.json"))) {
    try { $exes = @(Get-KancaExeleri $hookFile) }
    catch { Write-Warning "kanca dosyası okunamadı: $hookFile ($($_.Exception.Message))"; continue }
    if ($exes.Count -eq 0) {
        Write-Warning "kanca: $hookFile — hiç oom kancası bulunamadı (0 komut)"
        continue
    }
    $other = @($exes | Where-Object { -not [string]::Equals((Get-TamYol $_), $expectedExe, [System.StringComparison]::OrdinalIgnoreCase) } | Sort-Object -Unique)
    if ($other.Count -gt 0) {
        Stop-Kurulum "kanca: $hookFile başka exe çağırıyor: $($other -join ', ') (beklenen: $expectedExe); hiçbir şey yazılmadı."
    }
    Write-Output "kanca: $hookFile — $($exes.Count) komut, hepsi $expectedExe"
}

Assert-Kilitsiz (Join-Path $bin "oom.exe")

# Stage beside bin so the swap is two renames on the same volume.
if (Test-Path -LiteralPath $yeni) { Remove-Item -LiteralPath $yeni -Recurse -Force }
Copy-Dizin $Source $yeni
$sourceSummary = @(Get-DizinOzeti $Source)
if ((@(Get-DizinOzeti $yeni) -join "`n") -ne ($sourceSummary -join "`n")) {
    Remove-Item -LiteralPath $yeni -Recurse -Force
    Stop-Kurulum "hazırlanan kopya kaynakla aynı değil; kurulum yapılmadı."
}

$hadBin = Test-Path -LiteralPath $bin
if ($hadBin) {
    try { [System.IO.Directory]::Move($bin, $eski) }
    catch {
        Remove-Item -LiteralPath $yeni -Recurse -Force
        Stop-Kurulum "mevcut bin taşınamadı (kilitli olabilir), hiçbir şey değişmedi: $($_.Exception.Message)"
    }
}
try { [System.IO.Directory]::Move($yeni, $bin) }
catch {
    $yeniHata = $_.Exception.Message
    if ($hadBin) {
        try { [System.IO.Directory]::Move($eski, $bin) }
        catch { Stop-Kurulum "yeni bin yerine konamadı VE eski bin geri konamadı; bin yok, elle geri koy: $eski -> $bin ($($_.Exception.Message))" }
    }
    Stop-Kurulum "yeni bin yerine konamadı, eski bin geri kondu: $yeniHata"
}
if ($hadBin) {
    # Atomic-ish rotation: rename the old bin.onceki out of the way and move the just-
    # replaced generation into bin.onceki BEFORE deleting anything, so a Remove-Item
    # failure (locked file, PS 5.1 Remove-Item -Recurse flakiness) never leaves bin.onceki
    # half-destroyed. The new bin is already in place at this point either way.
    $sil = Join-Path $oomDir "bin.onceki.sil"
    if (Test-Path -LiteralPath $sil) {
        try { [System.IO.Directory]::Delete($sil, $true) }
        catch { Stop-Kurulum "önceki temizlik artığı silinemedi, elle sil: $sil ($($_.Exception.Message))" }
    }
    if (Test-Path -LiteralPath $onceki) {
        try { [System.IO.Directory]::Move($onceki, $sil) }
        catch { Stop-Kurulum "bin.onceki bir kenara taşınamadı (yeni bin ve eski nesil yerinde, yalnız rotasyon yarım kaldı): $onceki -> $sil ($($_.Exception.Message))" }
    }
    try { [System.IO.Directory]::Move($eski, $onceki) }
    catch { Stop-Kurulum "eski nesil bin.onceki yerine konamadı (yeni bin yerinde): $eski -> $onceki ($($_.Exception.Message))" }
    if (Test-Path -LiteralPath $sil) {
        try { [System.IO.Directory]::Delete($sil, $true) }
        catch { Write-Warning "en eski nesil silinemedi (yeni bin ve bin.onceki yerinde, yalnız temizlik başarısız), elle sil: $sil ($($_.Exception.Message))" }
    }
}

Write-Output "kur-3.1.0: kuruldu: $bin ($($sourceSummary.Count) dosya)"
if ($hadBin) { Write-Output "kur-3.1.0: önceki sürüm: $onceki (geri almak için geri-al-3.1.0.ps1)" }

# Cut/A3-11: report any stale backup piles left by the old manual pattern; this installer
# never creates them, and deleting them is outside this script's job (Master's own call).
$staleNames = @()
if (Test-Path -LiteralPath $oomDir -PathType Container) {
    $staleNames += @(Get-ChildItem -LiteralPath $oomDir -Directory -Filter "bin.bak-*" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
    if (Test-Path -LiteralPath (Join-Path $oomDir "bin-yedek")) { $staleNames += "bin-yedek" }
    if (Test-Path -LiteralPath (Join-Path $oomDir "kit.eski")) { $staleNames += "kit.eski" }
}
if ($staleNames.Count -gt 0) {
    Write-Output "kur-3.1.0: eski yedek izleri bulundu ($($staleNames.Count)): $($staleNames -join ', ') — silmek Master'ın işi"
}

exit 0
