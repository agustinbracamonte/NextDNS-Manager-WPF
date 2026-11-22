$ErrorActionPreference = 'Stop'

$version = '1.50.2'
$assetName = "nextdns_${version}_windows_amd64.zip"
$expectedArchiveSha256 = 'd61256a76783489a63e6e575cd1a34776878f8b4c2a601178dc09a6e49aea490'
$expectedExeSha256 = '5a603c759bc4a854a6aae46cadb91ae00a95ba5bb0591b985f49a95597856b8e'
$releaseBaseUrl = "https://github.com/nextdns/nextdns/releases/download/v${version}"
$projectRoot = Split-Path -Parent $PSScriptRoot
$targetExe = Join-Path $projectRoot 'NextDNSModernWPF\nextdns.exe'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('nextdns-cli-' + [guid]::NewGuid().ToString('N'))
$archivePath = Join-Path $tempRoot $assetName
$manifestPath = Join-Path $tempRoot 'checksums.txt'
$extractPath = Join-Path $tempRoot 'extracted'

New-Item -ItemType Directory -Path $tempRoot | Out-Null
try {
    Invoke-WebRequest -Uri "$releaseBaseUrl/checksums.txt" -OutFile $manifestPath
    Invoke-WebRequest -Uri "$releaseBaseUrl/$assetName" -OutFile $archivePath

    $manifestText = [System.IO.File]::ReadAllText($manifestPath)
    $checksumPattern = '(?m)^([0-9a-fA-F]{64})\s+\*?' + [regex]::Escape($assetName) + '\s*$'
    $manifestMatch = [regex]::Match($manifestText, $checksumPattern)
    if (-not $manifestMatch.Success) { throw "No checksum entry found for $assetName." }

    $manifestSha256 = $manifestMatch.Groups[1].Value.ToLowerInvariant()
    $archiveSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($manifestSha256 -ne $expectedArchiveSha256 -or $archiveSha256 -ne $expectedArchiveSha256) {
        throw "Release archive checksum mismatch. Expected $expectedArchiveSha256, manifest $manifestSha256, downloaded $archiveSha256."
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath
    $executables = @(Get-ChildItem -LiteralPath $extractPath -Filter 'nextdns.exe' -File -Recurse)
    if ($executables.Count -ne 1) { throw "Expected one nextdns.exe in the verified archive, found $($executables.Count)." }

    $exePath = $executables[0].FullName
    $exeBytes = [System.IO.File]::ReadAllBytes($exePath)
    if ($exeBytes.Length -lt 64) { throw 'The downloaded CLI executable is too small to be valid.' }
    $peOffset = [BitConverter]::ToInt32($exeBytes, 0x3c)
    if ($peOffset -lt 0 -or $peOffset + 6 -gt $exeBytes.Length) { throw 'The downloaded CLI executable has an invalid PE header.' }
    $machine = [BitConverter]::ToUInt16($exeBytes, $peOffset + 4)
    if ($machine -ne 0x8664) { throw ('Expected Windows amd64 PE (0x8664), got 0x{0:X4}.' -f $machine) }

    $embeddedVersionMatch = [regex]::Match([System.Text.Encoding]::ASCII.GetString($exeBytes), 'github\.com/nextdns/nextdns\s+v(\d+\.\d+\.\d+)')
    if (-not $embeddedVersionMatch.Success -or $embeddedVersionMatch.Groups[1].Value -ne $version) {
        throw "The downloaded CLI does not identify itself as v$version."
    }

    $exeSha256 = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($exeSha256 -ne $expectedExeSha256) { throw "CLI executable checksum mismatch. Expected $expectedExeSha256, got $exeSha256." }

    Copy-Item -LiteralPath $exePath -Destination $targetExe -Force
    Write-Output "Prepared official NextDNS CLI v$version at $targetExe (SHA-256 $exeSha256)."
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
