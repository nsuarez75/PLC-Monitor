# PLC-Monitor — Agente (`plcmon-agent`)

> Fase 2 (en desarrollo, aún sin publicar) · Última actualización: 2026-10-14 · [English](../en/agent.md)

El agente es un servicio sin interfaz que vigila varios PLC a la vez, mantiene el último valor de cada tag y lo
ofrece a sus clientes por **gRPC**: la futura aplicación de escritorio, el historiador, OPC UA... Cerrar la app no
corta la recogida de datos, y un agente remoto funciona igual que el local.

```
 cliente gRPC (app, scripts...)
          │  HTTP/2 · 127.0.0.1:5310
          ▼
 ┌──────────── plcmon-agent ────────────┐
 │ AgentGrpcService  (API)              │
 │ TagEngine         caché + difusión   │
 │ DeviceSupervisor  uno por PLC:       │
 │   conexión, reconexión, grupos       │
 └──────┬───────────────┬───────────────┘
        ▼               ▼
     PLC 1   ...     PLC N    (drivers s7-symbolic / s7-absolute)
```

## 1. Configuración (`agent.json`)

El agente lee `agent.json` de su misma carpeta. Se puede cambiar la ruta con `--Agent:ConfigPath=/ruta/agent.json`
o con la variable de entorno `PLCMON_Agent__ConfigPath`. Si el fichero no existe, el agente arranca sin dispositivos.
Junto al ejecutable hay un ejemplo, `agent.sample.json`. El JSON admite comentarios `//` y comas finales.

```jsonc
{
  "devices": [
    {
      "name": "prensa1",                  // único; sin '/'
      "driver": "s7-absolute",            // s7-absolute o s7-symbolic
      "enabled": true,                    // false: configurado pero sin conectar
      "writesEnabled": false,             // 1.ª llave de escritura
      "settings": { "host": "192.168.0.10", "cpu": "S71200", "rack": 0, "slot": 1 },
      "reconnect": { "initialDelayMs": 1000, "maxDelayMs": 30000, "multiplier": 2 },
      "scanGroups": [
        {
          "name": "rapido",
          "intervalMs": 500,              // mínimo 50
          "tags": [
            { "name": "velocidad", "address": "DB10.DBW0" },
            { "name": "marcha", "address": "DB10.DBX2.0", "writable": true, "dataType": "Boolean" },
            { "name": "presion", "address": "DB10.REAL4",
              "history": { "enabled": true, "minIntervalMs": 1000, "deadband": 0.5, "deadbandMode": "Absolute", "maxIntervalMs": 60000 } }
          ]
        }
      ]
    }
  ],
  "historian": { "enabled": true, "path": "history", "retentionDays": 90, "flushIntervalMs": 1000 }
}
```

| Campo | Significado |
|---|---|
| `settings` | Los mismos ajustes que el driver (`host`, `cpu`, `rack`, `slot`, `password`, `security`...). Ver el [manual de uso](usuario.md) |
| `reconnect` | Espera entre intentos de conexión: `initialDelayMs`, luego ×`multiplier` en cada fallo, hasta `maxDelayMs`. Al conectar vuelve al valor inicial |
| `scanGroups` | Grupos de escaneo (*scan classes*): los tags de un grupo se leen juntos cada `intervalMs` |
| `tags[].name` | Nombre del tag, único en el dispositivo. Si se omite se usa la dirección |
| `tags[].address` | Dirección en la sintaxis del driver |
| `tags[].writable` | 2.ª llave de escritura |
| `tags[].dataType` | Tipo con el que se interpretan los valores escritos, si el driver no lo deduce de la dirección |
| `tags[].history` | Historización del tag (ver [Histórico](#histórico)). Sin ella, el tag no se guarda |
| `historian` | Ajustes globales del historiador: activo, carpeta, días de retención y cada cuánto se vuelca a disco |

Al arrancar se valida toda la configuración. Si algo está mal, el agente no arranca y muestra todos los errores juntos.
Las direcciones con sintaxis incorrecta no impiden arrancar: esos tags quedan con calidad `BadConfiguration`.

> ⚠️ En esta versión la contraseña de los PLC simbólicos se guarda en claro en `agent.json`. Protege el fichero
> (permisos solo para el usuario del servicio). Está previsto cifrarla.

### Histórico

El agente guarda en disco los tags con `history.enabled`, aunque la app esté cerrada.

| Campo de `history` | Significado |
|---|---|
| `minIntervalMs` | Intervalo mínimo entre muestras (0 = cada cambio). Si el valor cambia más rápido, se guarda el **último** al cumplirse el intervalo: no se pierde |
| `deadband` / `deadbandMode` | Banda muerta: no se guardan cambios numéricos menores (`Absolute`) o menores en % del último valor guardado (`Percent`). 0 = cualquier cambio |
| `maxIntervalMs` | Tiempo máximo sin guardar: se repite el valor actual aunque no cambie (0 = nunca). Útil para saber que el dato sigue vivo |

- Los **cambios de calidad** (pérdida de conexión, error de lectura) se guardan siempre: así se ven los huecos.
- Ficheros: SQLite en `historian.path` (por defecto `history/` junto a `agent.json`), **uno por mes**
  (`history-AAAA-MM.db`), con las tablas `tags` y `samples` (`t` en milisegundos Unix UTC, `num`, `txt`, `q` = calidad).
  Se pueden copiar o abrir con cualquier visor de SQLite.
- **Retención**: cada 6 h se borran los meses enteros anteriores a `retentionDays` y las muestras viejas del mes frontera.
  `0` = sin límite.
- Si se **renombra** un tag, el histórico anterior se queda con el nombre antiguo.
- Consulta: en la app (pestaña **Histórico**), en la consola (`plcmon agent history`) o por la API (`QueryHistory`).

## 2. Comportamiento

- **Un supervisor por PLC.** Cada dispositivo conecta, lee y reconecta por su cuenta: un PLC caído no afecta a los demás.
- **Reconexión.** Si se pierde la conexión, sus tags pasan a `BadNotConnected` con el motivo, el estado del dispositivo
  a `Faulted` con la hora del próximo intento, y se reintenta con espera exponencial sin rendirse nunca.
- **Solo cambios.** Los clientes reciben un valor solo cuando cambia su valor o su calidad.
- **Activar y desactivar en caliente.** Un dispositivo se puede desactivar o activar sin reiniciar el agente
  (`SetDeviceEnabled`). Con `persist=true` el cambio se guarda en `agent.json`.
- **Escrituras.** Exigen las dos llaves: `writesEnabled` en el dispositivo y `writable` en el tag. Cada intento queda
  en el log de auditoría (categoría `PlcMonitor.Audit`) con tag, valor anterior, valor nuevo, usuario y resultado.
  El agente no ofrece nada para cambiar el estado de la CPU (RUN/STOP).

## 3. Ejecutar

```bash
# Desde el código
dotnet run --project src/PlcMonitor.Agent -- --Agent:ConfigPath=/ruta/agent.json

# Desde el paquete publicado (el agente está en la carpeta agent/)
agent/plcmon-agent                  # Linux
agent\plcmon-agent.exe              # Windows
```

Por defecto escucha **solo en local**, en `http://127.0.0.1:5310` (HTTP/2 sin TLS). Para aceptar conexiones de otros
equipos, cambia la URL en `appsettings.json` (`Kestrel:Endpoints:Grpc:Url`, p.ej. `http://0.0.0.0:5310`) o arranca con
`--Kestrel:Endpoints:Grpc:Url=http://0.0.0.0:5310`.

### Seguridad: token y TLS

Para abrir el agente a la red hay que protegerlo. El agente lee, si existe, `agent.settings.json` junto al ejecutable
(las actualizaciones no lo tocan):

```json
{
  "Agent": { "Security": { "TokenSha256": "<SHA-256 del token en hexadecimal>" } },
  "Kestrel": { "Endpoints": { "Grpc": {
    "Url": "https://0.0.0.0:5311",
    "Certificate": { "Path": "agent.pfx", "Password": "<clave del PFX>" } } } }
}
```

- Con `TokenSha256`, todas las llamadas exigen `authorization: Bearer <token>`; si falta o no coincide, `UNAUTHENTICATED`.
  El agente solo guarda el hash.
- El certificado es propio (autofirmado). Los clientes no confían en una autoridad: **fijan su huella SHA-256**.
- **Lo más sencillo es instalarlo desde la app** (sección siguiente): genera token y certificado, los deja en el equipo y
  guarda en el perfil de la app la URL, el token y la huella. En la CLI: `--token` y `--cert` (o `PLCMON_AGENT_TOKEN` y
  `PLCMON_AGENT_CERT`).

Sin `agent.settings.json` el agente escucha solo en `127.0.0.1:5310` sin token: uso local.

### Instalar desde la app (recomendado)

En la app, botón **Agentes** → elige o crea un agente → rellena el **equipo SSH** (IP, puerto, usuario; clave de `~/.ssh`
o contraseña, que no se guarda) → **Instalar / actualizar**. La app:

1. Detecta el sistema (Linux por `uname`, Windows por PowerShell) y la arquitectura (x64 / arm64).
2. Sube el paquete `agents/plcmon-agent-<versión>-<rid>` (incluido en la release; se puede indicar otra carpeta).
3. Instala el servicio:
   - **Linux** (como root, con `sudo -n` o con `sudo` y la contraseña): usuario `plcmon`, `/opt/plc-monitor/agent`,
     unidad systemd `plcmon-agent` endurecida y puerto abierto en ufw/firewalld si están activos.
   - **Windows** (OpenSSH Server activado y usuario administrador): `%ProgramFiles%\PLC-Monitor\agent`, servicio
     `PLCMonitorAgent` con reinicio automático y regla de firewall.
4. La primera vez genera token y certificado; al **actualizar** los conserva (salvo «Regenerar token y certificado»).
5. Fija la huella SSH del equipo: si cambia, la app se niega a conectar (posible suplantación o equipo reinstalado).

También: **Estado**, **Ver log** (Linux: journald) y **Desinstalar** (con copia de `agent.json` si se marca).

Para probarlo sin otro equipo: un contenedor con systemd y SSH (ver el [manual técnico](tecnico.md#probar-el-despliegue-por-ssh)).

### Instalar como servicio en Linux a mano (systemd)

```bash
sudo useradd --system --no-create-home plcmon
sudo mkdir -p /opt/plc-monitor && sudo tar -xzf plcmon-*-linux-x64.tar.gz -C /opt/plc-monitor --strip-components=1
sudo cp /opt/plc-monitor/agent/agent.sample.json /opt/plc-monitor/agent/agent.json   # y edítalo
sudo chown -R plcmon:plcmon /opt/plc-monitor/agent && sudo chmod 600 /opt/plc-monitor/agent/agent.json
sudo cp /opt/plc-monitor/deploy/plcmon-agent.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now plcmon-agent
journalctl -u plcmon-agent -f
```

### Instalar como servicio en Windows a mano

En una consola de administrador:

```bat
sc.exe create "PLC-Monitor Agent" binPath= "C:\PLC-Monitor\agent\plcmon-agent.exe" start= delayed-auto
sc.exe failure "PLC-Monitor Agent" reset= 86400 actions= restart/5000/restart/5000/restart/30000
sc.exe start "PLC-Monitor Agent"
```

Como servicio, el log va al Visor de eventos de Windows (registro *Aplicación*); si se ejecuta a mano, a la consola.

## 4. API gRPC

El contrato está en [`src/PlcMonitor.Agent.Contracts/Protos/agent.proto`](../../../src/PlcMonitor.Agent.Contracts/Protos/agent.proto)
(paquete `plcmonitor.agent.v1`). Los clientes .NET referencian el proyecto `PlcMonitor.Agent.Contracts`, que ya trae
el cliente generado.

| Método | Qué hace |
|---|---|
| `GetInfo` | Versión, máquina, sistema, fichero de configuración y drivers (con sus ajustes por defecto y si permiten explorar/escribir) |
| `ListDevices` | Dispositivos con estado, motivo, próximo reintento y número de tags |
| `SetDeviceEnabled` | Activa o desactiva un dispositivo (`persist` para guardarlo) |
| `GetDeviceInfo` | Datos que da el propio PLC (modelo, estado de la CPU...) |
| `ListTags` | Tags configurados con su último valor (de un dispositivo o de todos) |
| `Read` | Lectura bajo demanda de direcciones cualesquiera |
| `Write` | Escritura en un tag configurado (doble llave, auditoría) |
| `Subscribe` | Stream: primero un `snapshot` completo y después `tag_updates`, `device_status`, `device_configured` (alta o cambio: sustituye todos los tags del dispositivo) y `device_removed`, sin huecos |
| `Browse` | Explora las variables de un dispositivo conectado (filtro, máximo, `refresh`). Si no está conectado: `FAILED_PRECONDITION` |
| `GetConfiguration` / `GetDeviceConfig` | Configuración en uso, completa o de un dispositivo (los ajustes del driver van en JSON) |
| `PutDevice` | Alta o modificación de un dispositivo con sus grupos y tags (`original_name` para renombrar). Se valida todo antes de aplicar: si hay errores, `INVALID_ARGUMENT` con la lista completa y no cambia nada. El dispositivo se reinicia con la nueva configuración |
| `DeleteDevice` | Baja de un dispositivo |
| `QueryHistory` | Histórico de uno o varios tags entre dos instantes (por defecto la última hora), en bruto o agregado por intervalo (media, mín., máx., primero, último). Con el historiador desactivado: `FAILED_PRECONDITION`. `GetInfo` incluye el estado del historiador (carpeta, tamaño, ficheros, tags historizados) |

`SetDeviceEnabled`, `PutDevice` y `DeleteDevice` guardan en `agent.json` solo con `persist = true` (escritura atómica).

Cada valor (`TagValue`) viaja en texto (`text`, cultura invariante) y tipado (`bool_value`, `int_value`,
`double_value`...), con calidad, marca de tiempo y error. Si un dispositivo no existe, la llamada falla con `NOT_FOUND`.
Si un suscriptor no consume los eventos a tiempo, el agente cierra su stream con `RESOURCE_EXHAUSTED`; el cliente
debe volver a suscribirse y recibirá una foto nueva.

Para probar la API desde la consola está `plcmon agent ...` (`info`, `devices`, `tags`, `read`, `write`, `enable`, `disable`,
`watch`, `browse`, `config`, `device add/remove`, `tag add/remove`): ver el [manual de uso](usuario.md#plcmon-agent--hablar-con-un-agente).

Ejemplo mínimo en C#:

```csharp
using var channel = GrpcChannel.ForAddress("http://127.0.0.1:5310");
var client = new AgentService.AgentServiceClient(channel);
using var call = client.Subscribe(new SubscribeRequest());
await foreach (var e in call.ResponseStream.ReadAllAsync())
    if (e.EventCase == SubscribeEvent.EventOneofCase.TagUpdates)
        foreach (var u in e.TagUpdates.Updates)
            Console.WriteLine($"{u.Device}/{u.Tag} = {u.Value.Text} ({u.Value.Quality})");
```

## 5. Solución de problemas

| Síntoma | Causa probable y solución |
|---|---|
| El agente no arranca: «Configuración no válida» | Corrige los errores listados en `agent.json` |
| Dispositivo en `Faulted` con «próximo intento» | El PLC no responde; el agente reintenta solo. Revisa red e IP |
| Tags en `BadConfiguration` | Dirección mal escrita para ese driver |
| Tags en `BadAccessDenied` con «error S7 0x8104» (absoluto) | PUT/GET no está habilitado en la CPU. En TIA Portal: Propiedades de la CPU → Protección y seguridad → Mecanismos de conexión → «Permitir acceso vía comunicación PUT/GET», y cargar |
| `Write` devuelve `BadWriteDisabled` | Falta `writesEnabled` en el dispositivo o `writable` en el tag |
| El cliente no conecta | Por defecto el agente solo escucha en 127.0.0.1. Desde otro equipo, cambia la URL (ver «Ejecutar») |
