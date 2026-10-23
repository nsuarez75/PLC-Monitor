# Pendientes del manual

Este manual describe PLC-Monitor 1.16.0 a partir del código, los tests y capturas generadas con datos
sintéticos. Antes de publicarlo como documentación definitiva de una instalación real conviene cerrar estos
puntos.

## Validación pendiente

- **PLC real**: no se ha validado el manual contra un S7-1200/1500, un S7-300/400 con PUT/GET ni un equipo
  Modbus real. Los valores, tiempos y mensajes de error concretos pueden variar según el firmware y la
  configuración de la CPU.
- **Instalación por SSH**: no se han incluido capturas de una instalación real en Linux o Windows remotos.
  Los permisos, `sudo`, `pkexec`, el firewall y la huella SSH pueden diferir según la distribución.
- **OPC UA**: las capturas muestran un servidor y un cliente de ejemplo. Falta probar el flujo completo con
  un cliente concreto (Ignition, UaExpert u otro) en una red real.
- **Histórico**: las gráficas y tablas son sintéticas. Falta comprobar la retención, el crecimiento de los
  ficheros mensuales y la exportación CSV con un histórico de varios días.
- **Errores de driver**: faltan capturas de fallos típicos (CPU inaccesible, DB optimizado, timeout Modbus,
  orden de bytes incorrecto). El capítulo 14 explica el diagnóstico, pero no muestra cada mensaje real.

## Limitaciones conocidas del producto

- La CLI `plcmon` **no forma parte de la release de escritorio** generada por `scripts/publish-release.sh`.
  Hay que compilarla desde el repositorio. El capítulo 11 lo explica.
- Algunos flujos no se pueden reproducir en modo headless y, por tanto, no tienen captura: diálogos nativos
  de selección de fichero, instalación SSH con contraseña interactiva y aceptación de certificados del
  sistema operativo.
- Las capturas se han generado con una ventana de 1320x820. En pantallas pequeñas algunos formularios
  requieren desplazamiento; el manual indica dónde encontrarlo.

## Mantenimiento del manual

- Si cambia la versión, revisa primero `Directory.Build.props`, los textos de la interfaz y los valores por
  defecto de los drivers.
- Regenera las capturas con:

  ```bash
  PLCMON_UI_CAPTURE_DIR="$PWD/docs/manual/img" \
    dotnet test tests/PlcMonitor.App.Tests/PlcMonitor.App.Tests.csproj \
    --filter "FullyQualifiedName~ManualCaptureTests" -v q
  ```

  El comando necesita el SDK de .NET 10. En este equipo, si el `dotnet` del `PATH` no encuentra el SDK,
  usa la ruta directa del SDK instalado.
- Revisa que no aparezcan IPs reales, nombres de plantas, tokens, contraseñas ni huellas de certificados
  reales en el texto o en las imágenes.
- Actualiza el índice de `README.md` si cambias el nombre o el orden de los capítulos.
