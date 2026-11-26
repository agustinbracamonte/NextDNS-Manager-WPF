# Compatibilidad con Windows

El instalador actual incluye aplicación, servicio y CLI de NextDNS para **Windows de 64 bits**. Se requiere Windows 10 compilación 14393 o posterior; la disponibilidad de soporte de Microsoft depende de la versión y edición. El instalador incluye el runtime .NET, por lo que no hace falta instalarlo por separado.

| Sistema | Estado |
| --- | --- |
| Windows 11 x64 | Arquitectura incluida. Se debe probar cada escenario de red y políticas del equipo. |
| Windows 10 x64 | Arquitectura incluida; la compatibilidad efectiva debe verificarse en cada versión y edición. Microsoft limita el soporte actual de .NET 10 en Windows 10 a versiones específicas de LTSC/Enterprise. |
| Windows 11 ARM64 | Puede ejecutar el paquete x64 mediante emulación; no se ha verificado todo el funcionamiento. |
| Windows 10 ARM64 y Windows de 32 bits | El instalador x64 actual no es compatible; se requieren paquetes específicos. |

Windows Home, Pro, Education, Enterprise y LTSC pueden diferir en versión, ciclo de soporte, características y políticas de red. Evitá afirmar compatibilidad en una edición o equipo particular hasta ejecutar las pruebas correspondientes. Los efectos visuales de Windows 11 tienen una alternativa para versiones anteriores.

Las pruebas GitHub Actions en `windows-2022` y `windows-2025` corren en máquinas Windows Server aisladas, no en Windows 10 u 11 de escritorio. Cubren instalación y pruebas simuladas, pero no sustituyen la validación en equipos o máquinas virtuales con las ediciones que se desee admitir.

Antes de declarar compatibilidad, probar en una máquina virtual desechable: instalación elevada, uso con cuenta estándar, detección de perfil, activación y restauración de DNS IPv4/IPv6, cambio de perfil, pausa/reanudación, reinicio, reparación y desinstalación. No ejecutar pruebas que cambien DNS en una máquina de producción.

Referencias: [requisitos de .NET en Windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows), [Windows en ARM y emulación](https://learn.microsoft.com/en-us/windows/arm/apps-on-arm-x86-emulation), [arquitecturas de Inno Setup](https://jrsoftware.org/ishelp/topic_archidentifiers.htm).
