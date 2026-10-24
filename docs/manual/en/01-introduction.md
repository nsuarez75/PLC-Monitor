# 1. Introduction

PLC-Monitor is used to look at what is happening inside a PLC without having to open the programming
software or create a new screen every time. The app shows live values, stores a history, lets you compare
signals in charts, and publishes the data over OPC UA for other systems.

The program is intended for diagnostics and supervision, not to replace the plant SCADA. You can use it to
check a sequence, look for an intermittent failing condition, review a setpoint, or keep a record of a
machine's behavior during a shift.

## The two pieces

### The desktop app

It is the window you open on your computer. From it you:

- create and edit devices and tags;
- connect to one or several collectors;
- see the live values and write setpoints;
- query the history and create trends, panels, and transformations;
- configure the collector's OPC UA server;
- install or update collectors on other computers over SSH.

The app does not need a central server. It saves its preferences, collector profiles, views, and panels in your
user folder. The profiles include the URL and token of each collector; the token is stored protected by the
operating system.

### The collector

The collector is a service installed on the computer that is close to the PLC. It is responsible for:

- connecting to the PLCs through the drivers;
- reading the tags in the scan groups;
- keeping the last quality and the last value of each tag;
- saving the recorded samples in SQLite;
- publishing the tags over OPC UA if you enable it;
- serving the app over gRPC.

The collector keeps running when you close the app. If the app disconnects, the PLC keeps being read and the
history continues. When it reconnects, the app receives the current status and the new changes.

## Concepts that appear throughout the manual

| Concept | What it is |
|---|---|
| **Collector** | `plcmon-agent` service installed on a computer. It has a URL and a token. |
| **Collector profile** | The local configuration the app uses to connect to a collector: name, URL, token, certificate, and SSH data. |
| **Device** | A PLC, drive, or gateway configured inside the collector. It has a driver and some settings. |
| **Driver** | The protocol family: S7 symbolic, S7 absolute, or Modbus. |
| **Tag** | A specific variable of a device, with an address, a type, and a scan group. |
| **Scan group** | Set of tags that are read together at a certain interval (`intervalMs`). |
| **Quality** | Indicates whether the value is reliable (`Good`), uncertain (`Uncertain`), or bad (`Bad`). A `Bad` value usually comes with an error text. |
| **History recording** | Saving a tag's samples to query them later. It is enabled per tag. |
| **Transformation** | Translation of an integer value to text, for example `0 = Parado`, `1 = Marcha`. |
| **Trend view** | A saved chart with its tags, type, window, and data source. |
| **Panel** | A grid with several saved views, to see them at the same time. |
| **Write key** | Each of the permissions that must match for a write to reach the PLC. |

## Architecture

```text
Desktop app ─── gRPC (TLS + token) ───▶ Collector ─ S7 / Modbus ───▶ PLC
       ▲                                   │
       └──────────── status and values ────┘
                                           │
                                           ├── SQLite history
                                           └── OPC UA server
```

The app can be on the same computer as the collector or on another one. The usual setup in a plant is to have an
collector on the machine network and the app at the maintenance workstation. You can also install a collector on
your own laptop to test with a laboratory PLC.

By default, the collector listens on `http://127.0.0.1:5310` when it is run without installing and for local use
only. The installer creates a configuration with TLS on port `5311` and a token. The app uses that URL and
that token when connecting. Do not confuse the collector port with the PLC port: the PLC is configured inside
each device.

## What you can do with each driver

| Driver | Typical use | Symbol browsing | Addresses |
|---|---|---|---|
| **S7 simbólico** (S7 symbolic, `s7-symbolic`) | S7-1200 and S7-1500 with S7CommPlus symbolic access. | Yes. | Program names, for example `"MiDB".Motor.Velocidad`. |
| **S7 absoluto** (S7 absolute, `s7-absolute`) | S7-300/400 and also 1200/1500 with PUT/GET. | No. | Absolute addresses, for example `DB1.DBW10` or `M10.1`. |
| **Modbus** (`modbus`) | Modbus TCP, RTU over serial port, or RTU over TCP. | No. | `HR0`, `IR5`, `C3`, `DI2`, `40001`, with optional type and byte order. |

The symbolic driver is the most convenient when the PLC supports it: you can search for a variable by its
name and you do not have to calculate offsets. The absolute driver is necessary on older CPUs or when the
program does not expose symbols. Modbus is common in drives, power network analyzers, expansion modules, and
third-party equipment.

## Requirements

### For the app

- Windows x64, Linux x64, or Linux ARM64.
- The release is a self-contained executable; you do not need to install .NET.
- To build from the source code you do need the .NET 10 SDK.

### For the collector

- Linux or Windows on the same network as the PLC.
- Administrator permissions to install the service.
- Access to the PLC port: `102/tcp` for S7, `502/tcp` for Modbus TCP, or the serial port for Modbus RTU.

### Ports that PLC-Monitor usually uses

| Port | Use |
|---|---|
| `5310/tcp` | Local collector without TLS, only for testing on the same computer. |
| `5311/tcp` | Collector installed with TLS and token. It is the installer's default value. |
| `4840/tcp` | The collector's OPC UA server, if you enable it. |
| `102/tcp` | S7. |
| `502/tcp` | Modbus TCP. |

Open only the necessary ports and do not publish the collector on the Internet. The
[Security](12-security.md) chapter explains how to combine them.

## What it does not do

- It does not replace a SCADA system: it has no alarms, recipes, shifts, or plant user management.
- It does not program the PLC or download programs.
- It does not guarantee that a write is safe for the machine. The decision to write and the value must
  always be validated in the process.
- It does not synchronize data between several apps. Each app queries the same collector, but does not share its
  views or panels with other people.
- It does not send data to the cloud. Everything stays in the collector and on the computer where you run the
  app, unless you configure OPC UA or another integration.

## Quick tour of the window

![Main window with a collector connected](../img/01-window.png)

1. **Top bar**: collector profile, **Conectar** (Connect) or **Desconectar** (Disconnect) button, connection
   status, and the **Dispositivos** (Devices), **Tendencias** (Trends), **Transformaciones**
   (Transformations), **OPC UA**, and **Colectores** (Collectors) tabs.
2. **Device list**: on the left. Each row shows the name, the status, the driver, and the number of tags.
   The **+** button creates a device.
3. **Central area**: changes depending on the tab. In **Dispositivos** you have the **Tags**,
   **Histórico** (History), and **Explorar** (Browse) tabs for the selected device.
4. **Bottom bar**: app messages, errors, and confirmations. If a message appears in red, the operation has
   not completed.
5. **Theme**: the icon on the right of the top bar switches between light and dark.

With that you can now follow the [Installation and first use](02-installation-and-first-use.md) chapter. If
you already have a collector running, skip to [Devices and drivers](04-devices-and-drivers.md).
