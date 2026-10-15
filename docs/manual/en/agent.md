# PLC-Monitor — Agent (`plcmon-agent`)

> Phase 2 (in development, not released yet) · Last updated: 2026-10-14 · [Castellano](../es/agente.md)

The agent is a headless service that watches several PLCs at once, keeps the last value of every tag and serves it to
its clients over **gRPC**: the upcoming desktop app, the historian, OPC UA... Closing the app does not stop data
collection, and a remote agent works exactly like the local one.

```
 gRPC client (app, scripts...)
          │  HTTP/2 · 127.0.0.1:5310
          ▼
 ┌──────────── plcmon-agent ────────────┐
 │ AgentGrpcService  (API)              │
 │ TagEngine         cache + broadcast  │
 │ DeviceSupervisor  one per PLC:       │
 │   connection, reconnection, groups   │
 └──────┬───────────────┬───────────────┘
        ▼               ▼
     PLC 1   ...     PLC N    (s7-symbolic / s7-absolute drivers)
```

## 1. Configuration (`agent.json`)

The agent reads `agent.json` from its own folder. Change the path with `--Agent:ConfigPath=/path/agent.json` or the
`PLCMON_Agent__ConfigPath` environment variable. If the file does not exist the agent starts with no devices.
There is an example next to the executable, `agent.sample.json`. The JSON accepts `//` comments and trailing commas.

```jsonc
{
  "devices": [
    {
      "name": "press1",                   // unique; no '/'
      "driver": "s7-absolute",            // s7-absolute or s7-symbolic
      "enabled": true,                    // false: configured but not connected
      "writesEnabled": false,             // 1st write key
      "settings": { "host": "192.168.0.10", "cpu": "S71200", "rack": 0, "slot": 1 },
      "reconnect": { "initialDelayMs": 1000, "maxDelayMs": 30000, "multiplier": 2 },
      "scanGroups": [
        {
          "name": "fast",
          "intervalMs": 500,              // minimum 50
          "tags": [
            { "name": "speed", "address": "DB10.DBW0" },
            { "name": "run", "address": "DB10.DBX2.0", "writable": true, "dataType": "Boolean" }
          ]
        }
      ]
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `settings` | The driver's own settings (`host`, `cpu`, `rack`, `slot`, `password`, `security`...). See the [user guide](user-guide.md) |
| `reconnect` | Delay between connection attempts: `initialDelayMs`, then ×`multiplier` after each failure, up to `maxDelayMs`. Resets after a successful connection |
| `scanGroups` | Scan groups (*scan classes*): the tags of a group are read together every `intervalMs` |
| `tags[].name` | Tag name, unique within the device. Defaults to the address |
| `tags[].address` | Address in the driver's syntax |
| `tags[].writable` | 2nd write key |
| `tags[].dataType` | Type used to parse written values when the driver cannot infer it from the address |

The whole configuration is validated at startup. If something is wrong the agent does not start and lists every error.
Addresses with bad syntax do not prevent startup: those tags get the `BadConfiguration` quality.

> ⚠️ In this version the password of symbolic PLCs is stored in clear text in `agent.json`. Protect the file
> (permissions for the service user only). Encryption is planned.

## 2. Behaviour

- **One supervisor per PLC.** Each device connects, reads and reconnects on its own: a PLC that is down does not affect the others.
- **Reconnection.** When the connection drops, its tags become `BadNotConnected` with the reason, the device becomes
  `Faulted` with the time of the next attempt, and it retries with exponential backoff, forever.
- **Changes only.** Clients receive a value only when its value or quality changes.
- **Hot enable/disable.** A device can be disabled or enabled without restarting the agent (`SetDeviceEnabled`).
  With `persist=true` the change is saved to `agent.json`.
- **Writes.** They need both keys: `writesEnabled` on the device and `writable` on the tag. Every attempt is written to
  the audit log (category `PlcMonitor.Audit`) with tag, previous value, new value, user and result.
  The agent exposes nothing to change the CPU state (RUN/STOP).

## 3. Running

```bash
# From source
dotnet run --project src/PlcMonitor.Agent -- --Agent:ConfigPath=/path/agent.json

# From the release package (the agent lives in the agent/ folder)
agent/plcmon-agent                  # Linux
agent\plcmon-agent.exe              # Windows
```

By default it listens **locally only**, on `http://127.0.0.1:5310` (HTTP/2 without TLS). To accept connections from
other machines change the URL in `appsettings.json` (`Kestrel:Endpoints:Grpc:Url`, e.g. `http://0.0.0.0:5310`) or start
with `--Kestrel:Endpoints:Grpc:Url=http://0.0.0.0:5310`.

### Security: token and TLS

To expose the agent on the network it must be protected. If present, the agent reads `agent.settings.json` next to the
executable (updates never touch it):

```json
{
  "Agent": { "Security": { "TokenSha256": "<hex SHA-256 of the token>" } },
  "Kestrel": { "Endpoints": { "Grpc": {
    "Url": "https://0.0.0.0:5311",
    "Certificate": { "Path": "agent.pfx", "Password": "<PFX password>" } } } }
}
```

- With `TokenSha256`, every call requires `authorization: Bearer <token>`; missing or wrong gives `UNAUTHENTICATED`.
  The agent only stores the hash.
- The certificate is self-signed. Clients do not trust an authority: they **pin its SHA-256 thumbprint**.
- **The easiest way is to install from the app** (next section): it generates token and certificate, leaves them on the
  machine and stores the URL, token and thumbprint in the app's agent profile. In the CLI: `--token` and `--cert` (or
  `PLCMON_AGENT_TOKEN` and `PLCMON_AGENT_CERT`).

Without `agent.settings.json` the agent only listens on `127.0.0.1:5310` without a token: local use.

### Installing from the app (recommended)

In the app, **Agentes** button → pick or create an agent → fill in the **SSH machine** (IP, port, user; key from `~/.ssh`
or a password, which is not stored) → **Instalar / actualizar**. The app:

1. Detects the system (Linux via `uname`, Windows via PowerShell) and the architecture (x64 / arm64).
2. Uploads the `agents/plcmon-agent-<version>-<rid>` package (shipped with the release; another folder can be given).
3. Installs the service:
   - **Linux** (as root, with `sudo -n` or `sudo` and the password): `plcmon` user, `/opt/plc-monitor/agent`, hardened
     `plcmon-agent` systemd unit, port opened in ufw/firewalld if active.
   - **Windows** (OpenSSH Server enabled and an administrator user): `%ProgramFiles%\PLC-Monitor\agent`,
     `PLCMonitorAgent` service with automatic restart and a firewall rule.
4. The first time it generates token and certificate; an **update** keeps them (unless "Regenerar token y certificado").
5. Pins the machine's SSH host key: if it changes, the app refuses to connect (possible spoofing or reinstalled machine).

Also: **Estado** (status), **Ver log** (Linux: journald) and **Desinstalar** (keeping a copy of `agent.json` if ticked).

To try it without another machine: a container with systemd and SSH (see the [technical manual](technical.md#testing-ssh-deployment)).

### Installing as a Linux service by hand (systemd)

```bash
sudo useradd --system --no-create-home plcmon
sudo mkdir -p /opt/plc-monitor && sudo tar -xzf plcmon-*-linux-x64.tar.gz -C /opt/plc-monitor --strip-components=1
sudo cp /opt/plc-monitor/agent/agent.sample.json /opt/plc-monitor/agent/agent.json   # and edit it
sudo chown -R plcmon:plcmon /opt/plc-monitor/agent && sudo chmod 600 /opt/plc-monitor/agent/agent.json
sudo cp /opt/plc-monitor/deploy/plcmon-agent.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now plcmon-agent
journalctl -u plcmon-agent -f
```

### Installing as a Windows service by hand

From an administrator console:

```bat
sc.exe create "PLC-Monitor Agent" binPath= "C:\PLC-Monitor\agent\plcmon-agent.exe" start= delayed-auto
sc.exe failure "PLC-Monitor Agent" reset= 86400 actions= restart/5000/restart/5000/restart/30000
sc.exe start "PLC-Monitor Agent"
```

As a service, logs go to the Windows Event Viewer (*Application* log); when run by hand, to the console.

## 4. gRPC API

The contract is [`src/PlcMonitor.Agent.Contracts/Protos/agent.proto`](../../../src/PlcMonitor.Agent.Contracts/Protos/agent.proto)
(package `plcmonitor.agent.v1`). .NET clients reference the `PlcMonitor.Agent.Contracts` project, which ships the
generated client.

| Method | What it does |
|---|---|
| `GetInfo` | Version, machine, OS, configuration file and drivers (with their default settings and whether they can browse/write) |
| `ListDevices` | Devices with state, reason, next retry and tag count |
| `SetDeviceEnabled` | Enables or disables a device (`persist` to save it) |
| `GetDeviceInfo` | Data reported by the PLC itself (model, CPU state...) |
| `ListTags` | Configured tags with their last value (one device or all) |
| `Read` | On-demand read of arbitrary addresses |
| `Write` | Write to a configured tag (two keys, audited) |
| `Subscribe` | Stream: first a full `snapshot`, then `tag_updates`, `device_status`, `device_configured` (added or changed: replaces all the device's tags) and `device_removed`, with no gaps |
| `Browse` | Browses the variables of a connected device (filter, max, `refresh`). If not connected: `FAILED_PRECONDITION` |
| `GetConfiguration` / `GetDeviceConfig` | Configuration in use, whole or for one device (driver settings as JSON) |
| `PutDevice` | Adds or changes a device with its groups and tags (`original_name` to rename). Everything is validated first: on errors, `INVALID_ARGUMENT` with the full list and nothing changes. The device restarts with the new configuration |
| `DeleteDevice` | Removes a device |

`SetDeviceEnabled`, `PutDevice` and `DeleteDevice` save to `agent.json` only with `persist = true` (atomic write).

Each value (`TagValue`) travels as text (`text`, invariant culture) and typed (`bool_value`, `int_value`,
`double_value`...), with quality, timestamp and error. Unknown devices fail with `NOT_FOUND`.
If a subscriber does not keep up, the agent closes its stream with `RESOURCE_EXHAUSTED`; the client must subscribe
again and will receive a fresh snapshot.

To try the API from a terminal use `plcmon agent ...` (`info`, `devices`, `tags`, `read`, `write`, `enable`, `disable`,
`watch`, `browse`, `config`, `device add/remove`, `tag add/remove`): see the [user guide](user-guide.md#plcmon-agent--talking-to-an-agent).

Minimal C# example:

```csharp
using var channel = GrpcChannel.ForAddress("http://127.0.0.1:5310");
var client = new AgentService.AgentServiceClient(channel);
using var call = client.Subscribe(new SubscribeRequest());
await foreach (var e in call.ResponseStream.ReadAllAsync())
    if (e.EventCase == SubscribeEvent.EventOneofCase.TagUpdates)
        foreach (var u in e.TagUpdates.Updates)
            Console.WriteLine($"{u.Device}/{u.Tag} = {u.Value.Text} ({u.Value.Quality})");
```

## 5. Troubleshooting

| Symptom | Likely cause and fix |
|---|---|
| Agent does not start: "Configuración no válida" | Fix the errors listed for `agent.json` |
| Device `Faulted` with a "next retry" | The PLC does not answer; the agent keeps retrying. Check network and IP |
| Tags in `BadConfiguration` | Address misspelled for that driver |
| Tags in `BadAccessDenied` with "S7 error 0x8104" (absolute) | PUT/GET is not enabled on the CPU. In TIA Portal: CPU properties → Protection & Security → Connection mechanisms → "Permit access with PUT/GET communication from remote partner", then download |
| `Write` returns `BadWriteDisabled` | `writesEnabled` missing on the device or `writable` on the tag |
| Client cannot connect | By default the agent only listens on 127.0.0.1. From another machine, change the URL (see "Running") |
