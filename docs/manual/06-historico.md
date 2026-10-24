# 6. Histórico

El histórico guarda muestras de los tags en el disco del colector para poder responder preguntas como «¿qué
presión había cuando falló?» o «¿cuánto ha subido el contador en el turno?». No es necesario que la app esté
abierta: el colector sigue historizando aunque cierres la ventana o se desconecte la red.

## 6.1. Activar la historización

La historización se configura por tag, en su detalle:

1. Haz doble clic en el tag.
2. Baja hasta **HISTÓRICO**.
3. Marca **Guardar este tag en el histórico**.
4. Ajusta la **Frecuencia mínima**, la **Banda muerta** y el **Tiempo máximo**.
5. Pulsa **Guardar**.

La primera vez que se historiza un tag, el colector empieza a guardar desde ese momento. No recupera valores
anteriores que no se hayan guardado.

### Frecuencia mínima

Es el tiempo mínimo entre dos muestras. `0` significa «guarda cada cambio». Si un tag cambia muy rápido,
una frecuencia de `1000` ms evita llenar el disco con miles de puntos por minuto.

### Banda muerta

Solo se guarda una muestra si el valor ha cambiado más que la banda. Puede ser:

- un valor absoluto, por ejemplo `0.5` para una presión en bar;
- un porcentaje del último valor guardado, por ejemplo `1` para un 1 %.

La banda muerta se aplica a valores numéricos. Un cambio de calidad se guarda siempre, aunque el valor no
cambie.

### Tiempo máximo

Si el valor no cambia, el colector guarda una muestra cada cierto tiempo. Sirve para demostrar que el tag
seguía comunicando aunque su valor fuera constante. `0` desactiva esta garantía.

### Disparo

El disparo permite guardar solo mientras otro tag cumple una condición. Por ejemplo, guardar la presión
solo mientras `Marcha` vale `true`, o la temperatura solo cuando `Estado` es mayor que `2`.

1. Marca **Solo mientras otro tag cumpla una condición (disparo)**.
2. Elige el **Tag de disparo**.
3. Si es booleano, elige **true** o **false**. Si no, elige la condición (`=`, `≠`, `>`, `≥`, `<`, `≤`) y
   escribe el **valor**.
4. Indica **Y después** cuántos milisegundos se sigue guardando tras dejar de cumplirse.
5. Guarda.

Al cumplirse la condición se guarda el valor actual y, mientras dure, se aplican la frecuencia, la banda
muerta y el tiempo máximo. Al terminar se guarda un último punto. Los cambios de calidad se guardan siempre.

### Historiador del colector

El historiador se puede desactivar globalmente en `agent.json` (`historian.enabled`). Si está desactivado,
la configuración de los tags se guarda pero no se registra nada. La ventana de detalle lo avisa.

## 6.2. Consultar el histórico

1. Selecciona el dispositivo.
2. Abre la pestaña **Histórico**.
3. Marca los tags que quieres ver. Solo aparecen los tags historizados.
4. Elige el **periodo**: últimos 15 min, última hora, últimas 8 h, 24 h, 7 días o 30 días.
5. Elige el agregado:
   - **En bruto**: cada muestra guardada;
   - **Media**, **Mínimo**, **Máximo**, **Primero** o **Último**: un valor por intervalo.
6. Si has elegido un agregado, elige el **intervalo**: 1 s, 10 s, 1 min, 5 min, 15 min, 1 h o 1 día.
7. Pulsa **Consultar**.

El resultado se muestra como gráfica y, si marcas **Ver tabla**, también como tabla.

![Consulta de histórico con gráfica](img/06-historico-grafica.png)

La gráfica tiene una serie por tag. Puedes:

- pasar el ratón para ver valores y horas;
- arrastrar para hacer zoom;
- usar **Ajustar** para quitar el zoom;
- activar **Un eje Y por tag** si las escalas son muy distintas;
- abrir **Estadísticas** para ver mínimo, máximo, media, desviación, primero, último, delta, muestras y
  flancos de subida de cada tag;
- exportar la gráfica a PNG o copiarla al portapapeles.

El resumen indica cuántos puntos se han recibido, de cuántos tags, el periodo y el agregado. Si aparece
**hay más puntos: usa un agregado o un periodo más corto**, la consulta se ha recortado. El colector limita
el resultado a 20 000 puntos por consulta.

### En bruto frente a agregado

- **En bruto** conserva los huecos y los cambios de calidad. Es lo mejor para ver un evento corto.
- **Agregado** reduce el número de puntos. La **Media** suaviza el ruido; **Mínimo** y **Máximo** resaltan
  picos; **Primero** y **Último** sirven para ver el estado al inicio o al final de cada intervalo.

Un intervalo de agregado más corto da más detalle, pero también más puntos. Si la consulta se recorta,
aumenta el intervalo o reduce el periodo.

## 6.3. La tabla

![Resultado del histórico en forma de tabla](img/06-historico-tabla.png)

La tabla muestra una fila por punto y tag, ordenada de más reciente a más antigua:

| Columna | Contenido |
|---|---|
| **Hora** | Hora local con milisegundos. |
| **Tag** | Nombre del tag. |
| **Valor** | Valor numérico o texto. En la tabla se aplican decimales y unidad del tag. |
| **Calidad** | `Good`, `Uncertain` o el error. |

La tabla es útil para localizar un valor exacto y para copiar datos. El CSV exportado conserva el valor
completo, no solo el redondeado que se ve en la tabla.

## 6.4. Exportar a CSV

1. Haz una consulta.
2. Pulsa **CSV**.
3. Elige dónde guardar el fichero.
4. La app confirma cuántas filas ha escrito.

El fichero tiene esta cabecera:

```text
device,tag,timestamp,value,quality,unit
```

- `device`: nombre del dispositivo.
- `tag`: nombre del tag.
- `timestamp`: hora UTC en formato ISO 8601, por ejemplo `2026-10-23T08:15:30.000Z`.
- `value`: valor completo. Los textos con comas o comillas se escapan según RFC 4180.
- `quality`: calidad del punto.
- `unit`: unidad configurada en el tag.

También se pueden leer CSV antiguos sin la columna `unit`. El separador es la coma y la codificación es
UTF-8 sin BOM.

El CSV se puede abrir en una hoja de cálculo o volver a cargar en **Tendencias > Datos > Fichero CSV
exportado**. Es una forma de conservar una captura de datos o de compartirla sin dar acceso al colector.

## 6.5. Abrir en Tendencias

El botón **Abrir en Tendencias** crea una vista con los tags marcados y el periodo elegido, leída del
histórico, y la abre. Desde allí puedes cambiar el tipo de gráfica:

- tendencia;
- ciclos superpuestos;
- X-Y;
- cronograma de estados;
- histograma con Cp/Cpk;
- barras por periodo.

La vista se guarda en la lista de tendencias. El capítulo [Tendencias y gráficas](07-tendencias.md) explica
cada tipo.

## 6.6. Borrar datos

1. Marca los tags.
2. Pulsa **Borrar datos…**.
3. Elige la fecha y hora de inicio y de fin. Los campos se rellenan con el periodo que estabas
   consultando.
4. Pulsa **Borrar** y confirma.

El borrado elimina las muestras de esos tags en ese intervalo. No se puede deshacer. Si el colector es
antiguo y no admite la operación, la app pedirá actualizarlo desde **Colectores > Instalar / actualizar**.

**Precaución**: antes de borrar, exporta el intervalo a CSV si puede ser necesario para una investigación.
El borrado no afecta a la configuración del tag ni a su historización futura.

## 6.7. Dónde se guarda

El colector guarda el histórico en SQLite, en la carpeta `history/` de su instalación. Crea un fichero por
mes:

```text
history-2026-10.db
history-2026-11.db
```

La retención se configura en `agent.json`:

```json
"historian": {
  "enabled": true,
  "path": "history",
  "retentionDays": 30,
  "flushIntervalMs": 5000
}
```

- `retentionDays`: días que se conservan los ficheros. `0` desactiva el borrado automático.
- `flushIntervalMs`: cada cuánto se vuelcan las muestras a disco.

Los detalles de configuración y copia de seguridad están en
[Configuración y mantenimiento](13-configuracion-y-mantenimiento.md).

## 6.8. Buenas prácticas

- Historiza solo los tags que necesites. Cada tag historizado consume disco y CPU.
- Usa una frecuencia mínima y una banda muerta coherentes con la dinámica de la señal.
- Para un evento concreto, activa el disparo en lugar de guardar todo el día.
- Revisa la retención antes de necesitar datos antiguos. Si la política es de 30 días, no podrás consultar
  un fallo de hace tres meses.
- Exporta a CSV los periodos que debas conservar fuera del colector.
- Sincroniza el reloj del colector y del PLC. Una hora incorrecta hace que los datos aparezcan en el periodo
  equivocado.

## Siguiente paso

Para convertir estos datos en gráficas guardadas y paneles, sigue con
[Tendencias y gráficas](07-tendencias.md).
