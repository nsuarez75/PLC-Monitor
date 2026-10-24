# 14. Resolución de problemas

Los fallos en PLC-Monitor suelen estar en una de estas capas: la app, la conexión con el colector, el
dispositivo, un tag concreto, el histórico o el servidor OPC UA. Antes de cambiar cosas al azar, identifica
la capa y mira el mensaje exacto.

## 14.1. Antes de empezar

1. Anota la hora del fallo y el texto exacto del error.
2. Comprueba si afecta a un tag, a un dispositivo, a toda la app o solo a OPC UA.
3. No reinicies el colector ni el PLC sin necesidad: un reinicio borra pistas y puede interrumpir producción.
4. No borres el histórico ni la configuración para “probar”.
5. Si el fallo empezó tras un cambio, piensa qué cambió: red, contraseña, firmware, dirección, actualización.

La barra inferior de la app muestra el resultado de la última operación. Si el mensaje es rojo, la operación
no se completó.

## 14.2. La app no conecta con el colector

La app necesita una URL correcta, red, token y, si el perfil lo tiene, la huella del certificado.

| Mensaje | Causa habitual | Qué hacer |
|---|---|---|
| **El certificado del colector en … no es el esperado** | El colector se reinstaló, cambió de equipo o cambió el certificado. | Actualiza la huella en el perfil o vuelve a instalar/renovar. |
| **El colector en … rechaza el token de acceso** | Token rotado, mal copiado o de otro colector. | Copia el token actual o renueva credenciales. |
| **No se puede conectar con el colector en …** | Servicio parado, URL/puerto incorrecto, firewall o red. | Comprueba servicio, URL y puerto. |
| **El colector en … no responde a tiempo** | Red lenta, equipo saturado o bloqueado. | Comprueba el equipo y la red; reintenta. |
| **… no es un colector de PLC-Monitor (o es de otra versión)** | La URL apunta a otro servicio o el colector es antiguo. | Revisa la URL y actualiza el colector. |
| **URL no válida: …** | Falta el esquema o el puerto está mal. | Usa `https://equipo:5311` o la URL del perfil. |

Pasos:

1. Comprueba que el colector está en marcha. En **Colectores**, pulsa **Estado**.
2. Comprueba la URL del perfil. Debe incluir `https://` si el colector se instaló con TLS.
3. Desde el equipo de la app, prueba el puerto:

   ```bash
   nc -vz planta-demo.local 5311
   ```

   En Windows:

   ```powershell
   Test-NetConnection planta-demo.local -Port 5311
   ```

4. Si no responde, revisa firewall, VLAN, rutas y VPN. El instalador abre el puerto en el firewall del
   colector, pero no puede abrir un firewall de red intermedio.
5. Si el colector está en el mismo equipo, prueba `https://127.0.0.1:5311` con el perfil correcto. No mezcles
   un perfil con huella fijada con otro colector.
6. Si la cabecera indica **solo lectura**, la conexión funciona: el token no permite cambiar nada. Es
   intencionado.

Cuando se pierde una conexión ya establecida, la app muestra “Conexión con el colector perdida… Reintentando”.
No hace falta cerrar la app; si no vuelve, revisa el colector y la red.

## 14.3. El colector no arranca o no aparece

### Linux

```bash
sudo systemctl status plcmon-agent
sudo journalctl -u plcmon-agent -n 100 --no-pager
```

### Windows

En PowerShell como administrador:

```powershell
Get-Service PLCMonitorAgent
```

Y revisa `%ProgramFiles%\PLC-Monitor\agent\logs\`.

Causas frecuentes:

- **Sin token y escucha en red**: el colector no arranca. El log lo dice: “El colector escucha en … sin token
  de acceso”. Instálalo desde la app o configura un token.
- **Puerto ocupado**: cambia el puerto del colector o libera el que lo usa.

  ```bash
  ss -ltnp | grep 5311
  ```

- **`agent.json` inválido**: el colector no puede cargar la configuración. Restaura la copia anterior.
- **Contraseñas que no se descifran**: falta `agent.key` o es de otro equipo. Vuelve a introducir las
  contraseñas de los dispositivos que indique el log.
- **Permisos**: en Linux, el servicio corre como `plcmon` y solo escribe en su carpeta. Un fichero de otro
  propietario o una ruta de histórico fuera de la carpeta puede impedir el arranque o el registro.
- **Disco lleno**: el colector y SQLite necesitan espacio. Libera espacio o reduce la retención.
- **Ejecutable bloqueado en Windows**: antivirus o indexador. Revisa la exclusión y vuelve a actualizar.

Si el servicio está activo pero la app no conecta, comprueba la dirección de escucha y el firewall. El
colector puede estar escuchando solo en `127.0.0.1` si se arrancó sin la configuración instalada.

## 14.4. El dispositivo no conecta o está en fallo

El estado **En fallo** significa que el colector no pudo abrir o mantener la conexión. El detalle bajo el
nombre del dispositivo suele indicar la causa. El colector reintenta solo, con espera creciente; no hace
falta reiniciarlo para cada intento. Si el dispositivo está **Desactivado**, actívalo: no es un fallo.

Comprueba primero:

1. Que el PLC o equipo está encendido y responde en la red.
2. Que la IP o el nombre del dispositivo es correcto.
3. Que el puerto no está bloqueado por firewall o por un switch con ACL.
4. Que no hay otra máquina usando la misma IP.
5. Que el PLC acepta conexiones desde el equipo del colector, no solo desde tu PC.

### S7 absoluto (PUT/GET)

| Síntoma | Causa probable | Acción |
|---|---|---|
| Error S7 `0x8104` | PUT/GET deshabilitado o DB optimizado. | Habilita **Permitir acceso vía comunicación PUT/GET** en la CPU, carga la configuración y usa DB no optimizados. |
| La CPU rechaza la conexión | Rack/slot incorrectos, puerto cerrado o CPU inaccesible. | Revisa rack y slot; en S7-300 el slot suele ser 2 y en S7-1200/1500, 1. |
| Timeout | IP, ruta o firewall. | Comprueba `102/tcp` y la ruta al PLC. |
| Valor absurdo | Dirección o tipo incorrecto. | Revisa `DB1.DBW`, `DB1.DINT`, `DB1.REAL` y el tipo del tag. |

Este driver no lee DB optimizados ni explora símbolos. Si necesitas símbolos o DB optimizados, usa el
driver S7 simbólico.

### S7 simbólico (S7CommPlus)

| Mensaje | Causa probable | Acción |
|---|---|---|
| **La CPU necesita contraseña** | Falta la contraseña del nivel de acceso. | Escríbela en el dispositivo. |
| **Contraseña incorrecta** o **Acceso denegado** | Contraseña o usuario incorrectos. | Revisa el nivel de acceso y el usuario; vuelve a guardar la contraseña. |
| **La CPU no acepta comunicación segura (TLS)** | La CPU es antigua o la comunicación segura está desactivada. | Prueba **Automática**; si la CPU es anterior a TIA V16, usa **Sin TLS** y valora el riesgo. |
| **La CPU rechazó la autenticación clásica** | La CPU exige TLS o no reconoce la clave. | Usa **Automática** o **Solo TLS**; revisa la configuración de comunicación segura. |
| **Elemento no disponible en la CPU** | El símbolo no existe o no es accesible. | Usa **Explorar** y copia el nombre exacto. |

No bajes a **Sin TLS** por comodidad. Solo tiene sentido en una red y una CPU que no admiten otra cosa.

### Modbus

| Síntoma | Causa probable | Acción |
|---|---|---|
| Timeout | IP/puerto, unit id, cableado o equipo parado. | Revisa `502/tcp`, el unit id y la alimentación. |
| Excepción 01 | Función no admitida. | Si falla al escribir un solo registro, desactiva las funciones de escritura simple 05/06. |
| Excepción 02 | Dirección inexistente o numeración desde 0/1. | Revisa la tabla y **Numerar desde 1**; prueba la dirección en el manual del equipo. |
| Excepción 0A/0B | La pasarela no llega al esclavo. | Revisa unit id, ruta y ajustes serie de la pasarela. |
| `FLOAT` absurdo | Orden de bytes. | Prueba `CDAB` o indica el orden en la dirección: `HR100:FLOAT:CDAB`. |
| RTU no abre el puerto | Permisos o puerto ocupado. | Comprueba el grupo `dialout`/`uucp`, el nombre del puerto y que otro programa no lo use. |
| Puerto serie con ajustes distintos | Otro dispositivo usa el mismo puerto con otra velocidad o paridad. | Iguala los ajustes de todos los dispositivos del bus. |

En RTU, el usuario del servicio debe poder abrir el puerto. El instalador lo añade a `dialout` o `uucp`.
Si lo cambias a mano, reinicia el servicio para que cargue el grupo.

## 14.5. Tags con calidad mala

La columna **Calidad** muestra el estado. Un tag desactivado aparece como **Desactivado** y no es un fallo.

| Calidad | Significado | Qué mirar |
|---|---|---|
| `Good` | Valor fiable. | Nada. |
| `Uncertain` | Valor dudoso o pendiente. | Puede ser una respuesta lenta del equipo. |
| `BadNotConnected` | El dispositivo no está conectado. | Estado y detalle del dispositivo. |
| `BadAddressInvalid` | La dirección no existe o no es válida para el driver. | **Probar**, sintaxis y tipo. |
| `BadAccessDenied` | Permisos, contraseña o protección del PLC. | Contraseña, PUT/GET, nivel de acceso. |
| `BadTypeMismatch` | El valor no se puede convertir al tipo. | Tipo del tag, valor de escritura y escalado. |
| `BadTimeout` | El equipo no respondió a tiempo. | Red, carga del PLC y timeout. |
| `BadConfiguration` | Configuración inválida del tag o del dispositivo. | Dirección, tipo y configuración del dispositivo. |
| `BadNotSupported` | El driver no admite la operación. | Función Modbus o tipo no soportado. |
| `BadWriteDisabled` | La escritura está deshabilitada. | Llave del dispositivo o del tag. |
| `Bad` | Error genérico. | El detalle suele traer el mensaje del PLC. |

Pasos:

1. Abre el detalle del tag y mira el texto de error.
2. Pulsa **Probar**. Si el dispositivo conecta pero el tag falla, el problema está en la dirección, el tipo
   o el permiso.
3. En S7 simbólico, usa **Explorar** para comprobar que el símbolo existe.
4. Compara con otro tag del mismo dispositivo: si todos fallan, es del dispositivo; si falla uno, es del tag.
5. Si el fallo aparece solo al escribir, revisa las tres llaves de escritura y el valor.

### Una escritura no funciona

Comprueba, en este orden:

1. El token de la app es completo, no de solo lectura.
2. **Escrituras habilitadas** en el dispositivo.
3. **Escribible** en el tag.
4. El tag no está desactivado.
5. El valor tiene el tipo y el rango correctos.
6. Si hay escalado, el valor introducido se puede convertir al valor bruto.
7. El PLC no rechaza la escritura por protección o por estado.

Si la escritura llega por OPC UA, añade las comprobaciones de la sección 14.8.

## 14.6. No hay datos en el histórico

El histórico solo guarda tags activos, historizados y conectados. Revisa en orden:

1. **¿Está activo el historiador?** En la app, el detalle del tag avisa si está desactivado. En la CLI:

   ```bash
   plcmon agent info
   ```

2. **¿El tag está historizado?** El icono de histórico debe aparecer en la fila y **Histórico** debe estar
   activo en el detalle.
3. **¿El tag está activo?** Un tag desactivado no se lee ni se historiza.
4. **¿El dispositivo estaba conectado en ese periodo?** Durante una desconexión no hay muestras.
5. **¿La frecuencia y la banda muerta son las adecuadas?** Un intervalo mínimo alto, una banda muerta grande
   o un disparo que nunca se cumple pueden dejar el histórico casi vacío.
6. **¿La consulta incluye el periodo?** Revisa la hora de inicio y fin y la zona horaria. La app muestra la
   hora local; el colector guarda UTC.
7. **¿La retención borró los datos?** Si la política es de 30 días, no hay nada de hace tres meses.
8. **¿Hay espacio en disco?** Un disco lleno impide volcar. Mira el log y el espacio libre.
9. **¿Se renombró el tag o el dispositivo?** Si no aceptaste el traslado del histórico, las muestras
   antiguas siguen bajo el nombre anterior.
10. **¿Hay demasiados puntos?** Si la app avisa “hay más puntos: usa un agregado o un periodo más corto”,
    ajusta el periodo o el agregado. La consulta de la app pide como máximo 20.000 puntos.

Si el histórico estaba funcionando y dejó de hacerlo tras un corte, revisa el log por errores de SQLite o
de disco. No borres los ficheros `history-*.db` sin copiarlos antes.

## 14.7. Tendencias y paneles

- **Una vista sale vacía**: no hay histórico en el periodo, el tag no es dibujable o la consulta falló.
  Prueba un periodo más amplio y revisa el histórico del mismo tag.
- **Los datos no coinciden con el PLC**: la vista histórica usa el histórico, no el valor en vivo. La
  ventana en vivo solo conserva lo recibido desde que la app empezó a leer ese tag, como máximo una hora.
- **Un panel muestra un error en una celda**: esa vista no se pudo cargar; las demás siguen. Corrige la vista
  o vuelve a añadirla.
- **Un panel no muestra datos de otro colector**: las vistas y los paneles guardados pertenecen a un colector
  concreto. Al cambiar de colector, los del anterior no aparecen; no se han perdido.
- **El zoom se mueve en todos los paneles**: **Sincronizar zoom** está activo. Desactívalo si quieres moverlos
  por separado.
- **Cambios que no se ven**: pulsa **Guardar** en la vista o el panel. Cambiar el título o el rango no guarda
  solo.

## 14.8. OPC UA

### El servidor no arranca

En la pestaña **OPC UA**, el estado y el detalle indican el motivo.

- **Desactivado**: activa **Servidor OPC UA** y guarda.
- **El puerto está ocupado**: otro programa usa `4840`. Cambia el puerto y abre el nuevo en el firewall.
- **Error de certificado o de permisos**: revisa `opcua/` y los permisos de la carpeta del colector.
- **Error al crear la PKI**: en Linux, el servicio necesita escribir en `opcua/`; en Windows, revisa las ACL.

El servidor OPC UA puede estar en **Error** sin tumbar al colector: la app y los PLC siguen funcionando.

### El cliente no conecta

1. Comprueba la URL: `opc.tcp://planta-demo.local:4840`. Si usas una IP y el certificado se emitió para el
   nombre, la validación puede fallar. Usa el mismo nombre del equipo.
2. Comprueba el firewall: `4840/tcp` debe estar abierto desde el cliente.
3. Acepta el certificado del servidor en el cliente. La huella SHA-1 que muestra la pestaña **OPC UA** debe
   coincidir.
4. El servidor no acepta anónimos: configura usuario y contraseña.
5. El servidor solo ofrece canales firmados y cifrados. Un cliente configurado con política `None` será
   rechazado.
6. Si el certificado del cliente está pendiente, apruébalo en la pestaña **OPC UA**.

Un aviso `BadCertificateUntrusted` en el arranque del servidor sobre su propio certificado autofirmado es
normal. No impide que los clientes conecten tras confiar en él.

### Usuario o contraseña incorrectos

- El nombre de usuario no distingue mayúsculas; la contraseña sí.
- Si cambias la contraseña, guarda y vuelve a probar. La app no muestra la contraseña guardada.
- Si el usuario se creó sin contraseña, no podrá autenticarse.
- El servidor limita los intentos de autenticación fallidos: espera unos segundos antes de reintentar.

### No aparecen tags

Deben cumplirse las dos publicaciones:

1. El dispositivo tiene **Publicar en OPC UA** activo.
2. El tag tiene **Publicar en OPC UA** activo.

Además, el tag debe estar activo. Revisa la carpeta del dispositivo en `Objects > PLC-Monitor`. Si cambiaste
el nombre de un tag, el cliente puede seguir usando el NodeId antiguo.

### No se puede escribir por OPC UA

Comprueba las cuatro condiciones:

1. **Permitir escribir en los PLC por OPC UA** en el servidor.
2. Usuario OPC UA con **Puede escribir**.
3. **Escrituras habilitadas** en el dispositivo.
4. **Escribible** en el tag.

Y que el valor sea válido para el tipo. Los errores habituales:

- `BadNotWritable`: falta la tercera llave, o el dispositivo/tag no permite escribir.
- `BadUserAccessDenied`: el usuario es de solo lectura o la contraseña no es correcta.
- `BadTypeMismatch`: el valor no encaja con el tipo del tag.
- `BadNodeIdUnknown`: el cliente usa un NodeId que ya no existe.

## 14.9. CLI

La CLI no forma parte de la release de escritorio actual; si la usas, compílala como se explica en el
[capítulo 11](11-cli.md).

- **`command not found`**: no está en el `PATH` o no la has compilado.
- **`Unauthenticated`**: falta `--token` o el token es antiguo.
- **`Unavailable`**: URL, puerto o colector parado.
- **`PermissionDenied`**: estás usando el token de solo lectura para una orden que cambia algo.
- **`Unimplemented`**: el colector es antiguo y no tiene esa orden. Actualízalo.
- **Código de salida 1**: error de conexión, lectura con calidad mala o escritura fallida. El mensaje de la
  CLI indica cuál.
- **Código de salida 2**: argumentos inválidos o una confirmación cancelada.
- **Código de salida 3**: escritura cancelada por el usuario.

Para aislar un problema de driver, usa una orden directa contra el PLC (sin colector) y compárala con la orden
a través del colector. Si la directa funciona y la del colector no, el problema está en la configuración del
colector o en la red entre colector y PLC.

## 14.10. Checklist de diagnóstico

1. ¿La app está conectada al colector? Mira la barra superior y **Colectores > Estado**.
2. ¿El colector es la versión esperada? **Estado** o `plcmon agent info`.
3. ¿El dispositivo está **Conectado**? Mira el detalle bajo el nombre.
4. ¿El fallo es de un tag o de todos? Compara varios tags del mismo dispositivo.
5. ¿La dirección pasa **Probar**? Si no, revisa sintaxis, tipo y permisos.
6. ¿El log del colector muestra el mismo error? **Colectores > Ver log**.
7. ¿La red permite el puerto? PLC `102`/`502`, colector `5311`, OPC UA `4840`.
8. ¿Cambió algo justo antes? IP, contraseña, firmware, configuración del PLC, actualización o firewall.
9. ¿El problema es de escritura? Revisa las llaves y el valor.
10. ¿El problema es del histórico? Revisa historización, conexión, periodo y retención.

## 14.11. Reiniciar, actualizar y reinstalar

### Reiniciar el colector

Linux:

```bash
sudo systemctl restart plcmon-agent
sudo systemctl status plcmon-agent
```

Windows, en PowerShell como administrador:

```powershell
Restart-Service PLCMonitorAgent
Get-Service PLCMonitorAgent
```

El colector vuelca el histórico al parar. Evita reiniciarlo en mitad de un turno si puedes esperar.

### Actualizar

Usa **Instalar / actualizar** en **Colectores**. Conserva configuración, histórico y, salvo que marques
**Regenerar token y certificado**, las credenciales. Si el colector es muy antiguo y la app avisa de que no
admite una operación, actualízalo.

### Reinstalar sin perder datos

1. Haz una copia completa.
2. Instala o actualiza desde la app.
3. Si algo falla, restaura la copia como se explica en
   [Configuración y mantenimiento](13-configuracion-y-mantenimiento.md).

No borres `agent.key` ni `history/` para “empezar limpio” salvo que quieras perder las contraseñas y los
datos.

### La app no arranca o se queda con una configuración rota

Cierra la app y haz una copia de la carpeta de datos. Renombra `app.json` para que se regenere; si el
problema está en los perfiles, renombra también `agents.json`, pero tendrás que volver a introducir URL y
token. No borres `trends.json` ni `panels.json` si quieres conservar las vistas.

## 14.12. Qué enviar a soporte

Incluye:

- versión de la app y del colector;
- sistema operativo del colector y de la app;
- driver y configuración del dispositivo, sin contraseñas;
- nombre y dirección del tag, sin datos confidenciales;
- texto exacto del error y hora aproximada;
- últimas líneas del log del colector;
- qué cambió antes del fallo.

**Nunca envíes** tokens, contraseñas, `agent.key`, `agent.pfx` ni copias de `agent.settings.json`. Si el log
contiene nombres de planta o IPs, redáctalos antes de compartirlo.

## Siguiente paso

Con esto termina el manual. Vuelve al [índice](README.md) para consultar cualquier capítulo.
