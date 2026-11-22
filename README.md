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

Puedes descargar la última versión desde la sección de **[Releases](https://github.com/agustinbracamonte/NextDNS-Manager-WPF/releases)**.

1.  Descarga el instalador `NextDNS-Manager-<versión>-Setup-x64.exe` de la última versión.
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

El resultado es un instalador autocontenido x64 en `artifacts\NextDNS-Manager-1.3.1-Setup-x64.exe`. El asistente mantiene el tema oscuro y usa el escudo azul minimalista de NextDNS; agrega acceso directo al escritorio como opción y ofrece abrir el manager al terminar. Instala el servicio auxiliar de control una sola vez. El instalador no sustituye el cliente GUI oficial de Windows.

La versión 1.1.2 evita instancias e iconos duplicados y bloquea la instalación o desinstalación mientras el Manager siga abierto. La X oculta la ventana; usa **Salir** desde la bandeja para cerrar el programa antes de reparar o actualizar. El instalador verifica todos los archivos copiados mediante SHA-256. Puedes repetir esa comprobación con `powershell -File .\scripts\Test-InstallLayout.ps1 -Directory 'C:\Program Files\NextDNS Manager'`. El diagnóstico del error de arranque está en [docs/ARRANQUE_Y_BANDEJA.md](docs/ARRANQUE_Y_BANDEJA.md).

## ⚠️ Requisitos

* Windows 10 o Windows 11 (64 bits).
* Permisos de Administrador para instalar, actualizar o desinstalar. La interfaz normal permite activar y desactivar sin elevación desde la versión 1.2.0.

El [servicio auxiliar de control](docs/PERMISOS_Y_SERVICIO.md) limita el acceso al usuario de la sesión que instaló el Manager y conserva el DNS anterior al desactivar. El CLI permanece instalado entre activaciones. Cerrar la interfaz conserva el estado de protección elegido.

El Manager se registra una vez para iniciar en la bandeja al comenzar la sesión de Windows. Se puede desactivar desde la sección Aplicaciones de inicio del Administrador de tareas. La protección DNS funciona mediante los servicios de Windows aunque la ventana esté cerrada, y el servicio comprueba su estado al reanudar desde suspensión o hibernación.

## Actualizaciones

El Manager consulta automáticamente la última versión estable de GitHub al iniciarse y cada 24 horas mientras permanece abierto en la bandeja. Al detectar un instalador más reciente muestra un aviso y un acceso en la ventana. También puedes elegir **Buscar actualizaciones** en el menú del icono de la bandeja. Una falla de red no bloquea el inicio ni la protección.

El aviso abre la página oficial de Releases, donde puedes descargar el instalador y ejecutarlo. Windows pedirá permisos de administrador para actualizar el servicio. Cierra el Manager desde la bandeja antes de instalar. La búsqueda automática no descarga ni ejecuta archivos por su cuenta. El instalador actualmente no está firmado digitalmente y Windows puede mostrar una advertencia.

Para publicar una versión, actualiza las versiones de los proyectos, `Build-Installer.ps1` y `NextDNSManager.iss`; integra los cambios en `main` y sube una etiqueta estable `vX.Y.Z` que coincida con esas versiones. El workflow `release.yml` compila el instalador para Windows, verifica el CLI, genera `SHA256SUMS.txt` y crea la Release con ambos archivos. El hash detecta errores de descarga; una firma digital de código requeriría un certificado de publicación.

## 📄 Créditos

Este proyecto es una interfaz gráfica no oficial. Utiliza el cliente CLI oficial de [NextDNS](https://nextdns.io) como motor subyacente.

---
Hecho con ❤️ y .NET
