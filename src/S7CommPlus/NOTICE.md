# S7CommPlus — aviso de modificaciones / modification notice

Este directorio contiene un trabajo derivado de **S7CommPlusDriver**
(© Thomas Wiens, https://github.com/thomas-v2/S7CommPlusDriver, commit `dbd61e4`, 2026-03-24),
distribuido bajo **GNU LGPL v3** (ver `LICENSE`). Incluye zlib.net (© ComponentAce, licencia BSD,
ver `ThirdParty/Zlib/LICENSE`).

This directory is a derivative work of S7CommPlusDriver, licensed under the GNU LGPL v3.
It is built as a separate assembly (`S7CommPlus.dll`) so it can be replaced by the user.

## Modificaciones / Modifications (PLC-Monitor)

| Fecha | Cambio |
|---|---|
| 2026-10-14 | Proyecto SDK para .NET 10 (antes .NET Framework 4.7.2); zlib.net integrado como código fuente |
| 2026-10-14 | `OpenSSL/Native.cs`: nombres lógicos `libcrypto`/`libssl`, `size_t` = 64 bits |
| 2026-10-14 | `OpenSSL/OpenSslLibraryResolver.cs` (nuevo): carga de OpenSSL 3 en Windows/Linux/macOS (`PLCMONITOR_OPENSSL_DIR`) |
| 2026-10-14 | `Net/S7Client.cs`: el volcado de secretos TLS (`key_*.log`) es opcional mediante `S7Client.KeyLogDirectory` (antes siempre activo en el directorio de trabajo) |
| 2026-10-14 | `S7pLog.cs` (nuevo): las llamadas a `Console.Write*` se redirigen a `S7pLog.Sink` |
