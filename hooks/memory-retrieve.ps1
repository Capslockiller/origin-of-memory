# memory-retrieve.ps1 - UYUMLULUK KABUGU (A-borc 3, 2026-09-06).
#
# Sozlesme: getirmenin TEK giris noktasi scripts/retrieve.py'dir. Canli
# kullanici ayarlari UserPromptSubmit'te dogrudan `retrieve.py hook` cagirir;
# bu dosya artik kayitli degildir. Eskiden burada ikinci bir eleme mantigi
# (uzunluk esigi, slash komutu atlama) vardi ve retrieve.py'nin ilgi esigi
# YOKTU - yani kapisiz ikinci bir giris idi. Govde bosaltildi: eleme, dedup,
# ilgi esigi ve cikti bicimi TAMAMEN retrieve.py'de yasar.
#
# Silinmedi cunku eski kayitlari olan kurulumlar bu yolu hala cagirabilir;
# silme karari sahibinindir. Burasi stdin'i oldugu gibi gecirir, stdout'u ve
# cikis kodunu aynen dondurur.
#
# yazan: claude - model: opus-5
$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
if ($env:BEYIN_INVOKED_BY) { exit 0 }

$stdin = [Console]::In.ReadToEnd()
if (-not $stdin) { exit 0 }

$py = $null
$pyPrefix = @()
if ($env:BEYIN_PYTHON) {
  $candidate = Get-Command $env:BEYIN_PYTHON -ErrorAction SilentlyContinue
  if ($candidate) { $py = $candidate.Source }
  elseif (Test-Path -LiteralPath $env:BEYIN_PYTHON -PathType Leaf) { $py = $env:BEYIN_PYTHON }
}
if (-not $py) {
  $candidate = Get-Command python -ErrorAction SilentlyContinue
  if ($candidate) { $py = $candidate.Source }
}
if (-not $py) {
  $candidate = Get-Command py -ErrorAction SilentlyContinue
  if ($candidate) { $py = $candidate.Source; $pyPrefix = @('-3') }
}
$script = Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts') 'retrieve.py'
if (-not ($py -and (Test-Path $script))) { exit 0 }

$arguments = @($pyPrefix) + @('-X', 'utf8', $script, 'hook')
$out = $stdin | & $py @arguments
$code = $LASTEXITCODE
if ($out) { [Console]::Out.WriteLine(($out -join "`n")) }
if ($null -eq $code) { $code = 0 }
exit $code
