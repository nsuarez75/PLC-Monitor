#!/usr/bin/env bash
# Genera los paquetes de la release (CLI plcmon + agente en agent/ + app de escritorio en app/) en artifacts/release/<version>/.
# Uso: scripts/publish-release.sh [rid...]   (por defecto: win-x64 linux-x64 linux-arm64)
# No se usa single-file para que S7CommPlusDriver.dll (LGPL-3) siga siendo un fichero reemplazable.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
VERSION=$(grep -oE '<Version>[^<]+' Directory.Build.props | sed 's/<Version>//')
RIDS=("${@:-win-x64 linux-x64 linux-arm64}")
[ $# -eq 0 ] && RIDS=(win-x64 linux-x64 linux-arm64)
OUT="$ROOT/artifacts/release/$VERSION"
# bsdtar sabe crear zip (en Windows: tar.exe del sistema); el GNU tar de Git Bash no.
ZIPTAR=$(command -v bsdtar || echo /c/Windows/System32/tar.exe)
rm -rf "$OUT" && mkdir -p "$OUT"

for RID in "${RIDS[@]}"; do
  DIR="$OUT/plcmon-$VERSION-$RID"
  echo "== $RID"
  dotnet publish src/PlcMonitor.Cli -c Release -r "$RID" --self-contained true -p:PublishSingleFile=false -o "$DIR" -v q -nologo
  # El agente va en su propia carpeta: lleva el runtime de ASP.NET y no debe pisar ficheros de la CLI.
  dotnet publish src/PlcMonitor.Agent -c Release -r "$RID" --self-contained true -p:PublishSingleFile=false -o "$DIR/agent" -v q -nologo
  dotnet publish src/PlcMonitor.App -c Release -r "$RID" --self-contained true -p:PublishSingleFile=false -o "$DIR/app" -v q -nologo
  rm -f "$DIR"/*.pdb "$DIR"/agent/*.pdb "$DIR"/app/*.pdb
  mkdir -p "$DIR/deploy" && cp deploy/linux/plcmon-agent.service "$DIR/deploy/"
  mkdir -p "$DIR/licenses"
  cp external/S7CommPlusDriver/LICENSE "$DIR/licenses/LGPL-3.0.txt"
  cp external/S7CommPlusDriver/PLCMONITOR-NOTICE.md "$DIR/licenses/S7CommPlusDriver-NOTICE.md"
  if [[ "$RID" == win-* ]]; then
    (cd "$OUT" && "$ZIPTAR" -a -c -f "plcmon-$VERSION-$RID.zip" "plcmon-$VERSION-$RID")
  else
    # Desde Windows se pierde el bit de ejecución: se fija dentro del tar (ejecutables 0755, resto 0644).
    (cd "$OUT" && P="plcmon-$VERSION-$RID" \
      && EXES=("$P/plcmon" "$P/agent/plcmon-agent" "$P/app/plcmon-app") \
      && for f in "$P/createdump" "$P/agent/createdump" "$P/app/createdump"; do [ -f "$f" ] && EXES+=("$f"); done \
      && tar -cf "$P.tar" --owner=0 --group=0 --mode=u+rw,go+r,a-x,a+X "${EXES[@]/#/--exclude=}" "$P" \
      && tar -rf "$P.tar" --owner=0 --group=0 --mode=0755 "${EXES[@]}" \
      && gzip -9f "$P.tar")
  fi
  # Paquete suelto del agente para este RID: es el que la app sube al instalar agentes remotos por SSH.
  mkdir -p "$OUT/agents"
  AP="plcmon-agent-$VERSION-$RID"
  rm -rf "$OUT/$AP" && mkdir -p "$OUT/$AP" && cp -r "$DIR/agent" "$OUT/$AP/plcmon-agent"
  if [[ "$RID" == win-* ]]; then
    (cd "$OUT/$AP" && "$ZIPTAR" -a -c -f "$OUT/agents/$AP.zip" plcmon-agent)
  else
    (cd "$OUT/$AP" && tar -cf "$OUT/agents/$AP.tar" --owner=0 --group=0 --mode=u+rw,go+r,a-x,a+X --exclude=plcmon-agent/plcmon-agent --exclude=plcmon-agent/createdump plcmon-agent \
      && tar -rf "$OUT/agents/$AP.tar" --owner=0 --group=0 --mode=0755 plcmon-agent/plcmon-agent $( [ -f plcmon-agent/createdump ] && echo plcmon-agent/createdump ) \
      && gzip -9f "$OUT/agents/$AP.tar")
  fi
  rm -rf "$OUT/$AP"
done
(cd "$OUT" && sha256sum plcmon-* agents/* 2>/dev/null | grep -E '\.(zip|tar\.gz)$' > SHA256SUMS.txt || true)
ls -la "$OUT"
