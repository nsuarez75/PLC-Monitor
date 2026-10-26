# Pendientes

Lista de lo que está abierto. Última revisión: 26/10/2026 (versión 2.0.1).

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
- [ ] **Solo lectura en la app:** con un token de solo lectura la app ya bloquea escribir valores y
      editar OPC UA, pero deja abrir «Nuevo dispositivo» (y demás acciones que cambian la
      configuración) y solo avisa al guardar con un mensaje técnico («El token de solo lectura no
      permite PutDevice.»). Desactivar crear, editar, borrar y reordenar dispositivos y tags, y el resto
      de cambios de configuración, con el aviso «Conectado en solo lectura» al pasar el ratón; y si aun
      así llega el rechazo del colector, mostrar un mensaje claro sin nombres internos.
- [ ] **Texto del equipo conectado en la barra superior:** con la ventana estrecha tapa la
      información del colector. Acortarlo (con «…») en vez de que se solapen.
- [ ] **Test intermitente** que pasa al repetir:
      `MainViewModelTests.History_is_configured_in_the_tag_window_and_queried_in_the_history_tab`
      (espera de 10 s bajo carga).
- [ ] **Renombrar el tipo de gráfica «Indicadores de panel» a «Indicadores»** (en inglés,
      *Gauges*), para que «panel» quede solo para la rejilla de vistas (ver `GLOSSARY.md`). Afecta
      a la interfaz ES/EN y al manual; las vistas guardadas no cambian si el identificador interno se
      mantiene.

## Ideas sin decidir

- [ ] Sombrear en la tendencia en vivo los tramos en que el colector está historizando un tag con
      disparo.
