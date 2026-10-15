# PLC-Monitor — Manual (castellano)

> Versión 1.0.0 publicada (fase 1) · Fase 2 (agente) en desarrollo · Última actualización: 2026-10-14

PLC-Monitor es una herramienta propia y gratuita para pruebas y diagnóstico a pie de máquina con PLCs Siemens S7.

| Documento | Para quién |
|---|---|
| [Manual de uso](usuario.md) | Técnicos que van a conectarse a un PLC, buscar variables, leer, vigilar o escribir valores |
| [Agente](agente.md) | Quien instala y configura el servicio que vigila varios PLC y lo expone por gRPC |
| [Manual técnico](tecnico.md) | Desarrolladores: arquitectura, drivers, cómo compilar, probar y añadir protocolos |

## Estado actual

| Funcionalidad | Estado |
|---|---|
| Driver **S7 simbólico** (S7-1200/1500, S7CommPlus con o sin TLS, contraseña; sin PUT/GET) | ✅ Disponible |
| Driver **S7 absoluto** (S7-300/400/1200/1500, PUT/GET) | ✅ Disponible |
| CLI `plcmon`: info, explorar, leer, vigilar, escribir | ✅ Disponible |
| Agente (servicio) multi-PLC con API gRPC | 🚧 Fase 2, en desarrollo (rama `feature/fase2-agente`) |
| Aplicación de escritorio (Avalonia) | ⏳ Fase 3 |
| Historiador (SQLite / TimescaleDB) | ⏳ Fase 4 |
| Servidor OPC UA | ⏳ Fase 5 |
| Agentes remotos por SSH | ⏳ Fase 6 |
| Gráficas | ⏳ Fase 7 |
| Otros protocolos (Modbus TCP...) | ⏳ Previsto (la arquitectura ya lo contempla) |

## Historial de cambios del manual

| Fecha | Cambio |
|---|---|
| 2026-10-14 | Primera versión: drivers S7 simbólico y absoluto, CLI `plcmon` |
| 2026-10-14 | Fase 2: manual del agente; error S7 0x8104 (PUT/GET deshabilitado) en solución de problemas |
| 2026-10-15 | Órdenes `plcmon agent ...` (cliente del agente desde la consola) |
| 2026-10-15 | `s7-symbolic` con o sin TLS (`--security auto/tls/legacy`) sobre el fork de DotNetProjects.S7CommPlusDriver; sin OpenSSL nativo |
