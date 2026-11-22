param(
    [Parameter(Mandatory = $true)][string]$Platform,
    [Parameter(Mandatory = $true)][string]$Release,
    [Parameter(Mandatory = $true)][string]$Asset,
    [Parameter(Mandatory = $true)][string]$Sha256,
    [Parameter(Mandatory = $true)][string]$OutDir
)

$ErrorActionPreference = 'Stop'

$dll = Join-Path $OutDir 'libmpv-2.dll'
if (Test-Path -LiteralPath $dll) { exit 0 }

try {
    $url = "https://github.com/amilcarmalave/FlyPhotos/releases/download/$Release/$Asset"
    $tempDir = Join-Path $env:TEMP 'FlyPhotosLibMpv'
    New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
    $archive = Join-Path $tempDir $Asset

    # GitHub requires TLS 1.2; Windows PowerShell 5.1 may default to older protocols.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $ProgressPreference = 'SilentlyContinue'

    Write-Host "Downloading libmpv ($Platform) from $url"
    Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing

    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($actual -ne $Sha256) {
        Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
        throw "libmpv archive hash mismatch for $Asset (expected $Sha256, got $actual)"
    }

    $sevenZip = $null
    $cmd = Get-Command '7z.exe' -ErrorAction SilentlyContinue
    if ($cmd) { $sevenZip = $cmd.Source }
    elseif (Test-Path -LiteralPath 'C:\Program Files\7-Zip\7z.exe') { $sevenZip = 'C:\Program Files\7-Zip\7z.exe' }
    if (-not $sevenZip) { throw '7-Zip (7z.exe) is required to unpack libmpv' }

    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    & $sevenZip e $archive "-o$OutDir" 'libmpv-2.dll' -y | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $dll)) {
        throw "Failed to extract libmpv-2.dll from $Asset (7z exit code $LASTEXITCODE)"
    }
}
catch {
    [Console]::Error.WriteLine("FetchLibMpv.ps1 error: $($_.Exception.Message)")
    exit 1
}
