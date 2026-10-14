# Changelog

## [Sin publicar / Unreleased] — fase 2 / phase 2

### Añadido / Added
- **Motor** `PlcMonitor.Engine`: varios dispositivos a la vez, cada uno con su supervisor; reconexión con espera exponencial; grupos de escaneo; caché del último valor; activar/desactivar en caliente; lectura bajo demanda; escritura con doble llave (`writesEnabled` + `writable`) y log de auditoría; suscripciones con foto inicial y eventos sin huecos. Configuración en `agent.json` validada al arrancar.
- **Agente** `plcmon-agent`: servicio (Windows Service / systemd) con API gRPC (`agent.proto`, paquete `plcmonitor.agent.v1`) y streaming en vivo. Por defecto solo escucha en `127.0.0.1:5310`.
- `PlcMonitor.Agent.Contracts`: cliente y servidor gRPC generados, para la app de escritorio.
- Unidad systemd de ejemplo (`deploy/linux/plcmon-agent.service`) y manual del agente (ES/EN).
- El paquete de la release incluye el agente en `agent/`.

### Corregido / Fixed
- `s7-absolute`: el error S7 0x8104 (PUT/GET deshabilitado) se daba como `BadAddressInvalid` con los bytes de la respuesta; ahora es `BadAccessDenied` con la indicación de habilitar PUT/GET.
- Paquetes Linux (`publish-release.sh`): los directorios se empaquetaban sin permiso de ejecución (`drw-r--r--`) y un usuario normal no podía entrar en ellos tras descomprimir. Afecta a los `.tar.gz` de la v1.0.0.

### Verificado / Verified
- S7-1200 real (10.225.188.120, PUT/GET deshabilitado): conexión y estado de la CPU con `s7-absolute`; agente completo de punta a punta (estado, `GetDeviceInfo`, `ListTags`, `Subscribe`) también desde el paquete linux-x64 extraído como usuario normal.
- 310 tests correctos, 6 omitidos (integración sin PLC).

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
