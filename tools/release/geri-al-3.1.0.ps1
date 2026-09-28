[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Vault,
    [switch]$Canli
)

# F9-3: swaps <vault>/.oom/bin.onceki back into bin; the replaced bin becomes bin.onceki,
# so there is still exactly one previous generation. Hooks, oom.json and scheduled tasks
# are never touched.

$ErrorActionPreference = "Stop"
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

function Stop-GeriAlma {
    param([string]$Message)
    [Console]::Error.WriteLine("geri-al-3.1.0: $Message")
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
        Stop-GeriAlma "canlı-kasa yolu çözülemedi, güvenli taraf seçildi (geri alma reddedildi): $Path ($($_.Exception.Message))"
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
        Stop-GeriAlma "oom.exe kilitli (çalışan bir oom süreci olabilir), hiçbir şey değişmedi: $Path"
    }
}

$Vault = Get-TamYol $Vault

if ((Test-CanliKasa $Vault) -and -not $Canli) {
    Stop-GeriAlma "hedef canlı kasa ($Vault); canlı geri alma yalnız -Canli ile yapılır. Hiçbir şey yazılmadı."
}

$oomDir = Join-Path $Vault ".oom"
$bin = Join-Path $oomDir "bin"
$onceki = Join-Path $oomDir "bin.onceki"
$eski = Join-Path $oomDir "bin.eski"

if (-not (Test-Path -LiteralPath $onceki -PathType Container)) { Stop-GeriAlma "geri alınacak sürüm yok: $onceki" }
if (Test-Path -LiteralPath $eski) { Stop-GeriAlma "yarım kalmış bir kurulum var, önce elle incele: $eski" }
Assert-Kilitsiz (Join-Path $bin "oom.exe")

$hadBin = Test-Path -LiteralPath $bin
if ($hadBin) {
    try { [System.IO.Directory]::Move($bin, $eski) }
    catch { Stop-GeriAlma "mevcut bin taşınamadı (kilitli olabilir), hiçbir şey değişmedi: $($_.Exception.Message)" }
}
try { [System.IO.Directory]::Move($onceki, $bin) }
catch {
    $hata = $_.Exception.Message
    if ($hadBin) {
        try { [System.IO.Directory]::Move($eski, $bin) }
        catch { Stop-GeriAlma "bin.onceki yerine konamadı VE mevcut bin geri konamadı; bin yok, elle geri koy: $eski -> $bin ($($_.Exception.Message))" }
    }
    Stop-GeriAlma "bin.onceki yerine konamadı, mevcut bin geri kondu: $hata"
}
if ($hadBin) {
    try { [System.IO.Directory]::Move($eski, $onceki) }
    catch { Stop-GeriAlma "geri alma tamamlandı (bin güncel) ama eski nesil bin.onceki'ye taşınamadı, elle taşı: $eski -> $onceki ($($_.Exception.Message))" }
}

Write-Output "geri-al-3.1.0: önceki sürüm geri kondu: $bin"
if ($hadBin) { Write-Output "geri-al-3.1.0: geri alınan sürüm: $onceki" }
exit 0
