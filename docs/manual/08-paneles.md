# 8. Paneles

Un **panel** coloca varias vistas de tendencia en una rejilla para verlas a la vez. Es útil para una pantalla
de operador, para comparar varios ciclos o para tener una visión conjunta de una máquina.

![Panel con cuatro vistas de la misma prensa](img/08-panel.png)

## 8.1. Crear un panel

1. Abre **Tendencias**.
2. En **PANELES**, pulsa **+**.
3. Escribe el **Nombre**.
4. Elige las **Columnas**: de 1 a 4.
5. Marca **Sincronizar el zoom de las gráficas de tiempo** si quieres que al ampliar una se amplíen las
   demás.
6. Añade vistas desde **VISTAS GUARDADAS** con el botón **+**.
7. Ordena las vistas con las flechas de subir y bajar. Se colocan por filas, en ese orden.
8. Pulsa **Guardar** o **Abrir**.

Un panel puede contener vistas en vivo, del histórico, de CSV y de barras. Las vistas deben existir antes de
añadirlas. Un panel no crea tags ni gráficas nuevas: reutiliza las vistas de **TENDENCIAS**.

## 8.2. La rejilla

- **Columnas**: 1, 2, 3 o 4. Las filas se crean solas según el número de vistas.
- **Orden**: primero de izquierda a derecha y luego de arriba abajo.
- **Quitar del panel**: la vista deja de mostrarse, pero no se borra de **TENDENCIAS**.
- **Editar panel**: vuelve al formulario.
- **Abrir**: vuelve a construir todas las celdas y a consultar el histórico.

El panel abierto tiene una altura de celda fija para que la rejilla sea uniforme. En pantallas pequeñas
puede aparecer desplazamiento vertical. Usa menos columnas o menos vistas para que todo quepa.

## 8.3. Sincronizar el zoom

Con **Sincronizar zoom** activo, al ampliar un tramo en una gráfica de tiempo se amplía el mismo tramo en
las demás gráficas de tiempo del panel. La sincronización se aplica a:

- tendencias;
- cronogramas de estados.

No se aplica a X-Y, ciclos, histogramas, indicadores ni barras, porque no comparten un eje de tiempo
absoluto.

Puedes cambiar la sincronización en el formulario o con la casilla **Sincronizar zoom** de la barra del
panel abierto. El cambio se guarda con el panel.

## 8.4. Datos en vivo y datos históricos

- Las celdas en vivo se actualizan con el reloj, igual que una vista abierta.
- Las celdas del histórico se consultan al abrir el panel y no cambian hasta que pulses **Actualizar**.
- Las celdas de barras se consultan al abrir y también se actualizan con **Actualizar**.
- Las celdas de CSV se leen del fichero al abrir.

**Actualizar** vuelve a leer el histórico y las barras, y reconstruye las celdas. Si has cambiado un tag o
una vista, es la forma de verlo reflejado en el panel.

Si una vista falla, su celda muestra el error y las demás siguen funcionando. Un panel no queda inservible
porque una vista haya perdido su tag.

## 8.5. Exportar el panel

La barra del panel tiene:

- **PNG**: exporta el panel entero, con fondo blanco, títulos y celdas.
- **Copiar**: copia la imagen al portapapeles.

El PNG es útil para adjuntar una captura a un informe o a una incidencia. La imagen se genera con el tamaño
de la rejilla abierta; si quieres más detalle, amplía la ventana antes de exportar.

## 8.6. Paneles y perfiles de agente

Los paneles se guardan por agente. Un panel creado con el perfil `Planta demo` no aparece al conectar con
otro agente, aunque las vistas tengan los mismos nombres. Las vistas de CSV son la excepción: se pueden
usar en cualquier perfil porque no dependen de un agente.

Si conectas con otro agente y no ves tus paneles, comprueba el perfil seleccionado en la barra superior.

## 8.7. Ejemplos de paneles

### Pantalla de máquina

- **Tendencia**: velocidad y par.
- **Cronograma**: secuencia de la máquina.
- **Indicadores**: producción, presión y temperatura.
- **Barras**: producción por turno.

### Diagnóstico de un ciclo

- **Ciclos superpuestos**: fuerza y posición.
- **X-Y**: fuerza frente a posición.
- **Histograma**: distribución de la fuerza.
- **Cronograma**: estados del ciclo.

### Comparación de turnos

- **Barras** de producción por turno.
- **Tendencia** de la velocidad media del turno.
- **Histograma** de la presión del turno.
- **Indicadores** del valor actual.

## 8.8. Buenas prácticas

- No pongas más de 6 u 8 vistas en un panel: a partir de ahí las gráficas se hacen pequeñas.
- Para una pantalla fija, usa 2 columnas y vistas en vivo.
- Para un informe, usa 1 o 2 columnas y datos históricos, y exporta a PNG.
- Nombra el panel por su uso (`Línea 3 · turno`), no por el número.
- Si una vista se usa en varios paneles, edítala en **TENDENCIAS**; el cambio se verá en todos al volver a
  abrirlos.
- Borrar una vista la quita de los paneles, pero no borra los paneles.

## Siguiente paso

Para mostrar valores enteros como texto y asignarlos a los tags, sigue con
[Transformaciones](09-transformaciones.md).
