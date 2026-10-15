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

> ⚠️ The API has **no authentication or encryption yet** (they come with remote agents, phase 6). If you expose it,
> do so only on a trusted network and keep `writesEnabled: false` on PLCs that must not be written.

### Installing as a Linux service (systemd)

```bash
sudo useradd --system --no-create-home plcmon
sudo mkdir -p /opt/plc-monitor && sudo tar -xzf plcmon-*-linux-x64.tar.gz -C /opt/plc-monitor --strip-components=1
sudo cp /opt/plc-monitor/agent/agent.sample.json /opt/plc-monitor/agent/agent.json   # and edit it
sudo chown -R plcmon:plcmon /opt/plc-monitor/agent && sudo chmod 600 /opt/plc-monitor/agent/agent.json
sudo cp /opt/plc-monitor/deploy/plcmon-agent.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now plcmon-agent
journalctl -u plcmon-agent -f
```

### Installing as a Windows service

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
| `GetInfo` | Version, machine, OS, configuration file and drivers |
| `ListDevices` | Devices with state, reason, next retry and tag count |
| `SetDeviceEnabled` | Enables or disables a device (`persist` to save it) |
| `GetDeviceInfo` | Data reported by the PLC itself (model, CPU state...) |
| `ListTags` | Configured tags with their last value (one device or all) |
| `Read` | On-demand read of arbitrary addresses |
| `Write` | Write to a configured tag (two keys, audited) |
| `Subscribe` | Stream: first a full `snapshot`, then `tag_updates` and `device_status` with no gaps |

Each value (`TagValue`) travels as text (`text`, invariant culture) and typed (`bool_value`, `int_value`,
`double_value`...), with quality, timestamp and error. Unknown devices fail with `NOT_FOUND`.
If a subscriber does not keep up, the agent closes its stream with `RESOURCE_EXHAUSTED`; the client must subscribe
again and will receive a fresh snapshot.

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
