# PLC-Monitor — Technical manual

> Version 1.0.0 · Last updated: 2026-10-14 · [Castellano](../es/tecnico.md)

## 1. Architecture

The full picture (agent, Avalonia app, historian, OPC UA, SSH) is in [docs/arquitectura.md](../../arquitectura.md) (Spanish). This phase builds the foundation:

```
                 plcmon (CLI)            [later phases: Agent, App]
                      │
              PlcMonitor.Core  ── DriverRegistry · TagPoller · ValueParser · ValueFormatter
                      │
           PlcMonitor.Abstractions ── IDriverFactory · IDeviceConnection · IBrowsableConnection
              │                   │                                   │
   Drivers.S7Symbolic     Drivers.S7Absolute                 (future) Drivers.ModbusTcp ...
        │                         │
   S7CommPlus (LGPL-3)       S7.Net Plus (MIT)
        │
   OpenSSL 3 (native)
```

### Projects

| Project | Responsibility |
|---|---|
| `src/PlcMonitor.Abstractions` | Common driver contract: `IDriverFactory`, `DriverFactoryBase<TSettings>`, `IDeviceConnection`, `DeviceConnectionBase`, `IBrowsableConnection`, `TagValue`, `QualityCode`, `TagDataType` |
| `src/PlcMonitor.Core` | Protocol-independent logic: `DriverRegistry`, `TagPoller` (polling with change detection), `ValueParser` (text → type), `ValueFormatter` |
| `src/PlcMonitor.Drivers.S7Symbolic` | `s7-symbolic` driver on top of S7CommPlus |
| `src/PlcMonitor.Drivers.S7Absolute` | `s7-absolute` driver on top of S7.Net Plus |
| `src/S7CommPlus` | .NET 10 port of [S7CommPlusDriver](https://github.com/thomas-v2/S7CommPlusDriver), **LGPL-3**. Separate assembly; changes listed in `NOTICE.md` |
| `src/PlcMonitor.Cli` | `plcmon` CLI (Spectre.Console) |
| `tests/*` | xUnit v3 on Microsoft.Testing.Platform |

## 2. Driver contract

To add a protocol (e.g. Modbus TCP):

1. Create `src/PlcMonitor.Drivers.<Protocol>` referencing `PlcMonitor.Abstractions`.
2. Define a settings class (a POCO with defaults). It is serialized as JSON (`DriverJson.Options`: camelCase, enums as strings).
3. Derive from `DriverFactoryBase<TSettings>`:
   - `TypeId`: stable id used in configuration, e.g. `"modbus-tcp"`.
   - `Validate(settings)`: returns the list of errors.
   - `CreateConnection(...)`: creates the connection.
4. Derive the connection from `DeviceConnectionBase` and implement:
   - `OpenCoreAsync` / `CloseCoreAsync`.
   - `ReadCoreAsync`: one `TagValue` per address, in the same order. **Per-address** errors are returned as bad quality, not thrown.
   - `WriteCoreAsync`.
   - `ValidateAddress`: syntax only, without touching the device.
   - `IsConnectionLost(ex)`: which exceptions mean a reconnect is needed.
   - Optionally `IBrowsableConnection`, if the protocol supports discovery.
5. Register the factory in `DriverRegistry`: in `CliRuntime` for now, in the agent from phase 2.

`DeviceConnectionBase` already takes care of:
- State (`Disconnected → Connecting → Connected / Faulted`) and the `StateChanged` event.
- Serializing every operation with a semaphore, so there is a single device access at a time.
- Read or write while disconnected: returns `BadNotConnected` without touching the device.
- Communication exception: closes, moves to `Faulted` and marks every item `BadNotConnected`.
- Unexpected exception: returns `Bad` quality and keeps the connection.

**Automatic reconnection** is not the driver's job. The agent (phase 2) will handle it with backoff.

## 3. S7 symbolic driver (`s7-symbolic`)

- Protocol: S7CommPlus over ISO-on-TCP (port 102).
  1. Clear-text `InitSsl`.
  2. **TLS 1.3**, with suites `TLS_AES_256_GCM_SHA384` / `TLS_AES_128_GCM_SHA256` and groups `X25519:P-256:P-384`.
  3. Session and password legitimation.
- **On-demand symbol resolution** (`getPlcTagBySymbol`): reading does not require browsing the whole CPU. Each resolved symbol is cached per session, including symbols that do not exist (negative cache). The cache is cleared on reconnect.
- `BrowseAsync`: full browse (`Browse`), cached in memory (name + type). Supports a filter, a limit and `Refresh`.
- **Types** are derived from the tag class created by the library itself (`PlcTags.TagFactory`), so every type it supports (BBOOL, BLOCK_FB, HW_*...) is covered. Conversions:
  - TIME, TOD, LTIME, LTOD and S5TIME → `TimeSpan`.
  - DTL, DT, DATE and LDT → `DateTime`.
  - CHAR and WCHAR → `string`.
  - Arrays → .NET arrays.
- Writes use a fresh tag instance so the read tag is not altered. If the CPU rejects the write → `BadAccessDenied`.
- Library errors become `S7CommPlusException` with the `S7Consts` code. `IsCommunicationError` decides whether to reconnect (TCP, ISO, timeout, TLS).

### Key findings (fixed in the port)

| Problem | Cause | Fix |
|---|---|---|
| Connection times out with OpenSSL 3.5 | OpenSSL ≥ 3.5 offers the post-quantum hybrid group **X25519MLKEM768** by default; the ClientHello grows and the CPU never answers | `S7Client.TlsGroups = "X25519:P-256:P-384"` (AGLink/Ignition offer X25519 only) |
| A wrong password was accepted | The legitimation `ReturnValue` (UInt64) was truncated to `Int16`. The 1517F returned `0x2605990006990016` → `0x0016` > 0 | Any `ReturnValue != 0` → `errCliAccessDenied` |
| The original project ships OpenSSL 3.0.8 DLLs (unsupported) | — | OpenSSL 3.5 LTS is used instead: `scripts/get-openssl-windows.sh`, loaded through `OpenSslLibraryResolver` |

### OpenSSL loading (`OpenSslLibraryResolver`)
Search order:
1. `PLCMONITOR_OPENSSL_DIR` environment variable.
2. Application directory.
3. `runtimes/<rid>/native`.
4. OS default search.

File names:
- Windows: `libcrypto-3-x64.dll`, `libssl-3-x64.dll`.
- Linux: `libcrypto.so.3`, `libssl.so.3`.
- macOS: `.3.dylib`.

## 4. S7 absolute driver (`s7-absolute`)

- `S7Address`: in-house parser (generated regex), compatible with the Ignition and STEP 7 syntaxes. See the table in the [user guide](user-guide.md#absolute-s7-absolute). `ToString()` gives the canonical form.
- `S7Codec`: big-endian, S7 STRING (max/current length header, Latin-1), BCD DATE_AND_TIME (years 1990–2089), arrays.
- `ReadPlanner`: merges addresses in the same area/DB when the gap is ≤ `ReadGapBytes` (default 32) and the block stays ≤ 4096 bytes, so fewer requests are needed. S7.Net splits by PDU size.
- Transport: `IS7Transport` (S7.Net Plus), replaceable in tests. Bits are written with `WriteBit`, with no read-modify-write.
- A failing block (for example a missing DB) only turns its own addresses into `BadAddressInvalid`.

## 5. Build and test

```bash
# Windows: copy OpenSSL 3.5 (from Git for Windows or a given path) into native/win-x64
scripts/get-openssl-windows.sh

dotnet build PlcMonitor.slnx
dotnet test --solution PlcMonitor.slnx          # Microsoft.Testing.Platform (global.json)
dotnet run --project src/PlcMonitor.Cli -- info -H 192.168.2.1
```

### Tests

| Project | Coverage |
|---|---|
| `PlcMonitor.Core.Tests` | Driver registry, factories, polling with a fake clock, value parser and formatter |
| `PlcMonitor.Drivers.S7Absolute.Tests` | Address parser, codec, read planner, and the connection against an in-memory simulated PLC |
| `PlcMonitor.Drivers.S7Symbolic.Tests` | Type mapping on the real library tag classes, and the connection against a simulated CPU |
| `PlcMonitor.IntegrationTests` | **Real PLC, read-only.** Skipped unless these environment variables are set: `PLCMON_IT_SYMBOLIC_HOST`, `PLCMON_IT_PASSWORD`, `PLCMON_IT_SYMBOL`, `PLCMON_IT_ABSOLUTE_HOST`, `PLCMON_IT_ABSOLUTE_ADDR` |

Status on 2026-10-14: 228 unit tests passing. Integration against the S7-1517F: 5 passed, 1 skipped. The absolute driver is not tested against that PLC because PUT/GET is disabled on it; a PUT/GET-enabled PLC is still needed for a real validation.

### Conventions
- .NET 10, `Nullable` on, `TreatWarningsAsErrors`, `AnalysisLevel latest-recommended`. The legacy `S7CommPlus` code is excluded from analysis.
- Package versions are set centrally in `Directory.Packages.props`.
- Git: one branch per piece of work (`feature/...`), small commits. The ES/EN manuals are updated in the same branch as the feature.

## 6. Licenses

| Component | License | Usage |
|---|---|---|
| S7CommPlusDriver (port in `src/S7CommPlus`) | LGPL-3.0 | Separate, replaceable assembly; changes published (`NOTICE.md`) |
| zlib.net (ComponentAce) | BSD | Inside `S7CommPlus` |
| S7.Net Plus | MIT | NuGet package |
| Spectre.Console | MIT | CLI |
| OpenSSL 3.5 | Apache-2.0 | Native library |

The AGLink library and the activation key bundled in Ignition's module are **not** used: they belong to Inductive Automation and DELTA LOGIC.
