# 12. Seguridad

PLC-Monitor puede leer y escribir en equipos industriales. La seguridad no depende de una sola contraseña:
hay varias capas que deben estar bien configuradas. Este capítulo explica qué protege cada una y qué debes
revisar antes de dejar el sistema en producción.

## 12.1. Qué se protege

| Activo | Riesgo | Protección principal |
|---|---|---|
| La API del agente | Cualquiera en la red podría leer o escribir. | Token + TLS + firewall. |
| Las credenciales del PLC | Robo de contraseñas S7. | Cifradas con `agent.key`. |
| El histórico | Fuga de datos de producción. | Permisos del sistema y token. |
| El servidor OPC UA | Lectura o escritura no autorizada. | Usuarios, certificados y tres llaves. |
| Las escrituras | Movimiento no deseado de una máquina. | Tres llaves y auditoría. |
| Los perfiles de la app | Robo del token de un agente. | DPAPI o permisos de usuario. |

No hay autenticación multiusuario dentro de la app. Quien tiene el perfil con token completo puede hacer todo
lo que permita ese token. La separación real se hace con tokens, cuentas del sistema operativo y red.

## 12.2. La API del agente

### Token

El agente exige un token en cada llamada si está configurado. Hay dos:

- **Token completo**: leer, escribir, configurar, historizar, borrar y administrar OPC UA.
- **Token de solo lectura**: leer, explorar, suscribirse y consultar el histórico. No puede escribir ni
  cambiar configuración.

El agente no guarda el token en claro. Guarda su hash SHA-256. La app envía el token como
`Authorization: Bearer ...` y el agente compara el hash.

Sin token, el agente solo puede escuchar en `127.0.0.1`, `::1` o `localhost`. Si intentas abrirlo a la red
sin token, el agente no arranca. Es una protección deliberada: una API sin autenticación en la red permite
escribir en el PLC a cualquiera que llegue al puerto.

### TLS

El instalador genera un certificado autofirmado y configura HTTPS. La app fija la huella SHA-256 del
certificado en el perfil. Si el certificado cambia, la app no conecta hasta que actualices la huella.

El certificado se guarda en `agent.pfx`. La contraseña del `.pfx` está en `agent.settings.json`. Ambos
ficheros deben tener permisos restringidos:

- Linux: propietario `plcmon`, modo `600`.
- Windows: ACL del usuario del servicio y administradores.

El certificado se genera para el host, el nombre del equipo y `localhost`. La app valida la huella fijada,
pero otros clientes TLS pueden validar además el nombre: usa uno de esos nombres o una entrada DNS local.

### Rotación

**Renovar credenciales** en la app genera un token y un certificado nuevos. El token anterior deja de valer
inmediatamente. Las apps y scripts que lo usen deben actualizarse. Si sospechas que un token se ha filtrado,
renueva y revisa los logs.

## 12.3. SSH

La instalación remota usa SSH para copiar el agente y crear el servicio.

- La primera conexión muestra la huella del servidor SSH. Compruébala antes de aceptarla. La app la fija en
  el perfil.
- La contraseña SSH y la de `sudo` se usan solo durante la operación. No se guardan en `agents.json`.
- Si usas una clave privada, protege el fichero con una passphrase y permisos `600`.
- La cuenta SSH debe tener los permisos mínimos necesarios. No uses `root` para el día a día.
- El script de instalación se ejecuta con privilegios elevados. Revisa el origen de la release antes de
  instalar.

## 12.4. Credenciales del PLC

Las contraseñas de las CPUs S7 se guardan cifradas en `agent.json` con una clave local, `agent.key`.

- `agent.key` es un secreto. Si se pierde, las contraseñas guardadas no se pueden descifrar y hay que
  volver a introducirlas.
- Si copias `agent.json` a otro equipo sin `agent.key`, las contraseñas no funcionan.
- No incluyas `agent.key` en una copia de seguridad sin protegerla.
- Al desinstalar con **Conservar agent.json**, la app conserva la configuración y la clave. Si no las
  necesitas, bórralas.

En la interfaz, la contraseña guardada se muestra como `********`. Deja ese valor para conservarla.

## 12.5. Escrituras: las tres llaves

Una escritura desde la app o desde OPC UA requiere:

1. **Dispositivo**: **Escrituras habilitadas (1.ª llave)**.
2. **Tag**: **Escribible (2.ª llave)**.
3. **OPC UA**, solo si la escritura llega por OPC UA:
   - **Permitir escribir en los PLC por OPC UA** en el servidor;
   - usuario de OPC UA con **Puede escribir**.

Además, el token de la API debe ser completo. El token de solo lectura nunca escribe.

Antes de escribir, la app pide confirmación y muestra el valor actual. La CLI también pide confirmación
salvo que uses `-y`. Cada escritura queda en la auditoría con el usuario declarado, el origen y el tipo de
credencial.

**Precaución**: las llaves no sustituyen al procedimiento de la planta. Una escritura correcta desde el
punto de vista técnico puede ser peligrosa si se hace en el momento equivocado.

## 12.6. OPC UA

El servidor OPC UA:

- no permite acceso anónimo;
- exige canales firmados y cifrados;
- valida usuario y contraseña;
- guarda las contraseñas como hash PBKDF2;
- solo acepta certificados de clientes que hayas aprobado;
- aplica las tres llaves de escritura.

La huella que muestra la pestaña **OPC UA** es la del certificado del servidor, normalmente SHA-1, que es la
que muestran clientes como UaExpert o Ignition. No la confundas con la huella SHA-256 del certificado TLS
del agente que usa la app.

Buenas prácticas:

- Un usuario de lectura para supervisión y otro de escritura para el sistema que consigna.
- Contraseñas distintas de las de SSH y de la app.
- Revisar la lista de clientes aceptados y quitar los que ya no se usen.
- No aceptar certificados sin comprobar la huella.
- No publicar `4840/tcp` en Internet.

## 12.7. La app de escritorio

La app guarda los perfiles en `agents.json`. El token:

- en Windows se protege con DPAPI del usuario actual;
- en Linux se guarda en un fichero con permisos `600` del usuario.

Esto protege frente a otros usuarios del equipo, no frente a un proceso que se ejecute con tu misma cuenta.
Cierra la sesión al ausentarte y no dejes el portátil desbloqueado.

Las vistas y paneles no contienen secretos, pero sí nombres de dispositivos y tags. Trata `trends.json` y
`panels.json` como información interna.

## 12.8. Auditoría y logs

El agente escribe en el logger `PlcMonitor.Audit`:

- escrituras;
- activación y desactivación de dispositivos;
- cambios de configuración;
- cambios de transformaciones;
- borrado y renombrado de histórico;
- cambios de OPC UA y aceptación de certificados.

Cada entrada incluye el usuario declarado por el cliente, la dirección desde la que llama y el tipo de
credencial. El usuario declarado no se puede verificar; lo que sí se verifica es el token.

Dónde verlos:

- **Linux**: `journalctl -u plcmon-agent`. La app puede mostrar las últimas líneas con **Ver log**.
- **Windows**: carpeta `logs\` del agente, un fichero por día. La retención por defecto es de 7 días.
- **App**: mensajes de la barra inferior y errores de la operación.

Los logs no deben incluir tokens ni contraseñas. Si vas a enviar un log a soporte, revisa que no contenga
nombres de planta, IPs o datos que no quieras compartir.

## 12.9. Red

- Instala el agente en una red industrial segmentada, no en la red de oficina sin control.
- Abre solo los puertos necesarios: `5311/tcp` para la app y `4840/tcp` para OPC UA.
- No expongas `5310/tcp`, `5311/tcp` ni `4840/tcp` a Internet.
- Si necesitas acceso remoto, usa VPN o un salto controlado.
- Limita la app y los clientes OPC UA por firewall a los equipos que realmente los necesitan.
- Separa la red de PLC de la red de gestión cuando sea posible.
- Revisa qué equipos pueden alcanzar el PLC; el agente no es el único camino.

## 12.10. Lista de comprobación

Antes de dejar una instalación en producción:

- [ ] El agente está instalado con TLS y token, no con la configuración local sin token.
- [ ] El token completo está guardado en un gestor de secretos, no en un correo ni en un chat.
- [ ] Existe un token de solo lectura para quien solo consulta.
- [ ] La huella del certificado del agente está fijada en el perfil.
- [ ] La huella del servidor SSH se comprobó al instalar.
- [ ] `agent.key`, `agent.json`, `agent.settings.json` y `agent.pfx` tienen permisos restringidos.
- [ ] Las escrituras están deshabilitadas por defecto y solo se activan donde se necesitan.
- [ ] OPC UA no permite anónimos y los clientes aceptados están revisados.
- [ ] El firewall solo abre los puertos necesarios.
- [ ] El reloj del agente y del PLC está sincronizado.
- [ ] Hay una copia de seguridad de la configuración y del histórico.
- [ ] Sabes cómo renovar credenciales y dónde están los logs.

## 12.11. Si sospechas un incidente

1. Deshabilita las escrituras en el dispositivo y en OPC UA.
2. Detén el servidor OPC UA si no es necesario.
3. Renueva el token y el certificado del agente.
4. Cambia las contraseñas de OPC UA y las credenciales del PLC que estuvieran guardadas.
5. Revisa los clientes OPC UA aceptados y elimina los desconocidos.
6. Revisa la auditoría y los logs del sistema.
7. Comprueba la configuración de los dispositivos y los tags: alguien con token completo pudo cambiarla.
8. Si el equipo del agente está comprometido, no confíes en sus ficheros: reinstala y restaura solo lo que
   hayas verificado.

## Siguiente paso

Para copias, actualizaciones, retención y configuración fina, sigue con
[Configuración y mantenimiento](13-configuracion-y-mantenimiento.md).
