# 🛡️ NextDNS Manager

**Administrá NextDNS desde Windows con una interfaz simple, perfiles locales y acceso a tu cuenta.**

[Descargar la última versión](https://github.com/agustinbracamonte/NextDNS-Manager-WPF/releases/latest) · [Reportar un problema](https://github.com/agustinbracamonte/NextDNS-Manager-WPF/issues) · [Documentación técnica](docs/ARQUITECTURA.md)

> Proyecto independiente de NextDNS, desarrollado para Windows. Requiere una cuenta de NextDNS para las funciones de API.

![Captura real de Inicio en modo oscuro](docs/screenshots/manager-dark.png)

![Captura real de Inicio en modo claro](docs/screenshots/manager-light.png)

*Capturas generadas desde la interfaz WPF con perfiles y datos de demostración. La transparencia del escritorio puede verse diferente en una captura.*

## ¿Qué permite hacer?

- Activar, desactivar y pausar la protección desde la ventana o la bandeja del sistema.
- Guardar perfiles locales, seleccionar un perfil y cambiarlo mientras está activo.
- Consultar el DNS configurado en los adaptadores de red; `127.0.0.1` indica un resolvedor local y no identifica por sí solo su servidor remoto.
- Conectar una clave API para consultar y administrar listas de permitidos y bloqueados; añadir dominios, activar o desactivar entradas e importar o exportar listas.
- Consultar actividad por perfil y dispositivo, filtrar resultados y fechas, mostrar bloqueados y activar el modo en vivo. La disponibilidad depende de la configuración y la API de NextDNS.
- Leer y modificar los ajustes de registro y página de bloqueo disponibles en la API.
- Elegir modo claro, oscuro o automático; consultar diagnósticos y buscar actualizaciones desde la aplicación.

## Instalación

1. Descargá el instalador x64 de [Releases](https://github.com/agustinbracamonte/NextDNS-Manager-WPF/releases/latest).
2. Abrilo y completá el asistente de instalación.
3. Iniciá NextDNS Manager y agregá un perfil con su identificador de NextDNS.
4. Para las listas, actividad y ajustes remotos, ingresá y verificá tu clave API en **Ajustes**.

El instalador incluye las dependencias necesarias para Windows x64. Solicita permisos de administrador para instalar o actualizar el servicio auxiliar; **el uso habitual no requiere ejecutar la interfaz como administrador**.

El instalador no está firmado digitalmente y Windows puede mostrar una advertencia. La release incluye `SHA256SUMS.txt` para comprobar la integridad de la descarga; un hash publicado junto al instalador no acredita por sí solo la identidad del editor.

## Primeros pasos

En **Inicio** elegí un perfil y activá la protección. Podés agregar y administrar identificadores desde **Ajustes → Perfiles locales**. El estado se muestra en la ventana y hay accesos rápidos en la bandeja.

En **Listas** seleccioná el perfil de tu cuenta y administrá sus dominios. La eliminación solicita confirmación; revisá las operaciones masivas antes de aplicarlas. En **Actividad** seleccioná el perfil y, si querés, un dispositivo, un período o el filtro de bloqueados. Las horas se presentan según la zona horaria del equipo. Las consultas disponibles dependen de los registros y las opciones de privacidad de la cuenta.

En **Ajustes** podés verificar y guardar la clave API, consultar los valores actuales de un perfil y modificar las opciones disponibles. Los controles remotos se habilitan después de leer la configuración, para evitar confundir un valor desconocido con uno desactivado.

## Privacidad y seguridad

La clave API se protege localmente mediante DPAPI de Windows para el usuario actual. No compartas tu clave, tus perfiles, registros o archivos de diagnóstico sin revisarlos primero. Las opciones de API transmiten solicitudes a NextDNS; la disponibilidad de información depende de tu cuenta y de la configuración del servicio.

El servicio auxiliar administra acciones del sistema y la interfaz opera con permisos normales. Consultá [seguridad y privacidad](docs/SEGURIDAD_Y_PRIVACIDAD.md), [permisos y servicio](docs/PERMISOS_Y_SERVICIO.md) y [arquitectura](docs/ARQUITECTURA.md) para conocer sus límites y funcionamiento.

## Compilar y contribuir

Se requieren .NET SDK 10, Windows x64 e Inno Setup compatible para crear el instalador. El script prepara el CLI NextDNS verificado, publica la aplicación y el servicio, valida el contenido y crea el instalador:

```powershell
.\scripts\Build-Installer.ps1
```

Las pruebas de API y actualización usan transporte simulado; los probes del servicio incluyen pruebas sin conexión. Las instrucciones, requisitos y comandos completos están en [compilación y pruebas](docs/COMPILACION_Y_PRUEBAS.md). El flujo de GitHub Actions para publicar se ejecuta manualmente con una etiqueta existente; la release también puede generarse localmente.

Más información: [instalación y actualizaciones](docs/INSTALACION_Y_ACTUALIZACIONES.md) · [referencias visuales](docs/REFERENCIAS_VISUALES.md) · [motor NextDNS CLI](https://github.com/nextdns/nextdns).
