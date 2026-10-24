# 14. Troubleshooting

Failures in PLC-Monitor are usually in one of these layers: the app, the connection to the collector, the
device, a specific tag, history, or the OPC UA server. Before changing things at random, identify
the layer and look at the exact message.

## 14.1. Before you start

1. Note the time of the failure and the exact error text.
2. Check whether it affects one tag, one device, the whole app, or only OPC UA.
3. Do not restart the collector or the PLC without need: a restart erases clues and may interrupt production.
4. Do not delete history or the configuration “to test”.
5. If the failure started after a change, think about what changed: network, password, firmware, address, update.

The bottom bar of the app shows the result of the last operation. If the message is red, the operation
was not completed.

## 14.2. The app does not connect to the collector

The app needs a correct URL, network, token, and, if the profile has one, the certificate thumbprint.

| Message | Usual cause | What to do |
|---|---|---|
| **El certificado del colector en … no es el esperado** | The collector was reinstalled, changed computers, or its certificate changed. | Update the thumbprint in the profile or reinstall/renew. |
| **El colector en … rechaza el token de acceso** | Token rotated, copied incorrectly, or from another collector. | Copy the current token or renew credentials. |
| **No se puede conectar con el colector en …** | Service stopped, incorrect URL/port, firewall, or network. | Check the service, URL, and port. |
| **El colector en … no responde a tiempo** | Slow network, computer overloaded or locked up. | Check the computer and the network; retry. |
| **… no es un colector de PLC-Monitor (o es de otra versión)** | The URL points to another service or the collector is old. | Check the URL and update the collector. |
| **URL no válida: …** | The scheme is missing or the port is wrong. | Use `https://host:5311` or the profile URL. |

Steps:

1. Check that the collector is running. In **Colectores** (Collectors), click **Estado** (Status).
2. Check the profile URL. It must include `https://` if the collector was installed with TLS.
3. From the app computer, test the port:

   ```bash
   nc -vz planta-demo.local 5311
   ```

   On Windows:

   ```powershell
   Test-NetConnection planta-demo.local -Port 5311
   ```

4. If it does not respond, check the firewall, VLAN, routes, and VPN. The installer opens the port in the
   collector firewall, but it cannot open an intermediate network firewall.
5. If the collector is on the same computer, try `https://127.0.0.1:5311` with the correct profile. Do not mix
   a profile with a pinned thumbprint with another collector.
6. If the header shows **solo lectura** (read-only), the connection works: the token does not allow changing
   anything. This is intentional.

When an already established connection is lost, the app shows “Conexión con el colector perdida… Reintentando”.
You do not need to close the app; if it does not come back, check the collector and the network.

## 14.3. The collector does not start or does not appear

### Linux

```bash
sudo systemctl status plcmon-agent
sudo journalctl -u plcmon-agent -n 100 --no-pager
```

### Windows

In PowerShell as administrator:

```powershell
Get-Service PLCMonitorAgent
```

And check `%ProgramFiles%\PLC-Monitor\agent\logs\`.

Frequent causes:

- **No token and listening on the network**: the collector does not start. The log says: “El colector escucha
  en … sin token de acceso”. Install it from the app or configure a token.
- **Port in use**: change the collector port or free the one using it.

  ```bash
  ss -ltnp | grep 5311
  ```

- **Invalid `agent.json`**: the collector cannot load the configuration. Restore the previous copy.
- **Passwords that do not decrypt**: `agent.key` is missing or is from another computer. Re-enter the
  passwords of the devices indicated by the log.
- **Permissions**: on Linux, the service runs as `plcmon` and only writes to its folder. A file owned by
  another user or a history path outside the folder can prevent startup or logging.
- **Disk full**: the collector and SQLite need space. Free up space or reduce retention.
- **Executable blocked on Windows**: antivirus or indexer. Check the exclusion and update again.

If the service is active but the app does not connect, check the listening address and the firewall. The
collector may be listening only on `127.0.0.1` if it was started without the installed configuration.

## 14.4. The device does not connect or is in the failure state

The **En fallo** (Failed) status means that the collector could not open or maintain the connection. The
detail under the device name usually indicates the cause. The collector retries on its own, with increasing
wait; you do not need to restart it for each attempt. If the device is **Desactivado** (Disabled), enable
it: it is not a failure.

Check first:

1. That the PLC or computer is on and responds on the network.
2. That the IP or device name is correct.
3. That the port is not blocked by a firewall or by a switch with an ACL.
4. That no other machine is using the same IP.
5. That the PLC accepts connections from the collector computer, not only from your PC.

### Absolute S7 (PUT/GET)

| Symptom | Probable cause | Action |
|---|---|---|
| S7 error `0x8104` | PUT/GET disabled or optimized DB. | Enable **Permitir acceso vía comunicación PUT/GET** on the CPU, load the configuration, and use non-optimized DBs. |
| The CPU rejects the connection | Incorrect rack/slot, closed port, or unreachable CPU. | Check rack and slot; on S7-300 the slot is usually 2, and on S7-1200/1500, 1. |
| Timeout | IP, route, or firewall. | Check `102/tcp` and the route to the PLC. |
| Absurd value | Incorrect address or type. | Check `DB1.DBW`, `DB1.DINT`, `DB1.REAL`, and the tag type. |

This driver does not read optimized DBs or browse symbols. If you need symbols or optimized DBs, use the
symbolic S7 driver.

### Symbolic S7 (S7CommPlus)

| Message | Probable cause | Action |
|---|---|---|
| **La CPU necesita contraseña** | The access level password is missing. | Enter it on the device. |
| **Contraseña incorrecta** or **Acceso denegado** | Incorrect password or user. | Check the access level and the user; save the password again. |
| **La CPU no acepta comunicación segura (TLS)** | The CPU is old or secure communication is disabled. | Try **Automática** (Automatic); if the CPU is earlier than TIA V16, use **Sin TLS** (No TLS) and assess the risk. |
| **La CPU rechazó la autenticación clásica** | The CPU requires TLS or does not recognize the key. | Use **Automática** or **Solo TLS** (TLS only); check the secure communication configuration. |
| **Elemento no disponible en la CPU** | The symbol does not exist or is not accessible. | Use **Explorar** (Browse) and copy the exact name. |

Do not drop to **Sin TLS** for convenience. It only makes sense on a network and a CPU that do not support
anything else.

### Modbus

| Symptom | Probable cause | Action |
|---|---|---|
| Timeout | IP/port, unit id, wiring, or device stopped. | Check `502/tcp`, the unit id, and the power supply. |
| Exception 01 | Function not supported. | If it fails when writing a single register, disable the simple write functions 05/06. |
| Exception 02 | Nonexistent address or 0/1-based numbering. | Check the table and **Numerar desde 1** (Number from 1); test the address in the device manual. |
| Exception 0A/0B | The gateway cannot reach the slave. | Check the unit id, route, and gateway serial settings. |
| Absurd `FLOAT` | Byte order. | Try `CDAB` or specify the order in the address: `HR100:FLOAT:CDAB`. |
| RTU does not open the port | Permissions or port in use. | Check the `dialout`/`uucp` group, the port name, and that no other program is using it. |
| Serial port with different settings | Another device uses the same port at a different speed or parity. | Match the settings of all devices on the bus. |

In RTU, the service user must be able to open the port. The installer adds it to `dialout` or `uucp`.
If you change it by hand, restart the service so it loads the group.

## 14.5. Tags with bad quality

The **Calidad** (Quality) column shows the status. A disabled tag appears as **Desactivado** (Disabled) and
is not a failure.

| Quality | Meaning | What to look at |
|---|---|---|
| `Good` | Reliable value. | Nothing. |
| `Uncertain` | Doubtful or pending value. | It may be a slow response from the device. |
| `BadNotConnected` | The device is not connected. | Device status and details. |
| `BadAddressInvalid` | The address does not exist or is not valid for the driver. | **Probar** (Test), syntax, and type. |
| `BadAccessDenied` | Permissions, password, or PLC protection. | Password, PUT/GET, access level. |
| `BadTypeMismatch` | The value cannot be converted to the type. | Tag type, write value, and scaling. |
| `BadTimeout` | The device did not respond in time. | Network, PLC load, and timeout. |
| `BadConfiguration` | Invalid tag or device configuration. | Address, type, and device configuration. |
| `BadNotSupported` | The driver does not support the operation. | Unsupported Modbus function or type. |
| `BadWriteDisabled` | Writing is disabled. | Device or tag key. |
| `Bad` | Generic error. | The detail usually carries the PLC message. |

Steps:

1. Open the tag details and look at the error text.
2. Click **Probar**. If the device connects but the tag fails, the problem is in the address, the type,
   or the permission.
3. In symbolic S7, use **Explorar** to check that the symbol exists.
4. Compare with another tag on the same device: if all fail, it is the device; if one fails, it is the tag.
5. If the failure appears only when writing, check the three write keys and the value.

### A write does not work

Check, in this order:

1. The app token is complete, not read-only.
2. **Escrituras habilitadas** (Writes enabled) on the device.
3. **Escribible** (Writable) on the tag.
4. The tag is not disabled.
5. The value has the correct type and range.
6. If there is scaling, the entered value can be converted to the raw value.
7. The PLC does not reject the write due to protection or state.

If the write comes through OPC UA, add the checks in section 14.8.

## 14.6. No data in history

History only stores tags that are active, recorded to history, and connected. Check in order:

1. **Is the historian active?** In the app, the tag details warn if it is disabled. In the CLI:

   ```bash
   plcmon collector info
   ```

2. **Is the tag recorded to history?** The history icon must appear in the row and **Histórico** (History)
   must be active in the details.
3. **Is the tag active?** A disabled tag is not read or recorded to history.
4. **Was the device connected during that period?** During a disconnection there are no samples.
5. **Are the frequency and deadband adequate?** A high minimum interval, a large deadband, or a trigger
   that is never met can leave history almost empty.
6. **Does the query include the period?** Check the start and end time and the time zone. The app shows
   local time; the collector stores UTC.
7. **Did retention delete the data?** If the policy is 30 days, there is nothing from three months ago.
8. **Is there disk space?** A full disk prevents flushing. Look at the log and the free space.
9. **Was the tag or device renamed?** If you did not accept the history transfer, old samples remain under
   the previous name.
10. **Are there too many points?** If the app warns “hay más puntos: usa un agregado o un periodo más corto”,
    adjust the period or the aggregate. The app query requests at most 20,000 points.

If history was working and stopped after a cut, check the log for SQLite or disk errors. Do not delete the
`history-*.db` files without copying them first.

## 14.7. Trends and panels

- **A view comes up empty**: there is no history in the period, the tag is not plottable, or the query
  failed. Try a wider period and check the history of the same tag.
- **The data does not match the PLC**: the historical view uses history, not the live value. The live
  window only keeps what was received since the app started reading that tag, at most one hour.
- **A panel shows an error in one cell**: that view could not be loaded; the others continue. Fix the view
  or add it again.
- **A panel does not show data from another collector**: the saved views and panels belong to a specific collector.
  When you switch collectors, the previous ones do not appear; they have not been lost.
- **Zoom moves in all panels**: **Sincronizar zoom** (Synchronize zoom) is active. Disable it if you want to
  move them separately.
- **Changes that are not visible**: click **Guardar** (Save) in the view or panel. Changing the title or
  range does not save by itself.

## 14.8. OPC UA

### The server does not start

In the **OPC UA** tab, the status and the details indicate the reason.

- **Desactivado** (Disabled): enable **Servidor OPC UA** (OPC UA server) and save.
- **El puerto está ocupado** (The port is in use): another program uses `4840`. Change the port and open the
  new one in the firewall.
- **Error de certificado o de permisos** (Certificate or permission error): check `opcua/` and the
  permissions of the collector folder.
- **Error al crear la PKI** (Error creating the PKI): on Linux, the service needs to write to `opcua/`; on
  Windows, check the ACLs.

The OPC UA server can be in **Error** without taking down the collector: the app and the PLCs keep working.

### The client does not connect

1. Check the URL: `opc.tcp://planta-demo.local:4840`. If you use an IP and the certificate was issued for
   the name, validation may fail. Use the same computer name.
2. Check the firewall: `4840/tcp` must be open from the client.
3. Accept the server certificate on the client. The SHA-1 thumbprint shown in the **OPC UA** tab must
   match.
4. The server does not accept anonymous access: configure a user and password.
5. The server only offers signed and encrypted channels. A client configured with policy `None` will be
   rejected.
6. If the client certificate is pending, approve it in the **OPC UA** tab.

A `BadCertificateUntrusted` notice at server startup about its own self-signed certificate is normal. It
does not prevent clients from connecting after trusting it.

### Incorrect user or password

- The user name is case-insensitive; the password is not.
- If you change the password, save and try again. The app does not show the saved password.
- If the user was created without a password, it cannot authenticate.
- The server limits failed authentication attempts: wait a few seconds before retrying.

### Tags do not appear

Both publications must be met:

1. The device has **Publicar en OPC UA** (Publish to OPC UA) active.
2. The tag has **Publicar en OPC UA** active.

Also, the tag must be active. Check the device folder under `Objects > PLC-Monitor`. If you changed a tag
name, the client may still be using the old NodeId.

### Cannot write over OPC UA

Check the four conditions:

1. **Permitir escribir en los PLC por OPC UA** (Allow writing to PLCs over OPC UA) on the server.
2. OPC UA user with **Puede escribir** (Can write).
3. **Escrituras habilitadas** on the device.
4. **Escribible** on the tag.

And that the value is valid for the type. The usual errors:

- `BadNotWritable`: the third key is missing, or the device/tag does not allow writing.
- `BadUserAccessDenied`: the user is read-only or the password is not correct.
- `BadTypeMismatch`: the value does not fit the tag type.
- `BadNodeIdUnknown`: the client uses a NodeId that no longer exists.

## 14.9. CLI

The CLI is not part of the current desktop release; if you use it, build it as explained in
[chapter 11](11-cli.md).

- **`command not found`**: it is not in the `PATH` or you have not built it.
- **`Unauthenticated`**: `--token` is missing or the token is old.
- **`Unavailable`**: URL, port, or collector stopped.
- **`PermissionDenied`**: you are using the read-only token for a command that changes something.
- **`Unimplemented`**: the collector is old and does not have that command. Update it.
- **Exit code 1**: connection error, read with bad quality, or failed write. The CLI message indicates
  which.
- **Exit code 2**: invalid arguments or a canceled confirmation.
- **Exit code 3**: write canceled by the user.

To isolate a driver problem, use a direct command against the PLC (without a collector) and compare it with
the command through the collector. If the direct one works and the collector one does not, the problem is in the
collector configuration or in the network between collector and PLC.

## 14.10. Diagnostics checklist

1. Is the app connected to the collector? Look at the top bar and **Colectores > Estado**.
2. Is the collector the expected version? **Estado** or `plcmon agent info`.
3. Is the device **Conectado** (Connected)? Look at the detail under the name.
4. Is the failure on one tag or on all? Compare several tags on the same device.
5. Does the address pass **Probar** (Test)? If not, check syntax, type, and permissions.
6. Does the collector log show the same error? **Colectores > Ver log** (View log).
7. Does the network allow the port? PLC `102`/`502`, collector `5311`, OPC UA `4840`.
8. Did something change just before? IP, password, firmware, PLC configuration, update, or firewall.
9. Is the problem with writing? Check the keys and the value.
10. Is the problem with history? Check history recording, connection, period, and retention.

## 14.11. Restart, update, and reinstall

### Restart the collector

Linux:

```bash
sudo systemctl restart plcmon-agent
sudo systemctl status plcmon-agent
```

Windows, in PowerShell as administrator:

```powershell
Restart-Service PLCMonitorAgent
Get-Service PLCMonitorAgent
```

The collector flushes history when it stops. Avoid restarting it in the middle of a shift if you can wait.

### Update

Use **Instalar / actualizar** (Install / update) in **Colectores**. It preserves the configuration, history,
and, unless you check **Regenerar token y certificado** (Regenerate token and certificate), the
credentials. If the collector is very old and the app warns that it does not support an operation, update it.

### Reinstall without losing data

1. Make a full backup.
2. Install or update from the app.
3. If something fails, restore the backup as explained in
   [Configuration and maintenance](13-configuration-and-maintenance.md).

Do not delete `agent.key` or `history/` to “start clean” unless you want to lose the passwords and the
data.

### The app does not start or is stuck with a broken configuration

Close the app and make a copy of the data folder. Rename `app.json` so it is regenerated; if the problem is
in the profiles, also rename `agents.json`, but you will have to re-enter the URL and token. Do not delete
`trends.json` or `panels.json` if you want to keep the views.

## 14.12. What to send to support

Include:

- app and collector version;
- collector and app operating system;
- device driver and configuration, without passwords;
- tag name and address, without confidential data;
- exact error text and approximate time;
- last lines of the collector log;
- what changed before the failure.

**Never send** tokens, passwords, `agent.key`, `agent.pfx`, or copies of `agent.settings.json`. If the log
contains plant names or IPs, redact them before sharing it.

## Next step

With this, the manual ends. Go back to the [index](README.md) to check any chapter.
