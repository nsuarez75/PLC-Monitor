# 2. Instalación y primer uso

Este capítulo te lleva desde el archivo descargado hasta un PLC leyendo valores en la pantalla. Si ya tienes
un colector instalado y solo quieres añadir un dispositivo, puedes ir directamente al capítulo
[Dispositivos y drivers](04-dispositivos-y-drivers.md).

## 2.1. Ejecutar la app

La release se publica como un único ejecutable por plataforma:

```text
artifacts/release/2.0.1/
├── plcmon-app-2.0.1-win-x64.exe
├── plcmon-app-2.0.1-linux-x64
└── plcmon-app-2.0.1-linux-arm64
```

El ejecutable es autocontenido: incluye el runtime de .NET y las librerías nativas. No necesitas instalar
.NET ni copiar carpetas adicionales.

### Windows

1. Copia el `.exe` a una carpeta local, por ejemplo `C:\Herramientas\PLC-Monitor`.
2. Ejecútalo con doble clic.
3. Si Windows SmartScreen muestra un aviso por ser un ejecutable sin firma comercial, revisa el origen y
   elige ejecutarlo igualmente solo si confías en el archivo.

### Linux

1. Copia el binario a una carpeta de tu usuario.
2. Dale permiso de ejecución:

   ```bash
   chmod +x plcmon-app-2.0.1-linux-x64
   ```

3. Ejecútalo desde la terminal o desde el explorador de archivos:

   ```bash
   ./plcmon-app-2.0.1-linux-x64
   ```

La app guarda sus preferencias en la carpeta de datos del usuario:

- Linux: `~/.config/PLC-Monitor/` (sigue la convención de `ApplicationData` del sistema).
- Windows: `%APPDATA%\PLC-Monitor\`.

Allí aparecen `app.json`, `agents.json`, `trends.json` y `panels.json`. No los edites mientras la app está
abierta.

## 2.2. El primer arranque

Al abrir la app sin ningún colector configurado verás la ventana principal con el estado **Sin conexión**.

![Primer arranque, sin colector conectado](img/02-primer-arranque.png)

La barra superior tiene tres zonas:

- **Perfil de colector**: el desplegable con los colectores conocidos. La primera vez puede estar vacío o tener
  un perfil local.
- **Conectar / Desconectar**: cambia según el estado.
- **Pestañas**: **Dispositivos**, **Tendencias**, **Transformaciones**, **OPC UA** y **Colectores**.

No hace falta conectar para crear un perfil, pero sí para leer o configurar un colector.

## 2.3. Instalar el colector

El colector es un servicio independiente. La app puede instalarlo en este mismo equipo o en otro por SSH.
La instalación crea un token, un certificado TLS y el servicio del sistema.

### En este equipo

1. Abre la pestaña **Colectores**.
2. Pulsa **+** para crear un perfil.
3. Escribe un **Nombre**, por ejemplo `Planta demo`.
4. Marca **Este equipo (sin SSH)**.
5. Deja el **Puerto del colector** en `5311` salvo que ya esté ocupado.
6. Pulsa **Instalar / actualizar**. El sistema pedirá permisos de administrador (`sudo`, `pkexec` o UAC).
7. Al terminar, la app rellena la **URL**, el **Token** y la **Huella del certificado**. Pulsa **Guardar**.
8. Selecciona el perfil en la barra superior y pulsa **Conectar**.

La instalación local copia el colector incluido en la app, crea el usuario de servicio y registra el servicio.
No necesitas descargar nada más.

### En otro equipo por SSH

1. Crea un perfil nuevo en **Colectores**.
2. Escribe el **Nombre** y el **Puerto del colector**.
3. En **Equipo**, escribe el host o la IP del equipo del PLC y el puerto SSH (normalmente `22`).
4. Escribe el **Usuario** SSH.
5. Indica una **Clave privada** o deja el campo vacío para usar la contraseña.
6. Pulsa **Instalar / actualizar**.
7. La primera vez se muestra la huella del servidor SSH. Compruébala con quien administre el equipo y
   acéptala. La app la fija en el perfil.
8. Si hace falta, introduce la contraseña SSH y la de `sudo` para esa operación. No se guardan.
9. Al terminar, guarda el perfil y conecta.

La instalación remota necesita que el equipo tenga SSH, `bash` o PowerShell y permisos de administrador.
En Linux se crea el servicio systemd `plcmon-agent`; en Windows, el servicio `PLCMonitorAgent`.

### Rutas que crea el instalador

| Plataforma | Carpeta | Servicio |
|---|---|---|
| Linux | `/opt/plc-monitor/agent` | `plcmon-agent` |
| Windows | `%ProgramFiles%\PLC-Monitor\agent` | `PLCMonitorAgent` |

Dentro de la carpeta del colector:

- `plcmon-agent` o `plcmon-agent.exe`: el ejecutable.
- `agent.json`: dispositivos, tags, transformaciones e historizador.
- `agent.settings.json`: puerto, hash del token y certificado TLS.
- `agent.pfx`: certificado y clave del servidor.
- `agent.key`: clave con la que se protegen las contraseñas guardadas.
- `history/`: ficheros SQLite del histórico.
- `opcua/`: certificados y configuración del servidor OPC UA.
- `logs/`: logs en Windows y, si se configura, en Linux.

No copies `agent.key` ni `agent.pfx` a un equipo que no sea el del colector. Son secretos.

## 2.4. Conectar y comprobar el estado

Selecciona el perfil y pulsa **Conectar**. La barra superior cambia a **Conectado** y aparece el nombre del
colector, la plataforma y la versión. La lista de dispositivos se rellena con lo que tenga configurado el
colector.

Si la conexión falla:

- comprueba que el servicio está activo;
- comprueba que la URL termina en el puerto correcto (`5311` con TLS);
- confirma que el token es el del colector y no el de otro perfil;
- revisa la huella del certificado si el colector se reinstaló;
- revisa el firewall entre la app y el colector.

El capítulo [Resolución de problemas](14-resolucion-de-problemas.md) tiene una lista más completa.

## 2.5. Crear el primer dispositivo

1. Pestaña **Dispositivos**.
2. Pulsa **+** en **DISPOSITIVOS**.
3. Escribe un **Nombre** sin `/`, por ejemplo `prensa-linea3`.
4. Elige el **Driver**.
5. Rellena los ajustes del driver. Para el S7 simbólico necesitas al menos el host o la IP del PLC; para
   Modbus, el host y el puerto; para S7 absoluto, el host y la CPU.
6. Deja **Activo** marcado.
7. Pulsa **Guardar**.

El dispositivo aparece en la lista con el estado **Conectando** y luego **Conectado** o **En fallo**. El
detalle del fallo se muestra bajo el nombre y en la cabecera del dispositivo.

Si no conoces la dirección, en el driver simbólico puedes usar **Explorar** para buscar símbolos. En los
demás drivers tendrás que escribir la dirección del tag.

## 2.6. Añadir un tag y verlo en vivo

1. Selecciona el dispositivo.
2. En la pestaña **Tags**, pulsa **Añadir tag**.
3. Escribe la **Dirección**. En el driver simbólico puede ser un nombre, por ejemplo
   `"MiDB".Motor.Velocidad`.
4. Escribe un **Nombre** o deja el campo vacío para usar la dirección.
5. Elige el **Grupo** y el intervalo. Si el grupo no existe, se crea.
6. Pulsa **Probar** para leer la dirección sin añadirla. Es la forma más rápida de detectar una dirección
   mal escrita.
7. Pulsa **Añadir**.

El tag aparece en la tabla con su valor, tipo, calidad y hora de actualización. Si la calidad es `Bad`,
revisa la dirección, el tipo y el estado del dispositivo.

También puedes usar **Explorar** en el driver simbólico para ver los símbolos de la CPU y añadirlos con un
botón. Es la vía recomendada cuando no conoces el nombre exacto.

## 2.7. Recorrer las pestañas

- **Dispositivos**: tags en vivo, histórico y explorador del PLC seleccionado.
- **Tendencias**: gráficas guardadas y paneles. Se pueden crear sin tener un dispositivo seleccionado.
- **Transformaciones**: tablas de valores enteros a texto.
- **OPC UA**: estado y configuración del servidor OPC UA del colector.
- **Colectores**: perfiles, instalación, credenciales, estado y logs.

El botón de tema de la barra superior cambia entre claro y oscuro. La preferencia se guarda en `app.json`.

## 2.8. Cerrar la app y el colector

Cerrar la app no detiene el colector ni el histórico. Si quieres dejar de leer un PLC, desactiva el
dispositivo o el tag. Si quieres detener el servicio del colector:

```bash
# Linux
sudo systemctl stop plcmon-agent

# Windows (PowerShell como administrador)
Stop-Service PLCMonitorAgent
```

Para volver a arrancarlo, usa `start` en lugar de `stop` o `Start-Service` en PowerShell.

**Precaución**: no detengas el servicio mientras estés escribiendo una consigna o justo después de un
cambio, salvo que sea necesario. El historiador vuelca sus datos al disco al cerrarse, pero una parada
forzada puede dejar las últimas muestras sin guardar.

## Siguiente paso

Con un colector y un dispositivo conectados, el siguiente capítulo explica cómo gestionar varios perfiles,
instalar en remoto y renovar credenciales: [Colectores y perfiles](03-colectores-y-perfiles.md).
