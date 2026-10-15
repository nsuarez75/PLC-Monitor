# Changelog

## [Sin publicar / Unreleased] — fase 2 / phase 2

### Cambiado / Changed
- **`s7-symbolic` sobre un fork propio de DotNetProjects.S7CommPlusDriver** (submódulo `external/S7CommPlusDriver`, rama `plcmonitor`) en lugar del port de thomas-v2 en `src/S7CommPlus`. / *Now built on an own fork of DotNetProjects.S7CommPlusDriver.*
- **Acceso simbólico sin TLS**: nuevo ajuste `security` (`auto` por defecto, `tls`, `legacy`; CLI `--security`). `auto` intenta TLS y, si la CPU lo rechaza, usa la autenticación clásica por *challenge* (HarpoS7), como el modo *Preferred* de Ignition. No hace falta PUT/GET. / *Symbolic access without TLS (`security`: `auto`/`tls`/`legacy`).*
- TLS 1.3 gestionado (BouncyCastle): **ya no hace falta OpenSSL** (ni las DLL en Windows ni `libssl3` en Linux). Se retiran `scripts/get-openssl-windows.sh` y `PLCMONITOR_OPENSSL_DIR`. / *Managed TLS: OpenSSL is no longer required.*
- `GetDeviceInfo`/`plcmon info` muestran la seguridad negociada (`Security`). La excepción del driver pasa a llamarse `S7SymbolicException` e incluye el detalle de la librería.
- Se retira la opción `--tls-keylog` (y `tlsKeyLogDirectory`).

### Corregido en el fork / Fixed in the fork
- Contraseña incorrecta dada por válida (truncado del `ReturnValue` a `Int16`), igual que en la 1.0.
- S7-1200: un símbolo cuyo primer nivel no era un DB (o no existía) tiraba la sesión con "Could not get type info": la CPU no tiene áreas de temporizadores/contadores S7.

### Añadido / Added
- **`plcmon agent ...`**: la CLI como cliente gRPC del agente, local o remoto (`-a URL` o `PLCMON_AGENT`): `info`, `devices`, `tags`, `read`, `write` (con confirmación y usuario para la auditoría), `enable`/`disable` (`--persist`) y `watch` en directo con resuscripción automática. / *The CLI as a gRPC client of the agent.*
- Proyecto de tests `PlcMonitor.Cli.Tests` (órdenes contra el agente en memoria).
- **Motor** `PlcMonitor.Engine`: varios dispositivos a la vez, cada uno con su supervisor; reconexión con espera exponencial; grupos de escaneo; caché del último valor; activar/desactivar en caliente; lectura bajo demanda; escritura con doble llave (`writesEnabled` + `writable`) y log de auditoría; suscripciones con foto inicial y eventos sin huecos. Configuración en `agent.json` validada al arrancar.
- **Agente** `plcmon-agent`: servicio (Windows Service / systemd) con API gRPC (`agent.proto`, paquete `plcmonitor.agent.v1`) y streaming en vivo. Por defecto solo escucha en `127.0.0.1:5310`.
- `PlcMonitor.Agent.Contracts`: cliente y servidor gRPC generados, para la app de escritorio.
- Unidad systemd de ejemplo (`deploy/linux/plcmon-agent.service`) y manual del agente (ES/EN).
- El paquete de la release incluye el agente en `agent/`.

### Corregido / Fixed
- `s7-symbolic`: un valor que la librería rechaza al asignarlo (p. ej. STRING más largo que su longitud) daba «Exception has been thrown by the target of an invocation»; ahora `BadTypeMismatch` con el motivo.
- `s7-absolute`: el error S7 0x8104 (PUT/GET deshabilitado) se daba como `BadAddressInvalid` con los bytes de la respuesta; ahora es `BadAccessDenied` con la indicación de habilitar PUT/GET.
- Paquetes Linux (`publish-release.sh`): los directorios se empaquetaban sin permiso de ejecución (`drw-r--r--`) y un usuario normal no podía entrar en ellos tras descomprimir. Afecta a los `.tar.gz` de la v1.0.0.

### Verificado / Verified
- S7-1200 real (10.225.188.120, PUT/GET deshabilitado): conexión y estado de la CPU con `s7-absolute`; agente completo de punta a punta (estado, `GetDeviceInfo`, `ListTags`, `Subscribe`) también desde el paquete linux-x64 extraído como usuario normal.
- 310 tests correctos, 6 omitidos (integración sin PLC).
- S7-1200 real sin TLS ni PUT/GET (10.225.188.120): `s7-symbolic` en modo `auto` → `legacy`; exploración (1463 símbolos), lectura y sondeo, también desde el paquete linux-x64. Integración: 4 correctos, 2 omitidos. 312 tests unitarios correctos.
- Escritura en el S7-1200 sin TLS (laboratorio): Real, Int, Time, STRING (con `ñ`) y Word con `plcmon write`, y a través del agente con doble llave, validación de tipo y log de auditoría. Valores restaurados.

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
