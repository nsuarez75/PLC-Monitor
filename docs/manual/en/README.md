# PLC-Monitor user manual

**Version 1.16.0**

**Language:** [Español](../README.md) · [English](README.md)

The application interface is currently in Spanish. UI labels are kept in Spanish in this manual so they
match the running app and the screenshots.

PLC-Monitor is a desktop application for reading, recording to history, and visualizing variables from
programmable logic controllers (PLC). It consists of two pieces:

- **The desktop app**, from which you configure agents, devices, tags, charts, and OPC UA.
- **The agent**, a service installed next to the PLC that reads the variables, stores the history, and
  publishes the data over OPC UA. The app connects to the agent over gRPC, locally or over the network.

![PLC-Monitor main window](../img/01-window.png)

## First steps

1. Run `plcmon-app` (or `plcmon-app.exe` on Windows). The release is self-contained: you do not need to
   install .NET on the computer.
2. In the **Agentes** tab (Agents), create a profile or install a new agent. To test on the same computer,
   check **Este equipo (sin SSH)**.
3. Connect to the agent and create a device in the **Dispositivos** tab (Devices).
4. Add tags by typing their address or, in the drivers that allow it, with the symbol browser.
5. View the live values, enable history recording, and create trends or panels.

If this is your first time, follow the [Installation and first use](02-installation-and-first-use.md) chapter.

## Contents

| Chapter | Content |
|---|---|
| [1. Introduction](01-introduction.md) | Pieces, concepts, architecture, requirements, and ports. |
| [2. Installation and first use](02-installation-and-first-use.md) | Run the app, install the agent, connect, and tour the interface. |
| [3. Agents and profiles](03-agents-and-profiles.md) | Profiles, connection, installation over SSH, credentials, status, and logs. |
| [4. Devices and drivers](04-devices-and-drivers.md) | Adding PLCs, S7 and Modbus settings, JSON editing, and connection. |
| [5. Live tags](05-live-tags.md) | Tag table, adding by address, browser, details, and writing. |
| [6. History](06-history.md) | History recording, queries, aggregates, table, CSV, and deletion. |
| [7. Trends and charts](07-trends-and-charts.md) | Views, data sources, and the seven chart types. |
| [8. Panels](08-panels.md) | Grids with several views, synchronization, and PNG export. |
| [9. Transformations](09-transformations.md) | Displaying integer values as text and assigning them to tags. |
| [10. OPC UA](10-opc-ua.md) | Publishing tags, users, certificates, and writing over OPC UA. |
| [11. CLI](11-cli.md) | `plcmon`: direct reading, diagnostics, and agent administration. |
| [12. Security](12-security.md) | Tokens, TLS, write keys, permissions, and best practices. |
| [13. Configuration and maintenance](13-configuration-and-maintenance.md) | Files, backups, updates, retention, and logs. |
| [14. Troubleshooting](14-troubleshooting.md) | Connection, PLC, history, OPC UA, and chart troubleshooting. |

## Manual conventions

- Steps are numbered, and button and field names appear **in bold**.
- The examples use fictional names (`planta-demo.local`, `prensa-linea3`) and addresses reserved for
  documentation (`192.0.2.10`). They are not data from a real installation.
- Security notices are marked as **Important** or **Caution**.
- The screenshots were generated with synthetic data.

## Architecture on one page

```text
┌──────────────────────┐        gRPC (TLS + token)        ┌──────────────────────┐
│ Desktop app          │ ───────────────────────────────▶ │ Agent plcmon-agent   │
│ Windows / Linux      │                                  │ Linux / Windows      │
│                      │ ◀─────────────────────────────── │                      │
│ tags, trends,        │        status / values           │ PLCs, history,       │
│ history, OPC UA,     │                                  │ OPC UA               │
│ agent profiles       │                                  └──────────┬───────────┘
└──────────────────────┘                                             │
                                                           S7 / Modbus │
                                                                      ▼
                                                             ┌─────────────────┐
                                                             │ PLC / drive     │
                                                             └─────────────────┘
```

- The app **does not talk directly to the PLC** when you use an agent: it asks the agent for the data.
- The agent keeps the connection to the PLC, even if you close the app.
- Several apps can connect to the same agent if they share the URL and the token.
- The history and the configuration live on the agent's computer, not in the app.

## Quick requirements

- **App**: Windows x64, Linux x64, or Linux ARM64. The release includes everything needed.
- **Agent**: Linux or Windows, on the same network as the PLC. It is installed from the app itself.
- **Network**: access to the agent port (`5311/tcp` by default with TLS) and, if you use OPC UA, `4840/tcp`.
- **PLC**: depending on the driver, S7 access (102/tcp or S7CommPlus) or Modbus (502/tcp or serial port).
- **Permissions**: installing the agent requires administrator on Windows or `sudo`/`pkexec` on Linux.

## Security in two lines

Do not expose the agent directly to the Internet. Use a protected industrial network or a VPN, a different
token per installation, and the read-only token for anyone who only needs to query. Details are in
[Security](12-security.md).

## Status of this manual

The manual has been written from the code and from screenshots with synthetic data. Tests with real PLCs,
installation over SSH, and some error flows are still pending validation.
