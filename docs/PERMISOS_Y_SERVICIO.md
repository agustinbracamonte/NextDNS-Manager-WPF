# Activar y desactivar desde una cuenta normal

Desde la versión 1.2.0, la ventana se ejecuta con el token normal del usuario. El instalador registra una sola vez `NextDNSManagerBroker` como servicio LocalSystem en `Program Files\NextDNS Manager\service`. Instalar, actualizar y quitar ese componente requiere elevación; consultar y cambiar el estado desde la interfaz no.

## Comunicación y permisos

La interfaz usa una named pipe local. Antes de enviar datos, comprueba que el PID del servidor coincide con el servicio registrado en SCM, que su cuenta es LocalSystem y que tanto la ruta registrada como la del proceso corresponden al ejecutable instalado.

El instalador identifica al usuario de la sesión de Windows mediante WTS, incluso si se elevó con credenciales de otro administrador. Guarda ese SID en una clave HKLM protegida y lo conserva al actualizar. El pipe autoriza a ese usuario y a SYSTEM/Administradores mediante ACL; la petición exige además una sesión interactiva del usuario autorizado. Se deniega el SID Network. Una sesión RDP del mismo usuario también es interactiva: no se expone un control por red ni una API HTTP.

El usuario recibe sólo acceso de lectura/escritura de datos al pipe, sin permiso para crear instancias del servidor. El broker crea la primera instancia de forma exclusiva y conserva sus handles durante toda su ejecución. No recibe comandos, rutas ni argumentos arbitrarios: sólo `status`, `activate` y `deactivate`, con un ID de perfil hexadecimal de seis caracteres y dos opciones booleanas. El protocolo limita los mensajes a 4096 bytes y rechaza propiedades desconocidas o duplicadas. Las operaciones tienen plazos y se serializan.

El broker revierte la impersonación del cliente antes de ejecutar cualquier operación privilegiada. Sólo ejecuta el CLI fijado por ruta y SHA-256, mediante argumentos separados sin shell. Comprueba también la ruta y el hash del servicio CLI instalado. Un servicio `nextdns` ajeno no se adopta ni elimina por su nombre.

## DNS y persistencia

El CLI se instala una vez con `auto-activate=false`; el broker administra el DNS de los adaptadores. Esto evita el comportamiento de `nextdns deactivate` en Windows, que pone DNS en DHCP y no conserva valores estáticos previos.

Antes de cambiar una interfaz IP activa, guarda su configuración original IPv4/IPv6 en HKLM. Desactivar restaura las listas estáticas o DHCP, verifica la lectura y sólo entonces elimina la copia de recuperación. Una operación incompleta conserva esa copia para reintentar. Al aparecer nuevas interfaces, las incorpora sin sobrescribir las configuraciones originales ya guardadas.

El proxy sigue instalado al desactivar; no se reinstala con cada clic. El broker y el proxy se inician con Windows. El broker conserva la intención del usuario y reconcilia el estado al iniciar y tras cambios de direcciones de red. Cerrar la ventana no desactiva la protección.

Desde 1.2.1, el Manager registra una vez un inicio en bandeja para la sesión actual y una tarea interactiva sin elevación que lo vuelve a abrir en segundo plano al reanudar Windows si no seguía ejecutándose. La tarea responde a Power-Troubleshooter evento 1 y permite su ejecución con batería. Si el Manager ya está abierto, la ejecución en segundo plano termina sin restaurar ni duplicar su ventana. El broker recibe eventos de reanudación y disponibilidad de red, y conserva las solicitudes de revisión que llegan mientras otra sigue en curso.

Al desinstalar el Manager se restaura el DNS guardado y se elimina únicamente el servicio CLI cuya propiedad fue verificada, usando SCM. No se ejecuta `nextdns uninstall`, ya que ese comando restablece DNS a DHCP. La copia independiente del CLI y su configuración pueden permanecer en `Program Files\nextdns` sin un servicio registrado.

## Pruebas reproducibles

`tests/BrokerProbe` comprueba offline el protocolo y la validación de solicitudes. Los modos `--status` y `--malformed-live` consultan el servicio instalado desde una cuenta sin elevar. El modo `--cycle` activa y desactiva realmente, siempre intenta restaurar en `finally` y compara el DNS anterior con el final. Ese modo requiere un perfil válido guardado por el usuario y aborta si ya hay una protección activa.

No se modificó el cliente GUI oficial. Su servicio `NextDNSService` continúa bloqueando la activación paralela del CLI.

## Fuentes

- [Microsoft: seguridad de named pipes](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights)
- [Microsoft: impersonación y reversión](https://learn.microsoft.com/en-us/windows/win32/ipc/impersonating-a-named-pipe-client)
- [NextDNS v1.50.2: gestión del servicio](https://raw.githubusercontent.com/nextdns/nextdns/v1.50.2/service.go)
- [NextDNS v1.50.2: DNS en Windows](https://raw.githubusercontent.com/nextdns/nextdns/v1.50.2/host/dns_windows.go)
