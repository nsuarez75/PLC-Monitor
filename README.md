# PLC-Monitor

Aplicación de escritorio y agente para supervisar y diagnosticar PLCs. Lee variables por S7 y Modbus, las
muestra en vivo, las historiza en SQLite y permite crear tendencias, paneles y transformaciones. También
publica los datos por OPC UA y admite escritura de consignas con permisos por dispositivo, tag y servidor.

La app está hecha con Avalonia y .NET 10 para Windows y Linux. Se conecta por gRPC al agente, un servicio
que se instala junto al PLC y sigue leyendo y guardando datos aunque la app esté cerrada. El repositorio
incluye además la CLI `plcmon` para diagnóstico y administración.

**Documentación:** [Manual de usuario](docs/manual/README.md): instalación, configuración, OPC UA, seguridad,
mantenimiento y resolución de problemas.
