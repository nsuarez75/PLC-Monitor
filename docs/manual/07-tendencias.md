# 7. Tendencias y gráficas

Una **vista de tendencia** es una gráfica guardada: tiene un nombre, uno o varios tags, un tipo de gráfica
y una fuente de datos. Se crea una vez y se abre cuando hace falta. No hace falta seleccionar un
dispositivo para abrir una vista.

![Editor de una vista de tendencia](img/07-tendencias-editor.png)

## 7.1. Crear una vista

1. Abre **Tendencias**.
2. Pulsa **+** en **TENDENCIAS**.
3. Escribe un **Nombre**.
4. Elige el **Tipo de gráfica**.
5. Elige los **Datos**: en vivo, histórico del agente o fichero CSV.
6. Ajusta las opciones del tipo: ventana, periodo, límites, barras, etc.
7. Añade tags desde la lista de la derecha. Puedes buscar por dispositivo o tag y usar **Añadir todos**.
8. Configura el **Disparo** si quieres ver solo ciertos tramos.
9. Ajusta el **Eje Y** o marca **Un eje Y por tag**.
10. Pulsa **Guardar** para conservar la vista o **Abrir** para verla.

La lista de la izquierda tiene dos secciones:

- **PANELES**: rejillas con varias vistas. Se explican en [Paneles](08-paneles.md).
- **TENDENCIAS**: vistas guardadas de este agente y vistas de CSV, que se pueden abrir sin conexión.

Las vistas se guardan en `trends.json`, en la carpeta de datos de la app. Los paneles se guardan en
`panels.json`, junto a ese fichero.

## 7.2. Fuentes de datos

| Fuente | Qué muestra | Requisitos |
|---|---|---|
| **En vivo (lo que llega, última hora)** | Los valores que la app ha recibido desde que está abierta. | Agente conectado. |
| **Histórico del agente** | Los valores guardados por el agente en el periodo elegido. | Tags historizados. |
| **Fichero CSV exportado** | Los valores de un CSV del histórico. | Ninguno; se puede ver sin conexión. |

La fuente **En vivo** no consulta el histórico. Si abres la app y creas la vista, empiezas a ver datos desde
ese momento. La app guarda hasta una hora de valores por tag en memoria, así que una vista abierta después
de un rato puede mostrar los últimos minutos, pero no lo anterior a abrir la app.

La fuente **Histórico** consulta el agente al abrir la vista. La ventana es fija: los datos no cambian
mientras la miras. El botón **Actualizar** vuelve a consultar. Si un tag no se historiza, aparece en el
aviso de tags que faltan.

La fuente **CSV** lee un fichero exportado desde la pestaña Histórico. Sus tags son los del fichero, no los
del agente. Los tags de texto se ignoran; las calidades malas dejan un hueco.

## 7.3. La gráfica en vivo

![Tendencia de velocidad y fuerza con dos ejes](img/07-tendencia.png)

Al abrir una vista en vivo:

- La ventana avanza con el reloj. Puedes elegir **1 min**, **5 min**, **15 min** o **1 h**.
- **Pausa** congela la vista; **Seguir** vuelve a la ventana actual.
- Al hacer zoom, la vista se pausa automáticamente. **Ajustar** quita el zoom y continúa.
- La leyenda permite marcar y desmarcar tags.
- **Un eje Y por tag** da a cada tag su propia escala. Es útil cuando comparas una velocidad en rpm con una
  fuerza en kN.
- El botón de **cursores** muestra dos cursores, A y B, para medir el tiempo entre dos instantes y cuánto
  cambia cada tag.
- **Estadísticas** muestra mínimo, máximo, media ponderada en el tiempo, desviación típica, primero,
  último, delta, número de muestras y flancos de subida de cada tag en la ventana visible. Se pueden
  copiar o guardar en CSV.
- **PNG** exporta la gráfica con título, periodo y leyenda sobre fondo blanco. El botón de copiar la lleva
  al portapapeles.

La cabecera muestra el periodo del histórico o la ventana en vivo, cuántos puntos hay y cuándo se consultó.
Si el agente ha recortado la respuesta, lo indica.

## 7.4. Tipos de gráfica

### Tendencia

Es la gráfica de valor frente al tiempo. Cada tag se dibuja como una línea o escalón. Los booleanos se
dibujan como escalones entre 0 y 1.

Úsala para:

- ver la evolución de una señal;
- comparar varias señales;
- localizar un pico o un hueco;
- medir tiempos con los cursores.

### Ciclos superpuestos

![Ciclos superpuestos de una prensa](img/07-ciclos.png)

Cada vez que se cumple el disparo se dibuja un ciclo que empieza en 0. Los ciclos se superponen como la
persistencia de un osciloscopio: el último se resalta y los anteriores quedan tenues.

- Necesita un disparo. Sin él, la app no deja abrir la vista.
- Se dibujan como máximo los 20 ciclos más recientes.
- El eje X es el tiempo desde el disparo, en segundos.
- **Fijar último ciclo como referencia** guarda el último ciclo de cada tag y dibuja una banda de
  tolerancia. Los ciclos que se salen de la banda se ven en rojo.
- La **Tolerancia ±** puede ser un valor del tag o un porcentaje del rango de la referencia.

Sirve para comparar ciclos de máquina y detectar el ciclo que se sale de lo normal.

### X-Y

![Gráfica X-Y de fuerza frente a posición](img/07-xy.png)

Dibuja un tag frente a otro en lugar de frente al tiempo. El **primer tag de la vista es el eje X** y los
demás son el eje Y. Con el botón **Eje X** puedes mover un tag a la primera posición.

Necesita al menos dos tags. Ejemplos: fuerza contra posición en una prensa, presión contra caudal, par
contra velocidad.

Con disparo, se dibuja una curva por ciclo. Sin disparo, se dibuja la curva completa de la ventana.

### Cronograma de estados

![Cronograma de estados](img/07-cronograma.png)

Muestra una franja por tag, con un color por estado. Al pasar el ratón se ve la duración de cada tramo y el
porcentaje de tiempo en cada estado.

Está pensado para:

- booleanos (marcha/paro, abierto/cerrado, alarma/no alarma);
- enteros de estado o paso de secuencia.

Un valor que no está en la transformación se muestra con su número. Si el tag tiene una transformación, el
cronograma usa los textos.

### Histograma con Cp/Cpk

![Histograma de presión con media, ±3σ y tolerancias](img/07-histograma.png)

Muestra cómo se reparten los valores de la ventana:

- barras por clase;
- media y desviación típica;
- mínimo y máximo;
- líneas de ±3σ;
- tolerancias inferior y superior (LIE/LSE).

Si indicas una o las dos tolerancias, calcula la capacidad del proceso:

- **Cp** = (LSE − LIE) / (6σ), con las dos tolerancias;
- **Cpk** = la menor distancia de la media a una tolerancia / (3σ).

Puedes usar datos en vivo o del histórico. Con disparo, el histograma usa solo los valores de los ciclos.

### Indicadores de panel

![Indicadores con aguja, barra y valor](img/07-indicadores.png)

Muestra el valor actual de cada tag en grande, como un panel de operador. Hay tres estilos:

- **Aguja**;
- **Barra de nivel**;
- **Solo el valor**.

Si configuras **Límites** inferior y superior, el indicador se pone en rojo cuando el valor sale de ellos.
Los booleanos se muestran como una luz. Los indicadores solo funcionan en vivo: representan el valor actual,
no un periodo histórico.

### Barras por periodo

![Barras de producción por turno](img/07-barras.png)

Agrupa los datos del histórico por **Hora**, **Turno (8 h)** o **Día**. Para cada periodo y tag dibuja un
valor:

- **Máximo**;
- **Media**;
- **Mínimo**;
- **Incremento (contador)**: la diferencia entre el último y el primer valor del periodo, útil para
  contadores de producción.

Elige el rango: **Hoy y ayer**, **Últimos 7 días** o **Últimos 30 días**. Si usas turnos, indica la hora a
la que empieza el primer turno; los otros dos empiezan 8 y 16 horas después.

Las barras no son en vivo: se consultan al abrir la vista y se actualizan con **Actualizar**. Los tags
deben estar historizados; si alguno no lo está, la app lo avisa y dibuja los demás.

## 7.5. Disparo

El disparo limita lo que se ve a los tramos en que otro tag cumple una condición.

1. Marca **Mostrar solo mientras un tag cumpla una condición**.
2. Elige el **Tag de disparo**. Puede ser cualquiera del agente, esté o no en la vista.
3. Si es booleano, elige **true** o **false**. Si no, elige la condición y escribe el **valor**.
4. Indica **Antes del disparo**: segundos que se muestran antes de que se cumpla.
5. Indica **Después**: segundos que se muestran tras dejar de cumplirse.

Fuera de esos tramos la gráfica queda en blanco. En vivo, la cabecera indica **Activo** o **Esperando**.
El disparo se calcula con lo recibido en vivo o con el histórico consultado.

Los ciclos superpuestos necesitan disparo. El histograma con disparo usa solo los valores de los tramos
activos. Las barras y los indicadores no admiten disparo.

## 7.6. Eje Y y límites

- **Automático**: el eje se ajusta a los datos.
- **mín / máx**: fija la escala. Los valores fuera se recortan visualmente.
- **Un eje Y por tag**: cada tag tiene su escala y su color. En el histograma el rótulo cambia a **Eje de
  valores**; en los indicadores, a **Escala**.

Para el histograma, **Tolerancias** define LIE y LSE. Para los indicadores, **Límites** define cuándo se
pone en rojo el valor.

## 7.7. Guardar, abrir y borrar

- **Guardar** conserva los cambios en `trends.json`. No cierra la gráfica abierta.
- **Abrir** guarda y muestra la gráfica.
- **Borrar** elimina la vista. Si la vista está en un panel, sale del panel; el panel no se borra.
- **Editar vista** vuelve del modo gráfico al formulario.

Si cambias el nombre de un tag o lo quitas del agente, la vista conserva la referencia. Al abrirla, la app
avisa de que el tag no está disponible y dibuja los demás. Puedes editarla para sustituirlo.

## 7.8. Vistas de CSV

1. Elige **Fichero CSV exportado** como fuente.
2. Pulsa **Elegir CSV…**.
3. Selecciona un fichero exportado desde el histórico.
4. La lista de tags se llena con los tags del fichero.
5. Elige los que quieras y abre la vista.

Las vistas de CSV se pueden abrir sin estar conectado al agente. Se guardan en `trends.json` y aparecen en
la lista de todos los perfiles, porque no dependen de un agente concreto. La fuente CSV no está disponible
para indicadores.

## 7.9. Buenas prácticas

- Pon nombres que describan la máquina y la señal: `Prensa 3 · fuerza y posición` es mejor que `Tendencia 4`.
- Usa **En vivo** para diagnosticar ahora y **Histórico** para investigar lo que ya pasó.
- Para comparar magnitudes con unidades distintas, activa **Un eje Y por tag**.
- No abuses de tags en una misma vista: la leyenda y las escalas se vuelven difíciles de leer.
- Si una consulta histórica tarda o se recorta, reduce el periodo o usa un tipo con agregado.
- Guarda una copia de `trends.json` si has creado muchas vistas y vas a cambiar de equipo.

## Siguiente paso

Para ver varias gráficas a la vez, sigue con [Paneles](08-paneles.md).
