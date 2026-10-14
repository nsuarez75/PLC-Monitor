# PLC-Monitor — Manual técnico

> Versión 1.0.0 · Última actualización: 2026-10-14 · [English](../en/technical.md)

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
   S7CommPlus (LGPL-3)       S7.Net Plus (MIT)
        │
   OpenSSL 3 (nativo)
```

### Proyectos

| Proyecto | Responsabilidad |
|---|---|
| `src/PlcMonitor.Abstractions` | Contrato común de drivers: `IDriverFactory`, `DriverFactoryBase<TSettings>`, `IDeviceConnection`, `DeviceConnectionBase`, `IBrowsableConnection`, `TagValue`, `QualityCode`, `TagDataType` |
| `src/PlcMonitor.Core` | Lógica independiente del protocolo: `DriverRegistry`, `TagPoller` (sondeo con detección de cambios), `ValueParser` (texto → tipo), `ValueFormatter` |
| `src/PlcMonitor.Drivers.S7Symbolic` | Driver `s7-symbolic` sobre S7CommPlus |
| `src/PlcMonitor.Drivers.S7Absolute` | Driver `s7-absolute` sobre S7.Net Plus |
| `src/S7CommPlus` | Port a .NET 10 de [S7CommPlusDriver](https://github.com/thomas-v2/S7CommPlusDriver), **LGPL-3**. Ensamblado separado. Modificaciones en `NOTICE.md` |
| `src/PlcMonitor.Cli` | CLI `plcmon` (Spectre.Console) |
| `src/PlcMonitor.Engine` | Motor multi-dispositivo: `TagEngine` (caché, suscripciones, activar/desactivar, escritura con doble llave y auditoría), `DeviceSupervisor` (conexión, reconexión con backoff, grupos de escaneo), configuración `agent.json` |
| `src/PlcMonitor.Agent.Contracts` | `agent.proto` con cliente y servidor gRPC generados. Lo usarán la app y otros clientes sin depender de ASP.NET |
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

- Protocolo: S7CommPlus sobre ISO-on-TCP (puerto 102).
  1. `InitSsl` en claro.
  2. **TLS 1.3**, con suites `TLS_AES_256_GCM_SHA384` / `TLS_AES_128_GCM_SHA256` y grupos `X25519:P-256:P-384`.
  3. Sesión y legitimación con contraseña.
- **Resolución de símbolos bajo demanda** (`getPlcTagBySymbol`): no hace falta explorar toda la CPU para leer. Cada símbolo resuelto se guarda en caché por sesión, también si no existe (caché negativa). La caché se vacía al reconectar.
- `BrowseAsync`: exploración completa (`Browse`), guardada en caché en memoria (nombre + tipo). Admite filtro, límite y `Refresh`.
- **Tipos:** se deducen de la clase de tag que crea la propia librería (`PlcTags.TagFactory`), así cualquier tipo que soporte (BBOOL, BLOCK_FB, HW_*...) queda cubierto. Conversiones:
  - TIME, TOD, LTIME, LTOD y S5TIME → `TimeSpan`.
  - DTL, DT, DATE y LDT → `DateTime`.
  - CHAR y WCHAR → `string`.
  - Arrays → array .NET.
- Escrituras: se crea una instancia nueva del tag para no alterar el de lectura. Si la CPU rechaza la escritura → `BadAccessDenied`.
- Errores de la librería: `S7CommPlusException` con el código de `S7Consts`. `IsCommunicationError` decide si hay que reconectar (TCP, ISO, timeout, TLS).

### Hallazgos importantes (corregidos en el port)

| Problema | Causa | Solución |
|---|---|---|
| Con OpenSSL 3.5 la conexión da timeout | OpenSSL ≥ 3.5 ofrece por defecto el grupo post-cuántico **X25519MLKEM768**, el ClientHello crece y la CPU no responde | `S7Client.TlsGroups = "X25519:P-256:P-384"` (AGLink/Ignition ofrecen solo X25519) |
| Una contraseña incorrecta se daba por válida | El `ReturnValue` de la legitimación (UInt64) se truncaba a `Int16`. El 1517F devolvió `0x2605990006990016` → `0x0016` > 0 | Cualquier `ReturnValue != 0` → `errCliAccessDenied` |
| Las DLL de OpenSSL incluidas en el proyecto original son la 3.0.8 (sin soporte) | — | Se usa OpenSSL 3.5 LTS: `scripts/get-openssl-windows.sh`, resolución con `OpenSslLibraryResolver` |

### Carga de OpenSSL (`OpenSslLibraryResolver`)
Orden de búsqueda:
1. Variable de entorno `PLCMONITOR_OPENSSL_DIR`.
2. Directorio de la aplicación.
3. `runtimes/<rid>/native`.
4. Búsqueda del sistema.

Nombres de fichero:
- Windows: `libcrypto-3-x64.dll`, `libssl-3-x64.dll`.
- Linux: `libcrypto.so.3`, `libssl.so.3`.
- macOS: `.3.dylib`.

## 4. Driver S7 absoluto (`s7-absolute`)

- `S7Address`: parser propio (regex generada) compatible con la sintaxis de Ignition y la de STEP 7. Ver la tabla en el [manual de uso](usuario.md#absoluto-s7-absolute). `ToString()` da la forma canónica.
- `S7Codec`: big-endian, STRING S7 (cabecera de longitud máxima y actual, Latin-1), DATE_AND_TIME en BCD (años 1990–2089), arrays.
- `ReadPlanner`: agrupa las direcciones del mismo área/DB cuando el hueco entre ellas es ≤ `ReadGapBytes` (32 por defecto) y el bloque no pasa de 4096 bytes. Así se hacen menos peticiones. S7.Net trocea según el PDU.
- Transporte: `IS7Transport` (S7.Net Plus) para poder sustituirlo en los tests. Los bits se escriben con `WriteBit`, sin leer-modificar-escribir.
- Si falla un bloque (DB inexistente, por ejemplo), solo esas direcciones pasan a `BadAddressInvalid`.

## 5. Compilar y probar

```bash
# Windows: copiar OpenSSL 3.5 (desde Git for Windows o la ruta indicada) a native/win-x64
scripts/get-openssl-windows.sh

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
| `PlcMonitor.IntegrationTests` | **PLC real, solo lectura.** Se omiten si no hay variables de entorno: `PLCMON_IT_SYMBOLIC_HOST`, `PLCMON_IT_PASSWORD`, `PLCMON_IT_SYMBOL`, `PLCMON_IT_ABSOLUTE_HOST`, `PLCMON_IT_ABSOLUTE_ADDR` |

Estado a 2026-10-14 (rama `feature/fase2-agente`): 310 tests correctos y 6 omitidos (integración sin PLC). Un S7-1200 real (FW con PUT/GET deshabilitado) confirmó la conexión y la lectura de estado del driver absoluto, y el agente completo de punta a punta. Fase 1: 228 tests unitarios correctos. Integración contra el S7-1517F: 5 correctos y 1 omitido. El driver absoluto no se prueba contra ese PLC porque tiene PUT/GET deshabilitado; falta un PLC con PUT/GET para validarlo en real.

### Convenciones
- .NET 10, `Nullable` activo, `TreatWarningsAsErrors`, `AnalysisLevel latest-recommended`. El código heredado de `S7CommPlus` queda excluido del análisis.
- Paquetes con versión central en `Directory.Packages.props`.
- Git: una rama por trabajo (`feature/...`) y commits pequeños. Los manuales ES/EN se actualizan en la misma rama que la funcionalidad.

## 6. Licencias

| Componente | Licencia | Uso |
|---|---|---|
| S7CommPlusDriver (port en `src/S7CommPlus`) | LGPL-3.0 | Ensamblado separado y reemplazable. Las modificaciones se publican (`NOTICE.md`) |
| zlib.net (ComponentAce) | BSD | Dentro de `S7CommPlus` |
| S7.Net Plus | MIT | Paquete NuGet |
| Spectre.Console | MIT | CLI |
| OpenSSL 3.5 | Apache-2.0 | Librería nativa |

**No se usa** la librería AGLink ni su clave de activación incluidas en el módulo de Ignition: son de Inductive Automation y DELTA LOGIC.
