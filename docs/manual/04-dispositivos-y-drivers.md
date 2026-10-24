# 4. Dispositivos y drivers

Un **dispositivo** representa un PLC, variador o pasarela dentro del colector. Cada dispositivo tiene un
driver, unos ajustes de conexión y sus propios tags. Los tags de un dispositivo comparten la conexión y se
organizan en grupos de escaneo.

## 4.1. Crear un dispositivo

1. Conecta con el colector.
2. Abre **Dispositivos**.
3. Pulsa **+** en la lista **DISPOSITIVOS**.
4. Escribe un **Nombre**. No puede contener `/`, porque ese carácter separa el dispositivo del tag en el
   histórico y en OPC UA. Ejemplos: `prensa-linea3`, `horno-linea3`, `banco-demo`.
5. Elige el **Driver**. Al cambiar de driver se cargan sus ajustes por defecto.
6. Rellena los ajustes del driver.
7. Revisa las **Opciones**:
   - **Activo**: si se desmarca, el dispositivo queda configurado pero no se conecta.
   - **Escrituras habilitadas (1.ª llave)**: permite escribir en sus tags. Sin esta llave, ningún tag del
     dispositivo se puede escribir.
   - **Publicar en OPC UA (1.ª llave)**: publica el dispositivo en el servidor OPC UA. Los tags necesitan
     además su segunda llave.
8. Ajusta la **Reconexión** si el PLC tarda en arrancar o la red es inestable.
9. Pulsa **Guardar**.

El dispositivo aparece en la lista. El estado puede ser **Conectado**, **Conectando**, **En fallo** o
**Desconectado**. En la cabecera se muestran el driver, la hora desde la que está en ese estado y el detalle
del fallo.

## 4.2. Opciones del dispositivo

### Activo

Un dispositivo desactivado no se conecta, no lee tags, no historiza y no publica en OPC UA. Es útil para
preparar una máquina que todavía no está en red o para apartar un PLC averiado sin borrar su configuración.

### Escrituras habilitadas

Es la primera de las tres llaves de escritura. La segunda está en el tag (**Escribible**) y la tercera, si
la escritura llega por OPC UA, en el servidor (**Permitir escribir en los PLC por OPC UA**) y en el usuario
de OPC UA. Las tres deben estar activas. Las escrituras quedan en la auditoría del colector.

### Publicar en OPC UA

Es la primera llave de publicación. Si está desactivada, el dispositivo no aparece en el servidor OPC UA
aunque sus tags tengan la segunda llave. Al activarla, los tags con **Publicar en OPC UA** aparecen en el
árbol del servidor.

### Reconexión

El colector reintenta la conexión con una espera exponencial:

- **espera inicial**: tiempo antes del primer reintento;
- **espera máxima**: límite del crecimiento;
- **factor**: multiplicador de cada reintento.

Los valores por defecto son `1000 ms`, `30000 ms` y `2`. Si el PLC arranca muy despacio, sube la espera
máxima; si la red es rápida y quieres recuperar antes, baja la inicial.

## 4.3. Driver S7 simbólico

El driver `s7-symbolic` usa S7CommPlus para leer variables por nombre. Es la opción recomendada en
S7-1200 y S7-1500 cuando el programa tiene símbolos accesibles.

![Editor de un dispositivo S7 simbólico](img/04-dispositivo-s7-simbolico.png)

| Campo | Qué indica |
|---|---|
| **PLC** | IP o nombre del PLC. |
| **Contraseña** | Contraseña del nivel de acceso de la CPU. Si la CPU no está protegida, déjala vacía. |
| **Usuario** | Solo para CPUs con gestión de usuarios (firmware 3.1 o posterior). Si no, se usa la legitimación clásica por contraseña. |
| **Seguridad** | **Automática (TLS si la CPU lo admite)**, **Solo TLS** o **Sin TLS (TIA Portal V16 o anterior)**. |
| **Timeout** | Tiempo máximo de una operación, en milisegundos. |

Notas:

- La contraseña guardada se muestra como `********`. Si dejas ese valor, se conserva la que ya había. Si
  escribes otra, se reemplaza.
- **Automática** intenta TLS y, si la CPU lo rechaza, usa autenticación clásica. Es lo habitual.
- **Solo TLS** exige una CPU con comunicación PG/HMI segura. Si la CPU no la admite, la conexión falla.
- **Sin TLS** es para proyectos antiguos o CPUs con la comunicación segura desactivada. No lo uses en una
  red no confiable.
- La contraseña se guarda cifrada en el colector con `agent.key`.

Con este driver puedes usar **Explorar** para buscar símbolos. Las direcciones son rutas del programa, por
ejemplo `"MiDB".Motor.Velocidad` o `"Datos".Lista[3]`.

## 4.4. Driver S7 absoluto

El driver `s7-absolute` usa PUT/GET y direcciones absolutas. Sirve para S7-300/400 y también para
S7-1200/1500 cuando se habilita el acceso PUT/GET.

![Editor de un dispositivo S7 absoluto](img/04-dispositivo-s7-absoluto.png)

| Campo | Qué indica |
|---|---|
| **PLC** | IP o nombre del PLC. |
| **puerto** | Puerto TCP. Normalmente `102`. |
| **CPU** | S7-1500, S7-1200, S7-300 o S7-400. Al cambiarla se propone el slot habitual. |
| **rack** | Número de rack. Normalmente `0`. |
| **slot** | Slot de la CPU: `2` en S7-300 y `1` en S7-1200/1500. |
| **Timeout** | Tiempo máximo de una operación. |
| **Hueco máximo** | Bytes de separación que se toleran para leer varias variables en la misma petición. |

Requisitos en el PLC:

- el acceso PUT/GET debe estar permitido en la CPU;
- los bloques de datos que quieras leer deben ser **no optimizados**;
- las direcciones deben ser absolutas: `DB1.DBX0.3`, `DB1.DBW10`, `DB1.DINT12`, `DB1.REAL20`, `M10.1`,
  `MW20`, `IW64`, `QX4.0`.

Este driver no explora símbolos. Da de alta los tags escribiendo la dirección.

## 4.5. Driver Modbus

El driver `modbus` admite tres transportes:

- **Modbus TCP**: conexión directa al equipo por Ethernet, puerto `502` por defecto.
- **Modbus RTU**: puerto serie del equipo donde corre el colector (`COM3`, `/dev/ttyUSB0`...).
- **Modbus RTU sobre TCP**: tramas RTU dentro de una conexión TCP, para convertidores serie-Ethernet en
  modo transparente.

![Editor de un dispositivo Modbus](img/04-dispositivo-modbus.png)

| Campo | Qué indica |
|---|---|
| **Conexión** | TCP, RTU o RTU sobre TCP. |
| **Equipo / puerto** | Host y puerto TCP. En RTU no se usan. |
| **Puerto serie** | Puerto del equipo del colector. Solo RTU. |
| **Línea serie** | Baudios, paridad y bits de parada. Deben coincidir con el equipo. |
| **Unit id (esclavo)** | Número del esclavo. En TCP directo suele ser `1` o `255`; detrás de una pasarela, el del esclavo serie. |
| **Direcciones** | Marca **Numerar desde 1** si el manual del equipo cuenta `HR1` como el primer registro. Las direcciones `40001`, `30001`... siempre empiezan en 1. |
| **Orden de bytes** | ABCD, CDAB, BADC o DCBA para valores de 32 y 64 bits y textos. |
| **Timeout** | Tiempo máximo de una operación. |
| **Avanzado** | Tamaño de las peticiones, hueco entre tags y funciones de escritura. |

El orden de bytes es la causa más frecuente de un `FLOAT` con un valor absurdo. Si el valor no tiene
sentido, prueba `CDAB`. También puedes indicar el orden en la propia dirección del tag:
`HR100:FLOAT:CDAB`.

En RTU, el usuario del servicio necesita acceso al puerto serie. El instalador añade el usuario `plcmon` a
`dialout` o `uucp` si el grupo existe. Si el puerto no aparece, escribe la ruta a mano.

## 4.6. Editar los ajustes como JSON

El botón **Editar como JSON** muestra los mismos ajustes en el formato de `agent.json`. Sirve para copiar
la configuración a otro dispositivo, pegar un bloque preparado o ajustar un campo que no esté en el
formulario.

![Editor JSON de los ajustes de un dispositivo](img/04-dispositivo-json.png)

**Precaución**: el JSON debe ser válido. Si guardas un JSON incorrecto, el colector rechazará el cambio y la
app mostrará el error. La contraseña guardada se muestra como `********`; déjala así para conservarla.

## 4.7. Activar, desactivar y eliminar

- **Desactivar** deja el dispositivo en la configuración pero lo desconecta. Es reversible.
- **Editar** cambia nombre, driver, ajustes y opciones. Los tags se conservan.
- **Eliminar** borra el dispositivo y sus tags de la configuración. El histórico ya guardado no se borra
  automáticamente; para limpiarlo, usa **Borrar datos** en la pestaña **Histórico** o la CLI.

Si cambias el nombre de un dispositivo con tags historizados, la app ofrece trasladar el histórico al
nombre nuevo. Acepta el traslado si quieres conservar las muestras; si no, quedarán asociadas al nombre
antiguo.

## 4.8. Comprobar la conexión

Cuando guardas un dispositivo, el colector intenta conectar. Revisa:

1. El estado en la lista y en la cabecera.
2. El detalle bajo el nombre: host inalcanzable, timeout, error de autenticación, etc.
3. La pestaña **Tags**: si los tags tienen calidad `Bad`, el problema puede ser de dirección, tipo o
   permisos, no solo de conexión.
4. **Explorar** en S7 simbólico: si explora pero no lee un tag, la conexión está bien y el problema está en
   la dirección.
5. El log del colector en **Colectores > Ver log**.

## Siguiente paso

Con el dispositivo conectado, crea y revisa sus variables: [Tags en vivo](05-tags-en-vivo.md).
