# Análisis: consumo sostenido de CPU en NextDNS para Windows

**Investigación:** 22 de noviembre de 2026

**Ámbito:** servicio oficial de NextDNS observado en Windows y proyecto NextDNS Manager WPF

## Conclusión ejecutiva

La evidencia apunta al núcleo DNS de NextDNS, no al código WPF del administrador. La causa concreta conocida en el núcleo es un bucle infinito al analizar la sección `Additional` de un paquete DNS cuando contiene un registro adicional que no es OPT. El analizador consulta repetidamente el mismo registro porque no lo consume. El servicio procesa cada datagrama UDP mediante ese analizador; por eso un solo paquete válido con un registro adicional no OPT puede dejar un hilo consumiendo CPU de manera sostenida.

El cambio upstream `08e4948` describe y corrige precisamente ese caso: llama a `SkipAdditional()` para avanzar el parser y agrega pruebas de regresión. La corrección está presente en la fuente del CLI oficial v1.50.2. El binario del servicio instalado en esta máquina pertenece al cliente GUI oficial, no al CLI que usa el proyecto WPF. Su metadata Go identifica `github.com/nextdns/nextdns v1.37.10`, versión anterior al arreglo.

Esto confirma que el núcleo incorporado al servicio es el lugar del problema y que la versión instalada es anterior a la corrección. No se obtuvo un volcado de CPU ni una captura del paquete disparador; por eso no se afirma que se haya observado el mismo paquete en vivo. La firma de módulo v1.37.10 identifica la base del motor, aunque por sí sola no descarta una modificación privada del proveedor.

## Evidencia recopilada

- El Administrador de tareas mostró `NextDNSService.exe` usando aproximadamente 22,8 % y luego 19,4 % de CPU durante la observación; la memoria pasó de unos 114,7 MB a 126,4 MB. Esto evidencia consumo de CPU sostenido en el servicio. El aumento puntual de memoria no demuestra por sí solo una fuga de memoria lineal.
- El servicio observado se llama `NextDNSService`, se ejecuta como `LocalSystem` y apunta a `C:\Program Files (x86)\NextDNS\NextDNSService.exe`.
- El producto registrado es el cliente GUI oficial NextDNS 3.0.13. El ejecutable del servicio tiene firma válida de NextDNS Inc. y metadata del módulo Go `github.com/nextdns/nextdns v1.37.10`.
- El endpoint oficial de descarga GUI de Windows que se consultó redirigió al instalador `NextDNSSetup-3.0.13.exe`, igual a la versión registrada. No se encontró un paquete GUI posterior que permita confirmar que la aplicación oficial ya contiene la corrección.
- La documentación de NextDNS distingue la aplicación GUI oficial de la instalación manual del CLI. El CLI v1.50.2 crea otro servicio, `nextdns`, y copia el binario a `C:\Program Files\nextdns\nextdns.exe`. No es un reemplazo binario seguro de `NextDNSService.exe` ni de `NextDNSEngine.dll`.

## Cadena técnica de la falla

1. `proxy/udp.go` recibe una consulta DNS UDP y llama a `query.New` para analizarla.
2. `resolver/query/query.go` recorre las entradas de la sección `Additional` buscando el registro EDNS OPT.
3. Para un registro no OPT, el código anterior a `08e4948` no avanzaba el parser ni terminaba el bucle.
4. La siguiente llamada a `AdditionalHeader()` devolvía otra vez ese mismo encabezado.
5. El hilo quedaba girando sin esperar entrada adicional y podía mantener un núcleo de CPU ocupado.

Un registro A en `Additional` basta para reproducir el patrón documentado por upstream. Si el servicio acepta consultas de la red, cualquier dispositivo con acceso a ese listener podría activar el caso. El modo router amplía el conjunto de dispositivos capaces de enviar consultas al servicio; no es la causa del defecto del parser.

## Qué no explica el síntoma

- El administrador WPF no tiene un bucle de sondeo de CPU continuo. Su llamada de estado ocurre al iniciar la ventana.
- El manager sí tenía una posible espera bloqueante de proceso: redirigía stdout y stderr, pero solo leía stderr. Eso puede colgar una operación de administración si stdout se llena; no explica que `NextDNSService.exe` quede consumiendo CPU.
- El salto de memoria observado es compatible con actividad y buffers, pero la evidencia disponible no prueba una fuga de memoria. La firma más fuerte es el CPU sostenido del proceso del núcleo.

## Cambios aplicados al proyecto

- Fijar el CLI oficial Windows x64 v1.50.2, validar el checksum oficial del ZIP, arquitectura PE, versión embebida y SHA-256 del ejecutable antes de incorporarlo.
- Copiar `nextdns.exe` junto al manager publicado y hacer fallar la compilación si falta o si el hash del núcleo no coincide con el binario verificado.
- Rechazar la activación si la versión del CLI es menor que 1.50.2 o si no se puede leer con formato exacto.
- Después de instalar, comprobar que el servicio está activo y que Windows lo registró desde la ruta esperada con el mismo binario fijado.
- Detectar el servicio `NextDNSService` del cliente GUI y rechazar la instalación en paralelo. Esto evita que el manager CLI compita por el DNS o sustituya archivos del producto oficial.
- Comprobar errores de instalación/desinstalación y consultar el estado antes de informar éxito. Drenar stdout y stderr simultáneamente evita bloqueos de las operaciones del manager.
- Separar “Reportar nombre de dispositivo” de “Permitir consultas de otros equipos (modo router)”; el modo router queda desactivado por defecto.
- Limpiar los archivos temporales de descarga aunque la preparación falle.

## Alcance y limitación para el equipo actual

Los cambios son para el proyecto NextDNS Manager WPF; no modifican el cliente GUI oficial que está instalado. No se debe copiar el `nextdns.exe` del CLI sobre `NextDNSService.exe`: la identidad del servicio, la ruta, el motor auxiliar y la integración del cliente GUI son distintos. Además, el usuario de Windows observado no pertenece al grupo Administradores.

Por eso, el servicio oficial activo no se actualizó durante esta investigación. Para corregir esa instalación sin cambiar de cliente se necesita una versión GUI oficial que incluya el parser arreglado, distribuida por NextDNS. La alternativa CLI v1.50.2 es una migración a otro cliente y requiere retirar primero el GUI oficial y contar con permisos de administrador.

## Referencias primarias

- [Commit upstream que corrige el bucle infinito](https://github.com/nextdns/nextdns/commit/08e4948af65f60e9fe93856388408d21c346f0f1)
- [Fuente de `resolver/query/query.go` en v1.37.10](https://github.com/nextdns/nextdns/blob/v1.37.10/resolver/query/query.go)
- [Fuente de `resolver/query/query.go` en v1.50.2](https://github.com/nextdns/nextdns/blob/v1.50.2/resolver/query/query.go)
- [Instalación para Windows: aplicación GUI y CLI manual](https://github.com/nextdns/nextdns/wiki/Windows)
- [Release oficial del CLI v1.50.2](https://github.com/nextdns/nextdns/releases/tag/v1.50.2)
