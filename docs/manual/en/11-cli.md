# 11. CLI

`plcmon` is the PLC-Monitor command-line tool. It is used to diagnose a PLC without opening the
app, to read or write a single address, and to administer an agent from a script.

The CLI has two modes:

- **Direct commands** (`read`, `watch`, `write`, `browse`, `info`): they connect directly to the PLC with the
  data you provide. They do not need an agent.
- **Agent commands** (`plcmon agent ...`): they communicate with an agent over gRPC. They need the URL and the token
  of the agent.

**Important**: the CLI is not part of the current desktop release. It is built from the repository.

## 11.1. Build and run

```bash
dotnet build src/PlcMonitor.Cli/PlcMonitor.Cli.csproj -c Release
```

The executable is called `plcmon`:

```bash
src/PlcMonitor.Cli/bin/Release/net10.0/plcmon --help
```

You can also run it without publishing:

```bash
dotnet run --project src/PlcMonitor.Cli -- --help
```

If the system's `dotnet` command cannot find the SDK, use the path to the installed SDK or install .NET 10.

## 11.2. Help and exit codes

```bash
plcmon --help
plcmon read --help
plcmon agent --help
plcmon agent history --help
```

Exit codes:

| Code | Meaning |
|---|---|
| `0` | Success. |
| `1` | Connection error, read with bad quality, or failed write. |
| `2` | Invalid arguments or cancellation of a confirmation. |
| `3` | Write canceled by the user. |

Output uses tables and colors. If you redirect it to a file, the color codes may appear as
text; use the normal terminal output to read it.

## 11.3. Direct commands

### `drivers`

Lists the available drivers and their capabilities.

```bash
plcmon drivers
```

### `info`

Connects and shows device information.

```bash
plcmon info -H 192.0.2.10 -d s7-symbolic
```

### `read`

Reads one or more addresses once.

```bash
plcmon read '"MiDB".Motor.Velocidad' -H 192.0.2.10
plcmon read DB1.DBW10 DB1.REAL20 -d s7-absolute -H 192.0.2.10 --cpu S7300 --slot 2
plcmon read HR0 HR10:FLOAT C3 -d modbus -H 192.0.2.20 --unit-id 1
plcmon read 40001 30005:UINT16 -d modbus --serial /dev/ttyUSB0 --baud 19200 --parity even --unit-id 3
```

### `browse`

Searches for symbols in a symbolic S7 CPU.

```bash
plcmon browse Motor -H 192.0.2.10 -n 50
```

The filter is optional. `-n` limits the number of results.

### `watch`

Reads periodically and shows only changes. Ctrl+C to exit.

```bash
plcmon watch '"MiDB".Contador' -H 192.0.2.10 -i 500
```

The minimum interval is 50 ms.

### `write`

Writes a value and asks for confirmation. The `--type` option lets you specify the type if it cannot be inferred.

```bash
plcmon write '"MiDB".Consigna' 12.5 -H 192.0.2.10
plcmon write DB1.DBW10 42 -d s7-absolute -H 192.0.2.10 --type Int16
```

`-y` skips confirmation. Use it only in a controlled script.

### Common options for direct commands

| Option | Use |
|---|---|
| `-d, --driver` | `s7-symbolic` (default), `s7-absolute`, or `modbus`. |
| `-H, --host` | IP or name of the PLC. Not used in Modbus RTU. |
| `-p, --password` | Password for the symbolic S7 CPU. If omitted, `PLCMON_PASSWORD` is used. |
| `-u, --username` | User for CPUs with user management. |
| `--cpu` | `S7300`, `S7400`, `S71200`, or `S71500`. |
| `--rack`, `--slot` | Rack and slot in S7 absolute. |
| `--port` | TCP port. |
| `--serial` | Serial port for Modbus RTU. |
| `--rtu-over-tcp` | Modbus RTU over TCP. |
| `--baud`, `--parity`, `--stop-bits` | Serial line settings. |
| `--unit-id` | Modbus slave number. |
| `--byte-order` | `ABCD`, `BADC`, `CDAB`, or `DCBA`. |
| `--one-based` | Modbus per-table addresses starting at 1. |
| `-t, --timeout` | Timeout in milliseconds. |
| `--security` | `auto`, `tls`, or `legacy` in symbolic S7. |
| `-v, --verbose` | Detailed driver log. |

**Important**: do not write the password into the shell history. Use `PLCMON_PASSWORD`:

```bash
export PLCMON_PASSWORD='...'
plcmon read '"MiDB".Motor.Velocidad' -H 192.0.2.10
```

## 11.4. Agent commands

The URL, token, and certificate thumbprint can be passed through options or environment variables:

| Option | Variable | Default |
|---|---|---|
| `-a, --agent` | `PLCMON_AGENT` | `http://127.0.0.1:5310` |
| `--token` | `PLCMON_AGENT_TOKEN` | empty |
| `--cert` | `PLCMON_AGENT_CERT` | empty |

```bash
export PLCMON_AGENT='https://planta-demo.local:5311'
export PLCMON_AGENT_TOKEN='...'
export PLCMON_AGENT_CERT='...'

plcmon agent info
plcmon agent devices
```

Avoid passing the token with `--token` on a shared computer: it ends up in the shell history and in the process
list. Use environment variables or a secret manager.

### `agent info`

Shows version, machine, system, startup date, configuration path, drivers, access level, and
historian information.

```bash
plcmon agent info
```

### `agent devices`

Lists the devices with their driver, status, number of tags, and detail.

```bash
plcmon agent devices
```

### `agent tags`

Lists the configured tags with their latest value. You can limit it to one device.

```bash
plcmon agent tags
plcmon agent tags prensa-linea3
```

### `agent read`

Reads any addresses through the agent, even if they are not configured as tags.

```bash
plcmon agent read prensa-linea3 DB10.DBW0
```

### `agent write`

Writes a configured tag. It respects the agent keys and asks for confirmation.

```bash
plcmon agent write prensa-linea3 marcha true
plcmon agent write prensa-linea3 consigna 12.5 --user mantenimiento
```

`--user` only changes the user that appears in the audit; it does not authenticate. Authentication is the token.

### `agent enable` and `agent disable`

Enable or disable a device at runtime.

```bash
plcmon agent disable prensa-linea3
plcmon agent enable prensa-linea3 --persist
```

Without `--persist`, the change is lost when the agent restarts. This is the opposite of `tag add`, which saves by
default.

### `agent browse`

Browses the variables of a connected device. `--refresh` re-reads the CPU instead of using the
cache.

```bash
plcmon agent browse prensa-linea3 Motor -n 50
plcmon agent browse prensa-linea3 --refresh
```

### `agent config`

Shows the configuration as a tree: devices, scan groups, and tags.

```bash
plcmon agent config
plcmon agent config prensa-linea3
```

### `agent device add|remove|order`

Adding, removing, and ordering devices.

```bash
plcmon agent device add prensa-linea3 -d s7-symbolic -s '{"host":"192.0.2.10"}'
plcmon agent device add horno-linea3 -d modbus -s '{"transport":"Tcp","host":"192.0.2.20","port":502,"unitId":1}'
plcmon agent device remove horno-linea3
plcmon agent device order prensa-linea3 horno-linea3
```

- `device add` creates the device or updates its settings if it already exists. It preserves its tags.
- `--disabled` creates it without connecting.
- `--writes` enables the first write key.
- `--no-persist` does not save to `agent.json`.
- `device remove` asks for confirmation and deletes the device with its tags. It does not delete history.
- `device order` puts the specified devices first, in that order; the rest come after.

### `agent tag add|remove|enable|disable`

```bash
plcmon agent tag add prensa-linea3 '"MiDB".Velocidad' --name velocidad -g rapido -i 250
plcmon agent tag add prensa-linea3 HR100:FLOAT --name presion --unit bar --decimals 2
plcmon agent tag add prensa-linea3 DB1.REAL20 --name fuerza --writable --history
plcmon agent tag remove prensa-linea3 fuerza
plcmon agent tag disable prensa-linea3 fuerza
plcmon agent tag enable prensa-linea3 fuerza
```

Options of `tag add`:

| Option | Use |
|---|---|
| `--name` | Tag name. Defaults to the address. |
| `-g, --group` | Scan group. Created if it does not exist. |
| `-i, --interval` | Period of the new group, in ms. |
| `--writable` | Second write key. |
| `--type` | Type used to interpret writes. |
| `--history` | Enables history. |
| `--history-min`, `--history-max` | Minimum interval and maximum time (`1s`, `1m`...). |
| `--history-deadband` | Absolute deadband or percentage (`0.5`, `2%`). |
| `--unit` | Visible unit. |
| `--decimals` | Decimals from 0 to 9; `-1` shows the full value. |
| `--scale` | `rawMin:rawMax:engMin:engMax[:clamp]`, or `none`. |

If the tag already existed with that name, it is replaced. The transformation, display group, OPC UA
key, and active status are preserved.

### `agent history`

Queries history as a table or CSV.

```bash
plcmon agent history prensa-linea3 velocidad --last 2h -i 1m --agg max
plcmon agent history prensa-linea3 velocidad presion --from "2026-10-15 08:00" --csv turno.csv
```

| Option | Use |
|---|---|
| `--last` | Period up to now (`30s`, `15m`, `2h`, `7d`). Default, 1 h. |
| `--from`, `--to` | Absolute local dates. |
| `-i, --interval` | Aggregates by intervals. Without it, raw points. |
| `--agg` | `avg`, `min`, `max`, `first`, or `last`. |
| `-n, --max` | Maximum points per tag. Default, 10,000. |
| `--csv` | Saves the result to CSV. |

The CSV has the header `device,tag,timestamp,value,quality,unit`, with the time in UTC.

### `agent history-delete`

Deletes the history of some tags in an interval. Asks for confirmation.

```bash
plcmon agent history-delete prensa-linea3 velocidad --from "2026-10-15 08:00" --to "2026-10-15 14:00"
plcmon agent history-delete prensa-linea3 velocidad presion --all -y
```

`--all` deletes everything stored for those tags. It cannot be combined with `--from`, `--to`, or `--last`.

### `agent history-rename`

Moves history from one name to another, for example after renaming a tag or a device.

```bash
plcmon agent history-rename prensa-linea3/vel prensa-linea3/velocidad
plcmon agent history-rename prensa-linea3 prensa-linea4
```

If the destination already has history, they are merged. Asks for confirmation.

### `agent watch`

Subscribes to the agent and shows value and status changes live. Ctrl+C to exit.

```bash
plcmon agent watch
plcmon agent watch prensa-linea3
```

## 11.5. Diagnostic examples

### Check a PLC without an agent

```bash
plcmon info -H 192.0.2.10
plcmon browse Motor -H 192.0.2.10 -n 20
plcmon read '"MiDB".Motor.Velocidad' -H 192.0.2.10
```

### Check an agent

```bash
plcmon agent info
plcmon agent devices
plcmon agent tags prensa-linea3
```

### View a cycle live

```bash
plcmon agent watch prensa-linea3
```

### Export a shift

```bash
plcmon agent history prensa-linea3 velocidad presion fuerza \
  --from "2026-10-15 06:00" --to "2026-10-15 14:00" -i 10s --agg avg \
  --csv turno-2026-10-15.csv
```

### Change a setpoint with confirmation

```bash
plcmon agent write prensa-linea3 consigna 8.5
```

## 11.6. Best practices

- Do not pass tokens or passwords on the command line on shared computers.
- Use a read-only token for query scripts.
- Test first with `read` before `write`.
- Save diagnostic output to a file if you are going to open an incident.
- In scripts, check the exit code; `1` can mean a read with bad quality, not a syntax
  failure.
- Do not use `-y` on writes without a prior check of the device and the value.
- The CLI does not replace the app for configuring charts, panels, transformations, or OPC UA.

## Next step

To understand how the agent, tokens, and writes are protected, continue with
[Security](12-security.md).
