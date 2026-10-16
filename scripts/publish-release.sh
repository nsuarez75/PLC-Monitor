#!/usr/bin/env bash
# Genera la release en artifacts/release/<version>/: un único ejecutable de la app de escritorio por plataforma
# (plcmon-app-<version>-<rid>, .exe en Windows). Cada app lleva dentro los agentes de todas las plataformas para
# instalarlos en este equipo o por SSH sin más ficheros.
# Uso: scripts/publish-release.sh [rid...]   (por defecto: win-x64 linux-x64 linux-arm64)
# Los agentes incluidos son siempre los de todas las plataformas, aunque se pidan menos apps.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
VERSION=$(grep -oE '<Version>[^<]+' Directory.Build.props | sed 's/<Version>//')
ALL_RIDS=(win-x64 linux-x64 linux-arm64)
if [ $# -gt 0 ]; then RIDS=("$@"); else RIDS=("${ALL_RIDS[@]}"); fi
OUT="$ROOT/artifacts/release/$VERSION"
AGENTS="$OUT/.agents"
# Ejecutable único, autocontenido; las librerías nativas (Skia, SQLite) se extraen solas al arrancar.
SINGLE=(-c Release --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
        -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:SatelliteResourceLanguages=es -v q -nologo)
rm -rf "$OUT" && mkdir -p "$AGENTS"

for RID in "${ALL_RIDS[@]}"; do
  echo "== agente $RID"
  dotnet publish src/PlcMonitor.Agent -r "$RID" "${SINGLE[@]}" -o "$AGENTS/$RID"
  EXT=""; [[ "$RID" == win-* ]] && EXT=".exe"
  # Sin extensión: el nombre del recurso dentro de la app es agents/plcmon-agent-<rid>.
  mv "$AGENTS/$RID/plcmon-agent$EXT" "$AGENTS/plcmon-agent-$RID"
  rm -rf "${AGENTS:?}/$RID"
done

for RID in "${RIDS[@]}"; do
  echo "== app $RID"
  EXT=""; [[ "$RID" == win-* ]] && EXT=".exe"
  TMP="$OUT/.app-$RID"
  dotnet publish src/PlcMonitor.App -r "$RID" "${SINGLE[@]}" -p:PlcMonAgentsDir="$AGENTS" -o "$TMP"
  mv "$TMP/plcmon-app$EXT" "$OUT/plcmon-app-$VERSION-$RID$EXT"
  chmod 0755 "$OUT/plcmon-app-$VERSION-$RID$EXT"
  rm -rf "$TMP"
done
rm -rf "$AGENTS"
ls -la "$OUT"
