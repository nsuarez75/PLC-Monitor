# PLC-Monitor — Manual (English)

> Version 1.0.0 released (phase 1) · Phase 2 (agent) in development · Last updated: 2026-10-14

PLC-Monitor is an in-house, free tool for on-site testing and diagnostics of Siemens S7 PLCs.

| Document | Audience |
|---|---|
| [User guide](user-guide.md) | Technicians who connect to a PLC, search variables, read, watch or write values |
| [Agent](agent.md) | Whoever installs and configures the service that watches several PLCs and exposes them over gRPC |
| [Technical manual](technical.md) | Developers: architecture, drivers, building, testing, adding protocols |

## Current status

| Feature | Status |
|---|---|
| **S7 symbolic** driver (S7-1200/1500, S7CommPlus with or without TLS, password; no PUT/GET) | ✅ Available |
| **S7 absolute** driver (S7-300/400/1200/1500, PUT/GET) | ✅ Available |
| `plcmon` CLI: info, browse, read, watch, write | ✅ Available |
| Multi-PLC agent (service) with gRPC API | 🚧 Phase 2, in development (branch `feature/fase2-agente`) |
| Desktop app (Avalonia) | ⏳ Phase 3 |
| Historian (SQLite / TimescaleDB) | ⏳ Phase 4 |
| OPC UA server | ⏳ Phase 5 |
| Remote agents over SSH | ⏳ Phase 6 |
| Charts | ⏳ Phase 7 |
| More protocols (Modbus TCP...) | ⏳ Planned (the architecture already supports it) |

## Manual change log

| Date | Change |
|---|---|
| 2026-10-14 | First version: S7 symbolic and absolute drivers, `plcmon` CLI |
| 2026-10-14 | Phase 2: agent manual; S7 error 0x8104 (PUT/GET disabled) in troubleshooting |
| 2026-10-15 | `plcmon agent ...` commands (agent client from the terminal) |
| 2026-10-15 | Live configuration and browse API; `plcmon agent browse/config/device/tag` |
| 2026-10-15 | `s7-symbolic` with or without TLS (`--security auto/tls/legacy`) on the DotNetProjects.S7CommPlusDriver fork; no native OpenSSL |
