# Third-party notices

PLC-Monitor incluye o usa los siguientes componentes de terceros / includes or uses the following third-party components:

| Componente | Licencia | Notas |
|---|---|---|
| S7CommPlusDriver © Thomas Wiens — https://github.com/thomas-v2/S7CommPlusDriver | GNU LGPL v3 | Modificado. Código fuente en `src/S7CommPlus`, cambios en `src/S7CommPlus/NOTICE.md`. Se distribuye como ensamblado separado y reemplazable (`S7CommPlus.dll`). Texto de la licencia: `licenses/LGPL-3.0.txt` |
| zlib.net © ComponentAce | BSD | Dentro de S7CommPlus. `licenses/zlib.net.txt` |
| S7.Net Plus — https://github.com/S7NetPlus/s7netplus | MIT | |
| Spectre.Console — https://github.com/spectreconsole/spectre.console | MIT | |
| Microsoft .NET / Microsoft.Extensions.* | MIT | |
| OpenSSL 3.5 — https://www.openssl.org | Apache-2.0 | Solo en el paquete de Windows (`libcrypto-3-x64.dll`, `libssl-3-x64.dll`). En Linux se usa la del sistema |

PLC-Monitor no incluye ni usa la librería AGLink de DELTA LOGIC ni ningún componente de Inductive Automation.
PLC-Monitor neither includes nor uses DELTA LOGIC's AGLink library or any Inductive Automation component.
