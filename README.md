# PLC-Monitor

## Español

Aplicación de escritorio y agente para supervisar y diagnosticar PLCs. Lee variables por S7 y Modbus, las
muestra en vivo, las historiza en SQLite y permite crear tendencias, paneles y transformaciones. También
publica los datos por OPC UA y admite escritura de consignas con permisos por dispositivo, tag y servidor.

La app está hecha con Avalonia y .NET 10 para Windows y Linux. Se conecta por gRPC al agente, un servicio
que se instala junto al PLC y sigue leyendo y guardando datos aunque la app esté cerrada. El repositorio
incluye además la CLI `plcmon` para diagnóstico y administración.

**Documentación**
- [Manual de usuario (Español)](docs/manual/README.md)

## English

Desktop app and agent for monitoring and troubleshooting PLCs. It reads variables over S7 and Modbus,
displays them live, records them to SQLite, and supports trends, panels, and transformations. It also
publishes data over OPC UA and allows setpoint writes with per-device, per-tag, and per-server permissions.

The app is built with Avalonia and .NET 10 for Windows and Linux. It connects over gRPC to the agent, a
service installed next to the PLC that keeps reading and storing data even when the app is closed. The
repository also includes the `plcmon` CLI for diagnostics and administration.

**Documentation**
- [User manual (English)](docs/manual/en/README.md)
