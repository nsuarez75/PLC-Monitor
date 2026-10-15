# PLC-Monitor — User guide

> Version 1.0.0 · Last updated: 2026-10-15 · [Castellano](../es/usuario.md)

This version is used from the command line with **`plcmon`**. The desktop app arrives in phase 3.

## 1. Requirements

| Item | Requirement |
|---|---|
| OS | Windows 10/11 x64 or Linux x64/arm64 |
| .NET | .NET 10 runtime (or the self-contained executable once distributed) |
| Network | TCP access to PLC port **102** |

### Which driver?

| PLC | Driver | PLC-side requirements |
|---|---|---|
| S7-1200 (FW ≥ 4.3) / S7-1500, with or without secure communication (TLS) | **`s7-symbolic`** (default) | None: **PUT/GET is not needed**. The password if the CPU has a protection level |
| S7-300 / S7-400 | **`s7-absolute`** | — |
| S7-1200/1500 reading **non-optimized** DBs by address | **`s7-absolute`** | In TIA: *Permit access with PUT/GET* and DB *Optimized block access* disabled |

With `s7-symbolic`, `--security` selects the session security:

| `--security` | Use | In Ignition |
|---|---|---|
| `auto` (default) | Tries TLS and, if the CPU does not support it, connects without TLS (legacy authentication). Works with any CPU | *Preferred* |
| `tls` | Requires TLS: fails if the CPU has no secure communication (TIA ≥ V17, S7-1200 FW ≥ 4.5 / S7-1500 FW ≥ 2.9) | *Required* |
| `legacy` | No TLS at all (TIA ≤ V16 projects or secure communication turned off). Skips the TLS attempt | *Disabled* |

`plcmon info` shows the negotiated security under **Security** (`TLS` or `legacy (sin TLS)`).

## 2. Common options

| Option | Description | Default |
|---|---|---|
| `-H, --host` | PLC IP | (required) |
| `-d, --driver` | `s7-symbolic` or `s7-absolute` | `s7-symbolic` |
| `-p, --password` | CPU access password (symbolic) | `PLCMON_PASSWORD` environment variable |
| `-u, --username` | User (CPUs with user management, FW 3.1+) | — |
| `--cpu` | `S7300`, `S7400`, `S71200`, `S71500` (absolute) | `S71500` |
| `--rack`, `--slot` | Rack and slot (absolute). S7-300: slot 2; S7-1500: slot 1 | 0 / 1 |
| `-t, --timeout` | Timeout in ms | 10000 |
| `-v, --verbose` | Detailed log (for diagnostics) | — |
| `--security` | `auto`, `tls` or `legacy` (symbolic, see above) | `auto` |

> **Tip:** pass the password through the environment variable so it does not end up in your shell history:
> - PowerShell: `$env:PLCMON_PASSWORD = "..."`
> - Bash: `export PLCMON_PASSWORD=...`

## 3. Commands

### `plcmon drivers`
Lists the available drivers and their capabilities.

### `plcmon info`
Connects and shows the device state and information. Useful to check the network, the negotiated security (TLS or not) and the password.
```
plcmon info -H 192.168.2.1
```

### `plcmon browse [FILTER]` (symbolic only)
Searches for variables whose name contains the text (case-insensitive).
```
plcmon browse "Motor.Speed" -H 192.168.2.1 -n 50
```
- The first browse downloads every symbol from the CPU. An S7-1517F with 2.2 million variables takes about 10 s.
- The output shows the **symbol** (use it in `read`/`watch`/`write`), the S7 type and the normalized type.

### `plcmon read ADDRESS...`
Reads one or more addresses once.
```
plcmon read "\"MyDB\".Motor.Speed" "MyDB.Alarms[3]" -H 192.168.2.1
plcmon read DB1.DBW10 DB1.REAL20 M10.1 -d s7-absolute -H 192.168.0.10 --cpu S7300 --slot 2
```
Exit code is 0 if every value has good quality, and 1 otherwise.

### `plcmon watch ADDRESS... [-i MS]`
Polls periodically (1000 ms by default). It shows every value first and then only the changes. Press **Ctrl+C** to exit.
```
plcmon watch "MyDB.Counter" "MyDB.State" -H 192.168.2.1 -i 250
```

### `plcmon write ADDRESS VALUE [--type TYPE] [-y]`
Writes a value. It **asks for confirmation** and shows the current and the new value first.
```
plcmon write "MyDB.Setpoint" 12.5 -H 192.168.2.1
plcmon write DB1.DBX0.3 true -d s7-absolute -H 192.168.0.10
```
- The type is detected by reading the variable first. Use `--type` (`Boolean`, `Int16`, `Float`, `String`, ...) to force it.
- Value formats:
  - Decimals with a **dot** (`12.5`).
  - Booleans: `true/false`, `1/0`, `on/off`, `yes/no`.
  - Hex: `0xFF` or `16#FF`.
  - Times: `1500ms` or `00:00:01.5`.
  - ISO dates: `2026-10-14T12:00:00Z`.
  - Hex bytes: `01 0A FF`.
- ⚠️ **This writes to a real PLC.** Be extra careful with safety (F) CPUs. Safety variables cannot be written from outside.

### `plcmon agent ...` — talking to an agent
These commands do not connect to the PLC: they talk to a `plcmon-agent` (see the [agent manual](agent.md)), local or remote.
The URL is given with `-a, --agent` or the `PLCMON_AGENT` variable; the default is `http://127.0.0.1:5310`.

| Command | What it does |
|---|---|
| `plcmon agent info` | Agent version, machine, configuration file and drivers |
| `plcmon agent devices` | Configured devices: enabled, writes enabled, state, since when, tag count, last error and next retry |
| `plcmon agent tags [DEVICE]` | Configured tags with their last value, quality and scan group (✎ = writable) |
| `plcmon agent read DEVICE ADDRESS...` | Reads any addresses through the agent (they do not need to be configured) |
| `plcmon agent write DEVICE TAG VALUE [-y] [--user U]` | Writes a configured tag. Asks for confirmation; the agent applies the two-key rule and logs it in its audit log with the user |
| `plcmon agent enable/disable DEVICE [--persist]` | Enables or disables a device on the fly. With `--persist` it is saved to `agent.json` |
| `plcmon agent watch [DEVICE...]` | Initial snapshot, then live value and state changes (Ctrl+C to quit). If the agent drops the subscription for being slow, it resubscribes by itself |

```
plcmon agent devices
plcmon agent watch prensa1 -a http://192.168.0.5:5310
plcmon agent write prensa1 marcha true
```

## 4. Address syntax

### Symbolic (`s7-symbolic`)
Accepted forms:
- The name exactly as returned by `browse`: `++2032+CNV02_IDB.Sequence.watchdogTime`.
- TIA Portal style with quotes: `"MyDB".Motor.Speed`.
- Arrays: `MyDB.Values[3]`, `"MyDB".Matrix[1,2]`.
- Markers, inputs and outputs by their symbolic name in the tag table.

### Absolute (`s7-absolute`)
Compatible with the Ignition syntax and the classic STEP 7 syntax. Case-insensitive.

| Example | Meaning | Type |
|---|---|---|
| `DB1.DBX0.3` / `DB1.X0.3` | Bit 3 of byte 0 in DB1 | Bool |
| `DB1.DBB4` / `DB1.B4` | Byte | Byte |
| `DB1.DBW10` / `DB1.W10` | Word (unsigned) | UInt16 |
| `DB1.INT10` / `DB1.I10` | Int | Int16 |
| `DB1.DBD20` / `DB1.D20` | DWord | UInt32 |
| `DB1.DINT20` / `DB1.DI20` | DInt | Int32 |
| `DB1.REAL20` | Real | Float |
| `DB1.LREAL0`, `DB1.LINT0`, `DB1.L0` | LReal / LInt / LWord | Double / Int64 / UInt64 |
| `DB1.DT0` | Date_And_Time | DateTime |
| `DB1.C0` | Char | String (1 char) |
| `DB5.STRING30[20]` | 20-character STRING at byte 30 (default 254) | String |
| `DB1.REAL0[10]` | Array of 10 Real | Float[] |
| `DB1.B0[16]` | 16 raw bytes | ByteArray |
| `M10.1`, `MW20`, `MREAL100` | Markers | — |
| `I0.0` / `E0.0`, `IW64` | Inputs | — |
| `Q4.0` / `A4.0`, `QW10` / `AW10` | Outputs | — |

## 5. Qualities

| Quality | Meaning |
|---|---|
| `Good` | Valid value |
| `BadNotConnected` | No connection to the PLC |
| `BadAddressInvalid` | The address does not exist or is invalid (unknown symbol, missing DB, ...) |
| `BadAccessDenied` | The CPU refuses access (protection, read-only variable, ...) |
| `BadTypeMismatch` | The value cannot be converted to the variable type |
| `Bad` | Any other error (see the detail message) |

## 6. Troubleshooting

| Symptom | Likely cause and fix |
|---|---|
| `La CPU no acepta comunicación segura (TLS)` | `--security tls` was used with a CPU without secure communication. Use `auto` or `legacy` |
| `La CPU rechazó la autenticación clásica (sin TLS)` | `--security legacy` was used with a CPU that requires TLS, or its public key is not in HarpoS7's catalogue. Use `auto` or `tls` |
| `TCP : Error receiving Data` on connect | Network or port 102 blocked, or the CPU has no free connection resources (each HMI/SCADA uses one). Check with `-v` |
| `Access denied (wrong password?)` | The password does not match any CPU access level |
| Connects but reads return `BadAccessDenied` | No password given and the CPU is protected |
| Absolute: error reading a DB | The DB does not exist, is **optimized** (disable *Optimized block access* in TIA) or PUT/GET is not permitted |
| Absolute: connects and `info` works, but every read is `BadAccessDenied` "S7 error 0x8104" | PUT/GET is disabled. In TIA: CPU properties → Protection & Security → Connection mechanisms → *Permit access with PUT/GET communication*, then download |
| Unknown symbol | `BadAddressInvalid` "El símbolo '...' no existe en la CPU". Check the name with `plcmon browse` |
