# PLC-Monitor — Desktop application

> Version 1.0.0 · Last updated: 2026-10-15 · [Castellano](../es/app.md)

`plcmon-app` is the desktop application (Windows and Linux). **It does not talk to the PLCs**: it connects to an
[agent](agent.md) (local or remote) and works through it. Closing the app does not stop data collection.

## 1. Starting

```bash
app/plcmon-app            # Linux (release package)
app\plcmon-app.exe        # Windows
dotnet run --project src/PlcMonitor.App    # from source
```

The agent (`agent/plcmon-agent`) must be running first. By default it listens on `http://127.0.0.1:5310`.

## 2. Connecting to an agent

Type the agent URL at the top and click **Conectar**. The app remembers the last URLs used (drop-down).
The connection state is shown on the right:

| State | Meaning |
|---|---|
| **En vivo** | Subscribed to the agent: values and states arrive instantly |
| **Reconectando…** | The connection to the agent was lost; the app resubscribes by itself every 2 s |
| **Sin conexión** | No agent connected |

Preferences are stored in `~/.config/PLC-Monitor/app.json` (Linux) or `%APPDATA%\PLC-Monitor\app.json` (Windows).

## 3. Devices

The left column lists the agent's PLCs with a coloured dot:

| Colour | State |
|---|---|
| Green | Connected |
| Yellow | Connecting |
| Red | Faulted (the detail and next retry time show when selected) |
| Grey | Disconnected or disabled |

- **Activar / Desactivar** (on each row): connects or disconnects the PLC and saves it in the agent configuration.
- **Nuevo**: adds a PLC. Choosing the driver fills in its default JSON settings (`host`, `security`, `cpu`...; see the
  [user guide](user-guide.md)). Tick **Escrituras habilitadas** only if you will write (first key).
- **Editar**: changes name, driver, settings, keys and reconnection. Tags are kept. On errors, the agent returns them all
  at once and nothing changes.
- **Eliminar**: removes the PLC and its tags (asks for confirmation).

Every change is saved to the agent's `agent.json`.

## 4. Tags

The **Tags** tab shows the selected PLC's tags with their value, type, quality and last change time, live.

- **Escribir**: only on writable tags (second key). Type the value (decimal point; `true/false`), click **Escribir** and
  confirm. The agent checks both keys and logs the write with your user name in its audit log.
- **Quitar**: removes the tag from the configuration (asks for confirmation).

## 5. Browsing and adding tags

In the **Explorar** tab (with the PLC connected and a driver that supports it, such as `s7-symbolic`):

1. Type part of the name and click **Buscar** (Enter). The first search browses the CPU; later ones use the agent's
   cache. **Volver a explorar la CPU** refreshes it (after downloading a new program).
2. Choose the scan **group** (created with the given **period** if missing) and whether the tag is **writable**.
3. Click **Añadir** on the symbol. It shows up in the Tags tab with its value as soon as the agent reads it.

## 6. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| "No se puede conectar con el agente" | The agent is not running, the URL is wrong or it only listens on 127.0.0.1 (see [agent](agent.md#3-running)) |
| "… no es un agente de PLC-Monitor" | The URL points to another service or to an agent of another version |
| Search says "no está conectado" | The PLC must be connected to browse |
| Search says "no permite explorar variables" | The `s7-absolute` driver cannot browse: add addresses with `plcmon agent tag add` |
| Constant "Reconectando…" | The agent or the network went down; the app recovers by itself when it is back |
