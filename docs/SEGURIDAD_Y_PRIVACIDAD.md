# Seguridad y privacidad

Esta guía resume controles visibles en el código. No sustituye una revisión de seguridad ni garantiza seguridad ante malware o código que ya se ejecute con los mismos permisos.

## Límites de privilegios

- La ventana se ejecuta con el usuario estándar. La instalación, reparación y desinstalación del servicio requiere elevación.
- `NextDNSManagerBroker` se ejecuta como LocalSystem y acepta solo el protocolo local definido por el proyecto. No expone un servidor HTTP ni una API de control por red.
- El cliente comprueba la identidad del proceso/servicio registrado antes de conectarse a la named pipe. El pipe deniega Network, permite solo las identidades configuradas y exige SID y sesión interactiva.
- Las solicitudes se limitan a operaciones conocidas, tamaño/profundidad acotados y un perfil de seis caracteres hexadecimales. El broker no acepta comandos, argumentos ni rutas arbitrarias.
- El broker revierte la impersonación antes de ejecutar una acción privilegiada. Ejecuta el CLI desde rutas verificadas y con argumentos separados, sin shell.

Los detalles de ACL, persistencia y restauración están en [Permisos y servicio](PERMISOS_Y_SERVICIO.md).

## Clave API y tráfico remoto

La clave API opcional se guarda en `%LOCALAPPDATA%\NextDNS Manager\api-key.dpapi`, protegida con DPAPI en ámbito `CurrentUser` y escrita mediante archivo temporal. Esto dificulta que otra cuenta de Windows lea un archivo copiado, pero **no** protege contra procesos maliciosos que ya corren como el mismo usuario, administradores locales ni un equipo comprometido.

El cliente valida la clave antes de usarla y la envía en el encabezado `X-Api-Key` a `https://api.nextdns.io/`; no la añade a URLs. El código redacta el valor de la clave en errores de API y limpia buffers binarios de clave usados por DPAPI. La clave en uso existe en memoria mientras la sesión API está conectada.

La API puede devolver dominios, estados, dispositivos e información de consultas. Estos datos se muestran en Actividad y se mantienen temporalmente en memoria; el código de la UI limita la colección a 1000 registros visibles. La clave es opcional y las funciones locales de protección DNS no la necesitan.

El administrador puede configurar que NextDNS registre consultas, omita la IP del cliente, omita dominios o muestre página de bloqueo. Cambiar estos ajustes es una operación remota: se leen valores actuales, se solicita confirmación antes de aplicar y se envía un PATCH a NextDNS.

## Perfiles y DNS local

Los nombres e IDs de perfiles locales se guardan como JSON legible en `%APPDATA%\NextDNSManager\profiles.json`. Un ID de configuración NextDNS no es una contraseña, pero identifica una configuración de cuenta; mantén privado el archivo si no quieres compartirlo.

Antes de cambiar DNS, el servicio conserva el estado IPv4/IPv6 para poder restaurarlo al desactivar. El estado protegido y la copia de recuperación se guardan en el Registro HKLM con permisos restringidos. No borres manualmente esos datos mientras el servicio esté administrando DNS.

La opción **Permitir modo router** amplía la aplicación del servicio a solicitudes de otros equipos de la red. Está desactivada inicialmente; habilítala solo si pretendes extender la configuración a otros dispositivos.

## CLI y archivos instalados

El proyecto no implementa el resolver DNS. `scripts/Prepare-NextDNSCli.ps1` descarga el CLI oficial v1.50.2 para Windows x64 y valida el hash del archivo ZIP, ejecutable, arquitectura y versión embebida. El build también compara el hash del `nextdns.exe` con el valor fijado en el `.csproj`.

El servicio distingue la copia incluida para empaquetado de la copia instalada por el servicio upstream y comprueba propiedad/ruta antes de cambiar o quitar un servicio. El Manager no debe instalarse al mismo tiempo que el cliente GUI oficial NextDNS con ambos servicios activos: compiten por configurar DNS.

## Diagnósticos

Los logs del Manager usan códigos y textos fijos; no guardan deliberadamente claves API, dominios, IP ni IDs de perfil. La aplicación omite los logs si no puede escribirlos, para que una falla de registro no bloquee una operación DNS. Los detalles de ruta, límites y retención están en [Registros de diagnóstico](REGISTROS_DIAGNOSTICO.md).

## Actualizador

El actualizador limita la consulta a la API de releases del repositorio configurado, valida la versión, nombres y URLs de assets y compara el tamaño y SHA-256 del instalador con el manifiesto `SHA256SUMS.txt`. La instalación no inicia sin una acción explícita.

El SHA-256 comprueba que el archivo coincida con el manifiesto descargado; no autentica por sí solo al publicador. El instalador no está firmado digitalmente. Si el repositorio, la cuenta o la publicación se comprometieran, el instalador y su checksum podrían reemplazarse juntos. Revisa la fuente de descarga y la publicación antes de instalar.

## Uso de red

En el código actual, la red se usa para:

- API de NextDNS cuando el usuario conecta una clave;
- comprobación de releases de GitHub y descarga del instalador solo después de pedirla;
- descarga de la release del CLI durante preparación/compilación;
- consultas de DNS del CLI al servicio NextDNS configurado.

No se observa en el repositorio un canal separado de telemetría del Manager. El diagnóstico local puede contener eventos operativos sanitizados, pero no es un registro de consultas DNS.
