# Compilación y pruebas

## Herramientas

- Windows x64 y PowerShell.
- .NET 10 SDK para compilar los proyectos.
- Inno Setup 6.6+ para generar un instalador local.
- Conexión a GitHub al preparar el CLI, al generar recursos que falten o al empaquetar el instalador completo.

El proyecto WPF apunta a `net10.0-windows`, x64, WPF y Windows Forms (icono de bandeja). `NextDNSManager.Service` también apunta a `net10.0-windows`. `NextDNSModernWPF.slnx` incluye el proyecto WPF principal; el servicio y los probes se compilan desde sus proyectos.

## Preparar el CLI

El repositorio no asume que `nextdns.exe` esté presente. El build WPF exige `NextDNSModernWPF\nextdns.exe` y comprueba su SHA-256. Para prepararlo:

```powershell
.\scripts\Prepare-NextDNSCli.ps1
```

El script descarga `nextdns_1.50.2_windows_amd64.zip` y verifica el hash fijado del archivo, el hash del ejecutable extraído, su arquitectura PE amd64 y la versión embebida. Solo después copia el ejecutable al proyecto WPF.

## Build

```powershell
dotnet build .\NextDNSModernWPF.slnx -c Release
dotnet build .\NextDNSManager.Service\NextDNSManager.Service.csproj -c Release
```

Para compilar individualmente la aplicación:

```powershell
dotnet build .\NextDNSModernWPF\NextDNSModernWPF.csproj -c Release
```

La solución contiene solo el proyecto WPF; compilar el servicio aparte evita asumir que está incluido en la solución.

## Probes offline

Los probes son aplicaciones de consola que hacen aserciones y devuelven un código de salida distinto de cero si fallan. `NextDnsApiProbe` y `UpdateProbe` usan manejadores HTTP falsos, no una API key real ni HTTP real. `BrokerProbe` sin argumentos ejercita el protocolo/políticas localmente y no contacta el servicio instalado.

```powershell
dotnet run --project .\tests\BrokerProbe\BrokerProbe.csproj
dotnet run --project .\tests\NextDnsApiProbe\NextDnsApiProbe.csproj
dotnet run --project .\tests\UpdateProbe\UpdateProbe.csproj
```

Para `NextDnsApiProbe` hace falta que el build de la referencia WPF pueda validar `nextdns.exe`; ejecuta primero `Prepare-NextDNSCli.ps1`. `BrokerProbe` y `UpdateProbe` no necesitan una clave API ni el servicio en ejecución.

## Probes con entorno

`BrokerProbe` acepta:

| Modo | Efecto |
| --- | --- |
| `--status` | Consulta estado a la named pipe del servicio instalado. |
| `--malformed-live` | Envía solicitudes deliberadamente malformadas al servicio para comprobar rechazos. |
| `--cycle` | Activa y desactiva protección y cambia DNS real; necesita un perfil local válido y aborta si ya hay protección activa. |

No ejecutes `--cycle` en producción como una prueba de build. Usa una máquina de prueba y comprueba que el proceso puede restaurar DNS.

`tests/LifecycleProbe` crea una ventana WPF para ejercitar el cierre de sesión/apagado simulado. Como referencia al proyecto principal, puede ejecutar el inicio de la app e intentar recuperar una clave API guardada en el perfil actual de Windows. Ejecútalo solo en un escritorio de prueba, con el Manager cerrado y sin credenciales de producción.

## Crear y verificar el instalador

```powershell
.\scripts\Build-Installer.ps1
```

El script localiza `dotnet` e `ISCC.exe`, requiere Inno Setup 6.6+, prepara el CLI, publica Manager/servicio autocontenidos para `win-x64`, verifica el CLI, crea `install-files.sha256`, ejecuta `Test-InstallLayout.ps1` y compila con Inno Setup. El instalador sale en `artifacts\NextDNS-Manager-2.3.0-Setup-x64.exe`.

La verificación de una carpeta ya publicada es:

```powershell
.\scripts\Test-InstallLayout.ps1 -Directory "$env:ProgramFiles\NextDNS Manager"
```

Este script comprueba que el manifiesto exista, que todos los archivos enumerados estén bajo la carpeta indicada, que coincidan sus SHA-256 y que estén presentes los archivos obligatorios. No inicia el servicio ni ejecuta una activación.

## Workflow de release

`.github/workflows/release.yml` se inicia manualmente con una etiqueta estable existente `vX.Y.Z`. Verifica la coincidencia de versión entre etiqueta, `.csproj` y script de build; usa .NET 10 e instala Inno Setup 7.1.1 con hash/firma validados; después publica el instalador y `SHA256SUMS.txt`.

No hace falta publicar nada para compilar o probar localmente. Los comandos de esta guía no ejecutan el instalador ni modifican DNS, salvo el modo deliberado `BrokerProbe --cycle`.
