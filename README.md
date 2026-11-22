# 🛡️ NextDNS Manager (WPF)

**Cliente de escritorio moderno y nativo para gestionar el servicio NextDNS en Windows.**

Este proyecto es una interfaz gráfica (GUI) construida en **WPF** y **.NET 10** que facilita la instalación, configuración y gestión del CLI oficial de NextDNS. Olvídate de usar la consola de comandos; gestiona tus perfiles y el estado del servicio con una interfaz elegante y funcional.

![Captura de Pantalla](https://i.imgur.com/0DArnUB.png)

## ✨ Características Principales

* **🎨 Interfaz Moderna:** Diseño oscuro con estilo "Dark Mode", bordes redondeados y fondo translúcido (efecto ahumado) para integrarse con Windows 10/11.
* **⚙️ Gestión de Perfiles:** Guarda y cambia rápidamente entre diferentes IDs de configuración de NextDNS (Casa, Trabajo, Niños, etc.).
* **🚀 Control Total del Servicio:** Botones intuitivos para Instalar, Desinstalar, Iniciar y Detener el servicio de NextDNS en segundo plano.
* **tray Icon:** La aplicación se minimiza a la bandeja del sistema (al lado del reloj) para no molestar mientras protege tu red.
* **🔒 Privacidad:** Permite configurar si deseas reportar el nombre del dispositivo a los logs de NextDNS.
* **📦 Portátil e instalable:** El manager se distribuye con el CLI oficial de NextDNS como archivo auxiliar o mediante un instalador completo.

## 📥 Descarga e Instalación

Puedes descargar la última versión desde la sección de **[Releases](https://github.com/TU_USUARIO/NextDNS-Manager-WPF/releases)**.

1.  Descarga el archivo `Instalador_NextDNS_Manager.exe`.
2.  Ejecútalo y sigue los pasos de instalación.
3.  Abre la aplicación, introduce tu **ID de Perfil** de NextDNS y dale a **ACTIVAR**.

## 🛠️ Tecnologías Usadas

* **Lenguaje:** C#
* **Framework:** .NET 10 (Preview/RC)
* **UI:** WPF (Windows Presentation Foundation)
* **Estilos:** XAML personalizado con `WindowChrome` para bordes modernos.
* **Empaquetado:** Inno Setup.

## Núcleo oficial y prevención de versiones vulnerables

El diagnóstico detallado del consumo de CPU, la causa en el parser DNS y el alcance sobre el cliente oficial están documentados en [docs/ANALISIS_CONSUMO_CPU_NEXTDNS.md](docs/ANALISIS_CONSUMO_CPU_NEXTDNS.md).

El manager instala el CLI oficial de NextDNS; no implementa su propio resolver. El CLI v1.50.2 incluye la corrección upstream [08e4948](https://github.com/nextdns/nextdns/commit/08e4948af65f60e9fe93856388408d21c346f0f1) del bucle de CPU al procesar registros DNS adicionales no OPT. El manager ahora rechaza activar el servicio si `nextdns.exe` es anterior a v1.50.2.

Antes de compilar o publicar, prepara el binario oficial de Windows x64 y verifica su SHA-256:

```powershell
powershell -File .\scripts\Prepare-NextDNSCli.ps1
dotnet publish .\NextDNSModernWPF\NextDNSModernWPF.csproj -c Release
```

El proyecto copia `nextdns.exe` junto al manager publicado y falla al compilar si falta o si no coincide con el hash fijado. El script fija la release v1.50.2 y verifica el hash del ZIP, el hash del ejecutable, la arquitectura y la versión embebida.

El script prepara el CLI para este manager; no reemplaza el servicio ni el motor del cliente oficial de Windows. El cliente oficial incluye su propio `NextDNSService.exe` y `NextDNSEngine.dll`; no se deben sustituir manualmente por el ejecutable del CLI. Para mantener ambos clientes, no los actives simultáneamente en la misma máquina porque compiten por el servicio DNS.

“Reportar nombre de dispositivo” y “modo router” ahora son opciones independientes. El modo router queda desactivado por defecto porque acepta solicitudes de otros equipos de la red.

## Crear el instalador personalizado

Instala .NET 10 SDK e Inno Setup 7 y ejecuta:

```powershell
powershell -File .\scripts\Build-Installer.ps1
```

El resultado es un instalador autocontenido x64 en `artifacts\NextDNS-Manager-1.1.1-Setup-x64.exe`. El asistente mantiene el tema oscuro y usa el escudo azul minimalista de NextDNS; agrega acceso directo al escritorio como opción y ofrece abrir el manager al terminar. Al desinstalar, pide confirmación antes de quitar el servicio CLI `nextdns` si está registrado. El instalador no sustituye el cliente GUI oficial de Windows.

## ⚠️ Requisitos

* Windows 10 o Windows 11 (64 bits).
* Permisos de Administrador (necesarios para configurar los adaptadores de red y servicios).

## 📄 Créditos

Este proyecto es una interfaz gráfica no oficial. Utiliza el cliente CLI oficial de [NextDNS](https://nextdns.io) como motor subyacente.

---
Hecho con ❤️ y .NET
