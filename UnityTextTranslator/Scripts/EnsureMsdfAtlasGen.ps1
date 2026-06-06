param(
    [Parameter(Mandatory = $true)]
    [string] $OutDir
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$exePath = Join-Path $OutDir 'msdf-atlas-gen.exe'
if (Test-Path $exePath) {
    exit 0
}

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$zipUrl = 'https://github.com/Chlumsky/msdf-atlas-gen/releases/download/v1.4/msdf-atlas-gen-1.4-win64.zip'
$zipPath = Join-Path ([System.IO.Path]::GetTempPath()) ('msdf-atlas-gen-1.4-win64-' + [guid]::NewGuid().ToString('N') + '.zip')

try {
    Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing
    Expand-Archive -LiteralPath $zipPath -DestinationPath $OutDir -Force
}
finally {
    if (Test-Path $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Test-Path $exePath)) {
    $found = Get-ChildItem -LiteralPath $OutDir -Filter 'msdf-atlas-gen.exe' -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($found) {
        Copy-Item -LiteralPath $found.FullName -Destination $exePath -Force
    }
}

if (-not (Test-Path $exePath)) {
    throw "msdf-atlas-gen.exe not found under $OutDir after extracting the release zip."
}

exit 0
