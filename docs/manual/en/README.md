# PLC-Monitor — Manual (English)

> Version 0.1.0 · Phase 1 (core, S7 drivers and CLI) · Last updated: 2026-10-14

PLC-Monitor is an in-house, free tool for on-site testing and diagnostics of Siemens S7 PLCs.

| Document | Audience |
|---|---|
| [User guide](user-guide.md) | Technicians who connect to a PLC, search variables, read, watch or write values |
| [Technical manual](technical.md) | Developers: architecture, drivers, building, testing, adding protocols |

## Current status

| Feature | Status |
|---|---|
| **S7 symbolic** driver (S7-1200/1500, S7CommPlus with TLS and password) | ✅ Available |
| **S7 absolute** driver (S7-300/400/1200/1500, PUT/GET) | ✅ Available |
| `plcmon` CLI: info, browse, read, watch, write | ✅ Available |
| Multi-PLC agent (service) | ⏳ Phase 2 |
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
