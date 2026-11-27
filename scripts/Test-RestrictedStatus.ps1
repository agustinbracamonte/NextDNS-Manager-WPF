$ErrorActionPreference = 'Stop'

$registryPath = 'HKLM:\SOFTWARE\NextDNSManager'
$serviceName = 'NextDNSManagerBroker'
$runnerSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$originalControllerSid = (Get-ItemProperty -LiteralPath $registryPath -Name ControllerSid -ErrorAction Stop).ControllerSid
if (-not [string]::Equals($originalControllerSid, $runnerSid, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "La instalación no conservó el SID del usuario de sesión: esperado $runnerSid, obtenido $originalControllerSid."
}

$suffix = [Guid]::NewGuid().ToString('N')
$userName = "ndp_$($suffix.Substring(0, 12))"
$passwordBytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
$passwordText = 'Aa1!' + [Convert]::ToBase64String($passwordBytes)
$securePassword = ConvertTo-SecureString -String $passwordText -AsPlainText -Force
$passwordText = $null
$credential = $null
$testUserCreated = $false
$controllerSidChanged = $false
$cleanupFailures = [System.Collections.Generic.List[string]]::new()
$logBase = Join-Path $env:RUNNER_TEMP "nextdns-standard-probe-$suffix"
$stdoutPath = "$logBase.stdout.log"
$stderrPath = "$logBase.stderr.log"
$primaryFailure = $null

try {
    $testUser = New-LocalUser -Name $userName -Password $securePassword -AccountNeverExpires -PasswordNeverExpires -UserMayNotChangePassword -Description 'Temporary NextDNS CI status probe'
    $testUserCreated = $true
    $testUserSid = $testUser.SID.Value
    if ([string]::Equals($testUserSid, $runnerSid, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'La cuenta de prueba no debe compartir el SID del runner.'
    }

    Set-ItemProperty -LiteralPath $registryPath -Name ControllerSid -Value $testUserSid
    $controllerSidChanged = $true
    Restart-Service -Name $serviceName -Force -ErrorAction Stop
    (Get-Service -Name $serviceName -ErrorAction Stop).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))

    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $launcherDll = Join-Path $PSScriptRoot '..\tests\RestrictedLauncher\bin\Release\net10.0-windows\RestrictedLauncher.dll'
    $probeExe = Join-Path $PSScriptRoot '..\tests\BrokerProbe\bin\Release\net10.0-windows\BrokerProbe.exe'
    foreach ($requiredFile in @($dotnet, $launcherDll, $probeExe)) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) { throw "Falta un archivo de prueba requerido: $requiredFile" }
    }

    $credential = [System.Management.Automation.PSCredential]::new("$env:COMPUTERNAME\$userName", $securePassword)
    $arguments = '"{0}" "{1}" --current-user --dotnet "{2}"' -f $launcherDll, $probeExe, $dotnet
    $process = Start-Process -FilePath $dotnet -ArgumentList $arguments -Credential $credential -LoadUserProfile `
        -WorkingDirectory $env:GITHUB_WORKSPACE -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath `
        -Wait -PassThru
    $probeOutput = if (Test-Path -LiteralPath $stdoutPath) { Get-Content -LiteralPath $stdoutPath -Raw } else { '' }
    $probeError = if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath -Raw } else { '' }
    if ($probeOutput) { Write-Output $probeOutput.TrimEnd() }
    if ($probeError) { Write-Output $probeError.TrimEnd() }

    if ($process.ExitCode -ne 0) { throw "La prueba con usuario estándar terminó con código $($process.ExitCode)." }
    if ($probeOutput -notmatch '(?m)^Admin: false\s*$') { throw 'BrokerProbe no confirmó Admin: false.' }
    if ($probeOutput -notmatch '(?m)^Success: true\s*$') { throw 'BrokerProbe no confirmó Success: true.' }
    if ($probeOutput -notmatch '(?m)^PASS: Broker status was read by a standard user in its original logon token\.\s*$') {
        throw 'RestrictedLauncher no confirmó la ejecución bajo el token de usuario estándar.'
    }
}
catch {
    $primaryFailure = $_
}
finally {
    if ($controllerSidChanged) {
        try {
            Set-ItemProperty -LiteralPath $registryPath -Name ControllerSid -Value $originalControllerSid -ErrorAction Stop
            Restart-Service -Name $serviceName -Force -ErrorAction Stop
            (Get-Service -Name $serviceName -ErrorAction Stop).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
        }
        catch { $cleanupFailures.Add("No se pudo restaurar el SID original del broker: $($_.Exception.Message)") }
    }

    if ($testUserCreated) {
        try { Remove-LocalUser -Name $userName -ErrorAction Stop }
        catch { $cleanupFailures.Add("No se pudo eliminar la cuenta temporal: $($_.Exception.Message)") }
    }

    foreach ($logPath in @($stdoutPath, $stderrPath)) {
        if (Test-Path -LiteralPath $logPath) {
            try { Remove-Item -LiteralPath $logPath -Force -ErrorAction Stop }
            catch { $cleanupFailures.Add("No se pudo quitar el log temporal ${logPath}: $($_.Exception.Message)") }
        }
    }

    $securePassword.Dispose()
    [Array]::Clear($passwordBytes, 0, $passwordBytes.Length)
}

if ($primaryFailure -and $cleanupFailures.Count -gt 0) {
    throw "$($primaryFailure.Exception.Message) Limpieza incompleta: $($cleanupFailures -join '; ')"
}
if ($primaryFailure) { throw $primaryFailure }
if ($cleanupFailures.Count -gt 0) { throw ($cleanupFailures -join '; ') }

Write-Output "PASS: usuario estándar temporal $testUserSid consultó el broker; SID de instalación $runnerSid restaurado y cuenta eliminada."
