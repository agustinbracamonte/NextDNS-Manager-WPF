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
* **📦 Portátil e Instalable:** Disponible como un único archivo `.exe` (portable) o mediante un instalador completo.

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

## ⚠️ Requisitos

* Windows 10 o Windows 11 (64 bits).
* Permisos de Administrador (necesarios para configurar los adaptadores de red y servicios).

## 📄 Créditos

Este proyecto es una interfaz gráfica no oficial. Utiliza el cliente CLI oficial de [NextDNS](https://nextdns.io) como motor subyacente.

---
Hecho con ❤️ y .NET
