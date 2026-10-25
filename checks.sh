#!/usr/bin/env bash
# Comprobaciones del repo: compila (avisos = errores) y pasa los tests.
# Termina en 0 solo si todo pasa. No modifica ningún archivo.
# Uso: ./checks.sh (desde cualquier carpeta)
set -euo pipefail
cd "$(dirname "$0")"

# Los tests de integración contra PLCs reales solo corren si hay variables
# PLCMON_IT_*; aquí se quitan para que nunca toquen un equipo.
unset $(compgen -e | grep '^PLCMON_IT_') 2>/dev/null || true

dotnet build --nologo -v quiet
dotnet test --no-build
