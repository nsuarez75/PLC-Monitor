# Changelog

## [1.0.0] - 2026-10-14

Primera versión: núcleo multi-driver, drivers Siemens S7 y CLI `plcmon`.
First release: multi-driver core, Siemens S7 drivers and the `plcmon` CLI.

### Añadido / Added
- Contrato de drivers multi-protocolo (`IDriverFactory`, `IDeviceConnection`, `DeviceConnectionBase`) preparado para nuevos protocolos (Modbus TCP...).
- Driver **s7-symbolic**: S7CommPlus con TLS 1.3 y legitimación por contraseña para S7-1200/1500; resolución de símbolos bajo demanda; exploración con filtro.
- Driver **s7-absolute**: S7comm PUT/GET para S7-300/400/1200/1500 con sintaxis de direcciones compatible con Ignition/STEP 7 y agrupación de lecturas.
- CLI `plcmon`: `drivers`, `info`, `browse`, `read`, `watch`, `write` (con confirmación).
- Manuales técnico y de uso en castellano e inglés.

### Corregido en S7CommPlusDriver (LGPL-3) / Fixed in S7CommPlusDriver
- Con OpenSSL ≥ 3.5 el handshake TLS no terminaba (grupo X25519MLKEM768): se limitan los grupos a X25519/P-256/P-384.
- Una contraseña incorrecta se daba por válida (truncado del `ReturnValue` a Int16).

### Verificado / Verified
- S7-1517F real: TLS + contraseña, exploración de 2,2 M símbolos, lectura y sondeo. 228 tests unitarios.
- El driver absoluto solo está validado con PLC simulado (pendiente de un PLC con PUT/GET habilitado).
