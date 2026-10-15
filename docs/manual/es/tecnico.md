# PLC-Monitor — Manual técnico

> Versión 1.1.1 · Última actualización: 2026-10-15 · [English](../en/technical.md)

## 1. Arquitectura

La visión completa (agente, app Avalonia, historiador, OPC UA, SSH) está en [docs/arquitectura.md](../../arquitectura.md). Estado actual (fase 1 + fase 2 en desarrollo):

```
   clientes gRPC (app Avalonia en fase 3, scripts...)
                      │  agent.proto (PlcMonitor.Agent.Contracts)
              plcmon-agent (PlcMonitor.Agent) ── AgentGrpcService · EngineHost
                      │
              PlcMonitor.Engine ── TagEngine · DeviceSupervisor · agent.json
                      │
 plcmon (CLI) ── PlcMonitor.Core  ── DriverRegistry · TagPoller · ValueParser · ValueFormatter
                      │
           PlcMonitor.Abstractions ── IDriverFactory · IDeviceConnection · IBrowsableConnection
              │                   │                                   │
   Drivers.S7Symbolic     Drivers.S7Absolute                 (futuro) Drivers.ModbusTcp ...
        │                         │
   S7CommPlusDriver           S7.Net Plus (MIT)
   (fork propio, LGPL-3)
        │
   HarpoS7 (MIT) · BouncyCastle (MIT)
```

### Proyectos

| Proyecto | Responsabilidad |
|---|---|
| `src/PlcMonitor.Abstractions` | Contrato común de drivers: `IDriverFactory`, `DriverFactoryBase<TSettings>`, `IDeviceConnection`, `DeviceConnectionBase`, `IBrowsableConnection`, `TagValue`, `QualityCode`, `TagDataType` |
| `src/PlcMonitor.Core` | Lógica independiente del protocolo: `DriverRegistry`, `TagPoller` (sondeo con detección de cambios), `ValueParser` (texto → tipo), `ValueFormatter` |
| `src/PlcMonitor.Drivers.S7Symbolic` | Driver `s7-symbolic` sobre S7CommPlus |
| `src/PlcMonitor.Drivers.S7Absolute` | Driver `s7-absolute` sobre S7.Net Plus |
| `external/S7CommPlusDriver` | Submódulo git: [fork propio](https://github.com/nsuarez75/S7CommPlusDriver) (rama `plcmonitor`) de [DotNetProjects.S7CommPlusDriver](https://github.com/dotnetprojects/S7CommPlusDriver), **LGPL-3**. Ensamblado separado (`S7CommPlusDriver.dll`). Modificaciones en `PLCMONITOR-NOTICE.md` |
| `src/PlcMonitor.Cli` | CLI `plcmon` (Spectre.Console). Órdenes directas al PLC y `plcmon agent ...` como cliente gRPC del agente (`AgentCommands.cs`) |
| `src/PlcMonitor.Engine` | Motor multi-dispositivo: `TagEngine` (caché, suscripciones, activar/desactivar, escritura con doble llave y auditoría), `DeviceSupervisor` (conexión, reconexión con backoff, grupos de escaneo), configuración `agent.json` |
| `src/PlcMonitor.Agent.Contracts` | `agent.proto` con cliente y servidor gRPC generados. Lo usarán la app y otros clientes sin depender de ASP.NET |
| `src/PlcMonitor.Historian` | Historiador (Microsoft.Data.Sqlite): `HistorySampler` (muestreo por tag), `SqliteHistorianStore` (un fichero por mes, retención), `HistoryAggregator`, `HistorianService` (suscrito al motor, reloj de 100 ms, volcado por lotes). Lo arranca el agente |
| `src/PlcMonitor.Deploy` | Instalación remota del agente por SSH (SSH.NET): `SshNetSession` (clave o contraseña, huella SSH fijada), `AgentDeployer` (detecta sistema y RID, sube el paquete, instala/actualiza/desinstala, estado y log), `AgentScripts` (bash y PowerShell generados y probados) |
| `src/PlcMonitor.App` | App de escritorio `plcmon-app` (Avalonia 12, MVVM con CommunityToolkit.Mvvm). Cliente gRPC del agente: `MainViewModel` (conexión, suscripción con resuscripción, índice de tags), `DeviceViewModel`, `TagViewModel`, `BrowseViewModel`, `DeviceEditorViewModel`. Servicios sustituibles en tests: `IAgentConnector`, `IUiDispatcher`, `IDialogs`. Ver el [manual de la app](app.md) |
| `src/PlcMonitor.Agent` | Servicio `plcmon-agent` (ASP.NET Core, HTTP/2, Windows Service/systemd). Ver el [manual del agente](agente.md) |
| `tests/*` | xUnit v3 sobre Microsoft.Testing.Platform |

## 2. Contrato de drivers

Para añadir un protocolo (p. ej. Modbus TCP):

1. Crear `src/PlcMonitor.Drivers.<Protocolo>` con referencia a `PlcMonitor.Abstractions`.
2. Definir la clase de ajustes (POCO con valores por defecto). Se serializa en JSON (`DriverJson.Options`: camelCase y enums como texto).
3. Heredar de `DriverFactoryBase<TSettings>`:
   - `TypeId`: identificador estable usado en la configuración, por ejemplo `"modbus-tcp"`.
   - `Validate(settings)`: devuelve la lista de errores.
   - `CreateConnection(...)`: crea la conexión.
4. Heredar la conexión de `DeviceConnectionBase` e implementar:
   - `OpenCoreAsync` / `CloseCoreAsync`.
   - `ReadCoreAsync`: devuelve un `TagValue` por dirección, en el mismo orden. Los errores **por dirección** van como calidad mala, no como excepción.
   - `WriteCoreAsync`.
   - `ValidateAddress`: sintaxis, sin acceder al dispositivo.
   - `IsConnectionLost(ex)`: qué excepciones significan que hay que reconectar.
   - Opcional: `IBrowsableConnection` si el protocolo permite descubrir variables.
5. Registrar la factoría en `DriverRegistry`. De momento en `CliRuntime`; en la fase 2, en el agente.

`DeviceConnectionBase` ya resuelve:
- Estado (`Disconnected → Connecting → Connected / Faulted`) y el evento `StateChanged`.
- Serialización de todas las operaciones con un semáforo: un único acceso al dispositivo a la vez.
- Lectura o escritura sin conexión: devuelve `BadNotConnected` sin tocar el dispositivo.
- Excepción de comunicación: cierra, pasa a `Faulted` y marca todos los ítems `BadNotConnected`.
- Excepción inesperada: devuelve calidad `Bad` sin tirar la conexión.

La **reconexión automática** no es tarea del driver: la gestionará el agente (fase 2) con *backoff*.

## 3. Driver S7 simbólico (`s7-symbolic`)

- Librería: `S7CommPlusClient` del fork de DotNetProjects.S7CommPlusDriver, envuelta en `ISymbolicClient` (`S7CommPlusSymbolicClient`) para poder sustituirla en los tests.
- Protocolo: S7CommPlus sobre ISO-on-TCP (puerto 102). Ajuste `security`:

| `security` | Qué hace | Equivale en Ignition a |
|---|---|---|
| `auto` (por defecto) | Intenta TLS; si la CPU lo rechaza en `InitSsl`, abre otra conexión con la autenticación clásica | *Preferred* |
| `tls` | Solo TLS 1.3 (TLS gestionado con BouncyCastle, sin OpenSSL nativo). S7-1200 FW ≥ 4.5 / S7-1500 FW ≥ 2.9 con comunicación PG/HMI segura (TIA ≥ V17) | *Required* |
| `legacy` | Solo autenticación clásica por *challenge* (sin TLS): proyectos TIA ≤ V16 o CPU con la comunicación segura desactivada | *Disabled* |

- **Autenticación clásica** (HarpoS7, MIT): la CPU envía en `CreateObject` un *challenge* de 20 bytes y la huella de su clave pública (familia `00` = 1500, `01` = 1200, `03` = PLCSIM). Con la clave pública conocida se calcula la clave de sesión y cada PDU va firmada (*digest* de 32 bytes). La clave de sesión se renueva cada 25 minutos.
- Después: legitimación con contraseña (si se indica). Con usuario, la legitimación nueva (FW 3.1+).
- `AutoReconnect = false` en la librería: la reconexión es del `DeviceSupervisor`. `WriteEnabled = true`: la doble llave de escritura la aplica el motor.
- **Resolución de símbolos bajo demanda** (`GetTagBySymbolAsync`): no hace falta explorar toda la CPU para leer. Cada símbolo resuelto se guarda en caché por sesión, también si no existe (caché negativa). Las escrituras usan una caché aparte con instancias propias (no alteran el valor leído y conservan la longitud declarada de los STRING). Las cachés se vacían al reconectar.
- `BrowseAsync`: exploración completa, guardada en caché en memoria (nombre + tipo). Admite filtro, límite y `Refresh`.
- **Tipos:** se deducen de la clase de tag que crea la propia librería (`PlcTags.TagFactory`), así cualquier tipo que soporte (BBOOL, BLOCK_FB, HW_*...) queda cubierto. Conversiones:
  - TIME, TOD, LTIME, LTOD y S5TIME → `TimeSpan`.
  - DTL, DT, DATE y LDT → `DateTime`.
  - CHAR y WCHAR → `string`.
  - Arrays → array .NET.
- Si la CPU rechaza una escritura → `BadAccessDenied`. La misma dirección dos veces en una escritura → `BadConfiguration`.
- Errores: las excepciones de la librería se convierten en `S7SymbolicException` (código de `S7Consts` + detalle de la librería). `IsCommunicationError` decide si hay que reconectar: errores TCP/ISO, timeout, TLS, firma de integridad incorrecta o los que la librería marca como transitorios.
- `GetDeviceInfo` incluye `Security`: la seguridad negociada (`TLS` o `legacy (sin TLS)`).

### Cambios en el fork (rama `plcmonitor`)

| Problema | Causa | Solución |
|---|---|---|
| Una contraseña incorrecta se daba por válida | El `ReturnValue` de la legitimación (UInt64) se truncaba a `Int16`. El 1517F devolvió `0x2605990006990016` → `0x0016` > 0 | Cualquier `ReturnValue != 0` → `errCliAccessDenied` |
| En un S7-1200, cualquier símbolo cuyo primer nivel no fuera un DB tiraba la sesión ("Could not get type info") | Tras los DB se busca en M, Q, I, temporizadores y contadores S7; el 1200 no tiene estos dos últimos y la petición de su tipo fallaba | Un área que la CPU no tiene cuenta como «no encontrado» y se recuerda durante la sesión |
| 24 MB de DLL de OpenSSL en la salida | El paquete las incluye para el backend OpenSSL | `S7CommPlusIncludeOpenSslRuntimes=false` (en `external/Directory.Build.props`) |
| El driver usa `PlcTags.TagFactory` (interno) | — | `InternalsVisibleTo` para el driver y sus tests |

Cuando aparezca un fallo en la librería se corrige en el fork; no se abren incidencias ni PR en el proyecto original.

### Hallazgos de la versión 1.0 (port anterior de thomas-v2)

- Con OpenSSL ≥ 3.5 el handshake TLS no terminaba con el S7-1517F por el grupo post-cuántico X25519MLKEM768. Con el TLS gestionado (BouncyCastle) ya no aplica.
- La CPU S7-1200 de pruebas responde a `InitSsl` con `ReturnValue=0xA201D60001E6FDF9` (bit de error): no tiene comunicación segura. Con `security: auto` se conecta sin TLS.

## 4. Driver S7 absoluto (`s7-absolute`)

- `S7Address`: parser propio (regex generada) compatible con la sintaxis de Ignition y la de STEP 7. Ver la tabla en el [manual de uso](usuario.md#absoluto-s7-absolute). `ToString()` da la forma canónica.
- `S7Codec`: big-endian, STRING S7 (cabecera de longitud máxima y actual, Latin-1), DATE_AND_TIME en BCD (años 1990–2089), arrays.
- `ReadPlanner`: agrupa las direcciones del mismo área/DB cuando el hueco entre ellas es ≤ `ReadGapBytes` (32 por defecto) y el bloque no pasa de 4096 bytes. Así se hacen menos peticiones. S7.Net trocea según el PDU.
- Transporte: `IS7Transport` (S7.Net Plus) para poder sustituirlo en los tests. Los bits se escriben con `WriteBit`, sin leer-modificar-escribir.
- Si falla un bloque (DB inexistente, por ejemplo), solo esas direcciones pasan a `BadAddressInvalid`.

## 5. Compilar y probar

```bash
git clone --recurse-submodules git@github.com:nsuarez75/PLC-Monitor.git   # o: git submodule update --init
dotnet build PlcMonitor.slnx
dotnet test --solution PlcMonitor.slnx          # usa Microsoft.Testing.Platform (global.json)
dotnet run --project src/PlcMonitor.Cli -- info -H 192.168.2.1
dotnet run --project src/PlcMonitor.Agent -- --Agent:ConfigPath=/ruta/agent.json
scripts/publish-release.sh linux-x64    # paquete con la CLI y el agente (en agent/)
```

### Tests

| Proyecto | Qué cubre |
|---|---|
| `PlcMonitor.Core.Tests` | Registro de drivers, factorías, sondeo con reloj simulado, parser y formateador de valores |
| `PlcMonitor.Drivers.S7Absolute.Tests` | Parser de direcciones, codec, planificador y conexión contra un PLC simulado en memoria |
| `PlcMonitor.Drivers.S7Symbolic.Tests` | Conversión de tipos sobre las clases reales de la librería y conexión contra una CPU simulada |
| `PlcMonitor.Engine.Tests` | Validación y carga de la configuración, backoff, motor con driver simulado (que hereda de `DeviceConnectionBase`) y reloj controlado: reconexión, activar/desactivar, 4 PLC independientes, doble llave de escritura, suscripciones y suscriptor lento |
| `PlcMonitor.Agent.Tests` | API gRPC contra el agente en memoria (`WebApplicationFactory`) con driver simulado; conversión a mensajes protobuf |
| `PlcMonitor.App.Tests` | ViewModels contra el agente en memoria (conexión, valores en vivo, escritura con confirmación, activar, explorar y añadir, editor con validación y renombrado, bajas) y la ventana real con Avalonia Headless + Skia (capturas PNG con `PLCMON_UI_CAPTURE_DIR`; contra un agente real con `PLCMON_IT_AGENT_URL`) |
| `PlcMonitor.Historian.Tests` | Muestreador (frecuencia mínima, bandas muertas, tiempo máximo, calidad), almacén (meses, tipos, retención), agregados y el servicio sobre el motor con reloj simulado |
| `PlcMonitor.Deploy.Tests` | Despliegue con SSH simulado (detección, sudo/root, credenciales nuevas o conservadas, Windows, errores, `bash -n` de los scripts) y uno **real** contra un equipo Linux con systemd (`PLCMON_IT_SSH_HOST`, `_PORT`, `_USER`, `_PASSWORD`, `PLCMON_IT_AGENT_PACKAGES`) |
| `PlcMonitor.Cli.Tests` | Órdenes `plcmon agent ...` con la configuración real de la CLI (`CliApp`), contra el agente en memoria y una consola de prueba (Spectre.Console.Testing) |
| `PlcMonitor.IntegrationTests` | **PLC real, solo lectura.** Se omiten si no hay variables de entorno: `PLCMON_IT_SYMBOLIC_HOST`, `PLCMON_IT_SECURITY`, `PLCMON_IT_EXPECTED_SECURITY`, `PLCMON_IT_PASSWORD`, `PLCMON_IT_SYMBOL`, `PLCMON_IT_ABSOLUTE_HOST`, `PLCMON_IT_ABSOLUTE_ADDR` |

Estado a 2026-10-15 (rama `feature/s7plus-legacy`): 312 tests correctos y 6 omitidos sin PLC. Contra el S7-1200 real (10.225.188.120, sin TLS y sin PUT/GET): conexión `auto` → `legacy`, exploración (1463 símbolos), lectura y sondeo; integración 4 correctos y 2 omitidos (contraseña y absoluto). Pendiente repetir contra el S7-1517F (TLS + contraseña).

Estado a 2026-10-14 (rama `feature/fase2-agente`): 310 tests correctos y 6 omitidos (integración sin PLC). Un S7-1200 real (FW con PUT/GET deshabilitado) confirmó la conexión y la lectura de estado del driver absoluto, y el agente completo de punta a punta. Fase 1: 228 tests unitarios correctos. Integración contra el S7-1517F: 5 correctos y 1 omitido. El driver absoluto no se prueba contra ese PLC porque tiene PUT/GET deshabilitado; falta un PLC con PUT/GET para validarlo en real.

### Probar el despliegue por SSH

Un contenedor Ubuntu con systemd y SSH hace de equipo remoto:

```bash
cat > Dockerfile <<'EOF2'
FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends systemd systemd-sysv openssh-server sudo ca-certificates \
 && useradd -m -s /bin/bash tecnico && echo 'tecnico:clave-de-prueba' | chpasswd && usermod -aG sudo tecnico && systemctl enable ssh
STOPSIGNAL SIGRTMIN+3
CMD ["/sbin/init"]
EOF2
docker build -t plcmon-sshbox . && docker run -d --name plcmon-sshbox --privileged --cgroupns=host \
  -v /sys/fs/cgroup:/sys/fs/cgroup:rw -p 2222:22 -p 5311:5311 plcmon-sshbox
scripts/publish-release.sh linux-x64        # genera artifacts/release/<versión>/agents/
PLCMON_IT_SSH_HOST=127.0.0.1 PLCMON_IT_SSH_PORT=2222 PLCMON_IT_SSH_USER=tecnico PLCMON_IT_SSH_PASSWORD=clave-de-prueba \
PLCMON_IT_AGENT_PACKAGES=$PWD/artifacts/release/1.0.0/agents dotnet test --solution PlcMonitor.slnx
```

La imagen mínima no trae D-Bus: un usuario normal no puede usar `systemctl` y el estado se obtiene con `pidof`.
**Windows no se ha probado en un equipo real**: sus scripts están cubiertos por tests con SSH simulado.

### Convenciones
- .NET 10, `Nullable` activo, `TreatWarningsAsErrors`, `AnalysisLevel latest-recommended`. El código de terceros (`external/`) no hereda estos ajustes (`external/Directory.Build.props` y `Directory.Packages.props` propios).
- Paquetes con versión central en `Directory.Packages.props`.
- Git: una rama por trabajo (`feature/...`) y commits pequeños. Los manuales ES/EN se actualizan en la misma rama que la funcionalidad.

## 6. Licencias

| Componente | Licencia | Uso |
|---|---|---|
| S7CommPlusDriver (fork en `external/S7CommPlusDriver`) | LGPL-3.0 o posterior | Ensamblado separado y reemplazable. Modificaciones publicadas (`PLCMONITOR-NOTICE.md`) |
| HarpoS7 / HarpoS7.PublicKeys | MIT | Autenticación clásica sin TLS |
| BouncyCastle.Cryptography | MIT | TLS 1.3 gestionado |
| zlib.net-mutliplatform | BSD-3-Clause | Descompresión de bloques |
| S7.Net Plus | MIT | Paquete NuGet |
| Spectre.Console | MIT | CLI |
| Avalonia, SkiaSharp, HarfBuzzSharp | MIT | App de escritorio |
| CommunityToolkit.Mvvm | MIT | App de escritorio |
| Fuente Inter | SIL OFL 1.1 | App de escritorio (Avalonia.Fonts.Inter) |
| Iconos Lucide (trazados) | ISC | App de escritorio (`Controls/Icon.cs`) |
| SSH.NET | MIT | Despliegue remoto del agente |
| Microsoft.Data.Sqlite (SQLitePCLRaw, SQLite) | MIT / dominio público | Historiador |
| System.Security.Cryptography.ProtectedData | MIT | Token de los agentes protegido con DPAPI en Windows |

**No se usa** la librería AGLink ni su clave de activación incluidas en el módulo de Ignition: son de Inductive Automation y DELTA LOGIC.
