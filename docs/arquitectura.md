# s7plus-monitor — Arquitectura propuesta

Herramienta propia y gratuita para S7-300/400/1200/1500 con:
- Conexiones a varios PLCs, cada una en modo **simbólico** (S7CommPlus + TLS) o **absoluto** (S7comm PUT/GET).
- Tags con suscripción, lectura y escritura.
- Historiador con muestreo configurable al estilo de Ignition.
- Servidor OPC UA propio.
- Despliegue por SSH de un servicio recolector en un PC remoto.
- Escritorio en Windows y Linux con Avalonia.

## 1. Idea central: la app es un cliente y la recolección la hace un *agente*

```
┌──────────────────────── App de escritorio (Avalonia) ─────────────────────────┐
│  PLCs · Tags · Historiador · OPC UA · Gráficas (fase posterior) · Agentes SSH │
└───────────────┬──────────────────────────────────────────────┬────────────────┘
                │ gRPC/HTTP + stream en vivo                    │ SSH (despliegue)
                ▼                                               ▼
┌──────── Agente local (mismo PC) ───────┐        ┌──────── Agente remoto ─────────┐
│  S7Monitor.Agent (servicio headless)    │        │  mismo binario, como servicio  │
│  ├─ ConnectionManager (N PLCs)          │        │  systemd / Windows Service     │
│  ├─ TagEngine (suscripciones/polling)   │        └────────────────────────────────┘
│  ├─ Historian (SQLite)                  │
│  └─ OPC UA Server                       │
└────────────┬────────────────────────────┘
             ▼
     PLCs S7 (puerto 102)
```

- **Un solo motor:** la recolección, el historiador y OPC UA viven siempre en el agente, no en la UI. Cerrar la app no corta la recogida de datos, y un PC remoto funciona igual que el local.
- **Del lado de la app**, "agente local" y "agente remoto" son lo mismo, solo cambia la dirección. La función SSH se encarga de instalarlo y arrancarlo en la máquina remota.

## 2. Solución .NET

Todo en .NET 10 (LTS), Windows y Linux x64/arm64.

| Proyecto | Contenido | Licencia/dependencias |
|---|---|---|
| `S7CommPlus` | Port a .NET 10 de *S7CommPlusDriver* (protocolo S7CommPlus + TLS + legitimación + browse/read/write/suscripciones) | **LGPL-3**, en ensamblado separado. Los cambios que hagamos en él se publican |
| `S7Monitor.Drivers` | `IPlcDriver` común con dos implementaciones: **Symbolic** (S7CommPlus) y **Absolute** (PUT/GET, S7.Net Plus) | S7.Net Plus: MIT |
| `S7Monitor.Core` | Modelo: Plc, Tag, TagGroup (scan class), calidad, timestamp, escrituras y auditoría | — |
| `S7Monitor.Historian` | Almacenamiento con *min sample rate* y *deadband*, consultas por rango, agregación | SQLite (Microsoft.Data.Sqlite, MIT) |
| `S7Monitor.OpcUa` | Servidor OPC UA que expone los tags | OPC Foundation UA-.NETStandard (**pendiente de verificar la licencia**: en el pasado fue doble GPL-2/RCL) |
| `S7Monitor.Agent` | Servicio headless con API gRPC o REST y stream en vivo | ASP.NET Core |
| `S7Monitor.App` | UI Avalonia 11 (MVVM, CommunityToolkit.Mvvm) | Avalonia: MIT |
| `S7Monitor.Deploy` | Despliegue por SSH (subir binario, crear servicio systemd o Windows Service, enviar config) | SSH.NET: MIT |

Requisito nativo: OpenSSL 3 (`libssl-3`/`libcrypto-3`). En Linux viene con el sistema; en Windows se distribuye con la app.

## 3. Modelo funcional

**PLC**
- Nombre, IP, puerto, modo (Symbolic o Absolute), rack/slot (solo en absoluto), contraseña (guardada cifrada), timeout, `Enabled`.
- `WritesEnabled` desactivado por defecto.

**Tag**
- PLC, dirección y TagGroup.
  - En simbólico: ruta, por ejemplo `"CNV02_IDB".Sequence.watchdogTime`.
  - En absoluto: por ejemplo `DB1.DBW10`.
- Tipo, que se detecta solo en modo simbólico.
- `Writable` e historización activable (*min sample rate*, *deadband*, *max time between samples*).

**TagGroup (scan class)**: periodo de lectura y modo.
- Simbólico: **suscripción nativa S7CommPlus**. El PLC notifica los cambios y no hace falta sondear.
- Absoluto: sondeo agrupado con optimización de lecturas por DB y rango.

**Símbolos**
- El browse completo se guarda en caché local por PLC (2,2 M variables en 8 s en el 1517F).
- La caché se invalida cuando cambia el *PLC program checksum*.
- La UI tiene un buscador con filtro, sin cargar el árbol entero.

**Escrituras** (el 1517F es un PLC de seguridad):
- Doble llave: `WritesEnabled` en el PLC y `Writable` en el tag.
- Confirmación en la UI y log de auditoría con usuario, valor anterior, valor nuevo y fecha.
- Las funciones de cambio de estado del PLC (RUN/STOP) **no se exponen**.

## 4. Fases

| Fase | Entregable | Prueba de aceptación |
|---|---|---|
| **1. Núcleo** | Port de `S7CommPlus` a .NET 10, `IPlcDriver` simbólico y absoluto, CLI de pruebas (`connect`, `browse --filter`, `read`, `subscribe`) | Leer y suscribir en el 1517F en simbólico y en un PLC en absoluto. Funciona en Windows y Linux (Docker) |
| **2. Agente** | ConnectionManager multi-PLC, TagGroups, configuración JSON, API y stream en vivo, reconexión con backoff | 4 PLCs a la vez, activar y desactivar en caliente |
| **3. App Avalonia** | CRUD de PLCs y tags, buscador de símbolos, watch-list en vivo, activar/desactivar PLC, escritura controlada | Uso diario completo sin tocar ficheros |
| **4. Historiador** | SQLite, muestreo y deadband, consulta por rango, retención | 1 semana de datos, consulta de 1 día en menos de 1 s |
| **5. OPC UA** | Servidor con los tags (lectura y, si se habilita, escritura) | Ignition o UaExpert leen los tags |
| **6. Agentes remotos SSH** | Instalación remota (systemd o Windows Service), envío de config y gestión desde la app | Despliegue en un PC Linux limpio en un clic |
| **7. Gráficas** | Tendencias en vivo e históricas (ScottPlot o LiveCharts2, ambos MIT) | — |

## 5. Riesgos

- *S7CommPlusDriver* lo marca su autor como "en desarrollo". Riesgo de incompatibilidades con firmwares concretos. Se mitiga con pruebas contra cada PLC real y con las capturas descifrables (`key_*.log`) para depurar.
- El modo simbólico requiere firmware con TLS: S7-1500 ≥ V2.9 y S7-1200 ≥ V4.3, proyectado en TIA ≥ V17. Para CPUs antiguas queda el modo absoluto.
- Hay límite de conexiones HMI en la CPU: cada agente consume una.
