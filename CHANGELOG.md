# Changelog

## [Sin publicar / Unreleased]

### Corregido / Fixed
- App, panel Agentes: tras instalar o consultar se perdía el agente seleccionado arriba y no se veía «Actividad».
- App: la carpeta de paquetes del agente se recuerda y, desde el código, se encuentra sola la de `artifacts/release`.

## [1.1.0] - 2026-10-15

Agente multi-PLC con API gRPC, app de escritorio, acceso simbólico sin TLS y agentes remotos instalables por SSH.
Multi-PLC agent with gRPC API, desktop app, symbolic access without TLS and remote agents installed over SSH.

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
- **Agentes remotos (fase 6 adelantada)**: la app gestiona una lista de agentes (perfiles con URL, token y huella; token protegido con DPAPI en Windows o fichero 0600 en Linux) y los **instala, actualiza, consulta, muestra su log y desinstala por SSH** en equipos Linux (systemd, sudo) y Windows (OpenSSH, servicio). Proyecto `PlcMonitor.Deploy` (SSH.NET). / *Remote agents managed and installed from the app over SSH.*
- **Seguridad del agente**: token de acceso (`Agent:Security:TokenSha256`) y TLS con certificado propio fijado por huella; `agent.settings.json` junto al ejecutable. CLI `--token` / `--cert`. / *Agent token and TLS with certificate pinning.*
- La release incluye `agents/plcmon-agent-<versión>-<rid>` (paquete del agente que sube la app).
- **Rediseño de la app**: tema claro/oscuro, tarjetas, pastillas de estado e iconos de línea.
- **Aplicación de escritorio `plcmon-app` (fase 3)**, Avalonia 12, Windows y Linux: conexión a un agente local o remoto (recuerda las URL), dispositivos con estado en color y activar/desactivar, tags en vivo por suscripción (con resuscripción automática), escritura con confirmación, explorador de símbolos con alta de tags, editor de dispositivos (ajustes JSON con valores por defecto del driver, llaves de escritura, reconexión) y bajas. Incluida en el paquete de la release (`app/`). Manual ES/EN. / *Desktop application.*
- **API del agente para la app (fase 3)**: `Browse`, `GetConfiguration`, `GetDeviceConfig`, `PutDevice` (alta, edición y renombrado validados) y `DeleteDevice`, con `persist` en `agent.json`; eventos `device_configured` y `device_removed`; drivers con ajustes por defecto y capacidades. En la CLI: `plcmon agent browse`, `config`, `device add/remove`, `tag add/remove`. / *Live configuration and browse API.*
- **Motor** `PlcMonitor.Engine`: varios dispositivos a la vez, cada uno con su supervisor; reconexión con espera exponencial; grupos de escaneo; caché del último valor; activar/desactivar en caliente; lectura bajo demanda; escritura con doble llave (`writesEnabled` + `writable`) y log de auditoría; suscripciones con foto inicial y eventos sin huecos. Configuración en `agent.json` validada al arrancar.
- **Agente** `plcmon-agent`: servicio (Windows Service / systemd) con API gRPC (`agent.proto`, paquete `plcmonitor.agent.v1`) y streaming en vivo. Por defecto solo escucha en `127.0.0.1:5310`.
- `PlcMonitor.Agent.Contracts`: cliente y servidor gRPC generados, para la app de escritorio.
- Unidad systemd de ejemplo (`deploy/linux/plcmon-agent.service`) y manual del agente (ES/EN).
- El paquete de la release incluye el agente en `agent/`.

### Corregido / Fixed
- App: pulsar Intro en el buscador de símbolos cerraba la aplicación.
- `s7-symbolic`: un valor que la librería rechaza al asignarlo (p. ej. STRING más largo que su longitud) daba «Exception has been thrown by the target of an invocation»; ahora `BadTypeMismatch` con el motivo.
- `s7-absolute`: el error S7 0x8104 (PUT/GET deshabilitado) se daba como `BadAddressInvalid` con los bytes de la respuesta; ahora es `BadAccessDenied` con la indicación de habilitar PUT/GET.
- Paquetes Linux (`publish-release.sh`): los directorios se empaquetaban sin permiso de ejecución (`drw-r--r--`) y un usuario normal no podía entrar en ellos tras descomprimir. Afecta a los `.tar.gz` de la v1.0.0.

### Verificado / Verified
- S7-1200 real (10.225.188.120, PUT/GET deshabilitado): conexión y estado de la CPU con `s7-absolute`; agente completo de punta a punta (estado, `GetDeviceInfo`, `ListTags`, `Subscribe`) también desde el paquete linux-x64 extraído como usuario normal.
- 310 tests correctos, 6 omitidos (integración sin PLC).
- S7-1200 real sin TLS ni PUT/GET (10.225.188.120): `s7-symbolic` en modo `auto` → `legacy`; exploración (1463 símbolos), lectura y sondeo, también desde el paquete linux-x64. Integración: 4 correctos, 2 omitidos. 312 tests unitarios correctos.
- Escritura en el S7-1200 sin TLS (laboratorio): Real, Int, Time, STRING (con `ñ`) y Word con `plcmon write`, y a través del agente con doble llave, validación de tipo y log de auditoría. Valores restaurados.
- Despliegue del agente por SSH probado de verdad en un contenedor Ubuntu 24.04 con systemd (instalar, TLS + token, estado, log, actualizar, desinstalar). **Windows sin probar en un equipo real.**
- **Pendiente:** validar `s7-symbolic` con la librería nueva contra el S7-1517F (TLS + contraseña).
- 390 tests correctos, 9 omitidos (integración sin PLC ni equipo SSH).

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
