# 9. Transformaciones

Una **transformación** convierte un valor entero en un texto. Por ejemplo:

```text
0 = Parado
1 = Marcha
2 = Avería
```

La transformación se guarda en el colector y se asigna a un tag. El valor que se lee, se historiza y se
publica por OPC UA sigue siendo el número; lo que cambia es cómo se muestra en la app.

![Editor de transformaciones con una tabla de estados](img/09-transformaciones.png)

## 9.1. Crear una transformación

1. Abre **Transformaciones**.
2. Pulsa **+** en **TRANSFORMACIONES**.
3. Escribe el **Nombre**, por ejemplo `Estado del motor`.
4. En la tabla **VALOR / SE MUESTRA COMO**, escribe el número y su texto.
5. Pulsa **Añadir valor** para crear más filas.
6. Pulsa **Guardar en el colector**.

La app crea una transformación nueva con los valores `0` y `1` para que puedas empezar. Los cambios no se
aplican hasta que pulsas **Guardar en el colector**. Si cambias de idea, pulsa **Descartar**.

### Reglas de validación

- El nombre no puede estar vacío ni repetirse con otra transformación, sin distinguir mayúsculas.
- El valor debe ser un número entero. Se admiten negativos.
- No puede haber dos filas con el mismo valor en una transformación.
- Las filas sin texto no se guardan. Sirven para dejar un hueco mientras editas.
- Los valores se ordenan de menor a mayor al guardar.

Si algo no cumple, la app muestra el error y no envía los cambios al colector.

## 9.2. Asignar una transformación a un tag

1. Haz doble clic en el tag.
2. En **Mostrar valor como**, elige la transformación.
3. Pulsa **Guardar**.

La opción **Valor sin transformar** quita la transformación. En la tabla, el tag muestra el texto y el
valor entre paréntesis:

```text
Marcha (1)
```

Si el valor no está en la transformación, se muestra el número tal cual. Esto permite añadir estados nuevos
sin romper los tags existentes.

La transformación solo se aplica a valores enteros. Un `REAL` o un `STRING` no se transforman. Los
booleanos cuentan como `0` (`false`) y `1` (`true`), así que puedes usar una transformación para mostrarlos
como `Parado` y `Marcha`, o dejar que la app los muestre como `false` y `true`.

## 9.3. Transformaciones en uso

Cada transformación muestra cuántos tags la usan:

- **Sin usar** si no la usa ninguno.
- **1 tag** o **N tags**, con el nombre de los tags.

No puedes borrar una transformación en uso. La app lo impide y te dice qué tags la usan. Para borrarla:

1. Abre cada tag que la use.
2. Cambia **Mostrar valor como** a **Valor sin transformar** o a otra transformación.
3. Guarda los tags.
4. Vuelve a **Transformaciones** y bórrala.

Tampoco puedes renombrar una transformación en uso sin que la app actualice los tags. Al guardar un cambio
de nombre, los tags que la usaban pasan al nombre nuevo. Si el nombre nuevo choca con otra transformación,
la app pide guardar los cambios por separado.

## 9.4. Editar y borrar

- **Selecciona** una transformación en la lista para editarla.
- **Añadir valor** crea una fila con el siguiente número libre.
- El icono de papelera de una fila quita ese valor.
- La papelera de la cabecera borra la transformación completa, si no está en uso.
- **Descartar** recarga lo guardado y pierde los cambios sin guardar.

Los cambios se envían todos juntos. Si otro equipo modifica las transformaciones mientras tienes cambios
sin guardar, la app no recarga la lista para no pisar tu edición. Al guardar, el colector valida el conjunto.

## 9.5. Dónde se aplica

La transformación afecta a:

- la columna **Valor** de la tabla de tags;
- el **valor actual** de la ventana de detalle;
- la etiqueta que se muestra al pasar el ratón por algunos elementos de la app.

No cambia:

- el valor numérico que se envía al PLC al escribir;
- el valor guardado en el histórico;
- el valor publicado por OPC UA;
- los cálculos de las gráficas, que usan el número.

Por eso, si necesitas que un sistema externo reciba el texto del estado, la transformación no basta: el
sistema externo debe interpretar el número o recibir otro tag de texto.

## 9.6. Ejemplos

### Estado de una máquina

```text
0 = Parado
1 = Marcha
2 = Avería
3 = Emergencia
```

### Paso de secuencia

```text
10 = Inicio
20 = Carga
30 = Prensa
40 = Descarga
50 = Fin
```

### Válvula

```text
0 = Cerrada
1 = Abierta
```

### Bool como texto

```text
0 = No
1 = Sí
```

## 9.7. Buenas prácticas

- Usa nombres estables y descriptivos: `Estado del motor` es mejor que `Transformación 1`.
- No reutilices el mismo nombre para estados de máquinas distintas.
- Mantén los textos cortos: se muestran en una tabla y en ventanas estrechas.
- Si un estado nuevo aparece en el PLC, añádelo a la transformación; los valores no listados se muestran
  como número y no se pierde nada.
- Documenta el significado de los valores en el nombre o en el texto, no solo en un comentario externo.
- Antes de borrar una transformación, comprueba los tags que la usan en el panel de la derecha.

## Siguiente paso

Para publicar los tags a otros sistemas, sigue con [OPC UA](10-opc-ua.md).
