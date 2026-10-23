# 1. Introducción

PLC-Monitor sirve para mirar lo que está pasando dentro de un PLC sin tener que abrir el programa de
programación ni crear una pantalla nueva cada vez. La app muestra los valores en vivo, guarda un histórico,
permite comparar señales en gráficas y publica los datos por OPC UA para otros sistemas.

El programa está pensado para diagnóstico y supervisión, no para sustituir al SCADA de la planta. Puedes
usarlo para comprobar una secuencia, buscar una condición que falla de forma intermitente, revisar una
consigna o dejar registrado el comportamiento de una máquina durante un turno.

## Las dos piezas

### La app de escritorio

Es la ventana que abres en tu equipo. Desde ella:

- creas y editas dispositivos y tags;
- conectas con uno o varios agentes;
- ves los valores en vivo y escribes consignas;
- consultas el histórico y creas tendencias, paneles y transformaciones;
- configuras el servidor OPC UA del agente;
- instalas o actualizas agentes en otros equipos por SSH.

La app no necesita un servidor central. Guarda sus preferencias, perfiles de agentes, vistas y paneles en tu
carpeta de usuario. Los perfiles incluyen la URL y el token de cada agente; el token se guarda protegido por
el sistema operativo.

### El agente

El agente es un servicio que se instala en el equipo que está cerca del PLC. Se encarga de:

- conectar con los PLC mediante los drivers;
- leer los tags en los grupos de escaneo;
- mantener la última calidad y el último valor de cada tag;
- guardar las muestras historizadas en SQLite;
- publicar los tags por OPC UA si lo activas;
- atender a la app por gRPC.

El agente sigue funcionando cuando cierras la app. Si la app se desconecta, el PLC se sigue leyendo y el
histórico continúa. Al volver a conectar, la app recibe el estado actual y los cambios nuevos.

## Conceptos que aparecen en todo el manual

| Concepto | Qué es |
|---|---|
| **Agente** | Servicio `plcmon-agent` instalado en un equipo. Tiene una URL y un token. |
| **Perfil de agente** | La configuración local que usa la app para conectar con un agente: nombre, URL, token, certificado y datos SSH. |
| **Dispositivo** | Un PLC, variador o pasarela configurada dentro del agente. Tiene un driver y unos ajustes. |
| **Driver** | La familia de protocolo: S7 simbólico, S7 absoluto o Modbus. |
| **Tag** | Una variable concreta de un dispositivo, con una dirección, un tipo y un grupo de escaneo. |
| **Grupo de escaneo** | Conjunto de tags que se leen juntos cada cierto tiempo (`intervalMs`). |
| **Calidad** | Indica si el valor es fiable (`Good`), dudoso (`Uncertain`) o malo (`Bad`). Un valor `Bad` suele traer un texto de error. |
| **Historización** | Guardar las muestras de un tag para consultarlas después. Se activa por tag. |
| **Transformación** | Traducción de un valor entero a texto, por ejemplo `0 = Parado`, `1 = Marcha`. |
| **Vista de tendencia** | Una gráfica guardada con sus tags, tipo, ventana y fuente de datos. |
| **Panel** | Una rejilla con varias vistas guardadas, para verlas a la vez. |
| **Llave de escritura** | Cada uno de los permisos que deben coincidir para que una escritura llegue al PLC. |

## Arquitectura

```text
App de escritorio ─── gRPC (TLS + token) ───▶ Agente ─── S7 / Modbus ───▶ PLC
       ▲                                        │
       └──────────── estados y valores ─────────┘
                                                │
                                                ├── histórico SQLite
                                                └── servidor OPC UA
```

La app puede estar en el mismo equipo que el agente o en otro. Lo habitual en una planta es tener un agente
en la red de máquinas y la app en el puesto de mantenimiento. También puedes instalar un agente en tu propio
portátil para probar con un PLC de laboratorio.

El agente escucha por defecto en `http://127.0.0.1:5310` cuando se ejecuta sin instalar y solo para uso
local. El instalador crea una configuración con TLS en el puerto `5311` y un token. La app usa esa URL y ese
token al conectar. No confundas el puerto del agente con el puerto del PLC: el PLC se configura dentro de
cada dispositivo.

## Qué puedes hacer con cada driver

| Driver | Uso típico | Explorar símbolos | Direcciones |
|---|---|---|---|
| **S7 simbólico** (`s7-symbolic`) | S7-1200 y S7-1500 con acceso simbólico S7CommPlus. | Sí. | Nombres del programa, por ejemplo `"MiDB".Motor.Velocidad`. |
| **S7 absoluto** (`s7-absolute`) | S7-300/400 y también 1200/1500 con PUT/GET. | No. | Direcciones absolutas, por ejemplo `DB1.DBW10` o `M10.1`. |
| **Modbus** (`modbus`) | Modbus TCP, RTU por puerto serie o RTU sobre TCP. | No. | `HR0`, `IR5`, `C3`, `DI2`, `40001`, con tipo y orden de bytes opcionales. |

El driver simbólico es el más cómodo cuando el PLC lo admite: puedes buscar una variable por su nombre y no
tienes que calcular desplazamientos. El absoluto es necesario en CPUs antiguas o cuando el programa no
expone símbolos. Modbus es habitual en variadores, analizadores de red, módulos de expansión y equipos de
terceros.

## Requisitos

### Para la app

- Windows x64, Linux x64 o Linux ARM64.
- La release es un ejecutable autocontenido; no hace falta instalar .NET.
- Para compilar desde el código fuente sí necesitas el SDK de .NET 10.

### Para el agente

- Linux o Windows en la misma red que el PLC.
- Permisos de administrador para instalar el servicio.
- Acceso al puerto del PLC: `102/tcp` para S7, `502/tcp` para Modbus TCP o el puerto serie para Modbus RTU.

### Puertos que suele usar PLC-Monitor

| Puerto | Uso |
|---|---|
| `5310/tcp` | Agente local sin TLS, solo para pruebas en el mismo equipo. |
| `5311/tcp` | Agente instalado con TLS y token. Es el valor por defecto del instalador. |
| `4840/tcp` | Servidor OPC UA del agente, si lo activas. |
| `102/tcp` | S7. |
| `502/tcp` | Modbus TCP. |

Abre solo los puertos necesarios y no publiques el agente en Internet. En el capítulo
[Seguridad](12-seguridad.md) se explica cómo combinarlos.

## Lo que no hace

- No sustituye a un SCADA: no tiene alarmas, recetas, turnos ni gestión de usuarios de planta.
- No programa el PLC ni descarga programas.
- No garantiza que una escritura sea segura para la máquina. La decisión de escribir y el valor deben
  validarse siempre en el proceso.
- No sincroniza los datos entre varias apps. Cada app consulta el mismo agente, pero no comparte sus vistas
  ni sus paneles con otras personas.
- No envía datos a la nube. Todo queda en el agente y en el equipo donde ejecutas la app, salvo que tú
  configures OPC UA u otra integración.

## Recorrido rápido por la ventana

![Ventana principal con un agente conectado](img/01-window.png)

1. **Barra superior**: perfil de agente, botón **Conectar** o **Desconectar**, estado de la conexión y
   pestañas **Dispositivos**, **Tendencias**, **Transformaciones**, **OPC UA** y **Agentes**.
2. **Lista de dispositivos**: a la izquierda. Cada fila muestra el nombre, el estado, el driver y el número
   de tags. El botón **+** crea un dispositivo.
3. **Zona central**: cambia según la pestaña. En **Dispositivos** tienes las pestañas **Tags**,
   **Histórico** y **Explorar** del dispositivo seleccionado.
4. **Barra inferior**: mensajes de la app, errores y confirmaciones. Si un mensaje aparece en rojo, no se
   ha completado la operación.
5. **Tema**: el icono de la derecha de la barra superior alterna entre claro y oscuro.

Con eso ya puedes seguir el capítulo [Instalación y primer uso](02-instalacion-y-primer-uso.md). Si ya
tienes un agente funcionando, salta a [Dispositivos y drivers](04-dispositivos-y-drivers.md).
