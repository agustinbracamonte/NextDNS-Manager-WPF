# NextDNS Manager 2.0

Aplicación de escritorio WPF para Windows que administra la protección DNS mediante el CLI oficial de NextDNS y un servicio auxiliar local. También ofrece una conexión opcional a la API de NextDNS para gestionar listas, consultar actividad y cambiar opciones compatibles.

El proyecto no implementa un resolver DNS propio ni reemplaza la aplicación GUI oficial de NextDNS. El instalador configura el servicio auxiliar para que la interfaz normal pueda consultar y cambiar el estado sin ejecutarse como administrador.

## Funciones

- **Inicio:** estado del servicio, perfil local seleccionado, DNS observado en los adaptadores, activar/desactivar, pausa temporal y acceso a la gestión de perfiles.
- **Perfiles locales:** nombre e ID NextDNS de seis caracteres hexadecimales. Se guardan en el perfil de Windows y agregarlos no cambia ni activa el perfil seleccionado.
- **Listas:** consulta de listas permitidas y bloqueadas del perfil remoto; agregar, eliminar, activar o desactivar entradas; importar TXT o JSON con previsualización y exportar JSON conservando el estado de las entradas exportadas.
- **Actividad:** registros DNS entregados por la API de NextDNS, filtros de dominio, dispositivo, resultado y fecha/hora local, paginación y consulta en vivo. El modo en vivo no admite filtros de fecha.
- **Ajustes:** apariencia automática/clara/oscura, opciones de activación del servicio, clave API protegida y opciones remotas compatibles de registros, privacidad de IP/dominio y página de bloqueo.
- **Bandeja y diagnóstico:** acceso al estado desde el área de notificación y vista de registros sanitizados de la aplicación/servicio.
- **Actualizaciones:** comprobación de nuevas versiones de GitHub; la descarga e instalación comienzan solo después de una acción explícita.

La aplicación es adaptable a la zona útil de pantalla y al DPI de Windows. En Windows 11 22H2 o posterior usa el material de ventana de DWM; en sistemas anteriores conserva una presentación translúcida de respaldo, sin prometer el mismo desenfoque.

## Requisitos

- Windows x64.
- Para compilar: .NET 10 SDK y PowerShell.
- Para crear el instalador: Inno Setup 6.6 o posterior. El workflow de publicación fija Inno Setup 7.1.1.
- Para instalar o quitar el servicio auxiliar: permisos de administrador.
- Para usar Listas, Actividad y ajustes remotos: una clave API válida de NextDNS. El control local de protección no requiere esa clave.

El instalador publicado incluye la aplicación, el runtime de .NET y el servicio auxiliar. El CLI oficial `nextdns.exe` se prepara desde la release v1.50.2 y se verifica mediante SHA-256 antes de empaquetarlo.

## Instalación y primeros pasos

1. Descarga `NextDNS-Manager-<versión>-Setup-x64.exe` desde [Releases](https://github.com/agustinbracamonte/NextDNS-Manager-WPF/releases) y ejecútalo. El instalador solicita elevación para registrar el servicio auxiliar.
2. En **Inicio**, pulsa **Administrar perfiles**. En **Ajustes → Perfiles locales**, agrega un nombre y el ID de seis caracteres que muestra tu configuración NextDNS.
3. Selecciona el perfil en Inicio y usa **Activar**. El perfil agregado no se activa automáticamente.
4. Para las funciones remotas, abre **Ajustes**, introduce una clave API de NextDNS, verifícala y guárdala si quieres que la aplicación la recuerde.

La X oculta la ventana en la bandeja; **Salir** en el menú de la bandeja o `Ctrl+Q` termina la aplicación. Cerrar la interfaz no desactiva el servicio DNS.

Si también está instalado el cliente GUI oficial de NextDNS, no actives ambos clientes al mismo tiempo: administran el DNS por separado y pueden competir por el servicio. El servicio auxiliar del Manager detecta el servicio oficial en los casos reconocidos y evita la activación incompatible.

## Importación y límites de datos

- TXT: un dominio o URL por línea; las entradas nuevas se agregan activas.
- JSON: arreglo de objetos con `id` (texto) y `active` (booleano). El archivo exportado tiene ese formato.
- La importación siempre requiere previsualizar. Se omiten duplicados, entradas inválidas y dominios que ya estén en NextDNS; las entradas existentes conservan su estado. Por eso, una importación JSON no restaura el estado de una entrada que ya existía.
- El archivo de importación tiene un límite de 2 MiB y cada lista está limitada por la API del cliente a 5000 entradas.
- Actividad consulta registros alojados por NextDNS, no un historial local independiente. La ventana conserva hasta 1000 consultas visibles. Las horas de filtro se eligen en hora local, se convierten a UTC y el límite **Hasta** es exclusivo.

## Compilar

Primero prepara el CLI oficial fijado por el proyecto; este paso descarga la release de NextDNS y comprueba versión, arquitectura y hashes:

```powershell
.\scripts\Prepare-NextDNSCli.ps1
dotnet build .\NextDNSModernWPF.slnx -c Release
```

El proyecto WPF falla intencionalmente si `NextDNSModernWPF\nextdns.exe` falta o no coincide con el SHA-256 esperado. Para compilar solo un componente:

```powershell
dotnet build .\NextDNSModernWPF\NextDNSModernWPF.csproj -c Release
dotnet build .\NextDNSManager.Service\NextDNSManager.Service.csproj -c Release
```

## Pruebas

Los proyectos `BrokerProbe`, `NextDnsApiProbe` y `UpdateProbe` ejecutan pruebas de consola. Tras preparar el CLI, las pruebas siguientes usan políticas locales o manejadores HTTP falsos; no necesitan una clave API real:

```powershell
dotnet run --project .\tests\BrokerProbe\BrokerProbe.csproj
dotnet run --project .\tests\NextDnsApiProbe\NextDnsApiProbe.csproj
dotnet run --project .\tests\UpdateProbe\UpdateProbe.csproj
```

`tests\LifecycleProbe` crea una ventana WPF para verificar el cierre de sesión/apagado simulado. No es una prueba de consola y puede inicializar la aplicación y la integración API del usuario; ejecútala solo en una sesión de prueba y con el Manager cerrado.

`BrokerProbe` también acepta modos contra un servicio instalado. `--status` consulta el servicio, `--malformed-live` comprueba su protocolo y `--cycle` activa/desactiva NextDNS y cambia DNS real para verificar la restauración. **No uses `--cycle` como prueba offline**; requiere un equipo de prueba y un perfil válido.

Más información en [compilación y pruebas](docs/COMPILACION_Y_PRUEBAS.md).

## Crear el instalador

Con .NET 10 SDK e Inno Setup 6.6 o posterior instalados:

```powershell
.\scripts\Build-Installer.ps1
```

El script prepara el CLI, publica aplicación y servicio como `win-x64` autocontenido, valida el manifiesto de instalación y genera:

```text
artifacts\NextDNS-Manager-2.0.0-Setup-x64.exe
```

Para verificar después una carpeta instalada:

```powershell
.\scripts\Test-InstallLayout.ps1 -Directory "$env:ProgramFiles\NextDNS Manager"
```

El instalador requiere permisos de administrador, comprueba que el Manager no esté abierto y verifica hashes de los archivos copiados. El pipeline de publicación está en `.github/workflows/release.yml`.

## Documentación

- [Arquitectura y organización del repositorio](docs/ARQUITECTURA.md)
- [Seguridad y privacidad](docs/SEGURIDAD_Y_PRIVACIDAD.md)
- [Instalación, inicio y actualizaciones](docs/INSTALACION_Y_ACTUALIZACIONES.md)
- [Compilación y pruebas](docs/COMPILACION_Y_PRUEBAS.md)
- [Referencias visuales históricas](docs/REFERENCIAS_VISUALES.md)
- [Permisos y servicio auxiliar](docs/PERMISOS_Y_SERVICIO.md)
- [Registros de diagnóstico](docs/REGISTROS_DIAGNOSTICO.md)
- [Análisis del consumo de CPU del cliente NextDNS](docs/ANALISIS_CONSUMO_CPU_NEXTDNS.md)
- [Investigación del inicio y la bandeja](docs/ARRANQUE_Y_BANDEJA.md)

## Alcance y limitaciones

- La clave API, los permisos de la cuenta NextDNS y la disponibilidad de su API determinan qué funciones remotas se pueden usar.
- Los filtros de fecha no se pueden usar mientras Actividad está en vivo.
- Las imágenes en `docs/referencias/` son maquetas históricas, no capturas actuales ni una especificación funcional. Las decisiones visuales vigentes se describen en [Referencias visuales](docs/REFERENCIAS_VISUALES.md); actualmente el escudo y la marca aparecen solo en Inicio.
- El instalador del proyecto no está firmado digitalmente. SHA-256 permite detectar errores o cambios respecto del manifiesto obtenido, pero no acredita por sí solo quién publicó el archivo.
- Este repositorio no contiene un archivo `LICENSE`; no se debe inferir una licencia de redistribución que no esté declarada.

El Manager es un proyecto independiente y no oficial de NextDNS. Utiliza el [CLI oficial de NextDNS](https://nextdns.io/) como motor DNS.
