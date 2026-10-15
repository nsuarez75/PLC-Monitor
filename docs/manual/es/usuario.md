# PLC-Monitor — Manual de uso

> Versión 1.0.0 · Última actualización: 2026-10-15 · [English](../en/user-guide.md)

En esta versión PLC-Monitor se usa desde la línea de comandos con el programa **`plcmon`**. La aplicación de escritorio llegará en la fase 3.

## 1. Requisitos

| Elemento | Requisito |
|---|---|
| Sistema | Windows 10/11 x64 o Linux x64/arm64 |
| .NET | Runtime .NET 10 (o el ejecutable autocontenido cuando se distribuya) |
| Red | Acceso TCP al puerto **102** del PLC |

### ¿Qué driver uso?

| PLC | Driver | Requisitos en el PLC |
|---|---|---|
| S7-1200 (FW ≥ 4.3) / S7-1500, con o sin comunicación segura (TLS) | **`s7-symbolic`** (por defecto) | Ninguno: **no hace falta PUT/GET**. Si la CPU tiene nivel de protección, la contraseña |
| S7-300 / S7-400 | **`s7-absolute`** | — |
| S7-1200/1500 para leer DBs **no optimizados** por dirección | **`s7-absolute`** | En TIA: *Permitir acceso PUT/GET* y DB con *acceso optimizado* desactivado |

Con `s7-symbolic` la seguridad se elige con `--security`:

| `--security` | Uso | En Ignition |
|---|---|---|
| `auto` (por defecto) | Prueba TLS y, si la CPU no lo admite, conecta sin TLS (autenticación clásica). Sirve para cualquier CPU | *Preferred* |
| `tls` | Exige TLS: falla si la CPU no tiene comunicación segura (TIA ≥ V17, S7-1200 FW ≥ 4.5 / S7-1500 FW ≥ 2.9) | *Required* |
| `legacy` | Sin TLS directamente (proyectos TIA ≤ V16 o comunicación segura desactivada). Ahorra el intento TLS | *Disabled* |

`plcmon info` muestra en **Security** lo que se negoció (`TLS` o `legacy (sin TLS)`).

## 2. Opciones comunes

Todas las órdenes que hablan con un PLC aceptan:

| Opción | Descripción | Por defecto |
|---|---|---|
| `-H, --host` | IP del PLC | (obligatorio) |
| `-d, --driver` | `s7-symbolic` o `s7-absolute` | `s7-symbolic` |
| `-p, --password` | Contraseña de acceso de la CPU (simbólico) | variable `PLCMON_PASSWORD` |
| `-u, --username` | Usuario (CPUs con gestión de usuarios, FW 3.1+) | — |
| `--cpu` | `S7300`, `S7400`, `S71200`, `S71500` (absoluto) | `S71500` |
| `--rack`, `--slot` | Rack y slot (absoluto). S7-300: slot 2; S7-1500: slot 1 | 0 / 1 |
| `-t, --timeout` | Timeout en ms | 10000 |
| `-v, --verbose` | Log detallado (útil para diagnosticar) | — |
| `--security` | `auto`, `tls` o `legacy` (simbólico, ver arriba) | `auto` |

> **Consejo:** pasa la contraseña con la variable de entorno para que no quede en el historial:
> - PowerShell: `$env:PLCMON_PASSWORD = "..."`
> - Bash: `export PLCMON_PASSWORD=...`

## 3. Órdenes

### `plcmon drivers`
Lista los drivers disponibles y sus capacidades.

### `plcmon info`
Conecta y muestra el estado y la información del dispositivo. Sirve para comprobar la red, la seguridad negociada (TLS o sin TLS) y la contraseña.
```
plcmon info -H 192.168.2.1
```

### `plcmon browse [FILTRO]` (solo simbólico)
Busca variables cuyo nombre contenga el texto, sin distinguir mayúsculas.
```
plcmon browse "Motor.Speed" -H 192.168.2.1 -n 50
```
- La primera exploración descarga todos los símbolos de la CPU. Un S7-1517F con 2,2 millones de variables tarda unos 10 s.
- Muestra el **símbolo** (para usarlo en `read`/`watch`/`write`), el tipo S7 y el tipo normalizado.

### `plcmon read DIRECCION...`
Lee una o varias direcciones una vez.
```
plcmon read "\"MiDB\".Motor.Speed" "MiDB.Alarmas[3]" -H 192.168.2.1
plcmon read DB1.DBW10 DB1.REAL20 M10.1 -d s7-absolute -H 192.168.0.10 --cpu S7300 --slot 2
```
Devuelve el código de salida 0 si todas las lecturas tienen calidad buena y 1 si alguna falla.

### `plcmon watch DIRECCION... [-i MS]`
Lee periódicamente (por defecto cada 1000 ms). Primero muestra todos los valores y después solo los cambios. Se sale con **Ctrl+C**.
```
plcmon watch "MiDB.Contador" "MiDB.Estado" -H 192.168.2.1 -i 250
```

### `plcmon write DIRECCION VALOR [--type TIPO] [-y]`
Escribe un valor. **Pide confirmación** y enseña el valor actual y el nuevo antes de escribir.
```
plcmon write "MiDB.Consigna" 12.5 -H 192.168.2.1
plcmon write DB1.DBX0.3 true -d s7-absolute -H 192.168.0.10
```
- El tipo se averigua leyendo la variable. Con `--type` se fuerza (`Boolean`, `Int16`, `Float`, `String`, ...).
- Formatos de valor:
  - Decimales con **punto** (`12.5`).
  - Booleanos: `true/false`, `1/0`, `on/off`, `sí/no`.
  - Hexadecimal: `0xFF` o `16#FF`.
  - Tiempos: `1500ms` o `00:00:01.5`.
  - Fechas ISO: `2026-10-14T12:00:00Z`.
  - Bytes en hexadecimal: `01 0A FF`.
- ⚠️ **Se escribe en un PLC real.** Ten especial cuidado en CPUs de seguridad (F). Las variables de seguridad no se pueden escribir desde fuera.

## 4. Sintaxis de direcciones

### Simbólico (`s7-symbolic`)
Se aceptan:
- El nombre tal como lo devuelve `browse`: `++2032+CNV02_IDB.Sequence.watchdogTime`.
- El estilo TIA Portal con comillas: `"MiDB".Motor.Speed`.
- Arrays: `MiDB.Valores[3]`, `"MiDB".Matriz[1,2]`.
- Marcas, entradas y salidas por su nombre simbólico de la tabla de variables.

### Absoluto (`s7-absolute`)
Compatible con la sintaxis de Ignition y la clásica de STEP 7. No distingue mayúsculas.

| Ejemplo | Significado | Tipo |
|---|---|---|
| `DB1.DBX0.3` / `DB1.X0.3` | Bit 3 del byte 0 del DB1 | Bool |
| `DB1.DBB4` / `DB1.B4` | Byte | Byte |
| `DB1.DBW10` / `DB1.W10` | Word (sin signo) | UInt16 |
| `DB1.INT10` / `DB1.I10` | Int | Int16 |
| `DB1.DBD20` / `DB1.D20` | DWord | UInt32 |
| `DB1.DINT20` / `DB1.DI20` | DInt | Int32 |
| `DB1.REAL20` | Real | Float |
| `DB1.LREAL0`, `DB1.LINT0`, `DB1.L0` | LReal / LInt / LWord | Double / Int64 / UInt64 |
| `DB1.DT0` | Date_And_Time | DateTime |
| `DB1.C0` | Char | String (1 carácter) |
| `DB5.STRING30[20]` | STRING de 20 caracteres en el byte 30 (por defecto 254) | String |
| `DB1.REAL0[10]` | Array de 10 Real | Float[] |
| `DB1.B0[16]` | 16 bytes en bruto | ByteArray |
| `M10.1`, `MW20`, `MREAL100` | Marcas | — |
| `I0.0` / `E0.0`, `IW64` | Entradas | — |
| `Q4.0` / `A4.0`, `QW10` / `AW10` | Salidas | — |

## 5. Calidades

| Calidad | Significado |
|---|---|
| `Good` | Valor correcto |
| `BadNotConnected` | Sin conexión con el PLC |
| `BadAddressInvalid` | La dirección no existe o no es válida (símbolo inexistente, DB que no existe, ...) |
| `BadAccessDenied` | La CPU rechaza el acceso (protección, variable de solo lectura, ...) |
| `BadTypeMismatch` | El valor no se puede convertir al tipo de la variable |
| `Bad` | Otro error (el detalle aparece en el mensaje) |

## 6. Solución de problemas

| Síntoma | Causa probable y solución |
|---|---|
| `La CPU no acepta comunicación segura (TLS)` | Se usó `--security tls` con una CPU sin comunicación segura. Usa `auto` o `legacy` |
| `La CPU rechazó la autenticación clásica (sin TLS)` | Se usó `--security legacy` con una CPU que exige TLS, o su clave pública no está en el catálogo de HarpoS7. Usa `auto` o `tls` |
| `TCP : Error receiving Data` al conectar | Red o puerto 102 bloqueado, o CPU sin recursos de conexión libres (cada HMI/SCADA consume una). Revisa con `-v` |
| `Acceso denegado (¿contraseña incorrecta?)` | La contraseña no corresponde a ningún nivel de acceso de la CPU |
| Conecta, pero las lecturas dan `BadAccessDenied` | No se indicó contraseña y la CPU está protegida |
| Absoluto: error al leer un DB | El DB no existe, es **optimizado** (en TIA desactiva *Acceso optimizado al bloque*) o PUT/GET no está permitido |
| Absoluto: conecta y `info` funciona, pero toda lectura da `BadAccessDenied` «error S7 0x8104» | PUT/GET deshabilitado. En TIA: Propiedades de la CPU → Protección y seguridad → Mecanismos de conexión → *Permitir acceso vía comunicación PUT/GET*, y cargar |
| Símbolo inexistente | `BadAddressInvalid` «El símbolo '...' no existe en la CPU». Comprueba el nombre con `plcmon browse` |
