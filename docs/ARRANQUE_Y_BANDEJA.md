# Error de arranque e iconos duplicados

Investigación: 22 de noviembre de 2026.

## Evidencia

- Había dos procesos del Manager ejecutándose desde `C:\Program Files\NextDNS Manager`. Cada instancia creaba su propio `NotifyIcon`.
- El evento 1026 de `.NET Runtime` registró `FileNotFoundException` para `System.Diagnostics.Tracing, Version=10.0.0.0`. La pila pasó por `ControlsTraceLogger.LogUsedControlsDetails`, `Application.CriticalShutdown` y `WmQueryEndSession`.
- En la carpeta instalada faltaban `System.Diagnostics.Tracing.dll`, `NextDNSModernWPF.runtimeconfig.json`, `NextDNSModernWPF.deps.json` y `nextdns.exe`. Tampoco estaba el desinstalador ni su entrada de registro.
- El log del instalador 1.1.0 confirmó que esos archivos habían sido copiados correctamente junto con el resto del paquete. La publicación 1.1.1 también los contenía.

La carpeta había quedado incompleta después de la instalación. Los archivos residuales coincidían con los que seguían usando las instancias abiertas. Esto es compatible con una desinstalación o limpieza mientras el programa estaba abierto; no se encontró un log que identifique la operación exacta que eliminó los archivos. El código genérico `0xe0434352` por sí solo no identifica la causa; el evento de .NET y los archivos ausentes sí explican el fallo observado.

## Corrección en 1.1.2

- Una sola instancia por sesión. Al abrir de nuevo el programa, se restaura la ventana existente.
- La X conserva la función de ocultar a la bandeja. Salir y el cierre de sesión permiten terminar realmente y liberar el icono.
- El instalador y el desinstalador comprueban si existe una instancia abierta antes de modificar archivos. La comprobación también detecta versiones antiguas, que todavía no crean el mutex.
- La compilación genera un manifiesto SHA-256 de todos los archivos publicados. El instalador valida su contenido después de copiarlo.
- `scripts/Test-InstallLayout.ps1` permite verificar que una carpeta instalada contiene todos los archivos originales, incluido el runtime de .NET.

## Recuperación

Salir de las instancias antiguas y ejecutar el instalador 1.1.2 para restaurar la carpeta completa. Los perfiles se guardan en `%AppData%\NextDNSManager`, fuera de la carpeta del programa.

La reparación del Manager no requiere desinstalar ni reinstalar un servicio DNS. La activación del CLI sigue siendo una acción independiente que requiere permisos de administrador.

## Verificación realizada

- Compilación Release autocontenida x64 e instalador: correctas.
- Reparación real de `Program Files`: instalador terminó con código 0; versión registrada 1.1.2; los 474 archivos publicados coincidieron por SHA-256.
- Cuatro aperturas adicionales y seis aperturas casi simultáneas desde la instalación: una sola instancia.
- Alt+F4 ocultó la ventana; volver a ejecutar restauró el mismo proceso y la misma ventana. Ctrl+Q terminó el proceso.
- Instalación y desinstalación silenciosas con el mutex activo: ambas terminaron con código 1 sin modificar la instalación.
- `tests/LifecycleProbe` verificó los eventos de cierre de sesión y apagado dentro de WPF. Ambos permitieron cerrar la ventana real. La prueba no cierra la sesión de Windows ni apaga el equipo.
- La verificación de archivos rechazó una copia de prueba con `System.Diagnostics.Tracing.dll` ausente y otra con un hash incorrecto.

Para repetir la prueba de sesión, cerrar primero el Manager, publicar `tests/LifecycleProbe/LifecycleProbe.csproj` como aplicación autocontenida `win-x64` y ejecutar el probe con `Shutdown` y `Logoff`. No requiere paquetes de pruebas adicionales.

## Referencias

- [Inno Setup: AppMutex](https://jrsoftware.org/inno-setup/documentation/topic_setup_appmutex.htm)
- [Inno Setup: CloseApplications](https://jrsoftware.org/inno-setup/documentation/topic_setup_closeapplications.htm)
