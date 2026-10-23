# 13. Configuración y mantenimiento

Este capítulo reúne qué guarda PLC-Monitor, dónde lo guarda, cómo hacer copias y qué revisar con el tiempo.
La mayoría de los ajustes se hacen desde la app; unos pocos, como la retención global del histórico, se
cambian en `agent.json`.

## 13.1. Ficheros de la app

La app guarda sus datos en la carpeta de configuración del usuario:

- Linux: `~/.config/PLC-Monitor/`.
- Windows: `%APPDATA%\PLC-Monitor\`.

| Fichero | Contenido |
|---|---|
| `app.json` | Tema, última conexión y preferencias. |
| `agents.json` | Perfiles de agente, URL y token protegido. |
| `trends.json` | Vistas de tendencia guardadas. |
| `panels.json` | Paneles y distribución de vistas. |

Cierra la app antes de copiar o restaurar estos ficheros. Si los editas a mano, haz una copia y respeta el
formato JSON.

En Windows el token se protege con DPAPI del usuario. Una copia restaurada con otro usuario de Windows no
podrá descifrarlo: vuelve a introducir el token. En Linux el fichero tiene permisos `600`, pero el token no
va cifrado con una clave del sistema; protege la copia.

## 13.2. Ficheros del agente

Rutas por defecto:

- Linux: `/opt/plc-monitor/agent/`.
- Windows: `%ProgramFiles%\PLC-Monitor\agent\`.

| Fichero o carpeta | Contenido | ¿Entra en la copia? |
|---|---|---|
| `plcmon-agent` / `plcmon-agent.exe` | Ejecutable. | No hace falta. |
| `agent.json` | Dispositivos, tags, transformaciones, historiador y OPC UA. | Sí. |
| `agent.key` | Clave que descifra las contraseñas de `agent.json`. | Sí, es un secreto. |
| `agent.settings.json` | Token, TLS, puerto y logging. | Sí, es un secreto. |
| `agent.pfx` | Certificado TLS y su clave. | Sí, es un secreto. |
| `history/` | Ficheros SQLite del histórico. | Sí. |
| `opcua/` | Certificado del servidor OPC UA y clientes aceptados. | Sí. |
| `logs/` | Logs en Windows; en Linux solo si se activa el log a fichero. | Opcional. |
| `.bundle/` | Extracción interna del ejecutable en Linux. | No. |

Los cuatro primeros ficheros son secretos o contienen secretos. En Linux deben pertenecer a `plcmon` y
tener modo `600`; en Windows solo el servicio y los administradores deben poder leerlos.

El servicio Linux se ejecuta con `ProtectSystem=strict`, `ProtectHome=true` y solo puede escribir dentro de
`/opt/plc-monitor/agent`. Si configuras un `historian.path` absoluto fuera de esa carpeta, el historiador no
podrá escribir. Usa una ruta relativa o dentro de la carpeta del agente.

## 13.3. agent.json

`agent.json` es la configuración del motor. La app lo mantiene al añadir dispositivos, tags,
transformaciones y ajustes OPC UA. Su estructura es:

```json
{
  "devices": [],
  "historian": {
    "enabled": true,
    "path": "history",
    "retentionDays": 90,
    "flushIntervalMs": 1000
  },
  "transforms": [],
  "opcUa": {
    "enabled": false,
    "port": 4840,
    "allowWrites": false,
    "users": []
  }
}
```

Los nombres que escribe la app son camelCase. El fichero admite comentarios y comas finales.

### Historiador

- `enabled`: activa o desactiva el registro. Si está desactivado, los tags conservan su configuración pero
  no se guarda nada.
- `path`: carpeta de los ficheros, relativa a la carpeta del agente salvo que sea absoluta.
- `retentionDays`: días que se conservan las muestras. `0` significa sin límite.
- `flushIntervalMs`: cada cuánto se vuelcan las muestras acumuladas. Un valor bajo protege mejor frente a
  un corte, pero escribe más en disco.

La retención se aplica cada 6 horas y borra meses completos o, en el mes frontera, las muestras anteriores
al límite. Borrar muestras desde la app no reduce necesariamente el tamaño del fichero SQLite: SQLite reutiliza
el espacio libre. Para recuperar disco, exporta lo que necesites y elimina ficheros mensuales antiguos con el
servicio parado.

### Editar a mano

Hazlo con el agente parado o reinícialo justo después. Si la app guarda un cambio mientras tienes el fichero
abierto, puede sobrescribir tu edición. Antes de editar:

```bash
sudo systemctl stop plcmon-agent
sudo cp -p /opt/plc-monitor/agent/agent.json /opt/plc-monitor/agent/agent.json.bak
```

En Windows, para y copia con PowerShell como administrador:

```powershell
Stop-Service PLC-MonitorAgent
Copy-Item "$env:ProgramFiles\PLC-Monitor\agent\agent.json" "$env:ProgramFiles\PLC-Monitor\agent\agent.json.bak"
```

Después valida el JSON, arranca el servicio y comprueba que aparece en la app. Si el agente no arranca,
restaura la copia.

No edites `agent.key` ni cambies las contraseñas cifradas a mano. Si necesitas cambiar una contraseña,
hazlo desde la app.

## 13.4. agent.settings.json

Este fichero lo genera el instalador y contiene:

- el hash SHA-256 del token completo y, si se pidió, del token de solo lectura;
- la URL HTTPS y el puerto;
- la ruta y la contraseña de `agent.pfx`;
- opcionalmente, ajustes de logging.

Ejemplo con valores ocultos:

```json
{
  "Agent": {
    "Security": {
      "TokenSha256": "…",
      "ReadOnlyTokenSha256": "…"
    }
  },
  "Kestrel": {
    "Endpoints": {
      "Grpc": {
        "Url": "https://0.0.0.0:5311",
        "Certificate": {
          "Path": "agent.pfx",
          "Password": "…"
        }
      }
    }
  }
}
```

No lo edites para “poner el token en claro”: aquí solo va el hash. Para cambiar token, puerto o certificado,
usa **Renovar credenciales** o reinstala desde la app. Esas operaciones reescriben el fichero; los cambios
manuales que hayas hecho en él se pierden.

Puedes sobrescribir ajustes con variables de entorno con prefijo `PLCMON_` y doble guion bajo, por ejemplo
`PLCMON_Logging__LogLevel__Default=Debug`. En systemd, añádelas con un `drop-in`; no las pongas en el
`ExecStart` si contienen secretos.

## 13.5. Copias de seguridad

### Qué copiar

Copia siempre:

1. `agent.json`;
2. `agent.key`;
3. `agent.settings.json`;
4. `agent.pfx`;
5. `history/`;
6. `opcua/`.

Y, si quieres conservar la configuración de la app, `app.json`, `agents.json`, `trends.json` y
`panels.json`.

**Importante**: `Conservar agent.json al desinstalar` guarda solo `agent.json` y `agent.key`. No guarda el
histórico, el certificado ni los clientes OPC UA. Si quieres conservarlos, haz una copia completa antes.

### Cómo copiar el agente

Para que SQLite y el historiador estén consistentes, para el servicio antes de copiar.

Linux:

```bash
sudo systemctl stop plcmon-agent
sudo tar -C /opt/plc-monitor --exclude=agent/.bundle \
  -czf /var/backups/plcmon-agent-$(date +%F).tar.gz agent
sudo chmod 600 /var/backups/plcmon-agent-$(date +%F).tar.gz
sudo systemctl start plcmon-agent
```

Windows, en PowerShell como administrador:

```powershell
Stop-Service PLC-MonitorAgent
Compress-Archive "$env:ProgramFiles\PLC-Monitor\agent" "D:\backups\plcmon-agent.zip" -Force
Start-Service PLC-MonitorAgent
```

Comprueba que la copia contiene `agent.json`, `agent.key`, `agent.settings.json`, `agent.pfx` y la carpeta
`history/`. Si el agente estaba en marcha, puede haber ficheros `-wal` y `-shm`: inclúyelos; forman parte de
la base de datos.

### Restaurar

1. Para el servicio.
2. Guarda la carpeta actual por si acaso.
3. Restaura el contenido.
4. Ajusta propietario y permisos.
5. Arranca y comprueba dispositivos, histórico y OPC UA.

Linux:

```bash
sudo systemctl stop plcmon-agent
sudo mv /opt/plc-monitor/agent /opt/plc-monitor/agent.antes
sudo tar -C /opt/plc-monitor -xzf /var/backups/plcmon-agent-2026-10-23.tar.gz
sudo chown -R plcmon:plcmon /opt/plc-monitor/agent
sudo chmod 600 /opt/plc-monitor/agent/agent.json /opt/plc-monitor/agent/agent.key \
  /opt/plc-monitor/agent/agent.settings.json /opt/plc-monitor/agent/agent.pfx
sudo systemctl start plcmon-agent
```

Windows: restaura la carpeta con el servicio parado y vuelve a aplicarle las ACL restringidas a
`agent.settings.json` y `agent.pfx`.

La copia contiene secretos. Guárdala cifrada, con acceso limitado y fuera del equipo del agente. No la
subas a un repositorio ni a un almacenamiento público.

### Copia de la app

Cierra la app y copia la carpeta completa. Para restaurar en otro equipo:

- Linux: copia los ficheros y conserva permisos `600` en `agents.json`.
- Windows: el token puede no descifrarse con otro usuario. Vuelve a introducirlo en el perfil.

## 13.6. Actualizaciones

**Instalar / actualizar** en la pestaña **Agentes** sustituye el ejecutable y conserva, por defecto:

- `agent.json` y `agent.key`;
- `agent.settings.json` y `agent.pfx`, si no marcas **Regenerar token y certificado**;
- `history/` y `opcua/`; en Windows también `logs/`.

Durante la actualización el servicio se para y vuelve a arrancar. En Windows, si un antivirus o el indexador
bloquean el ejecutable, el instalador reintenta y, si no puede, vuelve a arrancar la versión anterior. En ese
caso, revisa el antivirus y vuelve a intentarlo.

Antes de actualizar:

1. Haz una copia completa.
2. Lee las notas de la versión.
3. Comprueba que tienes acceso físico o remoto al equipo.
4. Evita hacerlo en un momento de producción crítica.

Si marcas **Regenerar token y certificado**, el token anterior deja de valer y la app actualiza el perfil.
Cualquier script o cliente que usara el token anterior fallará. Actualízalo.

Después de actualizar:

- comprueba que el agente está **Conectado**;
- revisa que los dispositivos están **Conectados**;
- abre el histórico y una tendencia;
- si usas OPC UA, comprueba que el servidor está activo y que los clientes conectan;
- revisa el log por si hay avisos nuevos.

No copies un ejecutable a mano sobre el servicio en marcha. Usa la app o para el servicio antes.

## 13.7. Logs

### Linux

```bash
sudo journalctl -u plcmon-agent -n 200 --no-pager
sudo journalctl -u plcmon-agent -f
```

El log incluye el arranque, la configuración cargada, los errores de conexión y la auditoría. journald aplica
su propia rotación.

Para guardar también en fichero, añade un `drop-in` al servicio:

```bash
sudo systemctl edit plcmon-agent
```

```ini
[Service]
Environment=PLCMON_Logging__File__Path=/opt/plc-monitor/agent/logs
Environment=PLCMON_Logging__File__RetentionDays=30
```

Después:

```bash
sudo systemctl daemon-reload
sudo systemctl restart plcmon-agent
```

Los ficheros se llaman `plcmon-agent-AAAAMMDD.log`.

### Windows

El agente escribe en `%ProgramFiles%\PLC-Monitor\agent\logs\`, un fichero por día, con retención de 7 días
por defecto. El Visor de eventos solo recibe avisos y errores. La app puede mostrar las últimas líneas con
**Ver log**.

### Qué mirar

- errores de conexión al PLC y reintentos;
- contraseñas que no se pueden descifrar;
- errores de escritura;
- cambios de configuración;
- aceptación de certificados OPC UA;
- falta de espacio o errores de SQLite.

## 13.8. Mantenimiento periódico

### Cada día

- Comprueba que el agente y los dispositivos están conectados.
- Revisa si hay tags en **Bad** o dispositivos en **Faulted**.
- Mira que el histórico reciente tiene datos.

### Cada semana

- Revisa el log por errores repetidos.
- Comprueba el espacio libre y el tamaño de `history/`.
- Revisa que las tendencias y paneles siguen mostrando lo esperado.
- Si usas OPC UA, comprueba que no hay clientes rechazados sin revisar.

### Cada mes

- Haz una copia completa y verifica que se puede restaurar.
- Revisa la retención del histórico y el espacio que ocupa.
- Revisa usuarios y certificados OPC UA; elimina los que ya no se usen.
- Comprueba actualizaciones disponibles.
- Revisa que el reloj del agente y del PLC está sincronizado.

### Cada año o ante un cambio

- Renueva tokens y certificados si ha habido cambios de personal o de red.
- Revisa la lista de personas con acceso al equipo y a las copias.
- Revisa el certificado TLS; el generado por el instalador dura 10 años, pero una migración de host o un
  cambio de nombre obliga a renovarlo.
- Revisa el procedimiento de respuesta a incidentes.

## 13.9. Mover el agente a otro equipo

1. Para el agente antiguo y haz una copia completa.
2. Instala el agente en el equipo nuevo desde la app. Se generan token y certificado nuevos.
3. Para el servicio nuevo.
4. Copia `agent.json`, `agent.key` y `history/`. Ajusta propietario y permisos.
5. Arranca el servicio.
6. Actualiza el perfil de la app con la URL y el token nuevos.
7. Si usas OPC UA, vuelve a aceptar los certificados de los clientes o copia `opcua/` solo si el nombre del
   host y el certificado siguen siendo válidos.

No copies `agent.settings.json` del equipo antiguo salvo que quieras conservar el mismo token y certificado.
Si cambia el nombre del host, el certificado puede no validar.

## 13.10. Desinstalar

Desde **Agentes**, **Desinstalar** para el servicio, elimina las reglas de firewall y borra la carpeta del
agente.

Si marcas **Conservar agent.json al desinstalar**, se guardan copias de `agent.json` y `agent.key` en:

- Linux: `/opt/plc-monitor/agent.json.backup` y `/opt/plc-monitor/agent.key.backup`.
- Windows: `%ProgramData%\PLC-Monitor-agent.json.backup` y
  `%ProgramData%\PLC-Monitor-agent.key.backup`.

El resto se borra: histórico, certificado, token y clientes OPC UA. Haz una copia completa antes si
necesitas algo de eso.

## Siguiente paso

Si algo no funciona, consulta [Resolución de problemas](14-resolucion-de-problemas.md).
