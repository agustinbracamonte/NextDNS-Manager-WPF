param(
    [string]$DotNetPath,
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
$appVersion = '2.0.0'
$expectedCliSha256 = '5a603c759bc4a854a6aae46cadb91ae00a95ba5bb0591b985f49a95597856b8e'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot 'NextDNSModernWPF\NextDNSModernWPF.csproj'
$installerScript = Join-Path $projectRoot 'installer\NextDNSManager.iss'
$artifactDirectory = Join-Path $projectRoot 'artifacts'
$publishDirectory = Join-Path $projectRoot "artifacts\publish-$appVersion-win-x64"
$installerName = "NextDNS-Manager-$appVersion-Setup-x64.exe"
$installerPath = Join-Path $artifactDirectory $installerName

if (-not $DotNetPath) {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnetCommand) { $DotNetPath = $dotnetCommand.Source }
}
if (-not $DotNetPath -or -not (Test-Path -LiteralPath $DotNetPath)) {
    throw 'No se encontró .NET SDK. Instala .NET 10 SDK o indica su ruta con -DotNetPath.'
}

if (-not $IsccPath) {
    $isccCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($isccCommand) { $IsccPath = $isccCommand.Source }
}
if (-not $IsccPath) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles} 'Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles} 'Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    )
    $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath)) {
    throw 'No se encontró ISCC.exe. Instala Inno Setup 7 y vuelve a ejecutar este script.'
}

$compilerVersionOutput = & $IsccPath --version 2>&1
if ($LASTEXITCODE -ne 0 -or $compilerVersionOutput -notmatch '(\d+\.\d+\.\d+)') {
    throw "No se pudo identificar la versión de Inno Setup: $compilerVersionOutput"
}
$compilerVersion = [Version]::Parse($Matches[1])
if ($compilerVersion -lt [Version]'6.6.0') {
    throw "Inno Setup $compilerVersion no es compatible. Se requiere la versión 6.6 o superior."
}

& (Join-Path $PSScriptRoot 'Prepare-NextDNSCli.ps1')
& (Join-Path $PSScriptRoot 'Generate-InstallerArtwork.ps1')

if (Test-Path -LiteralPath $publishDirectory) {
    $resolvedRoot = [System.IO.Path]::GetFullPath($projectRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $resolvedPublish = [System.IO.Path]::GetFullPath($publishDirectory)
    if (-not $resolvedPublish.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Se rechazó limpiar una ruta fuera del proyecto: $resolvedPublish"
    }
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

$assemblyVersion = "$appVersion.0"
$publishArguments = @(
    'publish', $projectFile, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', "-p:Version=$appVersion", "-p:AssemblyVersion=$assemblyVersion",
    "-p:FileVersion=$assemblyVersion", '-o', $publishDirectory
)
& $DotNetPath @publishArguments
if ($LASTEXITCODE -ne 0) { throw "dotnet publish terminó con código $LASTEXITCODE." }

$serviceProject = Join-Path $projectRoot 'NextDNSManager.Service\NextDNSManager.Service.csproj'
& $DotNetPath publish $serviceProject -c Release -r win-x64 --self-contained true '-p:PublishTrimmed=false' '-p:PublishSingleFile=false' -o (Join-Path $publishDirectory 'service')
if ($LASTEXITCODE -ne 0) { throw "No se pudo publicar el servicio auxiliar: $LASTEXITCODE." }

$managerExe = Join-Path $publishDirectory 'NextDNSModernWPF.exe'
$publishedCli = Join-Path $publishDirectory 'nextdns.exe'
foreach ($requiredFile in @($managerExe, $publishedCli)) {
    if (-not (Test-Path -LiteralPath $requiredFile)) { throw "Falta un archivo obligatorio en publish: $requiredFile" }
}
$publishedHash = (Get-FileHash -LiteralPath $publishedCli -Algorithm SHA256).Hash.ToLowerInvariant()
if ($publishedHash -ne $expectedCliSha256) { throw "El CLI publicado no está verificado: $publishedHash" }
Copy-Item -LiteralPath (Join-Path $projectRoot 'NextDNSModernWPF\escudo_dns.ico') -Destination (Join-Path $publishDirectory 'NextDNSManager.ico') -Force

$layoutManifest = Get-ChildItem -LiteralPath $publishDirectory -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relativePath = $_.FullName.Substring($publishDirectory.Length + 1)
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $relativePath
}
[System.IO.File]::WriteAllLines((Join-Path $publishDirectory 'install-files.sha256'), [string[]]$layoutManifest)
& (Join-Path $PSScriptRoot 'Test-InstallLayout.ps1') -Directory $publishDirectory

New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
$stagingDirectory = Join-Path $artifactDirectory ".installer-staging-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null

try {
    & $IsccPath "--output-dir=$stagingDirectory" $installerScript
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup terminó con código $LASTEXITCODE. Se conservó el instalador anterior." }

    $stagedInstaller = Join-Path $stagingDirectory $installerName
    if (-not (Test-Path -LiteralPath $stagedInstaller)) {
        throw "Inno Setup no generó el instalador esperado: $stagedInstaller. Se conservó el instalador anterior."
    }

    if (Test-Path -LiteralPath $installerPath) {
        $backupPath = Join-Path $stagingDirectory 'previous-installer.exe'
        [System.IO.File]::Replace($stagedInstaller, $installerPath, $backupPath)
    }
    else {
        [System.IO.File]::Move($stagedInstaller, $installerPath)
    }
}
finally {
    $resolvedArtifacts = [System.IO.Path]::GetFullPath($artifactDirectory).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $resolvedStaging = [System.IO.Path]::GetFullPath($stagingDirectory)
    if ($resolvedStaging.StartsWith($resolvedArtifacts, [System.StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedStaging)) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}

$installerInfo = Get-Item -LiteralPath $installerPath
$installerHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
Write-Output "Instalador generado: $($installerInfo.FullName)"
Write-Output "Tamaño: $([Math]::Round($installerInfo.Length / 1MB, 1)) MB"
Write-Output "SHA-256 instalador: $installerHash"
Write-Output "NextDNS CLI incluido: v1.50.2; SHA-256 $publishedHash"
