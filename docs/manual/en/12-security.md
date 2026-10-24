# 12. Security

PLC-Monitor can read from and write to industrial equipment. Security does not depend on a single password:
there are several layers that must be properly configured. This chapter explains what each one protects and
what you should review before leaving the system in production.

## 12.1. What is protected

| Asset | Risk | Main protection |
|---|---|---|
| The collector API | Anyone on the network could read or write. | Token + TLS + firewall. |
| PLC credentials | Theft of S7 passwords. | Encrypted with `agent.key`. |
| History | Leak of production data. | System permissions and token. |
| The OPC UA server | Unauthorized read or write. | Users, certificates, and three keys. |
| Writes | Unwanted movement of a machine. | Three keys and audit. |
| App profiles | Theft of a collector token. | DPAPI or user permissions. |

There is no multiuser authentication inside the app. Anyone who has the profile with the full token can do
everything that token allows. Real separation is achieved with tokens, operating system accounts, and the
network.

## 12.2. The collector API

### Token

The collector requires a token on every call if one is configured. There are two:

- **Full token**: read, write, configure, record to history, delete, and administer OPC UA.
- **Read-only token**: read, browse, subscribe, and query history. It cannot write or change
  configuration.

The collector does not store the token in plaintext. It stores its SHA-256 hash. The app sends the token as
`Authorization: Bearer ...` and the collector compares the hash.

Without a token, the collector can only listen on `127.0.0.1`, `::1`, or `localhost`. If you try to expose it to
the network without a token, the collector does not start. This is a deliberate protection: an unauthenticated
API on the network lets anyone who reaches the port write to the PLC.

### TLS

The installer generates a self-signed certificate and configures HTTPS. The app pins the certificate's
SHA-256 thumbprint in the profile. If the certificate changes, the app does not connect until you update the
thumbprint.

The certificate is stored in `agent.pfx`. The `.pfx` password is in `agent.settings.json`. Both files must
have restricted permissions:

- Linux: owner `plcmon`, mode `600`.
- Windows: ACL for the service user and administrators.

The certificate is generated for the host, the computer name, and `localhost`. The app validates the pinned
thumbprint, but other TLS clients may also validate the name: use one of those names or a local DNS entry.

### Rotation

**Renovar credenciales** (Renew credentials) in the app generates a new token and certificate. The previous
token stops working immediately. Apps and scripts that use it must be updated. If you suspect a token has
leaked, renew it and review the logs.

## 12.3. SSH

Remote installation uses SSH to copy the collector and create the service.

- The first connection shows the SSH server thumbprint. Verify it before accepting it. The app pins it in
  the profile.
- The SSH and `sudo` passwords are used only during the operation. They are not stored in `agents.json`.
- If you use a private key, protect the file with a passphrase and `600` permissions.
- The SSH account must have the minimum required permissions. Do not use `root` for day-to-day work.
- The installation script runs with elevated privileges. Review the origin of the release before
  installing.

## 12.4. PLC credentials

The passwords for S7 CPUs are stored encrypted in `agent.json` with a local key, `agent.key`.

- `agent.key` is a secret. If it is lost, the stored passwords cannot be decrypted and must be entered
  again.
- If you copy `agent.json` to another computer without `agent.key`, the passwords do not work.
- Do not include `agent.key` in an unprotected backup.
- When uninstalling with **Conservar agent.json** (Keep agent.json), the app preserves the configuration
  and the key. If you do not need them, delete them.

In the interface, the stored password is shown as `********`. Leave that value to preserve it.

## 12.5. Writes: the three keys

A write from the app or from OPC UA requires:

1. **Device**: **Escrituras habilitadas (1.ª llave)** (Writes enabled (1st key)).
2. **Tag**: **Escribible (2.ª llave)** (Writable (2nd key)).
3. **OPC UA**, only if the write arrives via OPC UA:
   - **Permitir escribir en los PLC por OPC UA** (Allow writing to PLCs over OPC UA) on the server;
   - an OPC UA user with **Puede escribir** (Can write).

In addition, the API token must be full. The read-only token never writes.

Before writing, the app asks for confirmation and shows the current value. The CLI also asks for
confirmation unless you use `-y`. Every write is recorded in the audit with the declared user, the origin,
and the credential type.

**Caution**: the keys do not replace the plant procedure. A technically correct write can be dangerous if
it is made at the wrong moment.

## 12.6. OPC UA

The OPC UA server:

- does not allow anonymous access;
- requires signed and encrypted channels;
- validates user and password;
- stores passwords as PBKDF2 hashes;
- only accepts certificates from clients you have approved;
- applies the three write keys.

The thumbprint shown on the **OPC UA** tab is that of the server certificate, normally SHA-1, which is what
clients such as UaExpert or Ignition show. Do not confuse it with the SHA-256 thumbprint of the collector TLS
certificate that the app uses.

Best practices:

- A read user for monitoring and a write user for the system that writes setpoints.
- Passwords different from those used for SSH and the app.
- Review the list of accepted clients and remove those that are no longer used.
- Do not accept certificates without verifying the thumbprint.
- Do not publish `4840/tcp` on the Internet.

## 12.7. The desktop app

The app stores profiles in `agents.json`. The token:

- on Windows it is protected with the current user's DPAPI;
- on Linux it is stored in a file with the user's `600` permissions.

This protects against other users of the computer, not against a process running under your own account.
Log out when you step away and do not leave the laptop unlocked.

Views and panels do not contain secrets, but they do contain device and tag names. Treat `trends.json` and
`panels.json` as internal information.

## 12.8. Audit and logs

The collector writes to the `PlcMonitor.Audit` logger:

- writes;
- enabling and disabling devices;
- configuration changes;
- transformation changes;
- deletion and renaming of history;
- OPC UA changes and certificate acceptance.

Each entry includes the user declared by the client, the address it calls from, and the credential type.
The declared user cannot be verified; what is verified is the token.

Where to find them:

- **Linux**: `journalctl -u plcmon-agent`. The app can show the last lines with **Ver log** (View log).
- **Windows**: collector `logs\` folder, one file per day. The default retention is 7 days.
- **App**: bottom bar messages and operation errors.

Logs must not include tokens or passwords. If you are going to send a log to support, check that it does not
contain plant names, IPs, or data you do not want to share.

## 12.9. Network

- Install the collector on a segmented industrial network, not on the office network without controls.
- Open only the necessary ports: `5311/tcp` for the app and `4840/tcp` for OPC UA.
- Do not expose `5310/tcp`, `5311/tcp`, or `4840/tcp` to the Internet.
- If you need remote access, use a VPN or a controlled jump host.
- Limit the app and OPC UA clients by firewall to the computers that really need them.
- Separate the PLC network from the management network when possible.
- Review which computers can reach the PLC; the collector is not the only path.

## 12.10. Checklist

Before leaving an installation in production:

- [ ] The collector is installed with TLS and a token, not with the local configuration without a token.
- [ ] The full token is stored in a secret manager, not in an email or a chat.
- [ ] There is a read-only token for those who only query.
- [ ] The collector certificate thumbprint is pinned in the profile.
- [ ] The SSH server thumbprint was verified during installation.
- [ ] `agent.key`, `agent.json`, `agent.settings.json`, and `agent.pfx` have restricted permissions.
- [ ] Writes are disabled by default and are only enabled where needed.
- [ ] OPC UA does not allow anonymous access and accepted clients have been reviewed.
- [ ] The firewall only opens the necessary ports.
- [ ] The collector and PLC clocks are synchronized.
- [ ] There is a backup of the configuration and history.
- [ ] You know how to renew credentials and where the logs are.

## 12.11. If you suspect an incident

1. Disable writes on the device and in OPC UA.
2. Stop the OPC UA server if it is not needed.
3. Renew the collector token and certificate.
4. Change the OPC UA passwords and any stored PLC credentials.
5. Review the accepted OPC UA clients and remove unknown ones.
6. Review the audit and the system logs.
7. Check the configuration of devices and tags: someone with the full token may have changed it.
8. If the collector computer is compromised, do not trust its files: reinstall and restore only what you have
   verified.

## Next step

For backups, updates, retention, and fine-grained configuration, continue with
[Configuration and maintenance](13-configuration-and-maintenance.md).
