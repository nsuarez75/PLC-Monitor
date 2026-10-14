# Ignition 8.3 — Siemens Enhanced Driver: cómo funciona

Análisis realizado el 2026-10-14 sobre:

- Ignition **8.3.8** (imagen `inductiveautomation/ignition:latest`, contenedor `ign83-s7re`, puerto 8089).
- Módulo **Siemens Enhanced Driver 1.3.8** (`com.inductiveautomation.opcua.drivers.siemens-symbolic`, build 2026071409).
- PLC de pruebas: **S7-1517F** en `192.168.2.1`, con contraseña de acceso, modo simbólico y conexión segura *Preferred*.

Material de trabajo en `research/`:
- `module/`: módulo `.modl` y jars extraídos.
- `decompiled/`: Java descompilado con Vineflower 1.11.1.
- `captures/`: pcap de la sesión y caché de símbolos `.agl`.

---

## 1. Arquitectura

```
Tag Ignition ──► OPC UA (Ignition OPC UA Server, ns=1;s=[Device]Ruta)
                    │
                    ▼
     driver-siemens-symbolic-1.3.8.jar   (código de Inductive Automation)
       SiemensDevice / SymbolicAddressSpace / AbsoluteAddressSpace
       SiemensS7Client  (conexión, reconexión, password, TLS fallback)
                    │  JNI
                    ▼
     aglink4-6.2.0.0.jar  (de.deltalogic.accon.aglink4 — wrapper Java)
                    │
                    ▼
     AGLink40_x64.dll / libAGLink40_x64.so / libAGLink40_arm64.so
     (DELTA LOGIC AGLink 4 — implementa S7comm, S7CommPlus y TLS)
     + libssl/libcrypto 3 (OpenSSL)
                    │  ISO-on-TCP, puerto 102
                    ▼
                 S7-1200 / S7-1500
```

- **El protocolo no lo implementa Inductive Automation.** Todo el S7CommPlus (handshake, carga de símbolos, lectura y escritura, TLS) está dentro de la librería nativa comercial **AGLink 4** de DELTA LOGIC. El código Java de IA solo mapea el modelo de AGLink a OPC UA.
- Al arrancar, `SiemensModuleHook.setup()` copia las librerías nativas de la plataforma a `lib/core/gateway` y llama a `AgLink.activate("<clave>")`. **La clave de licencia de AGLink va embebida y pertenece a Inductive Automation.** No se debe reutilizar fuera de Ignition.
- Cada dispositivo ocupa un "device number" de AGLink (`AgLink.getMaxDevices()` limita cuántos puede haber a la vez).

## 2. Configuración del dispositivo

Recurso: `data/config/resources/core/com.inductiveautomation.opcua/device/<nombre>/config.json`

```json
{
  "profile":  { "type": "com.inductiveautomation.ignition.siemens" },
  "settings": {
    "connectivity":   { "hostname": "192.168.2.1", "port": 102, "timeout": 30000 },
    "deviceSettings": { "deviceType": "S7_1500", "addressType": "SYMBOLIC", "rack": 0, "slot": 1 },
    "advanced":       { "optimizeWrites": false, "secureConnectionMode": "PREFERRED",
                        "password": { "type": "Embedded", "data": { /* JWE cifrado por el gateway */ } } }
  }
}
```

Reglas de validación (`SiemensDeviceConfig`):
- `deviceType` puede ser S7_300, S7_400, S7_1200 o S7_1500. Con 300/400 el modo **tiene** que ser ABSOLUTE.
- `secureConnectionMode` puede ser REQUIRED, PREFERRED o DISABLED. REQUIRED no se puede combinar con ABSOLUTE.
- Rack admite 0–7 y slot 0–31. Timeout por defecto: 30 s.
- La versión de configuración antigua (`forceSecureConnection: bool`) se migra a `secureConnectionMode`.

## 3. Conexión (`SiemensS7Client`)

### Modo simbólico (S7-1200/1500)
Parámetros AGLink (`Agl_S7_TCPIP_TIA` + `AglS7_TcpIPTia_1`):
- `bConnTypeEx = CT_HMI`: el driver se presenta al PLC **como un HMI**.
- `bPLCClassEx = PLCEx_AUTO_TIA`: AGLink detecta la familia.
- `wPlcNr = 2`, `Flags = 4`. Si la conexión es segura, `GlobalFlags = SECURE_COMMUNICATION`.

Secuencia:
1. `connect()`. Si el modo no es DISABLED, primero intenta con TLS.
2. Si falla (PLC sin comunicación segura, faltan libs SSL, etc.) y el modo es PREFERRED, crea una conexión nueva **sin TLS** y reintenta. Con REQUIRED se queda en error.
3. Una vez conectado, `setPassword(password)` hace la legitimación de nivel de acceso. Si el PLC responde `WRONG_PASSWORD` o `PASSWORD_NOT_REQUIRED`, quita la contraseña.
4. Reconexión con *backoff* exponencial de 1 s a 32 s como máximo (estado `ReconnectWait`).

### Modo absoluto
- `AglS7_TcpIp_1` con clase `PLC_300_400` o `PLC_1200_1500`: **S7comm clásico (PUT/GET)**, nunca con TLS.
- En los 1200/1500 requiere **PUT/GET habilitado** y que no haya protección por contraseña. Si no, AGLink devuelve `AGL40_WRONG_KONTEXT`.

## 4. Lo que se ve en el cable (captura real contra el 1517F)

Fichero `research/captures/s7_connect.pcap`, una única conexión TCP `172.17.0.2:33034 → 192.168.2.1:102`.

| Fase | Contenido |
|---|---|
| 1. ISO-on-TCP | TPKT + COTP **CR/CC** (frames 4/6) |
| 2. InitSSL **en claro** | S7CommPlus `72 01 00 12 31 0000 05B3 0000 0001 …`: opcode `0x31` = request, función `0x05B3` = **InitSsl**, seq 1. Respuesta: opcode `0x32` |
| 3. TLS dentro de COTP DT | ClientHello **TLS 1.3**, suites `TLS_AES_128_GCM_SHA256` / `TLS_AES_256_GCM_SHA384`, grupo **X25519**, **sin SNI**. El servidor elige `TLS_AES_128_GCM_SHA256` |
| 4. S7CommPlus sobre TLS | Todo lo demás va cifrado (`17 03 03 …` = application data). Ahí van CreateObject/sesión, legitimación con contraseña, exploración, etc. |

Volumen observado:

| Intervalo | PLC → Ignition | Ignition → PLC | Qué es |
|---|---|---|---|
| 0–50 s | **~72 MB** (≈67 k tramas) | ~200 kB | **Carga completa de símbolos** del proyecto del 1517F |
| 50–205 s | 0 | 0 | Sin tags: **ni keep-alive** |
| 205 s en adelante | 132 B/s | 173 B/s | 1 tag, **sondeo cada 1 s** (1 petición + 1 respuesta) |

Conclusiones:
- **La carga de símbolos es masiva y bloqueante.** En un proyecto grande (1517F) tarda unos 50 s y transfiere unos 72 MB. Nada se puede leer hasta que termina.
- **Lectura por sondeo (polling)**, no por suscripciones S7CommPlus. Cada ciclo del *tag group* hace un `symbolic_ReadMixEx` con todas las variables de ese ciclo.
- Con TLS activo **no se pueden ver las PDUs de S7CommPlus** sin extraer las claves de sesión (ver §8).

## 5. Símbolos

- `SymbolFsm`: estados NOT_LOADED → LOADING → LOADED, y además LOADING_WAIT (reintento cada 5 s), LOADING_NOT_SUPPORTED y SHUTDOWN. La carga es perezosa: empieza con el primer `browse`/`read`.
- `symbolic_LoadAGLinkSymbolsFromPLC()` descarga todo el modelo de tipos y variables a memoria nativa. Después `symbolic_SaveAGLinkSymbolsToFile()` lo guarda en caché en `temp/siemens-symbols-<device>-<hash>/plc-symbols-cached.agl`, con escritura atómica (staging seguido de move).
  - En este PLC el fichero ocupa **33 MB**. Formato binario propietario de AGLink, cabecera `AGLINK\0\0`.
  - El módulo monta dos rutas: `GET …/<device>/exportPLCSymbols` (permiso READ) para descargar el fichero y `POST …/<device>/invalidateBrowseCache` (permiso WRITE) para forzar la recarga. El prefijo exacto de la URL (montaje del módulo bajo `/data/…`) está sin verificar.
- Árbol OPC UA: raíz `PLC` y debajo `Blocks`, áreas, etc. Cada nodo se identifica por la **ruta escapada** de AGLink (`symbolic_GetEscapedPath`). Ejemplo real:
  `ns=1;s=[PLC2]PLC.Blocks.++2032+CNV02_IDB.Sequence.ilockAuto`
- La exploración usa `symbolic_Expand`, `GetChildCount`, `GetChild`, `GetName`, `GetComment`, `GetHierarchyType` y `GetSegmentType`.
- Permisos: antes de leer o escribir comprueba `symbolic_GetPermissionType`, que puede ser READ, READ_WRITE o ninguno.

## 6. Lectura y escritura simbólica

Por cada ítem:
1. `nodeHandle = ctx.getHandle(rutaEscapada)`.
2. `accessHandle = symbolic_CreateAccess(nodeHandle)` y `bufferSize = symbolic_GetAccessBufferSize(access)`.
3. Se agrupan en `List<AglSymbolicRW>`, se llama a `symbolic_ReadMixEx(lista, err, timeout)` y se interpreta el resultado:
   - `AGL40_SUCCESS`: todo correcto.
   - `AGL40_SYMBOLIC_PARTIAL_ERRORS`: se revisa cada ítem.
   - Cualquier otro valor: excepción.
4. Se decodifica con `symbolic_GetAccessBuffer<Tipo>(access, buffer, len, offset)` y se liberan los handles.

La escritura es simétrica (`symbolic_SetAccessBuffer*` y `symbolic_WriteMixEx`). `optimizeWrites` agrupa escrituras.

Mapeo de tipos (`getDataTypeId`):

| AGLink ValueType / SystemType | OPC UA |
|---|---|
| UINT8 + S7_Bool | Boolean |
| UINT8 | Byte |
| UINT16 / UINT32 / UINT64 | UInt16 / UInt32 / UInt64 |
| UINT64 + S7_LDT | DateTime (ns desde 1970) |
| INT8,CHAR8 / INT16,CHAR16 / INT32,CHAR32 / INT64 | SByte / Int16 / Int32 / Int64 |
| FLOAT32 / FLOAT64 | Float / Double |
| STRING8 / STRING16 (WString) | String |
| S7_S5Time | UInt32 (ms) |
| S7_Date_And_Time, S7_DTL | DateTime |
| S7_Counter | UInt16 |
| S7_Array | Array del tipo del elemento |
| otros (Struct/UDT) | BaseDataType (objeto navegable, sin valor) |

## 7. Direccionamiento absoluto (S7comm)

Sintaxis moderna (`ModernSyntaxParser`):
```
(I|E|A|Q|M|T|C|Z|DB<n>)[.][DB](X|B|C|W|I|D|L|DI|REAL|DT|LI|LREAL|STRING)<offset>[.bit][[len]][[dim,dim]]
```
Ejemplos: `DB1.DBX0.3`, `DB1.DBW10`, `DB1.REAL20`, `DB5.STRING30[20]`, `M10.1`, `DB1.REAL0[10]`, `DB1.I0[2,3]`.
La sintaxis heredada (`LegacySyntaxParser`) acepta formas como `DB1,REAL20`.
- `T` siempre se lee como DWord y `C`/`Z` como Word.
- No se admiten arrays de Bit, DT ni String.

## 8. Limitaciones y puntos abiertos

- **Ver las PDUs S7CommPlus dentro de TLS**: AGLink carga OpenSSL de forma dinámica, así que se podría inyectar un *keylog callback* (`LD_PRELOAD` sobre `SSL_new`/`SSL_CTX_new`) para generar un `SSLKEYLOGFILE` y descifrar la captura en Wireshark. No está hecho.
- Otra opción: repetir la captura con `secureConnectionMode = DISABLED`, si el PLC lo admite. Eso mostraría S7CommPlus "legacy" con comprobación de integridad propietaria, que no es lo mismo que la sesión TLS.
- La descarga completa de símbolos (unos 72 MB y 50 s) se repite en cada arranque o reinicio del dispositivo. La caché `.agl` solo sirve para exportar, no se recarga al arrancar.

## 9. Implicaciones para una herramienta propia

| Opción | Pros | Contras |
|---|---|---|
| **A. Usar Ignition como pasarela** (OPC UA del gateway, o REST/WebDev) | Sin licencias nuevas, sin tocar el protocolo | Necesita un gateway Ignition con el módulo licenciado |
| **B. Licenciar AGLink 4** (DELTA LOGIC tiene versión .NET) | La misma pila que Ignition, con soporte | Coste de licencia |
| **C. Implementación libre de S7CommPlus con TLS** (p. ej. el proyecto open-source *S7CommPlusDriver* en C#, pendiente de verificar su soporte de TLS y contraseña en un 1517F) | .NET nativo, sin dependencias comerciales | Protocolo no documentado oficialmente: hay riesgo de compatibilidad entre firmwares |
| **D. OPC UA nativo del S7-1500** | Estándar, sin ingeniería inversa | Requiere habilitarlo en el PLC (licencia de runtime OPC UA y configuración en TIA) |

### Prueba de viabilidad de la opción C (2026-10-14)

`research/spike`: .NET 8 x64 que compila el código de *S7CommPlusDriver* (LGPL-3, commit `dbd61e4`). Solo lectura contra el 1517F:

| Paso | Resultado | Comparación con Ignition/AGLink |
|---|---|---|
| Connect (InitSsl + TLS 1.3 + CreateObject + legitimación) | OK, 0,44 s | — |
| Lista de DBs | 749 DBs, 0,35 s | — |
| Browse completo | **2 205 676 variables en 8,2 s** | Unos 50 s y 72 MB |
| Lectura de 15 variables (Int, Bool) | ~30 ms por lectura | — |

Notas:
- Hay que definir `_WIN64` para que cargue `libcrypto-3-x64.dll` y `libssl-3-x64.dll`.
- Los nombres coinciden con los de Ignition (`++2032+CNV02_IDB.Sequence.…`).
- La dirección de acceso tiene el formato `8A0E0415.9.A`: `8A0E` + número de DB, seguido de los LIDs.
- La librería guarda las claves TLS en `key_*.log`, que con `editcap --inject-secrets` permite descifrar la captura.

**No es una opción** reutilizar la librería AGLink del módulo de Ignition junto con su clave de activación fuera del gateway, porque infringe las licencias de DELTA LOGIC e Inductive Automation.
