# Manual de usuario de PLC-Monitor

**Versión 1.16.0**

PLC-Monitor es una aplicación de escritorio para leer, historizar y visualizar variables de autómatas
programables (PLC). Se compone de dos piezas:

- **La app de escritorio**, desde la que configuras agentes, dispositivos, tags, gráficas y OPC UA.
- **El agente**, un servicio que se instala junto al PLC, lee las variables, guarda el histórico y publica
  los datos por OPC UA. La app se conecta al agente por gRPC, en local o por red.

![Ventana principal de PLC-Monitor](img/01-window.png)

## Primeros pasos

1. Ejecuta `plcmon-app` (o `plcmon-app.exe` en Windows). La release es autocontenida: no hace falta instalar
   .NET en el equipo.
2. En la pestaña **Agentes**, crea un perfil o instala un agente nuevo. Para probar en el mismo equipo, marca
   **Este equipo (sin SSH)**.
3. Conecta con el agente y crea un dispositivo en la pestaña **Dispositivos**.
4. Añade tags escribiendo su dirección o, en los drivers que lo permiten, con el explorador de símbolos.
5. Consulta los valores en vivo, activa el histórico y crea tendencias o paneles.

Si es tu primera vez, sigue el capítulo [Instalación y primer uso](02-instalacion-y-primer-uso.md).

## Índice

| Capítulo | Contenido |
|---|---|
| [1. Introducción](01-introduccion.md) | Piezas, conceptos, arquitectura, requisitos y puertos. |
| [2. Instalación y primer uso](02-instalacion-y-primer-uso.md) | Ejecutar la app, instalar el agente, conectar y recorrer la interfaz. |
| [3. Agentes y perfiles](03-agentes-y-perfiles.md) | Perfiles, conexión, instalación por SSH, credenciales, estado y logs. |
| [4. Dispositivos y drivers](04-dispositivos-y-drivers.md) | Alta de PLCs, ajustes de S7 y Modbus, edición JSON y conexión. |
| [5. Tags en vivo](05-tags-en-vivo.md) | Tabla de tags, alta por dirección, explorador, detalle y escritura. |
| [6. Histórico](06-historico.md) | Historización, consultas, agregados, tabla, CSV y borrado. |
| [7. Tendencias y gráficas](07-tendencias.md) | Vistas, fuentes de datos y los siete tipos de gráfica. |
| [8. Paneles](08-paneles.md) | Rejillas de varias vistas, sincronización y exportación PNG. |
| [9. Transformaciones](09-transformaciones.md) | Mostrar valores enteros como texto y asignarlos a tags. |
| [10. OPC UA](10-opc-ua.md) | Publicar tags, usuarios, certificados y escritura por OPC UA. |
| [11. CLI](11-cli.md) | `plcmon`: lectura directa, diagnóstico y administración del agente. |
| [12. Seguridad](12-seguridad.md) | Tokens, TLS, llaves de escritura, permisos y buenas prácticas. |
| [13. Configuración y mantenimiento](13-configuracion-y-mantenimiento.md) | Ficheros, copias, actualizaciones, retención y logs. |
| [14. Resolución de problemas](14-resolucion-de-problemas.md) | Diagnóstico de conexión, PLC, histórico, OPC UA y gráficas. |

## Convenciones del manual

- Los pasos se numeran y los nombres de botones y campos aparecen **en negrita**.
- Los ejemplos usan nombres ficticios (`planta-demo.local`, `prensa-linea3`) y direcciones reservadas
  para documentación (`192.0.2.10`). No son datos de una instalación real.
- Los avisos de seguridad se marcan como **Importante** o **Precaución**.
- Las capturas se han generado con datos sintéticos.

## Arquitectura en una página

```text
┌──────────────────────┐        gRPC (TLS + token)        ┌──────────────────────┐
│ App de escritorio    │ ───────────────────────────────▶ │ Agente plcmon-agent  │
│ Windows / Linux      │                                  │ Linux / Windows      │
│                      │ ◀─────────────────────────────── │                      │
│ tags, tendencias,    │        estados / valores         │ PLCs, histórico,     │
│ histórico, OPC UA,   │                                  │ OPC UA               │
│ perfiles de agentes  │                                  └──────────┬───────────┘
└──────────────────────┘                                             │
                                                           S7 / Modbus │
                                                                      ▼
                                                             ┌─────────────────┐
                                                             │ PLC / variador  │
                                                             └─────────────────┘
```

- La app **no habla directamente con el PLC** cuando usas un agente: le pide los datos al agente.
- El agente mantiene la conexión con el PLC, aunque cierres la app.
- Varias apps pueden conectarse al mismo agente si comparten la URL y el token.
- El histórico y la configuración viven en el equipo del agente, no en la app.

## Requisitos rápidos

- **App**: Windows x64, Linux x64 o Linux ARM64. La release incluye todo lo necesario.
- **Agente**: Linux o Windows, en la misma red que el PLC. Se instala desde la propia app.
- **Red**: acceso al puerto del agente (`5311/tcp` por defecto con TLS) y, si usas OPC UA, `4840/tcp`.
- **PLC**: según el driver, acceso S7 (102/tcp o S7CommPlus) o Modbus (502/tcp o puerto serie).
- **Permisos**: instalar el agente requiere administrador en Windows o `sudo`/`pkexec` en Linux.

## Seguridad en dos líneas

No expongas el agente directamente a Internet. Usa una red industrial protegida o una VPN, un token distinto
por instalación y el token de solo lectura para quien únicamente deba consultar. Los detalles están en
[Seguridad](12-seguridad.md).

## Estado de este manual

El manual se ha escrito a partir del código y de capturas con datos sintéticos. Las pruebas con PLC reales,
la instalación por SSH y algunos flujos de error siguen pendientes de validación.
