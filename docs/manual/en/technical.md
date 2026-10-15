# PLC-Monitor — Technical manual

> Version 1.0.0 · Last updated: 2026-10-15 · [Castellano](../es/tecnico.md)

## 1. Architecture

The full picture (agent, Avalonia app, historian, OPC UA, SSH) is in [docs/arquitectura.md](../../arquitectura.md) (Spanish). Current state (phase 1 + phase 2 in development):

```
   gRPC clients (Avalonia app in phase 3, scripts...)
                      │  agent.proto (PlcMonitor.Agent.Contracts)
              plcmon-agent (PlcMonitor.Agent) ── AgentGrpcService · EngineHost
                      │
              PlcMonitor.Engine ── TagEngine · DeviceSupervisor · agent.json
                      │
 plcmon (CLI) ── PlcMonitor.Core  ── DriverRegistry · TagPoller · ValueParser · ValueFormatter
                      │
           PlcMonitor.Abstractions ── IDriverFactory · IDeviceConnection · IBrowsableConnection
              │                   │                                   │
   Drivers.S7Symbolic     Drivers.S7Absolute                 (future) Drivers.ModbusTcp ...
        │                         │
   S7CommPlusDriver           S7.Net Plus (MIT)
   (own fork, LGPL-3)
        │
   HarpoS7 (MIT) · BouncyCastle (MIT)
```

### Projects

| Project | Responsibility |
|---|---|
| `src/PlcMonitor.Abstractions` | Common driver contract: `IDriverFactory`, `DriverFactoryBase<TSettings>`, `IDeviceConnection`, `DeviceConnectionBase`, `IBrowsableConnection`, `TagValue`, `QualityCode`, `TagDataType` |
| `src/PlcMonitor.Core` | Protocol-independent logic: `DriverRegistry`, `TagPoller` (polling with change detection), `ValueParser` (text → type), `ValueFormatter` |
| `src/PlcMonitor.Drivers.S7Symbolic` | `s7-symbolic` driver on top of S7CommPlus |
| `src/PlcMonitor.Drivers.S7Absolute` | `s7-absolute` driver on top of S7.Net Plus |
| `external/S7CommPlusDriver` | Git submodule: [own fork](https://github.com/nsuarez75/S7CommPlusDriver) (branch `plcmonitor`) of [DotNetProjects.S7CommPlusDriver](https://github.com/dotnetprojects/S7CommPlusDriver), **LGPL-3**. Separate assembly (`S7CommPlusDriver.dll`); changes listed in `PLCMONITOR-NOTICE.md` |
| `src/PlcMonitor.Cli` | `plcmon` CLI (Spectre.Console). Direct PLC commands and `plcmon agent ...` as a gRPC client of the agent (`AgentCommands.cs`) |
| `src/PlcMonitor.Engine` | Multi-device engine: `TagEngine` (cache, subscriptions, enable/disable, two-key writes with audit), `DeviceSupervisor` (connection, backoff reconnection, scan groups), `agent.json` configuration |
| `src/PlcMonitor.Agent.Contracts` | `agent.proto` with generated gRPC client and server. Used by the app and other clients without depending on ASP.NET |
| `src/PlcMonitor.Agent` | `plcmon-agent` service (ASP.NET Core, HTTP/2, Windows Service/systemd). See the [agent manual](agent.md) |
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

- Library: `S7CommPlusClient` from the DotNetProjects.S7CommPlusDriver fork, wrapped in `ISymbolicClient` (`S7CommPlusSymbolicClient`) so tests can replace it.
- Protocol: S7CommPlus over ISO-on-TCP (port 102). `security` setting:

| `security` | Behaviour | Ignition equivalent |
|---|---|---|
| `auto` (default) | Tries TLS; if the CPU rejects it at `InitSsl`, opens a new connection with the legacy authentication | *Preferred* |
| `tls` | TLS 1.3 only (managed TLS with BouncyCastle, no native OpenSSL). S7-1200 FW ≥ 4.5 / S7-1500 FW ≥ 2.9 with secure PG/HMI communication (TIA ≥ V17) | *Required* |
| `legacy` | Legacy challenge authentication only (no TLS): TIA ≤ V16 projects or CPUs with secure communication turned off | *Disabled* |

- **Legacy authentication** (HarpoS7, MIT): in `CreateObject` the CPU sends a 20-byte challenge and its public-key fingerprint (family `00` = 1500, `01` = 1200, `03` = PLCSIM). With the known public key the session key is derived and every PDU is signed (32-byte digest). The session key is renewed every 25 minutes.
- Then: password legitimation (if given). With a user name, the new legitimation (FW 3.1+).
- `AutoReconnect = false` in the library: reconnection belongs to `DeviceSupervisor`. `WriteEnabled = true`: the engine applies the two-key write rule.
- **On-demand symbol resolution** (`GetTagBySymbolAsync`): no need to browse the whole CPU before reading. Each resolved symbol is cached per session, including missing ones (negative cache). Writes use a separate cache with their own tag instances (they do not touch the read value and keep the declared STRING length). Caches are cleared on reconnect.
- `BrowseAsync`: full browse, cached in memory (name + type). Supports filter, limit and `Refresh`.
- **Types** are derived from the tag class the library itself creates (`PlcTags.TagFactory`), so every type it supports (BBOOL, BLOCK_FB, HW_*...) is covered. Conversions:
  - TIME, TOD, LTIME, LTOD and S5TIME → `TimeSpan`.
  - DTL, DT, DATE and LDT → `DateTime`.
  - CHAR and WCHAR → `string`.
  - Arrays → .NET arrays.
- A write rejected by the CPU → `BadAccessDenied`. The same address twice in one write → `BadConfiguration`.
- Errors: library exceptions become `S7SymbolicException` (`S7Consts` code + library detail). `IsCommunicationError` decides whether to reconnect: TCP/ISO errors, timeouts, TLS, integrity digest mismatch, or anything the library flags as transient.
- `GetDeviceInfo` includes `Security`: the negotiated security (`TLS` or `legacy (sin TLS)`).

### Changes in the fork (branch `plcmonitor`)

| Problem | Cause | Fix |
|---|---|---|
| A wrong password was accepted | The legitimation `ReturnValue` (UInt64) was truncated to `Int16`. The 1517F returned `0x2605990006990016` → `0x0016` > 0 | Any `ReturnValue != 0` → `errCliAccessDenied` |
| On an S7-1200, any symbol whose first level was not a DB broke the session ("Could not get type info") | After the DBs the lookup tries M, Q, I, S7 timers and S7 counters; the 1200 has no timers/counters and the type-info request failed | A missing area means "not found here" and is remembered for the session |
| 24 MB of OpenSSL DLLs in the output | The package ships them for the OpenSSL backend | `S7CommPlusIncludeOpenSslRuntimes=false` (in `external/Directory.Build.props`) |
| The driver uses `PlcTags.TagFactory` (internal) | — | `InternalsVisibleTo` for the driver and its tests |

Library bugs are fixed in the fork; no issues or PRs are opened upstream.

### Findings from version 1.0 (earlier thomas-v2 port)

- With OpenSSL ≥ 3.5 the TLS handshake with the S7-1517F never finished because of the post-quantum X25519MLKEM768 group. Not applicable with managed TLS (BouncyCastle).
- The test S7-1200 answers `InitSsl` with `ReturnValue=0xA201D60001E6FDF9` (error bit): no secure communication. With `security: auto` it connects without TLS.

## 4. S7 absolute driver (`s7-absolute`)

- `S7Address`: in-house parser (generated regex), compatible with the Ignition and STEP 7 syntaxes. See the table in the [user guide](user-guide.md#absolute-s7-absolute). `ToString()` gives the canonical form.
- `S7Codec`: big-endian, S7 STRING (max/current length header, Latin-1), BCD DATE_AND_TIME (years 1990–2089), arrays.
- `ReadPlanner`: merges addresses in the same area/DB when the gap is ≤ `ReadGapBytes` (default 32) and the block stays ≤ 4096 bytes, so fewer requests are needed. S7.Net splits by PDU size.
- Transport: `IS7Transport` (S7.Net Plus), replaceable in tests. Bits are written with `WriteBit`, with no read-modify-write.
- A failing block (for example a missing DB) only turns its own addresses into `BadAddressInvalid`.

## 5. Build and test

```bash
git clone --recurse-submodules git@github.com:nsuarez75/PLC-Monitor.git   # or: git submodule update --init
dotnet build PlcMonitor.slnx
dotnet test --solution PlcMonitor.slnx          # Microsoft.Testing.Platform (global.json)
dotnet run --project src/PlcMonitor.Cli -- info -H 192.168.2.1
dotnet run --project src/PlcMonitor.Agent -- --Agent:ConfigPath=/path/agent.json
scripts/publish-release.sh linux-x64    # package with the CLI and the agent (in agent/)
```

### Tests

| Project | Coverage |
|---|---|
| `PlcMonitor.Core.Tests` | Driver registry, factories, polling with a fake clock, value parser and formatter |
| `PlcMonitor.Drivers.S7Absolute.Tests` | Address parser, codec, read planner, and the connection against an in-memory simulated PLC |
| `PlcMonitor.Drivers.S7Symbolic.Tests` | Type mapping on the real library tag classes, and the connection against a simulated CPU |
| `PlcMonitor.Engine.Tests` | Configuration validation and loading, backoff, engine with a simulated driver (derived from `DeviceConnectionBase`) and a fake clock: reconnection, enable/disable, 4 independent PLCs, two-key writes, subscriptions and slow subscribers |
| `PlcMonitor.Agent.Tests` | gRPC API against the in-memory agent (`WebApplicationFactory`) with a simulated driver; protobuf mapping |
| `PlcMonitor.Cli.Tests` | `plcmon agent ...` commands with the real CLI wiring (`CliApp`), against the in-memory agent and a test console (Spectre.Console.Testing) |
| `PlcMonitor.IntegrationTests` | **Real PLC, read-only.** Skipped unless these environment variables are set: `PLCMON_IT_SYMBOLIC_HOST`, `PLCMON_IT_SECURITY`, `PLCMON_IT_EXPECTED_SECURITY`, `PLCMON_IT_PASSWORD`, `PLCMON_IT_SYMBOL`, `PLCMON_IT_ABSOLUTE_HOST`, `PLCMON_IT_ABSOLUTE_ADDR` |

Status on 2026-10-15 (branch `feature/s7plus-legacy`): 312 tests passing, 6 skipped without a PLC. Against the real S7-1200 (10.225.188.120, no TLS, no PUT/GET): `auto` connection → `legacy`, browse (1463 symbols), reads and polling; integration 4 passed, 2 skipped (password and absolute). Still to repeat against the S7-1517F (TLS + password).

Status on 2026-10-14 (branch `feature/fase2-agente`): 310 tests passing, 6 skipped (integration without a PLC). A real S7-1200 (PUT/GET disabled) confirmed the absolute driver's connection and status read, and the full agent end to end. Phase 1: 228 unit tests passing. Integration against the S7-1517F: 5 passed, 1 skipped. The absolute driver is not tested against that PLC because PUT/GET is disabled on it; a PUT/GET-enabled PLC is still needed for a real validation.

### Conventions
- .NET 10, `Nullable` on, `TreatWarningsAsErrors`, `AnalysisLevel latest-recommended`. Third-party code (`external/`) does not inherit these settings (its own `external/Directory.Build.props` and `Directory.Packages.props`).
- Package versions are set centrally in `Directory.Packages.props`.
- Git: one branch per piece of work (`feature/...`), small commits. The ES/EN manuals are updated in the same branch as the feature.

## 6. Licenses

| Component | License | Usage |
|---|---|---|
| S7CommPlusDriver (fork in `external/S7CommPlusDriver`) | LGPL-3.0 or later | Separate, replaceable assembly; changes published (`PLCMONITOR-NOTICE.md`) |
| HarpoS7 / HarpoS7.PublicKeys | MIT | Legacy authentication without TLS |
| BouncyCastle.Cryptography | MIT | Managed TLS 1.3 |
| zlib.net-mutliplatform | BSD-3-Clause | Block decompression |
| S7.Net Plus | MIT | NuGet package |
| Spectre.Console | MIT | CLI |

The AGLink library and the activation key bundled in Ignition's module are **not** used: they belong to Inductive Automation and DELTA LOGIC.
