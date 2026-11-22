param([Parameter(Mandatory = $true)][string]$Directory)

$ErrorActionPreference = 'Stop'
$resolvedDirectory = (Resolve-Path -LiteralPath $Directory).Path
$directoryPrefix = $resolvedDirectory.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$manifestPath = Join-Path $resolvedDirectory 'install-files.sha256'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Falta el manifiesto de instalación: $manifestPath" }

$verifiedFiles = @{}
foreach ($line in [System.IO.File]::ReadAllLines($manifestPath)) {
    if ($line -notmatch '^([0-9a-fA-F]{64})  (.+)$') { throw 'Entrada inválida en install-files.sha256.' }
    $expectedHash = $Matches[1]
    $relativePath = $Matches[2]
    $filePath = [System.IO.Path]::GetFullPath((Join-Path $resolvedDirectory $relativePath))
    if (-not $filePath.StartsWith($directoryPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Ruta fuera de la instalación: $relativePath"
    }
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) { throw "Falta un archivo de instalación: $relativePath" }
    if ((Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash -ne $expectedHash) {
        throw "El archivo instalado no coincide con el publicado: $relativePath"
    }
    $verifiedFiles[$relativePath] = $true
}

foreach ($requiredFile in @('NextDNSModernWPF.exe', 'NextDNSModernWPF.dll', 'NextDNSModernWPF.runtimeconfig.json',
    'NextDNSModernWPF.deps.json', 'System.Diagnostics.Tracing.dll', 'coreclr.dll', 'nextdns.exe',
    'NextDNSManager.ico', 'service\NextDNSManager.Service.exe', 'service\NextDNSManager.Service.dll',
    'service\NextDNSManager.Service.runtimeconfig.json', 'service\NextDNSManager.Service.deps.json')) {
    if (-not $verifiedFiles.ContainsKey($requiredFile)) { throw "El manifiesto no contiene $requiredFile" }
}
Write-Output "Instalación completa: $($verifiedFiles.Count) archivos verificados en $resolvedDirectory"
