$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$outDir = Join-Path $PSScriptRoot "output"
$fontDir = Join-Path $repoRoot "Tools\fonts"
$exe = Join-Path $repoRoot "UnityTextTranslator\Tools\msdf-atlas-gen\msdf-atlas-gen.exe"
if (-not (Test-Path $exe)) {
    $exe = Join-Path $PSScriptRoot "msdf-atlas-gen.exe"
}
$font = Join-Path $fontDir "LiberationSans-Regular.ttf"

New-Item -ItemType Directory -Path $outDir, $fontDir -Force | Out-Null

$charsetPath = Join-Path $outDir "charset.txt"
$lines = New-Object System.Collections.Generic.List[int]
32..126 | ForEach-Object { [void]$lines.Add($_) }
1024..1279 | ForEach-Object { [void]$lines.Add($_) }
Set-Content -Path $charsetPath -Value ($lines | ForEach-Object { $_.ToString() }) -Encoding UTF8

if (-not (Test-Path $font)) {
    Write-Error "Place LiberationSans-Regular.ttf at: $font"
}

$pngOut = Join-Path $outDir "atlas512_sdf.png"
$jsonOut = Join-Path $outDir "atlas512_sdf.json"
$args = @(
    "-font", "`"$font`"",
    "-type", "sdf",
    "-format", "png",
    "-dimensions", "512", "512",
    "-size", "36",
    "-imageout", "`"$pngOut`"",
    "-json", "`"$jsonOut`"",
    "-charset", "`"$charsetPath`""
)
Write-Host "[msdf cmd] $exe $($args -join ' ')"
& $exe @args
Write-Host "[msdf exit] $LASTEXITCODE"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$pngInfo = Get-Item $pngOut
Write-Host "[msdf output] PNG size: $($pngInfo.Length) bytes, path: $pngOut"
$fs = [System.IO.File]::OpenRead($pngOut)
$header = New-Object byte[] 24
[void]$fs.Read($header, 0, 24)
$fs.Close()
$width = ($header[16] -shl 24) -bor ($header[17] -shl 16) -bor ($header[18] -shl 8) -bor $header[19]
$height = ($header[20] -shl 24) -bor ($header[21] -shl 16) -bor ($header[22] -shl 8) -bor $header[23]
Write-Host "[msdf output] PNG dimensions: ${width}x${height}"
