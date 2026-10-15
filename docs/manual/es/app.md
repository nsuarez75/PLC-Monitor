# PLC-Monitor — Aplicación de escritorio

> Versión 1.0.0 · Última actualización: 2026-10-15 · [English](../en/app.md)

`plcmon-app` es la aplicación de escritorio (Windows y Linux). **No habla con los PLC**: se conecta a un
[agente](agente.md) (local o remoto) y trabaja sobre él. Cerrar la app no corta la recogida de datos.

## 1. Arrancar

```bash
app/plcmon-app            # Linux (paquete de la release)
app\plcmon-app.exe        # Windows
dotnet run --project src/PlcMonitor.App    # desde el código
```

Antes tiene que estar arrancado el agente (`agent/plcmon-agent`). Por defecto escucha en `http://127.0.0.1:5310`.

## 2. Conectar con un agente

Arriba, escribe la URL del agente y pulsa **Conectar**. La app recuerda las últimas URL usadas (desplegable).
A la derecha se ve el estado de la conexión:

| Estado | Significado |
|---|---|
| **En vivo** | Suscrita al agente: los valores y estados llegan al momento |
| **Reconectando…** | Se perdió la conexión con el agente; la app vuelve a suscribirse sola cada 2 s |
| **Sin conexión** | No hay agente conectado |

Las preferencias se guardan en `~/.config/PLC-Monitor/app.json` (Linux) o `%APPDATA%\PLC-Monitor\app.json` (Windows).

## 3. Dispositivos

La columna izquierda lista los PLC del agente con un punto de color:

| Color | Estado |
|---|---|
| Verde | Conectado |
| Amarillo | Conectando |
| Rojo | En fallo (el detalle y la hora del próximo reintento aparecen al seleccionarlo) |
| Gris | Desconectado o desactivado |

- **Activar / Desactivar** (en cada fila): conecta o desconecta el PLC y lo guarda en la configuración del agente.
- **Nuevo**: alta de un PLC. Al elegir el driver se rellenan sus ajustes por defecto en JSON (`host`, `security`, `cpu`...;
  ver el [manual de uso](usuario.md)). Marca **Escrituras habilitadas** solo si se va a escribir (primera llave).
- **Editar**: cambia nombre, driver, ajustes, llaves y reconexión. Los tags se conservan. Si hay errores, el agente los
  devuelve todos juntos y no cambia nada.
- **Eliminar**: da de baja el PLC y sus tags (pide confirmación).

Todos los cambios se guardan en el `agent.json` del agente.

## 4. Tags

La pestaña **Tags** muestra los tags del PLC seleccionado con su valor, tipo, calidad y hora del último cambio, en vivo.

- **Escribir**: solo en tags escribibles (segunda llave). Escribe el valor (punto decimal; `true/false`), pulsa **Escribir**
  y confirma. El agente comprueba las dos llaves y anota la escritura con tu usuario en su log de auditoría.
- **Quitar**: elimina el tag de la configuración (pide confirmación).

## 5. Explorar y añadir tags

En la pestaña **Explorar** (con el PLC conectado y un driver que lo permita, como `s7-symbolic`):

1. Escribe parte del nombre y pulsa **Buscar** (Intro). La primera búsqueda explora la CPU; las siguientes usan la
   caché del agente. **Volver a explorar la CPU** la refresca (tras cargar un programa nuevo).
2. Elige el **grupo** de escaneo (se crea con el **periodo** indicado si no existe) y si el tag será **escribible**.
3. Pulsa **Añadir** en el símbolo. Aparece en la pestaña Tags con su valor en cuanto el agente lo lee.

## 6. Solución de problemas

| Síntoma | Causa y solución |
|---|---|
| «No se puede conectar con el agente» | El agente no está arrancado, la URL es incorrecta o escucha solo en 127.0.0.1 (ver [agente](agente.md#3-ejecutar)) |
| «… no es un agente de PLC-Monitor» | La URL apunta a otro servicio o a un agente de otra versión |
| La búsqueda dice «no está conectado» | El PLC tiene que estar conectado para explorar |
| La búsqueda dice «no permite explorar variables» | El driver `s7-absolute` no explora: añade las direcciones con `plcmon agent tag add` |
| «Reconectando…» continuo | Se cayó el agente o la red; la app se recupera sola cuando vuelve |
