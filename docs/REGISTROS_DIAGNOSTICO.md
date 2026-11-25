# Registros de diagnóstico

El botón **Registros de diagnóstico** de la ventana principal reúne los eventos recientes de la aplicación y del servicio auxiliar. La consulta y lectura de registros funciona desde la sesión normal del usuario; no requiere elevación.

## Dónde se guardan

- La aplicación escribe archivos diarios en `%LOCALAPPDATA%\NextDNS Manager\Logs\Manager-AAAA-MM-DD.log`.
- El servicio escribe eventos en el registro **Aplicación** de Windows, con origen `NextDNSManagerBroker`. La ventana muestra los últimos 30 días. Esos eventos también se pueden consultar desde el Visor de eventos de Windows.

Los archivos de la aplicación se limitan a 30 fechas y a 512 KiB por archivo diario. Errores repetidos del mismo tipo se limitan a un evento por minuto en la aplicación y cada cinco minutos en el servicio; el siguiente evento informa cuántos se omitieron. Si un archivo diario alcanza su límite, las entradas adicionales de ese día se descartan.

El registro de Windows tiene su propio límite global de tamaño y retención, configurado por Windows. Por eso, el Manager filtra los eventos del servicio a 30 días, pero Windows puede rotarlos antes. La ventana consulta directamente el proveedor `NextDNSManagerBroker`, examina como máximo 1000 eventos coincidentes y muestra como máximo 250.

## Privacidad y límites

Los registros de esta versión usan códigos de evento y textos fijos. No guardan mensajes de excepción, stack traces, rutas, IDs de perfiles, dominios ni direcciones IP. La lectura del registro del servicio valida el formato de cada evento y reconstruye su texto desde la lista local de eventos permitidos; no muestra mensajes libres antiguos del mismo origen.

La aplicación no puede registrar un corte de energía, una terminación forzada del proceso o una falla que ocurra antes de que el sistema de registro esté disponible. Si el directorio local no se puede escribir, la aplicación omite el registro para no bloquear la interfaz ni el cambio de DNS. El acceso a **Visor de eventos** depende de la disponibilidad del registro Aplicación en Windows.

Los archivos heredados que existan fuera de `%LOCALAPPDATA%\NextDNS Manager\Logs` no se importan ni se modifican automáticamente.
