# yazan: codex
# model: gpt-5.6-sol
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path $repoRoot 'src\LocalBrain\Program.cs'
$icon = Join-Path $repoRoot 'assets\localbrain.ico'
$output = Join-Path $repoRoot 'LocalBrain.exe'

if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
  throw "C# compiler was not found at $compiler. No launcher was built."
}
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
  throw "Launcher source was not found at $source."
}

if (-not (Test-Path -LiteralPath $icon -PathType Leaf)) {
  $assetDirectory = Split-Path -Parent $icon
  if (-not (Test-Path -LiteralPath $assetDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $assetDirectory | Out-Null
  }
  Add-Type -AssemblyName System.Drawing
  $bitmap = New-Object Drawing.Bitmap 64, 64
  $graphics = [Drawing.Graphics]::FromImage($bitmap)
  $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
  try {
    $graphics.Clear([Drawing.Color]::Transparent)
    $background = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 23, 36, 49))
    $copper = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(255, 226, 138, 97)), 6
    $cream = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 247, 212, 146))
    try {
      $graphics.FillEllipse($background, 3, 3, 58, 58)
      $graphics.DrawArc($copper, 15, 15, 34, 34, 35, 285)
      $graphics.FillEllipse($cream, 14, 16, 10, 10)
      $graphics.FillEllipse($cream, 40, 38, 10, 10)
      $graphics.FillEllipse($cream, 27, 27, 10, 10)
    } finally {
      $background.Dispose()
      $copper.Dispose()
      $cream.Dispose()
    }
    $handle = $bitmap.GetHicon()
    $generated = [Drawing.Icon]::FromHandle($handle)
    $stream = New-Object IO.FileStream $icon, ([IO.FileMode]::Create), ([IO.FileAccess]::Write)
    try { $generated.Save($stream) } finally { $stream.Dispose(); $generated.Dispose() }
  } finally {
    $graphics.Dispose()
    $bitmap.Dispose()
  }
}

if (-not (Test-Path -LiteralPath $icon -PathType Leaf)) {
  throw "Launcher icon was not created at $icon."
}

& $compiler /nologo /target:winexe /optimize+ /reference:System.Windows.Forms.dll "/win32icon:$icon" "/out:$output" $source
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $output -PathType Leaf)) {
  throw "C# launcher compilation failed with exit code $LASTEXITCODE."
}
Write-Output "Built $output"
