# PLC-Monitor

Aplicación de escritorio y colector para supervisar y diagnosticar PLCs: lee variables por S7 y
Modbus, las muestra en vivo, guarda su histórico, las publica por OPC UA y permite escribir
consignas con permisos. Entre paréntesis, el término de la interfaz en inglés.

## Piezas

**Colector** (*collector*):
Servicio que se instala en un equipo cercano a los PLC, los lee, guarda el histórico y atiende a
la app. Sigue funcionando aunque la app esté cerrada.
_Evitar_: agente (se confunde con IA; solo sobrevive en nombres técnicos como `plcmon-agent`),
servidor, pasarela

**App** (*app*):
La aplicación de escritorio desde la que se configura un **colector** y se ven sus datos.
_Evitar_: cliente, programa

**Perfil de colector**:
Lo que la app guarda para conectarse a un **colector**: nombre, dirección, **token**,
certificado y datos de SSH.

**Token**:
Credencial con la que la app o la CLI se identifican ante el **colector**. El **token de solo
lectura** nunca puede escribir.
_Evitar_: contraseña, clave

**Auditoría**:
Registro en el colector de cada **escritura** y de cada cambio de configuración, con quién lo hizo
y desde dónde.

## Dispositivos y tags

**Dispositivo** (*device*):
Un PLC, variador o pasarela configurado en un **colector**, con su **driver** y sus ajustes de
conexión.
_Evitar_: equipo, PLC (cuando se habla de la configuración)

**Driver**:
La familia de protocolo de un dispositivo: **S7 simbólico**, **S7 absoluto** o **Modbus**.
_Evitar_: protocolo, conector

**S7 simbólico**:
Driver que accede a los PLC Siemens S7-1200/1500 por el nombre de sus variables, con TLS y
contraseña si el PLC los pide.

**S7 absoluto**:
Driver que accede a los PLC Siemens por dirección de memoria (DB, entradas, salidas, marcas);
necesita PUT/GET habilitado en el PLC.

**Tag**:
Una variable concreta de un **dispositivo**, con su dirección, su tipo y su **grupo de escaneo**.
_Evitar_: variable, señal, punto

**Grupo de escaneo** (*scan group*):
Conjunto de tags de un dispositivo que se leen juntos cada cierto intervalo.

**Calidad** (*quality*):
Lo fiable que es el último valor de un tag: buena (*Good*), dudosa (*Uncertain*) o mala (*Bad*).

**Escalado** (*scaling*):
Conversión lineal del valor en bruto del PLC (por ejemplo, de 0–27648) a unidades de ingeniería.

**Transformación** (*transform*):
Traducción de los valores enteros de un tag a textos, por ejemplo `0 = Parado`, `1 = Marcha`.

## Escrituras

**Consigna** (*setpoint*):
Valor que se escribe en el PLC desde la app, la CLI u OPC UA.

**Escritura**:
Envío de un valor a un tag del PLC. Solo llega si están abiertas todas las **llaves de
escritura** que le tocan.

**Llave de escritura** (*key*):
Cada permiso que tiene que estar activo para que una escritura llegue al PLC: **escrituras
habilitadas** en el dispositivo, **escribible** en el tag y, por OPC UA, el permiso del servidor y
del usuario.
_Evitar_: permiso (suelto), candado

**Escrituras habilitadas** (*writes enabled*):
Primera llave: el dispositivo admite escrituras.

**Escribible** (*writable*):
Segunda llave: el tag admite escrituras.

## Histórico

**Historización**:
Guardar en el colector las **muestras** de un tag para consultarlas después. Se activa tag a tag.

**Historiador** (*historian*):
La parte del colector que guarda las muestras; se puede apagar entera.

**Muestra**:
Un valor de un tag con su hora y su **calidad**, tal como quedó guardado.
_Evitar_: punto, registro

**Frecuencia mínima** (*minimum rate*):
Tiempo mínimo entre dos muestras guardadas de un tag.

**Banda muerta** (*deadband*):
Cambio mínimo, absoluto o en porcentaje, que tiene que tener un valor para que se guarde otra
muestra.

**Tiempo máximo** (*maximum time*):
Si un valor no cambia, cada cuánto se guarda igualmente una muestra para demostrar que el tag
sigue comunicando.

**Disparo** (*trigger*):
Condición sobre otro tag que limita cuándo se guarda un tag o qué tramos muestra una gráfica.

**En bruto** (*raw*) / **Agregado**:
Consulta del histórico con todas las muestras, o resumida por intervalos (media, mínimo, máximo,
primero, último).

## Gráficas

**Vista de tendencia**:
Gráfica guardada con sus tags, su tipo, su ventana de tiempo y su **fuente de datos**.
_Evitar_: tendencia (a secas, es uno de los tipos), gráfico

**Fuente de datos**:
De dónde saca los valores una vista de tendencia: **en vivo**, del **histórico del colector** o
de un **fichero CSV** exportado.

**Tipo de gráfica**:
Cómo dibuja una vista sus datos: tendencia, ciclos superpuestos, X-Y, cronograma de estados,
histograma con Cp/Cpk, indicadores de panel o barras por periodo.

**Ciclos superpuestos** (*overlaid cycles*):
Tipo de gráfica que dibuja uno encima de otro los tramos marcados por un **disparo**, para
comparar ciclos de máquina.

**Panel**:
Rejilla con varias **vistas de tendencia** guardadas, para verlas a la vez.
_Evitar_: dashboard, pantalla

## OPC UA

**Servidor OPC UA**:
El que lleva el colector para que otros programas (Ignition, UaExpert…) lean y, si se permite,
escriban sus tags.

**Publicar en OPC UA** (*publish over OPC UA*):
Las dos llaves de publicación, una en el dispositivo y otra en el tag: un tag solo aparece en
OPC UA si las dos están activas.

**Usuario de OPC UA**:
Cuenta con contraseña para conectarse al servidor OPC UA, de lectura o de escritura.
