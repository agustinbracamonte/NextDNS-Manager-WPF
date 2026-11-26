# Texto para presentar NextDNS Manager

Borrador para compartir en GitHub Discussions o en una comunidad de Windows/NextDNS. Elegí el idioma y el foro que mejor correspondan; adaptá el saludo y respetá las reglas del lugar. No publiques datos de tu cuenta, perfil, dispositivos, actividad o clave API.

## Español

Hola, quiero compartir **NextDNS Manager**, una aplicación de escritorio independiente para administrar NextDNS en Windows.

Permite activar, desactivar o pausar la protección, guardar y cambiar perfiles locales, administrar listas permitidas y bloqueadas, consultar actividad por dispositivo, cambiar opciones disponibles de la API y usar temas claro/oscuro. Las funciones remotas requieren una clave API propia; el control local de protección no.

La versión **2.2.0** y el instalador para Windows x64 están en GitHub Releases:

https://github.com/agustinbracamonte/NextDNS-Manager-WPF/releases/latest

Es un proyecto no oficial: usa el CLI oficial de NextDNS y no reemplaza la aplicación GUI oficial. El instalador todavía no está firmado digitalmente, así que Windows podría mostrar una advertencia; la release incluye un archivo SHA-256 para comprobar la integridad de la descarga.

Si lo probás, me sirve saber qué versión de Windows usás y qué mejorarías. Compartí errores reproducibles en Issues y preguntas o ideas en Discussions. Antes de adjuntar diagnósticos, ocultá claves, IDs de perfil, dominios privados, IPs, registros y nombres de dispositivos.

Repositorio: https://github.com/agustinbracamonte/NextDNS-Manager-WPF

## English

Hi! I would like to share **NextDNS Manager**, an independent Windows desktop app for managing NextDNS.

It can enable, disable, or pause protection; save and switch local profiles; manage allow/block lists; inspect DNS activity by device; edit settings supported by the API; and use light or dark themes. Remote features require your own NextDNS API key; local protection controls do not.

Version **2.2.0** and the Windows x64 installer are available on GitHub Releases:

https://github.com/agustinbracamonte/NextDNS-Manager-WPF/releases/latest

This is an unofficial project. It uses the official NextDNS CLI and does not replace the official GUI app. The installer is not digitally signed, so Windows may show a warning; the release includes a SHA-256 file to check download integrity.

If you try it, feedback about your Windows version and possible improvements is welcome. Please use Issues for reproducible bugs and Discussions for questions or ideas. Sanitize diagnostics first: remove API keys, profile IDs, private domains, IPs, DNS logs, and device names.

Repository: https://github.com/agustinbracamonte/NextDNS-Manager-WPF

## Subir la imagen de vista previa al repo

GitHub no expone una API pública para cambiar la imagen social del repositorio. La imagen está preparada en [`assets/social-preview.png`](assets/social-preview.png) (1280 × 640). Para que aparezca al compartir el enlace, hay que cargarla una sola vez en **Settings → Social preview → Edit → Upload an image**. [Documentación de GitHub](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/customizing-your-repositorys-social-media-preview).

Para volver a generarla desde la captura oscura de demostración, ejecutá `powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File .\scripts\New-SocialPreview.ps1` desde la raíz del repositorio.
