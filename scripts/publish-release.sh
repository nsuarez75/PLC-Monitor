#!/usr/bin/env bash
# Genera los paquetes de la release (CLI plcmon + agente en agent/) en artifacts/release/<version>/.
# Uso: scripts/publish-release.sh [rid...]   (por defecto: win-x64 linux-x64 linux-arm64)
# No se usa single-file para que S7CommPlus.dll (LGPL-3) siga siendo un fichero reemplazable.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
VERSION=$(grep -oE '<Version>[^<]+' Directory.Build.props | sed 's/<Version>//')
RIDS=("${@:-win-x64 linux-x64 linux-arm64}")
[ $# -eq 0 ] && RIDS=(win-x64 linux-x64 linux-arm64)
OUT="$ROOT/artifacts/release/$VERSION"
rm -rf "$OUT" && mkdir -p "$OUT"

for RID in "${RIDS[@]}"; do
  DIR="$OUT/plcmon-$VERSION-$RID"
  echo "== $RID"
  dotnet publish src/PlcMonitor.Cli -c Release -r "$RID" --self-contained true -p:PublishSingleFile=false -o "$DIR" -v q -nologo
  # El agente va en su propia carpeta: lleva el runtime de ASP.NET y no debe pisar ficheros de la CLI.
  dotnet publish src/PlcMonitor.Agent -c Release -r "$RID" --self-contained true -p:PublishSingleFile=false -o "$DIR/agent" -v q -nologo
  # OpenSSL solo para Windows (en Linux se usa la del sistema).
  if [[ "$RID" != win-* ]]; then rm -f "$DIR"/libcrypto-3-x64.dll "$DIR"/libssl-3-x64.dll "$DIR"/agent/libcrypto-3-x64.dll "$DIR"/agent/libssl-3-x64.dll; fi
  rm -f "$DIR"/*.pdb "$DIR"/agent/*.pdb
  mkdir -p "$DIR/deploy" && cp deploy/linux/plcmon-agent.service "$DIR/deploy/"
  mkdir -p "$DIR/licenses" "$DIR/docs"
  cp src/S7CommPlus/LICENSE "$DIR/licenses/LGPL-3.0.txt"
  cp src/S7CommPlus/ThirdParty/Zlib/LICENSE "$DIR/licenses/zlib.net.txt"
  cp src/S7CommPlus/NOTICE.md "$DIR/licenses/S7CommPlus-NOTICE.md"
  cp THIRD-PARTY-NOTICES.md CHANGELOG.md README.md "$DIR/"
  cp -r docs/manual "$DIR/docs/"
  if [[ "$RID" == win-* ]]; then
    [ -f "$DIR/libssl-3-x64.dll" ] && [ -f "$DIR/agent/libssl-3-x64.dll" ] || { echo "Falta OpenSSL: ejecuta scripts/get-openssl-windows.sh" >&2; exit 1; }
    # tar.exe de Windows (bsdtar) sabe crear zip; el GNU tar de Git Bash no.
    (cd "$OUT" && /c/Windows/System32/tar.exe -a -c -f "plcmon-$VERSION-$RID.zip" "plcmon-$VERSION-$RID")
  else
    # Desde Windows se pierde el bit de ejecución: se fija dentro del tar (ejecutables 0755, resto 0644).
    (cd "$OUT" && P="plcmon-$VERSION-$RID" \
      && EXES=("$P/plcmon" "$P/agent/plcmon-agent") \
      && for f in "$P/createdump" "$P/agent/createdump"; do [ -f "$f" ] && EXES+=("$f"); done \
      && tar -cf "$P.tar" --owner=0 --group=0 --mode=u+rw,go+r,a-x,a+X "${EXES[@]/#/--exclude=}" "$P" \
      && tar -rf "$P.tar" --owner=0 --group=0 --mode=0755 "${EXES[@]}" \
      && gzip -9f "$P.tar")
  fi
done
(cd "$OUT" && sha256sum plcmon-* 2>/dev/null | grep -E '\.(zip|tar\.gz)$' > SHA256SUMS.txt || true)
ls -la "$OUT"
