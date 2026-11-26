# Instalación, inicio y actualizaciones

## Instalación

1. Descarga el instalador x64 de la [sección Releases](https://github.com/agustinbracamonte/NextDNS-Manager-WPF/releases).
2. Ejecuta el instalador como administrador. Inno Setup instala la aplicación autocontenida y registra `NextDNSManagerBroker` como servicio auxiliar LocalSystem.
3. Puedes elegir un acceso directo en el escritorio y abrir la aplicación al finalizar.
4. Al abrir NextDNS Manager, añade un perfil local desde **Inicio → Administrar perfiles** y activa la protección cuando esté seleccionado el perfil deseado.

El instalador exige que el Manager no esté ejecutándose para evitar reemplazar binarios en uso. Si la app está en la bandeja, elige **Salir** o usa `Ctrl+Q` antes de reparar o desinstalar. La X solo oculta la ventana.

## Permisos durante el uso

El proceso de interfaz no se eleva. El servicio LocalSystem instalado realiza los cambios privilegiados y permite consultar/cambiar el estado mediante una named pipe local. No se reinstala el CLI en cada activación. El servicio conserva y restaura la configuración DNS guardada cuando se desactiva.

El servicio del Manager y el servicio de la GUI oficial NextDNS no deben usarse simultáneamente para controlar el mismo DNS. El broker rechaza activar el Manager si detecta instalado el servicio oficial `NextDNSService`.

## Bandeja e inicio de sesión

En la instalación bajo `%ProgramFiles%\NextDNS Manager`, la aplicación se registra una vez para iniciar en la bandeja al iniciar sesión. También configura una tarea interactiva para reabrir la interfaz después de reanudar Windows. El usuario puede desactivar el inicio desde **Aplicaciones de inicio** de Windows; la configuración no se vuelve a activar cada vez que se ejecuta la app.

Cerrar la ventana no interrumpe la protección DNS, que mantiene el servicio auxiliar. El botón Salir cierra la interfaz y el proceso; no equivale a desactivar NextDNS.

## Comprobación de actualizaciones

La app consulta al iniciar y cada 24 horas la release estable más reciente del repositorio configurado. También se puede seleccionar **Buscar actualizaciones** desde el menú de la bandeja. Un fallo de red no bloquea el inicio ni cambia el estado del servicio.

La comprobación solo avisa. Para instalar una versión:

1. Pulsa el botón de actualización y espera a que descargue y valide el archivo.
2. Revisa la confirmación y acepta iniciar el instalador.
3. Windows solicitará elevación para actualizar aplicación y servicio.

La descarga valida el nombre/ruta del asset, tamaño y SHA-256 publicado en `SHA256SUMS.txt`. El instalador comprueba el manifiesto SHA-256 de los archivos instalados. El hash permite detectar cambios respecto de su manifiesto, pero no sustituye firma de código; el instalador no está firmado actualmente.

## Construir instalador localmente

Requisitos: .NET 10 SDK, Inno Setup 6.6 o posterior y conexión para preparar el CLI y recursos asociados.

```powershell
.\scripts\Build-Installer.ps1
```

El script crea `artifacts\NextDNS-Manager-2.0.0-Setup-x64.exe`, prepara el CLI v1.50.2 con hashes fijados, publica el Manager y servicio `win-x64` autocontenidos, genera `install-files.sha256`, verifica el layout e invoca Inno Setup.

Para comprobar la instalación tras copiarla:

```powershell
.\scripts\Test-InstallLayout.ps1 -Directory "$env:ProgramFiles\NextDNS Manager"
```

Esta comprobación valida archivos/manifiesto, no el servicio en ejecución ni una operación DNS real.

## Publicación

El workflow `.github/workflows/release.yml` corre al subir una etiqueta estable `vX.Y.Z` o al iniciarse manualmente indicando una etiqueta existente. Valida que la etiqueta, el proyecto y el script de build tengan la misma versión, compila en Windows x64, crea el instalador, publica `SHA256SUMS.txt` y crea la Release.

La versión de esta entrega se declara en `NextDNSModernWPF/NextDNSModernWPF.csproj`, `scripts/Build-Installer.ps1` y `installer/NextDNSManager.iss`. Antes de etiquetar una versión nueva, deben coincidir las tres.

## Recuperación y desinstalación

El desinstalador intenta restaurar DNS desde el snapshot del servicio y quitar solo el CLI/servicio cuya propiedad fue comprobada. Si no confirma detener el servicio auxiliar o restaurar DNS, puede conservar archivos en lugar de eliminarlos a medias.

Los perfiles locales se guardan en `%APPDATA%\NextDNSManager`; la clave API protegida en `%LOCALAPPDATA%\NextDNS Manager`. Son datos de usuario separados de los binarios instalados. Los detalles del broker, snapshots y rollback están en [Permisos y servicio](PERMISOS_Y_SERVICIO.md) y [Arranque y bandeja](ARRANQUE_Y_BANDEJA.md).
