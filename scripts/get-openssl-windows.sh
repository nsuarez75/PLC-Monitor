#!/usr/bin/env bash
# Copia OpenSSL 3 (libcrypto-3-x64.dll, libssl-3-x64.dll) a native/win-x64 para compilar/ejecutar en Windows.
# Origen por defecto: Git for Windows (mantiene OpenSSL 3.5 LTS al día). Se puede indicar otro directorio:
#   scripts/get-openssl-windows.sh "C:/ruta/a/openssl/bin"
# En Linux no hace falta: se usa libssl.so.3 del sistema.
set -euo pipefail
SRC="${1:-/c/Program Files/Git/mingw64/bin}"
DST="$(cd "$(dirname "$0")/.." && pwd)/native/win-x64"
mkdir -p "$DST"
for f in libcrypto-3-x64.dll libssl-3-x64.dll; do
  [ -f "$SRC/$f" ] || { echo "No se encuentra $SRC/$f" >&2; exit 1; }
  cp "$SRC/$f" "$DST/"
done
VERSION=$(grep -aoE "OpenSSL 3\.[0-9]+\.[0-9]+[a-z]* +[0-9]+ [A-Za-z]+ [0-9]{4}" "$DST/libcrypto-3-x64.dll" | head -1)
echo "OpenSSL copiado a $DST: $VERSION"
case "$VERSION" in
  "OpenSSL 3.0."*|"OpenSSL 3.1."*|"OpenSSL 3.2."*|"OpenSSL 3.3."*|"OpenSSL 3.4."*) echo "AVISO: versión sin soporte, usa OpenSSL >= 3.5" >&2 ;;
esac
