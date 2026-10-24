# Pendientes

Lista de lo que está abierto. Última revisión: 24/10/2026 (versión 2.0.1).

## Por probar con equipos reales

- [ ] **Modbus contra un equipo real**, solo lectura. Hay test de integración preparado con
      `PLCMON_IT_MODBUS_SETTINGS` y `PLCMON_IT_MODBUS_ADDR`.
- [ ] **Escalado lineal con una entrada analógica real** (0–27648). Solo está probado con el
      driver simulado.
- [ ] **OPC UA desde Ignition** contra un colector instalado. Con UaExpert ya está probado.
- [ ] **Renovar credenciales y token de solo lectura** en real: colector local de Windows y
      colector Linux instalado por SSH.
- [ ] **Ejecutables de Windows y Linux ARM64** de la 2.0.x, y el ARM64 en hardware real.
- [ ] **Cambio de idioma en las gráficas**, mirado a ojo.
- [ ] **Rendimiento en vivo** de ciclos superpuestos, X-Y e histograma con varios tags: se
      recalculan en cada refresco.

## Mejoras de lo que ya hay

- [ ] **Aviso en los botones grises** de la barra superior (OPC UA, Transformaciones): al pasar el
      ratón, explicar que hay que conectarse a un colector.
- [ ] **Barras por periodo:** las etiquetas de los días se quedan en el idioma anterior hasta
      volver a consultar.
- [ ] **Tests intermitentes** que pasan al repetir:
      `OpcUaServerTests.A_new_client_is_rejected_until_its_certificate_is_trusted` y
      `MainViewModelTests.History_is_configured_in_the_tag_window_and_queried_in_the_history_tab`
      (espera de 10 s bajo carga).
- [ ] **Renombrar el tipo de gráfica «Indicadores de panel» a «Indicadores»** (en inglés,
      *Gauges*), para que «panel» quede solo para la rejilla de vistas (ver `GLOSSARY.md`). Afecta
      a la interfaz ES/EN y al manual; las vistas guardadas no cambian si el identificador interno se
      mantiene.

## Ideas sin decidir

- [ ] Sombrear en la tendencia en vivo los tramos en que el colector está historizando un tag con
      disparo.
