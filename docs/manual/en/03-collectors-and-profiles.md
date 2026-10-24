# 3. Collectors and profiles

An **collector** is the service that talks to the PLCs. A **profile** is the record the app stores to know
how to reach that collector. You can have several profiles and switch between them without closing the app.

![The Colectores tab (Collectors) with a local profile and a remote one](../img/03-agentes.png)

## 3.1. What a profile stores

| Field | What it is for |
|---|---|
| **Nombre** (Name) | Label to recognize it in the list. It does not have to match the hostname. |
| **URL** | gRPC address of the collector, for example `https://planta-demo.local:5311`. |
| **Token** | Full access token. It is stored securely and sent on every call. |
| **Huella del certificado** (Certificate thumbprint) | SHA-256 thumbprint of the collector's TLS certificate. It prevents accepting a changed certificate. |
| **Equipo / Puerto SSH** (Computer / SSH port) | Host and port to install or update the collector remotely. |
| **Usuario SSH** (SSH user) | User used to log in to the computer. |
| **Clave privada** (Private key) | Path to the SSH key. If it is empty, a password can be used. |
| **Puerto del colector** (Collector port) | Port that will be used when installing the collector (`5311` by default). |
| **Este equipo (sin SSH)** (This computer, no SSH) | Installs and manages the collector on the same computer where the app runs. |
| **Plataforma / Instalado** (Platform / Installed) | Data that the app fills in after a successful installation. |

The app stores profiles in `agents.json`, next to `app.json`. On Windows the token is protected with DPAPI;
on Linux it remains in a file with `600` permissions, unencrypted. In both cases, any process running
under your account can use it. Protect your system session.

## 3.2. Create a profile

1. Open **Colectores** (Collectors).
2. Click **+** in the **COLECTORES** (COLLECTORS) list.
3. Enter a **Nombre** (Name).
4. Fill in the **URL** if you already know the collector's. If you are going to install it from the app, you can leave it empty:
   it is filled in when the installation finishes.
5. Paste the **Token** if the collector already exists. If you are going to generate it now, leave it empty.
6. Paste the **Huella del certificado** if the collector already uses TLS.
7. If you are going to manage it over SSH, complete **Equipo**, **Usuario** and, if needed, **Clave privada**.
8. Click **Guardar** (Save).

The profile appears in the list and in the top bar drop-down.

## 3.3. Connect and disconnect

1. Select the profile on the top bar.
2. Click **Conectar** (Connect).
3. Wait for the status to change to **Conectado** (Connected). The app loads the device list and the collector
   status.
4. To close the session, click **Desconectar** (Disconnect). This does not stop the collector or history recording.

If the collector is on the same computer and runs without being installed, the default URL is
`http://127.0.0.1:5310`. That configuration has no TLS and should only be used for local tests. The
installer creates the secure configuration on `5311`.

## 3.4. Install or update the collector

The app can install the collector included in the release. The package is chosen automatically according to the
detected platform. If you want to use another package, specify a **Carpeta del colector** (Collector folder) in the form.

### Local installation

1. Check **Este equipo (sin SSH)**.
2. Choose the **Puerto del colector**.
3. Click **Instalar / actualizar** (Install / update).
4. Accept the system permission request.
5. Wait for the activity to finish. The app shows the progress and the result.
6. Save the profile if the app has not already done so.

### Remote installation

1. Fill in **Equipo**, **Puerto SSH**, **Usuario** and the key or password.
2. Click **Instalar / actualizar**.
3. The first connection shows the SSH server thumbprint. Verify it before accepting it.
4. If the user needs `sudo`, enter the password in **Contraseña de sudo** (sudo password). If you use the same one as for
   SSH, you can leave it empty.
5. The app copies the collector, creates the service, opens the ports in the firewall if it can and starts the service.

The SSH password and the `sudo` password are used only during the operation. They are not saved in the profile.

### What an update preserves

Installing or updating preserves:

- `agent.json` and `agent.key`;
- `agent.settings.json` and `agent.pfx`, unless you request regenerating credentials;
- the `history/` folder;
- the `opcua/` folder with the accepted certificates;
- on Windows, the `logs/` folder.

The rest of the collector folder is replaced by the new executable. That is why you should not store your own files
inside `/opt/plc-monitor/agent` or `%ProgramFiles%\PLC-Monitor\agent`.

## 3.5. Renew credentials

**Renovar credenciales** (Renew credentials) generates a new token and certificate without reinstalling the executable. The previous
token stops working immediately and the app updates the profile if the operation is performed from it.

Use it when:

- you suspect that a token has been shared;
- you have changed the people with access;
- you want to rotate credentials by policy;
- you have restored an old profile and the collector has a different token.

After renewing, other apps or scripts that use the previous token will stop connecting. Update their
profiles.

## 3.6. Read-only token

When installing, regenerating or renewing, you can check **Crear token de solo lectura** (Create read-only token). A second token
is generated that allows:

- reading tags and statuses;
- querying history;
- browsing symbols;
- subscribing to changes.

It does not allow writing, changing configuration, installing or uninstalling. Use it for plant screens,
people in training or integrations that should only query.

The read-only token is shown only once when it is generated. Store it in the corresponding secret
manager. If you lose it, renew credentials.

## 3.7. Status, logs and uninstallation

In the collector details you have:

- **Estado** (Status): check whether the service is installed, active and since when.
- **Ver log** (View log): shows the last lines of the service. On Linux it reads `journalctl`; on Windows, the file
  log if it exists.
- **Desinstalar** (Uninstall): stops and removes the service. If you check **Conservar agent.json al desinstalar** (Keep agent.json on uninstall), the app
  saves a copy of the configuration and of `agent.key` before deleting the folder.

**Caution**: uninstalling deletes the collector folder, including `history/` and `opcua/` if they are in the
default paths. If you want to keep them, copy those folders beforehand. The **Conservar agent.json** (Keep agent.json)
option only saves `agent.json` and `agent.key`.

## 3.8. Multiple collectors

You can have one profile per plant, line or test computer. The app only maintains one active connection at a
time, but it keeps the views and panels of each collector separately. When you change profiles, the device
list and trends reload.

View and panel names are stored associated with the collector. A panel created for `Planta demo` does not
appear in another collector even if the tags have the same name.

## 3.9. Best practices

- Use a different token per installation. Do not copy the same token to several plants.
- Do not share the full token with anyone who only needs to query: create a read-only one.
- Pin the certificate thumbprint after installation and check whether it changes without reason.
- Do not store SSH passwords in a profile text field. The app does not persist them.
- Keep the collector clock synchronized. History and charts use the collector and PLC time.
- Note the installed version on each computer. The details show the platform and the installation date.

## Next step

With the collector connected, the next step is to create the PLCs: [Devices and drivers](04-devices-and-drivers.md).
