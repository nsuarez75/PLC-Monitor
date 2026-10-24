# 10. OPC UA

The collector can publish its tags over OPC UA so that other systems can read them: Ignition, UaExpert, a
historian, an MES, or any OPC UA client. The server is configured from the **OPC UA** tab and
runs in the collector, not in the app.

![OPC UA server configuration](../img/10-opc-ua.png)

## 10.1. Enable the server

1. Connect to the collector.
2. Open **OPC UA**.
3. Check **Servidor OPC UA activo** (OPC UA server active).
4. Check the **Puerto** (Port). The standard is `4840`; the installer opens it in the firewall if it can.
5. Leave **Permitir escribir en los PLC por OPC UA** (Allow writing to PLCs over OPC UA) unchecked until you need it.
6. Add at least one user.
7. Click **Guardar en el colector** (Save to collector).

The **ESTADO** (STATUS) header shows:

- **En marcha** (Running), **Arrancando** (Starting), **Parado** (Stopped), or **Error**;
- the error detail, if any;
- the **URL para los clientes** (URL for clients), for example `opc.tcp://planta-demo.local:4840`;
- the **huella del servidor** (server thumbprint), which the client will ask you to accept.

The server does not support anonymous access. Without a username and password, a client cannot connect.

## 10.2. What is published

For a tag to appear in OPC UA, two keys must be enabled:

1. **Publicar en OPC UA (1.ª llave)** (Publish to OPC UA (1st key)) on the device.
2. **Publicar en OPC UA (2.ª llave)** (Publish to OPC UA (2nd key)) on the tag. It is enabled by default.

A disabled device or tag is not published. If you change a key, the OPC UA tree updates without
restarting the collector.

### Node tree

```text
Objects
└── PLC-Monitor
    ├── prensa-linea3
    │   ├── _Estado
    │   │   ├── Conectado
    │   │   ├── Estado
    │   │   ├── Detalle
    │   │   ├── Activo
    │   │   └── EscrituraPermitida
    │   ├── Velocidad
    │   ├── Fuerza
    │   └── ...
    └── horno-linea3
        └── ...
```

- The `PLC-Monitor` folder is the root of the server.
- Each device is a folder with its name.
- `_Estado` contains diagnostic variables for the device: connection, state, detail, active, and write
  permission.
- Tags are published with their name. If the tag has **Grupo en la tabla** (Group in table), nested folders are created with
  that name.
- The `NodeId` of a tag is `device/tag` within the PLC-Monitor namespace. The display
  name and the `BrowseName` are the tag name.

Tags with bad quality are published with a null value and a bad `StatusCode`. Tags with scaling publish
the engineering value, not the raw value. Transformations do not change the published value: the client receives
the number.

## 10.3. Users and permissions

In **USUARIOS** (USERS):

1. Click **Añadir usuario** (Add user).
2. Enter the **Usuario** (Username).
3. Enter the **Contraseña** (Password).
4. Check **Puede escribir** (Can write) if the user should be able to write to the PLCs.
5. Click **Guardar en el colector**.

- **Sin permiso de escritura** (No write permission): the user can read, browse, subscribe, and read history. The user cannot
  write.
- **Puede escribir**: the user can also write, but only if all three keys are enabled.
- There is no anonymous access.
- Passwords are stored as a PBKDF2 hash in `agent.json`. They cannot be recovered; if they are lost, you
  must enter a new one.
- The existing user shows the password as an ellipsis. If you do not enter a new one, the current one is kept.

To change a password, enter the new one in the corresponding field. To remove a user, click the
trash icon and save.

## 10.4. Client certificates

The server uses its own certificate and only accepts clients whose certificate is in the trust list. The first
time a client connects, its certificate remains **pendiente** (pending) in **CLIENTES** (CLIENTS).

1. Connect from the client to the server URL.
2. In the app, click **Actualizar** (Refresh) or wait for the tab to refresh itself.
3. Locate the pending certificate.
4. Verify the **huella** (thumbprint) with the client or with whoever administers it.
5. Click **Aceptar** (Accept).
6. The client can connect again.

To reject a client, click the trash icon. The client will no longer be able to connect. If it tries again,
it will appear as pending again.

**Caution**: do not accept a certificate without verifying the thumbprint. An accepted certificate can read the
tags and, if the user has permission, write to the PLC.

The server certificate is shown in the header. The client will ask you to trust it the first time.
Compare the thumbprint before accepting.

## 10.5. Reading and browsing from a client

1. Create a connection to `opc.tcp://<host>:4840`.
2. Choose a signed and encrypted channel.
3. Accept the server certificate after verifying the thumbprint.
4. Sign in with a username and password.
5. Browse `Objects > PLC-Monitor > <device>`.
6. Read or subscribe to the tags you need.

The URL shown by the app uses the collector computer's name. If the client cannot resolve that name, use the
appropriate address, but keep in mind that the certificate was issued for the server name: a different
address can cause a validation error. The most stable approach is to use the same computer
name or a local DNS entry.

Tags recorded to history expose history reads (`HistoryRead`) if the historian is active. Tags
not recorded to history only have the current value.

## 10.6. Writing over OPC UA

Writing over OPC UA requires **three keys**:

1. **Permitir escribir en los PLC por OPC UA** on the OPC UA server.
2. **Escrituras habilitadas (1.ª llave)** (Writes enabled (1st key)) on the device.
3. **Escribible (2.ª llave)** (Writable (2nd key)) on the tag.

In addition, the user must have **Puede escribir**. If a key is missing, the node is published as read-only
and the client receives an access error.

The `EscrituraPermitida` variable in `_Estado` indicates whether the device and the server allow writing; it
does not consider the user's permission or the tag key.

Each write is recorded in the collector audit with the OPC UA user and the value. The collector validates the
type and range before sending it to the PLC. If the PLC rejects the write, the client receives an error.

**Caution**: enabling writing over OPC UA expands the risk surface. Enable it only if an
external system must write setpoints, use different users for reading and writing, and review the
audit.

## 10.7. Stopping or changing the port

- If you uncheck **Servidor OPC UA activo** and save, the server stops and releases the port.
- If you change the port, the server restarts on the new port.
- If the port is in use, the status changes to **Error** and the detail indicates the reason.
- Uninstalling the collector deletes the `opcua/` folder, including the accepted certificates. If you want to
  keep them, copy that folder beforehand; when reinstalling, clients that already trusted it can continue
  to connect if you restore the same PKI and the server certificate is still valid.

The OPC UA server does not start if the collector is not running. If the tab shows **Parado** and the server
should be active, check the collector status first.

## 10.8. Best practices

- Use a read user for supervision clients and a write user only for the system that
  actually writes.
- Do not reuse the OPC UA password for SSH, the app, or the PLC.
- Review the list of accepted clients and remove those that are no longer used.
- Document the server certificate thumbprint and that of each client.
- Do not publish port `4840` on the Internet. Use a VPN or a segmented industrial network.
- If a client does not see a tag, check both publishing keys before touching the certificate.
- If a client connects but cannot read, check the user, the password, and the device status.

## Next step

To administer the collector from the terminal or integrate it into scripts, continue with [CLI](11-cli.md).
