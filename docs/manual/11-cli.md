# 11. CLI

`plcmon` es la herramienta de línea de órdenes de PLC-Monitor. Sirve para diagnosticar un PLC sin abrir la
app, para leer o escribir una dirección suelta y para administrar un agente desde un script.

La CLI tiene dos modos:

- **Órdenes directas** (`read`, `watch`, `write`, `browse`, `info`): conectan directamente al PLC con los
  datos que les pasas. No necesitan un agente.
- **Órdenes de agente** (`plcmon agent ...`): hablan con un agente por gRPC. Necesitan la URL y el token
  del agente.

**Importante**: la CLI no forma parte de la release de escritorio actual. Se compila desde el repositorio.

## 11.1. Compilar y ejecutar

```bash
dotnet build src/PlcMonitor.Cli/PlcMonitor.Cli.csproj -c Release
```

El ejecutable se llama `plcmon`:

```bash
src/PlcMonitor.Cli/bin/Release/net10.0/plcmon --help
```

También puedes ejecutarla sin publicar:

```bash
dotnet run --project src/PlcMonitor.Cli -- --help
```

Si el comando `dotnet` del sistema no encuentra el SDK, usa la ruta del SDK instalado o instala .NET 10.

## 11.2. Ayuda y códigos de salida

```bash
plcmon --help
plcmon read --help
plcmon agent --help
plcmon agent history --help
```

Códigos de salida:

| Código | Significado |
|---|---|
| `0` | Correcto. |
| `1` | Error de conexión, lectura con calidad mala o escritura fallida. |
| `2` | Argumentos inválidos o cancelación de una confirmación. |
| `3` | Escritura cancelada por el usuario. |

La salida usa tablas y colores. Si la rediriges a un fichero, los códigos de color pueden aparecer como
texto; usa la salida normal de la terminal para leerla.

## 11.3. Órdenes directas

### `drivers`

Lista los drivers disponibles y sus capacidades.

```bash
plcmon drivers
```

### `info`

Conecta y muestra información del dispositivo.

```bash
plcmon info -H 192.0.2.10 -d s7-symbolic
```

### `read`

Lee una o varias direcciones una vez.

```bash
plcmon read '"MiDB".Motor.Velocidad' -H 192.0.2.10
plcmon read DB1.DBW10 DB1.REAL20 -d s7-absolute -H 192.0.2.10 --cpu S7300 --slot 2
plcmon read HR0 HR10:FLOAT C3 -d modbus -H 192.0.2.20 --unit-id 1
plcmon read 40001 30005:UINT16 -d modbus --serial /dev/ttyUSB0 --baud 19200 --parity even --unit-id 3
```

### `browse`

Busca símbolos en una CPU S7 simbólica.

```bash
plcmon browse Motor -H 192.0.2.10 -n 50
```

El filtro es opcional. `-n` limita el número de resultados.

### `watch`

Lee periódicamente y muestra solo los cambios. Ctrl+C para salir.

```bash
plcmon watch '"MiDB".Contador' -H 192.0.2.10 -i 500
```

El intervalo mínimo es de 50 ms.

### `write`

Escribe un valor y pide confirmación. La opción `--type` permite indicar el tipo si no se puede deducir.

```bash
plcmon write '"MiDB".Consigna' 12.5 -H 192.0.2.10
plcmon write DB1.DBW10 42 -d s7-absolute -H 192.0.2.10 --type Int16
```

`-y` omite la confirmación. Úsalo solo en un script controlado.

### Opciones comunes de las órdenes directas

| Opción | Uso |
|---|---|
| `-d, --driver` | `s7-symbolic` (por defecto), `s7-absolute` o `modbus`. |
| `-H, --host` | IP o nombre del PLC. En Modbus RTU no se usa. |
| `-p, --password` | Contraseña de la CPU S7 simbólica. Si se omite, se usa `PLCMON_PASSWORD`. |
| `-u, --username` | Usuario de CPUs con gestión de usuarios. |
| `--cpu` | `S7300`, `S7400`, `S71200` o `S71500`. |
| `--rack`, `--slot` | Rack y slot en S7 absoluto. |
| `--port` | Puerto TCP. |
| `--serial` | Puerto serie para Modbus RTU. |
| `--rtu-over-tcp` | Modbus RTU sobre TCP. |
| `--baud`, `--parity`, `--stop-bits` | Ajustes de la línea serie. |
| `--unit-id` | Número de esclavo Modbus. |
| `--byte-order` | `ABCD`, `BADC`, `CDAB` o `DCBA`. |
| `--one-based` | Direcciones Modbus por tabla desde 1. |
| `-t, --timeout` | Timeout en milisegundos. |
| `--security` | `auto`, `tls` o `legacy` en S7 simbólico. |
| `-v, --verbose` | Log detallado del driver. |

**Importante**: no escribas la contraseña en el historial de la shell. Usa `PLCMON_PASSWORD`:

```bash
export PLCMON_PASSWORD='...'
plcmon read '"MiDB".Motor.Velocidad' -H 192.0.2.10
```

## 11.4. Órdenes de agente

La URL, el token y la huella del certificado se pueden pasar por opciones o por variables de entorno:

| Opción | Variable | Por defecto |
|---|---|---|
| `-a, --agent` | `PLCMON_AGENT` | `http://127.0.0.1:5310` |
| `--token` | `PLCMON_AGENT_TOKEN` | vacío |
| `--cert` | `PLCMON_AGENT_CERT` | vacío |

```bash
export PLCMON_AGENT='https://planta-demo.local:5311'
export PLCMON_AGENT_TOKEN='...'
export PLCMON_AGENT_CERT='...'

plcmon agent info
plcmon agent devices
```

Evita pasar el token con `--token` en un equipo compartido: queda en el historial de la shell y en la lista
de procesos. Usa las variables de entorno o un gestor de secretos.

### `agent info`

Muestra versión, máquina, sistema, fecha de arranque, ruta de configuración, drivers, nivel de acceso e
información del historiador.

```bash
plcmon agent info
```

### `agent devices`

Lista los dispositivos con su driver, estado, número de tags y detalle.

```bash
plcmon agent devices
```

### `agent tags`

Lista los tags configurados con su último valor. Puedes limitar a un dispositivo.

```bash
plcmon agent tags
plcmon agent tags prensa-linea3
```

### `agent read`

Lee direcciones cualesquiera a través del agente, aunque no estén configuradas como tags.

```bash
plcmon agent read prensa-linea3 DB10.DBW0
```

### `agent write`

Escribe un tag configurado. Respeta las llaves del agente y pide confirmación.

```bash
plcmon agent write prensa-linea3 marcha true
plcmon agent write prensa-linea3 consigna 12.5 --user mantenimiento
```

`--user` solo cambia el usuario que aparece en la auditoría; no autentica. La autenticación es el token.

### `agent enable` y `agent disable`

Activan o desactivan un dispositivo en caliente.

```bash
plcmon agent disable prensa-linea3
plcmon agent enable prensa-linea3 --persist
```

Sin `--persist`, el cambio se pierde al reiniciar el agente. Es al revés que en `tag add`, que guarda por
defecto.

### `agent browse`

Explora las variables de un dispositivo conectado. `--refresh` vuelve a leer la CPU en lugar de usar la
caché.

```bash
plcmon agent browse prensa-linea3 Motor -n 50
plcmon agent browse prensa-linea3 --refresh
```

### `agent config`

Muestra la configuración en árbol: dispositivos, grupos de escaneo y tags.

```bash
plcmon agent config
plcmon agent config prensa-linea3
```

### `agent device add|remove|order`

Altas, bajas y orden de dispositivos.

```bash
plcmon agent device add prensa-linea3 -d s7-symbolic -s '{"host":"192.0.2.10"}'
plcmon agent device add horno-linea3 -d modbus -s '{"transport":"Tcp","host":"192.0.2.20","port":502,"unitId":1}'
plcmon agent device remove horno-linea3
plcmon agent device order prensa-linea3 horno-linea3
```

- `device add` crea el dispositivo o actualiza sus ajustes si ya existe. Conserva sus tags.
- `--disabled` lo crea sin conectar.
- `--writes` activa la primera llave de escritura.
- `--no-persist` no guarda en `agent.json`.
- `device remove` pide confirmación y borra el dispositivo con sus tags. No borra el histórico.
- `device order` pone primero los indicados, en ese orden; los demás quedan detrás.

### `agent tag add|remove|enable|disable`

```bash
plcmon agent tag add prensa-linea3 '"MiDB".Velocidad' --name velocidad -g rapido -i 250
plcmon agent tag add prensa-linea3 HR100:FLOAT --name presion --unit bar --decimals 2
plcmon agent tag add prensa-linea3 DB1.REAL20 --name fuerza --writable --history
plcmon agent tag remove prensa-linea3 fuerza
plcmon agent tag disable prensa-linea3 fuerza
plcmon agent tag enable prensa-linea3 fuerza
```

Opciones de `tag add`:

| Opción | Uso |
|---|---|
| `--name` | Nombre del tag. Por defecto, la dirección. |
| `-g, --group` | Grupo de escaneo. Se crea si no existe. |
| `-i, --interval` | Periodo del grupo nuevo, en ms. |
| `--writable` | Segunda llave de escritura. |
| `--type` | Tipo para interpretar escrituras. |
| `--history` | Activa el histórico. |
| `--history-min`, `--history-max` | Frecuencia mínima y tiempo máximo (`1s`, `1m`...). |
| `--history-deadband` | Banda muerta absoluta o en porcentaje (`0.5`, `2%`). |
| `--unit` | Unidad visible. |
| `--decimals` | Decimales de 0 a 9; `-1` muestra el valor completo. |
| `--scale` | `brutoMín:brutoMáx:ingMín:ingMáx[:clamp]`, o `none`. |

Si el tag ya existía con ese nombre, se sustituye. Se conservan la transformación, el grupo visual, la
llave de OPC UA y el estado activo.

### `agent history`

Consulta el histórico en tabla o CSV.

```bash
plcmon agent history prensa-linea3 velocidad --last 2h -i 1m --agg max
plcmon agent history prensa-linea3 velocidad presion --from "2026-10-15 08:00" --csv turno.csv
```

| Opción | Uso |
|---|---|
| `--last` | Periodo hasta ahora (`30s`, `15m`, `2h`, `7d`). Por defecto, 1 h. |
| `--from`, `--to` | Fechas locales absolutas. |
| `-i, --interval` | Agrega por intervalos. Sin él, puntos en bruto. |
| `--agg` | `avg`, `min`, `max`, `first` o `last`. |
| `-n, --max` | Máximo de puntos por tag. Por defecto, 10 000. |
| `--csv` | Guarda el resultado en CSV. |

El CSV tiene la cabecera `device,tag,timestamp,value,quality,unit`, con la hora en UTC.

### `agent history-delete`

Borra el histórico de unos tags en un intervalo. Pide confirmación.

```bash
plcmon agent history-delete prensa-linea3 velocidad --from "2026-10-15 08:00" --to "2026-10-15 14:00"
plcmon agent history-delete prensa-linea3 velocidad presion --all -y
```

`--all` borra todo lo guardado de esos tags. No se puede combinar con `--from`, `--to` ni `--last`.

### `agent history-rename`

Lleva el histórico de un nombre a otro, por ejemplo después de renombrar un tag o un dispositivo.

```bash
plcmon agent history-rename prensa-linea3/vel prensa-linea3/velocidad
plcmon agent history-rename prensa-linea3 prensa-linea4
```

Si el destino ya tiene histórico, se juntan. Pide confirmación.

### `agent watch`

Se suscribe al agente y muestra los cambios de valores y de estado en directo. Ctrl+C para salir.

```bash
plcmon agent watch
plcmon agent watch prensa-linea3
```

## 11.5. Ejemplos de diagnóstico

### Comprobar un PLC sin agente

```bash
plcmon info -H 192.0.2.10
plcmon browse Motor -H 192.0.2.10 -n 20
plcmon read '"MiDB".Motor.Velocidad' -H 192.0.2.10
```

### Comprobar un agente

```bash
plcmon agent info
plcmon agent devices
plcmon agent tags prensa-linea3
```

### Ver un ciclo en directo

```bash
plcmon agent watch prensa-linea3
```

### Exportar un turno

```bash
plcmon agent history prensa-linea3 velocidad presion fuerza \
  --from "2026-10-15 06:00" --to "2026-10-15 14:00" -i 10s --agg avg \
  --csv turno-2026-10-15.csv
```

### Cambiar una consigna con confirmación

```bash
plcmon agent write prensa-linea3 consigna 8.5
```

## 11.6. Buenas prácticas

- No pases tokens ni contraseñas por la línea de órdenes en equipos compartidos.
- Usa un token de solo lectura para los scripts de consulta.
- Prueba primero con `read` antes de `write`.
- Guarda la salida de diagnóstico en un fichero si vas a abrir una incidencia.
- En scripts, revisa el código de salida; `1` puede significar una lectura con calidad mala, no un fallo de
  sintaxis.
- No uses `-y` en escrituras sin una comprobación previa del dispositivo y del valor.
- La CLI no sustituye la app para configurar gráficas, paneles, transformaciones ni OPC UA.

## Siguiente paso

Para entender cómo se protegen el agente, los tokens y las escrituras, sigue con
[Seguridad](12-seguridad.md).
