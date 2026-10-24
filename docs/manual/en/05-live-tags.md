# 5. Live tags

A **tag** is a PLC variable with a name, an address, a type and a scan group. On the
**Tags** tab you see its last value, quality, time and, if allowed, you can write to it.

![List of tags of a connected device](../img/05-tags.png)

## 5.1. The tag table

Each row shows:

| Column | What it means |
|---|---|
| **Nombre** (Name) | Tag name. Below it, the scan group and its interval. |
| **Dirección** (Address) | Address as understood by the driver. |
| **Valor** (Value) | Last value, with the configured transformation, decimals and unit. |
| **Tipo** (Type) | Type detected by the collector: `Bool`, `Int16`, `Real`, `String`... |
| **Calidad** (Quality) | `Good`, `Uncertain` or the error (`Bad...`). |
| **Hora** (Time) | Local time of the last update. |
| **Escritura** (Writing) | Field or buttons for writing, only if the tag is writable. |

The header icons allow sorting by name, address, value or quality. You can drag a column
header to change the order and drag tags to reorder them or move them to a group.

Tags with the same **Grupo en la tabla** (Group in table) are collapsed under a header. To create one, open the tag
details and enter a name in **Grupo en la tabla**. Right-clicking a group allows you to rename it or
undo it.

## 5.2. Table statuses

Above the table, counters appear:

- **en buen estado** (healthy): tags with `Good` quality.
- **con error** (with error): active tags with bad quality. If there is any, the counter is shown in red.
- **desactivados** (deactivated): tags that are not read, not recorded to history and not published.

A disabled tag shows `—` as its value and `Desactivado` (Disabled) as its quality. It is not a failure: it is set aside on
purpose.

## 5.3. Add a tag by address

1. Select the device.
2. In **Tags**, click **Añadir tag** (Add tag).
3. Enter the **Dirección** (Address).
4. Enter a **Nombre** (Name). If you leave it empty, the address is used.
5. Specify the **Grupo** (Group) and the interval in milliseconds. If the group does not exist, it is created.
6. Check **Escribible** (Writable) if you want to enable the second write key.
7. Click **Probar** (Test) to read the address without adding it.
8. Click **Añadir** (Add).

![Adding a tag by address](../img/05-alta-tag.png)

**Probar** shows the value, the type and the quality, or the error. It is useful to distinguish between an
incorrect address, a disconnected PLC and a permissions problem. The test does not create the tag or record it to history.

If the driver is **Modbus**, the form changes and offers drop-downs to compose the address:

![Adding a Modbus tag with the table, type and byte order drop-downs](../img/05-alta-tag-modbus.png)

1. Choose the **tabla** (table): HR (holding registers), IR (input registers), C (coils) or DI (discrete
   inputs).
2. Enter the **número** (number) of the register or bit.
3. Choose the **tipo** (type): INT16, UINT16, INT32, UINT32, FLOAT, DOUBLE, INT64, UINT64, BIT or STRING.
4. If the type occupies several registers, specify the **orden de bytes** (byte order) or leave **Orden del dispositivo** (Device order).
5. For **STRING**, specify how many characters.
6. For **BIT**, specify the bit within the register.
7. If you want to create several in a row, change **cantidad** (quantity). Up to 500 tags are created, each at the
   next address.
8. Click **Probar** or **Añadir**.

The composite address appears in the text field and can be edited by hand. If you edit it, the
range preview is lost.

### Supported addresses

| Driver | Examples |
|---|---|
| S7 symbolic | `"MiDB".Motor.Velocidad`, `"Datos".Lista[3]` |
| S7 absolute | `DB1.DBX0.3`, `DB1.DBW10`, `DB1.DINT12`, `DB1.REAL20`, `DB5.STRING30[20]`, `M10.1`, `MW20`, `IW64`, `QX4.0` |
| Modbus | `HR0`, `HR100:FLOAT`, `HR100:FLOAT:CDAB`, `IR5:UINT16`, `C0`, `DI3`, `HR10.3`, `HR20:STRING[10]`, `40001`, `30005:INT32` |

In S7 absolute, data blocks must be **non-optimized**. In S7 symbolic, the name must exist
in the program and be accessible from HMI.

## 5.4. Browse symbols (S7 symbolic)

In the `s7-symbolic` driver you can search for variables without knowing their address:

1. Select the device.
2. Open the **Explorar** (Browse) tab.
3. Type part of the name in **Buscar** (Search) or leave the field empty to see all of them.
4. Adjust the **máximo de resultados** (maximum results) if the CPU has many symbols.
5. Click **Buscar** (Search).
6. Choose the **Grupo** and the interval.
7. Check **Escribible** if applicable.
8. Click **Añadir** in the symbol row.

![Symbol browser of an S7 CPU](../img/05-explorar.png)

The table shows the **símbolo** (symbol), the **tipo PLC** (PLC type), the converted **tipo** (type) and an **Añadir** button. If the
symbol is already a tag, **Añadido** (Added) appears. **Refresh** re-reads the list after loading a new program
into the CPU.

The browser only exists in S7 symbolic. In S7 absolute and Modbus you must type the address.

## 5.5. Tag details

Double-click a tag to open its details.

![Tag details: value, mini trend and configuration](../img/05-detalle-tag.png)

The top part shows:

- the name, the address and the device;
- the **valor actual** (current value) with the unit and, if there is scaling, the raw value in parentheses;
- the quality, the type and the time;
- a **mini tendencia de los últimos 5 minutos** (mini trend for the last 5 minutes) with the values the app has received.

In **Configuración** (Configuration) you can change:

| Field | Effect |
|---|---|
| **Nombre** | Visible name and tag key in history. |
| **Dirección** | Address in the driver. It is validated on save. |
| **Grupo de escaneo** (Scan group) | Group and period. Changing the interval affects all tags in the group. |
| **Escribible (2.ª llave)** | Enables writing to this tag. It requires the device's first key. |
| **Tipo para escribir** (Type for writing) | Type used when converting the text you write. **Automático** (Automatic) uses the read type. |
| **Grupo en la tabla** | Visual folder to collapse several tags. |
| **Mostrar valor como** (Show value as) | Integer-to-text transformation. |
| **Unidad** (Unit) | Text added to the value (`bar`, `°C`, `rpm`...). |
| **Decimales** (Decimals) | Decimals for reals. **Todos** (All) shows the full value. |
| **Escalado** (Scaling) | Converts a raw value to engineering units. |
| **Publicar en OPC UA (2.ª llave)** | Publishes this tag if the device is also published. |
| **Activo** | If unchecked, the tag stops being read. |

### Scaling

Scaling is applied in the collector before recording to history and publishing. It is used to convert, for example,
`0..27648` into `0..100 %` or `0..10000` into `0..10 bar`.

1. Check **Convertir el valor bruto a unidades de ingeniería** (Convert the raw value to engineering units).
2. Enter the **Bruto de** (Raw from) and **a** (to).
3. Enter the **Ingeniería de** (Engineering from) and **a** (to).
4. Check **Limitar al rango de ingeniería** (Clamp to the engineering range) if you want to clip values outside the range.
5. Review the preview.

The app shows the converted value and, in the details, the raw one in parentheses. Trends and
history use the converted value.

### Tag history

In the **HISTÓRICO** (HISTORY) section:

- **Guardar este tag en el histórico** (Save this tag to history): enables sample collection.
- **Frecuencia mínima** (Minimum interval): milliseconds between samples. `0` saves every change.
- **Banda muerta** (Deadband): margin the value must change by to save another sample. It can be an absolute value
  or a percentage of the last value.
- **Tiempo máximo** (Maximum time): saves a sample even if the value does not change, to record that the tag is still
  alive. `0` disables this guarantee.
- **Solo mientras otro tag cumpla una condición (disparo)** (Only while another tag meets a condition (trigger)): records to history only during the periods in which the
  condition is met, plus an extra time.

If the collector historian is disabled, the configuration is saved but nothing is recorded. The app
warns about this in the window.

## 5.6. Writing to the PLC

To write, you need:

1. **Escrituras habilitadas (1.ª llave)** on the device.
2. **Escribible (2.ª llave)** on the tag.
3. A full access token. The read-only token cannot write.
4. The tag must be active and have good or uncertain quality.

In the table:

- Numeric tags show a **nuevo valor** (new value) field and a send button.
- Booleans show two buttons, **false** and **true**. The current value appears lit; the other one
  writes the change.

Before writing, the app asks for confirmation and shows the current value. If the write fails, the message
indicates the quality or the error returned by the collector. Every write is recorded in the collector
audit.

**Caution**: a write can move a machine. Check the address, the type and the value before
confirming. Do not write to safety tags, interlocks or critical setpoints without the
plant procedure.

## 5.7. Activate, deactivate and remove

- Right-click a tag > **Editar…** (Edit…) opens its details.
- **Abrir su histórico** (Open its history) jumps to the Histórico tab with that tag selected.
- **Activar tag** (Activate tag) or **Desactivar tag** (Deactivate tag) changes the state without deleting the configuration.
- **Quitar tag…** (Remove tag…) removes the tag from the device. History already saved is not deleted.
- With several tags selected (Ctrl or Shift), the actions apply to the set.

**Desactivar** is the recommended option to set a tag aside temporarily. **Quitar** is definitive in the
configuration, although you can create it again with the same address.

## 5.8. Good names and good groups

- Use stable names: history and OPC UA use them as a key. Changing the name of a tag with recorded history
  can separate the old series from the new ones.
- Avoid `/` in the tag name: it is used as a separator in history and in OPC UA.
- Group tags by machine or by function with **Grupo en la tabla**. The list becomes easier to read.
- Do not put all tags in the same scan group if they have different needs. A 100 ms group
  for everything generates unnecessary traffic.
- For tags that only change occasionally, raise the interval and use **Tiempo máximo** if you want a periodic
  record.

## Next step

To save and query values over time, continue with [History](06-history.md).
