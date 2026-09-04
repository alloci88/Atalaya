# Backlog Atalaya

Lo que queda por hacer, y lo que se decidió no hacer todavía. Vive en el repo y se mantiene al día
igual que `MANUAL.md` y `DECISIONS.md` (norma **N-4**): cada fase mueve a «Cerrado» lo que entrega
y apunta lo que deja pendiente. Un backlog que solo ve una persona no es del equipo.

Última revisión: 2026-09-03 (F21 — el corte en `unit_done`, con las cuentas cuadradas).

## En vuelo

- **F24 — el símbolo debería ir por UBICACIÓN, no por hallazgo.** Hoy `Finding.Symbol` es **uno para
  todo el hallazgo**, y un defecto sistémico tiene N ubicaciones en N miembros distintos. El auditor
  resuelve el desajuste como puede —metiendo una lista en el campo: «CargaMediaPorMetro /
  CargaEspecifica», 6 de 60 hallazgos medidos—, y F24 ha tenido que enseñarle al criterio a leer esa
  lista como un conjunto para no tratar el mismo defecto como dos sitios distintos. **Funciona, y es
  un parche sobre un modelo que no encaja**: el sitio natural del miembro es la ubicación, junto a
  la ruta y la línea, que es donde ya vive todo lo demás que localiza.
  - Lo que arreglaría de verdad: `add_locations` podría declarar el miembro de cada punto nuevo (hoy
    extiende ubicaciones y deja el símbolo del hallazgo intacto, así que un defecto extendido a otro
    método queda con el símbolo del primero), y el criterio compararía ubicación contra ubicación en
    vez de una cadena contra otra.
  - **No se hace en F24**: toca el modelo del dominio y el formato en disco de todo lo guardado, y
    esta fase no cambia nada de lo que ya está en el hub. Con el conjunto, el caso medido queda
    cubierto.
- **M1 — la auditoría estructurada se midió y NO se hace fase.** Recorrido miembro × familia con la
  identidad del hallazgo fijada en `(regla, miembro)`, medido con `opus` en tres tandas por brazo
  (D-908). Cumple tres de las cuatro condiciones —mismo núcleo, más defectos en 3 de 3, coste igual
  o menor— y **falla la suya**: quita la variante del catálogo y la pone en el criterio, con el total
  sin mover. La causa, escrita para que no se vuelva a intentar igual: **una identidad solo es
  identidad mientras la familia tenga bordes**, y `criterio.<área>` no los tiene. La palanca se queda
  en el banco (`--estructurado`), desarmada, por si alguien la retoma con otra idea para el criterio.
- **M1-b — segunda versión del brazo estructurado, con UN solo cambio, y es la ÚLTIMA.** Se declara
  aquí antes de medirla, como pide el anti-objetivo de M1: no se ajusta un brazo sobre la marcha
  para que gane, se declara la versión siguiente y se mide aparte.
  - **El cambio, uno y nada más**: el criterio del auditor **deja de ser una etapa**. En la v1 el
    paso 4 decía «al final, lo que no encaje en ninguna regla», y eso convirtió el residuo en
    trabajo obligatorio: el estructurado produjo **más** fuera de catálogo que el libre (56 % contra
    44 %) y ahí es donde se le fueron las variantes que había quitado del catálogo (D-908). En la v2
    el criterio solo admite un hallazgo **con una consecuencia que ninguna regla del catálogo
    cubra**, y **uno por miembro** como mucho.
  - **Todo lo demás igual**: mismo recorrido miembro × familia, misma identidad `(regla, miembro)`,
    mismo escenario (las dos clases del banco, `opus`, tope 6, sin corte), tres tandas por brazo y
    el mismo control temático. Si cambia algo más, no es la v2: es otra medida.
  - **La condición de fase es la de M1 §3, sin rebajarla**: mismo núcleo, menos variantes, más
    defectos en 3 de 3 y coste igual o menor. Con las cuatro se propone la fase; con menos se anota.
  - **Y es la última.** Si la v2 tampoco quita variantes, la conclusión no es probar una v3: es que
    la identidad del hallazgo no se arregla desde el prompt, y el sitio de esa pregunta es el modelo
    de dominio —lo que ya apunta la deuda del símbolo por ubicación, más arriba en esta lista—.
- **El margen del hilo contra la producción CON corte, sin medir.** Es el único dato que el §6 de F25
  pedía y no se sacó: los créditos no son infinitos y se paró tras la primera tanda (D-927). M2 comparó
  hilo contra producción **los dos sin corte**, porque cortar mata la conversación (D-914 §4), pero la
  producción de antes de F25 llevaba el corte puesto y el corte ya se llevaba un 67 % de la escritura
  (D-881). Así que **el −69 % es contra una línea que no era la real**, y el margen verdadero es menor.
  Cuánto menos, no se sabe. La tanda que lo contestaría está implementada y probada: `PromptBench
  barrido --corte-en-hilo` degenera el barrido exactamente en el de antes —prompt entero por pasada y
  corte incluido—, así que es una orden y una espera, no trabajo.
- **La varianza del hilo sobre el banco, con una sola muestra.** F25 corrió una tanda donde M2 corrió
  tres: 17 de 20 defectos, dentro del rango que M2 dejó (17-18, media 17,7). Con una muestra no se
  puede decir si esta unidad cae en 17 o en 18. No es urgente —M2 tiene tres tandas detrás— pero está
  dicho para que nadie lea «17 de 20» como si fuera una medida nueva.
- **Verificar el hilo en Copilot con una sesión real.** F25 lo implementa para las dos casas con los
  mismos tests de forma, pero en esta máquina no hay asiento (`models.list` contesta 403, D-883), así
  que lo que está comprobado es la forma y no el ahorro. **La verificación es del usuario**: correr
  un barrido con Copilot elegido y mirar en el anexo técnico la **escritura de caché por pasada**. Si
  las pasadas 2..N no bajan como bajan en Claude Code —−79 %—, el parte de esa sesión es la evidencia
  y se abre un retoque. Lo que lo desmentiría está dicho: el SDK documenta que una sesión mantiene su
  historia y publica `CacheReadTokens` / `CacheWriteTokens`, pero nadie lo ha visto funcionar aquí.
- **El hilo se hizo fase, y lo que quedaría por preguntar.** F25 lo puso en producción con los
  números de M2 delante (D-913…D-919): **−69 %** de coste por unidad, **−79 %** de escritura de caché
  en las pasadas 2..N, cero variantes marcadas contra 7, reconciliación intacta — y **17-18 de 20**
  defectos de D-895 contra los 20 de 20 del barrido anterior. Lo que se pierde son dos de los cinco
  tardíos reales, y se perdió a sabiendas.
  - **La pregunta que queda es «¿se puede tener el hilo sin la convergencia temprana?»**, y la medida
    deja servido por qué es difícil: el hilo también quita variantes de verdad, así que **quitar
    variantes y perder tardíos son el mismo dial**, no dos objetivos. Cualquier intento tiene que
    atacar esa unión, no el transporte — y **no** retocando el texto de continuación, que es el
    extremo barato de ese mismo dial.
  - **El texto de continuación es una constante y así se queda.** Cambiarle una palabra es cambiar la
    medida: si algún día hace falta otra redacción, se mide en el banco antes y se dice cuál es cuál.
- **M1-c — el mismo retoque, pero sobre el prompt LIBRE, que es el que corre.** La v2 de arriba mide
  el brazo estructurado; esto mide si el hallazgo se puede aprovechar sin cambiar cómo se pregunta.
  - **El cambio**: acotar el criterio del auditor en el prompt de producción igual que en M1-b —solo
    con una consecuencia que ninguna regla cubra, uno por miembro—, sin tocar el método de barrido.
  - **Contra qué se mide, y esto es lo que lo hace barato**: las **tres tandas libres de M1 ya están
    grabadas** y son la línea base —32,7 hallazgos por tanda, 44 % de criterio, 85,2 k tokens de
    salida, 16 de 19 defectos en 3 de 3, y los 11 del núcleo en 3 de 3—. Basta con tres tandas
    nuevas del prompt retocado y compararlas contra esas cifras.
  - **Lo que decide**: que el núcleo siga en 3 de 3 y que baje el fuera de catálogo sin perder
    defectos de la cola. Si pierde cola, se descarta — es la vara de D-874 y D-907.
- **M1 — lo aprovechable de la medida: el TOPE, no el prompt.** El brazo estructurado alcanza con
  **tope 4** la cobertura que el libre alcanza con **tope 6**, y de ahí no sube ninguno (D-908). Es
  una palanca sobre el presupuesto, no sobre la regla de parada. Antes de tocar el tope de fábrica
  hace falta la misma medida con Copilot y sobre unidades que no sean las dos del banco: con dos
  clases de 34 líneas, «no sube de 18 de 19» puede ser del escenario y no del método.
- **Variantes: observadas con `opus`, y si se retoma se empieza por el banco.** El mismo defecto
  contado dos veces —otra regla, otra consecuencia, otro título— existe: se vio en el informe de
  referencia con Copilot y se reprodujo con `opus` (mismo sitio y mismo miembro bajo reglas
  distintas). Lo que F24 construyó para evitarlo —contrato en el prompt y filtro en la puerta— se
  midió con nueve tandas y **se retiró**: hacía converger el barrido pero las tandas que convergían
  eran las que menos encontraban (D-907). Si alguien lo retoma, **se empieza por el banco**
  (`PromptBench barrido`), que es lo único que sabe medir pasadas y cobertura con la aplicación de
  verdad delante, y la primera pregunta es la de siempre: qué deja de encontrarse.
- **F21 — el corte, con Copilot delante.** Aquí no hay asiento, así que §4 se resolvió leyendo el
  contrato del SDK y no midiéndolo (D-883): `CopilotToolOptions.IsTerminal` dice que una llamada
  con éxito **termina el turno en vez de devolverle el resultado al modelo**, y `unit_done` lo lleva
  desde F14 — es decir, allí la llamada de cortesía nunca existió. Lo que hay que ver con un asiento
  delante es el desglose por pasada del informe: **las llamadas por pasada tienen que ser 1**, dos
  con `read_signatures`. Si salieran 2 y 3, el `IsTerminal` no está haciendo lo que dice su
  documentación y hay que medir allí lo mismo que se ha medido aquí.
- **F21 — el presupuesto de razonamiento, medido.** Es la palanca grande que queda y está
  cuantificada (D-882), pero no se toca sin una comparación de COBERTURA: se pagaría en hallazgos
  tardíos, que es la variable de control de F20. Medir con `PromptBench --pasadas 3` y mirar
  «Hallazgos en pasadas >= 2» antes y después, no los tokens.
- **F20 — el reparto del coste, con una sesión real delante.** La aritmética está fijada con tests
  y reproduce al credit la aceptación de F19 (D-871), pero falta verlo: la línea «Reparto del coste»
  en el informe de una sesión de Copilot, y el trozo del pie en vivo a 1366×768 —tiene que ceder
  antes que el coste y quedarse en «escritura de caché 61 %»—.
- **F20 — la conversación compartida: cerrada, y está en producción.** Se implementó en F20, se midió
  y se cayó (D-874: el auditor dejaba de usar herramientas a partir de la segunda pasada). Se volvió a
  intentar en M2 y esa parte quedó descartada —el auditor sí llama a herramientas en todos los turnos
  y reconcilia entero—, pero converge dos pasadas antes y pierde dos de los cinco tardíos reales.
  **F25 la hizo modo de producción con esa pérdida escrita y aceptada** (D-920). Ya no hay nada
  pendiente aquí; lo que queda vivo es la pregunta de arriba.
- **F20 — la escritura de caché, después de F21.** Las dos hipótesis de F20 siguen caídas —A porque
  no hay corte de caché que podamos pedir (D-872), B porque el modelo deja de trabajar (D-874)—,
  pero el nudo que dejaron escrito **sí se ha deshecho**: la llamada de cortesía escribía el
  razonamiento de la anterior y ya no existe (F21, D-880). Lo que queda del frente es la escritura
  de la llamada que **sí** trabaja, y ahí la palanca medida es el **razonamiento**: es casi todo lo
  que la conversación vuelve a escribir (D-882). No se toca sin medirlo contra los hallazgos
  tardíos.

- **F19 — la economía de turnos, con Copilot delante.** Todo lo de la fase es prompt y coordinador,
  así que vale para las dos casas sin una línea por proveedor, pero **solo se ha medido con Claude
  Code**: aquí no hay asiento de Copilot. Dos cosas que solo se ven allí (D-869): que el modelo
  agrupe igual —se lee en el desglose por pasada del informe: las llamadas por unidad tienen que
  bajar de ~3,5 a ~2— y que `unit_done`, que allí es `IsTerminal`, no trunque el resto del turno.
  El prompt le pide que vaya la última justo para eso, pero es un razonamiento, no una medida.
- **F19 — `read_signatures` por adelantado, si sale a cuenta.** Medido: aparece en 2 de cada 6
  unidades y cuesta una llamada entera (~25.000 tokens). Mandar las firmas de las dependencias sin
  que las pidan cambiaría esa llamada condicional por tokens incondicionales en todas las unidades.
  Puede ganar, pero exige resolver dependencias de verdad —hoy `ReadSignatures` es heurístico
  (D-018)— y eso no se decide a ojo: medir con `PromptBench` antes de tocar nada.

- **F18 — la línea de composición, con una sesión real delante.** Toda la aritmética está fijada
  con tests y la medida contra el CLI de Claude Code está hecha y escrita (D-850…D-858), pero
  quedan dos cosas que solo se ven usando la aplicación: **el pie en vivo** con el trozo nuevo
  («código 2,1 % · 11 llamadas/unidad») en una sesión de verdad, a 1366×768 y con la ventana a la
  mitad —tiene que ceder ANTES que los tokens—, y el bloque **por fase** de Métricas con
  descubrimiento, verificación y arreglo del hub real. Y comparar la línea del informe de una
  sesión de Copilot con lo que dice el panel de la organización.
- **F18 — la palanca de Copilot, cuando haya asiento.** `SessionConfig.SystemMessage` existe y
  tiene secciones nombradas, con `EnvironmentContext` entre ellas: es donde vive lo per-máquina que
  contaminaría un prefijo cacheable (D-854). Verificado por reflexión, **sin medir**: aquí no hay
  asiento. Quien lo tenga empieza por ahí, midiendo con `PromptBench` antes y después, y sin tocar
  el mensaje de sistema sin una comparación de calidad delante.
- **F18 — la calibración de `caracteres / 4`.** Medido: el texto en español de estos prompts sale a
  ~3,0 caracteres por token, así que la regla subestima ~25 % (D-857). Cambiarla mueve a la vez la
  composición y el **presupuesto de directivas** de F7, que está calibrado con ella y se enseña en
  un panel. Se toca entero o no se toca.

- **F17 — la aceptación humana, sobre el banco.** Todo lo que se puede fijar sin un modelo delante
  está fijado —el prompt lleva la lupa y sus exclusiones, la reconciliación acotada, la siembra,
  el diálogo en sus tres momentos, la cinta—, pero el juicio del modelo solo se ve auditando:
  **Configurar ciclo → Rendimiento** sobre el banco → auditar → tiene que encontrar el doble
  recorrido y similares, y **NO** las credenciales de ClienteRemoto ni los nulos de Voladura; los
  hallazgos previos de otras temáticas tienen que quedar intactos (mismo `TimesConfirmed`, misma
  fecha). Después, **cambiar a Seguridad** → el aviso de «N unidades auditadas pasarán a
  pendientes» → todas a pendiente → auditar ClienteRemoto → las credenciales caen, y solo cosas de
  seguridad. Si el modelo se sale de la lupa, el sitio para apretar es `ThemeCatalog.Excludes`.
- **F17-RETOQUE — el pie, con una sesión corriendo.** Está medido a los tres anchos y renderizado en
  los dos temas con los números del parte (D-837), pero el pie cambia cada llamada: verlo con Claude
  Code y con Copilot auditando de verdad, a 1366×768 y con la ventana a la mitad, y comprobar que
  ceden los tokens antes que el coste y que el tooltip lleva el desglose entero.
- **Un test intermitente bajo carga.** `AssistedFixClaudeTests.Lo_que_el_usuario_escribe_llega_en_el_turno_siguiente`
  falló una vez en una pasada completa de la suite (F17.2) y pasó tres veces seguidas aislado y en
  la pasada siguiente: huele a tiempo de espera del CLI falso bajo paralelismo, no a regresión.
  Si vuelve a caer, medir el tiempo que espera y decidir con evidencia (N-2), no subir el tope a ojo.
- **F17.2 — la secuencia de ciclos, sobre el hub real.** Rehecha como capítulos y medida con datos
  desiguales (D-845…D-849), capturada en los dos temas a 1124 y 441 px; falta abrir Métricas con
  AtalayaBanco y XBLAST delante: XBLAST con su fila «sin ciclos en este periodo», AtalayaBanco con
  su bloque C1 (partido en Rendimiento → Seguridad solo si el cambio se hizo con F17.1 o después),
  las fechas debajo del rótulo, y la vista arrancando por el final sin arrastrar.

- **F16 — arreglar de verdad con Claude Code, con los ojos del usuario.** El circuito está probado
  de punta a punta contra el CLI real y contra un CLI falso que habla MCP, pero la aceptación es
  suya: **Ajustes → Claude Code → abrir un hallazgo del banco → Arreglar con agente → commitear →
  ver «Arreglada — pendiente de verificar» → Verificar → que quede limpia**. Y de paso mirar que la
  cabecera de la pantalla dice con quién trabaja, y que las tarjetas de pregunta y el diff se ven
  como con Copilot (D-804…D-808).
- **F16 §B/§C y F16-RETOQUE — las capturas.** Lo que se puede fijar sin ventana está fijado: los
  textos, carácter a carácter —el pie y el informe de la misma sesión, la unidad de cada casa, las
  cuatro apariciones del proveedor—, y la geometría de la cabecera, medida de verdad a 1124 y 658 px
  y comprobada en rojo con el layout viejo. Lo que queda necesita la aplicación apuntando al hub
  real y con sesiones dentro: el pie de una sesión en vivo, la columna «Proveedor» de la actividad
  de sesiones, la fila «Detectado con» de una ficha, un informe de verificación abierto en Informes,
  y **la cabecera del arreglo a 1366 y con la ventana a la mitad, en los dos temas**. Va con la
  aceptación de arriba, que es cuando esos datos existen.
- **F16 §E — el callejón, reproducido a mano.** El caso está fijado en test, pero conviene verlo
  una vez en la aplicación: arreglar algo que **borre el ancla y el símbolo** —quitar un campo
  estático al reestructurar—, dejarlo **sin commitear**, y pulsar **Verificar**: tiene que salir un
  veredicto útil con la unidad entera delante, no «no localizado» otra vez (D-813).
- **F16 §D — el tope nuevo, en un barrido de verdad.** Con 6 pasadas, mirar si las unidades que
  antes se quedaban en «cobertura posiblemente incompleta» llegan ahora a las dos secas. Si siguen
  sin llegar, el problema no era el presupuesto y hay que volver a mirar (D-812). Y al abrir la
  aplicación por primera vez, comprobar que el aviso de la promoción sale una vez y solo una.
- **BUGFIX-ARRANQUE — publicar la 1.1.4 y ver correr el candado.** El arreglo está verificado sobre
  el paquete real de esta máquina (`--selfcheck` → 8 pasos en verde, código 0), pero queda una cosa
  que solo puede comprobar el primer release que corra: **que el paso `carcasa` funcione en el
  runner de GitHub**. Construir la ventana es donde revientan los errores de XAML y es la mitad más
  valiosa del chequeo; si el runner no pudiera, saldría en el log del workflow con su paso nombrado
  (D-801). Y después, lo que de verdad cierra esto: **descomprimir la 1.1.4 en limpio y abrirla**.

- **BUGFIX-SYNC — la actualización real, en la máquina donde falló.** Es la aceptación del arreglo
  y no la puede hacer ningún test: reintentar 1.1.2 → 1.1.3 con Atalaya en
  `OneDrive\Escritorio\Atalaya-v1.1.1-win-x64`. **Con OneDrive pausado debe pasar**; y después,
  **movida la instalación a una carpeta no sincronizada, debe pasar sin pausar nada**. De paso,
  mirar el banner antes de pulsar: tiene que decir que está dentro de OneDrive y seguir ofreciendo
  el botón (D-795). Si vuelve a fallar, el mensaje ya nombra al culpable y el log dice qué carpeta
  quedó sin borrar (D-794).

- **F15 — cuadrar un día contra el panel de GitHub.** Es la aceptación de la fase y solo se puede
  hacer con datos reales: coger un día con auditorías, mirar el total de Atalaya y compararlo con la
  gráfica de **AI credits** del panel de Copilot de la organización para ese mismo día, y **anotar
  la desviación en DECISIONS**. Si es grande, investigar antes de dar nada por bueno; los
  sospechosos, por orden: la semántica de la caché (D-785), un promocional vencido en la tabla, y
  las llamadas que Copilot factura fuera de las sesiones de Atalaya (D-791).
- **F15 — las tarifas, revisadas cuando venzan los promocionales.** La siembra del 2026-09-01 trae
  GPT-5.6 Sol al 50 % **hasta el 2026-09-03** y Gemini 3.6/3.7 Flash **hasta el 2026-12-31**. Cuando
  pasen esas fechas el precio sube y la tabla del hub hay que corregirla a mano — que es justo para
  lo que se hizo editable (D-786).
- **F15 — la sesión en vivo y el azulejo de coste, vistos.** Los credits están probados por consulta
  en todos los sitios; falta abrirlos en la ventana: la cifra en vivo mientras corre una sesión, el
  azulejo con su equivalente en dólares y el enlace de tarifas, y el aviso de «parcial» cuando un
  modelo no tiene tarifa (D-787).

- **F14 — el caso de aceptación con Claude Code, con los ojos del usuario.** El circuito está
  verificado de punta a punta contra el CLI real —auditoría de una unidad sembrada con su hallazgo y
  su reconciliación, y una verificación que devolvió «resuelto» citando el código—, pero sobre un
  toolbox de prueba, no sobre el hub. Falta el recorrido **en la aplicación viva**: Ajustes →
  Claude Code → auditar 2 unidades del banco → ver los hallazgos con sus severidades y la
  reconciliación normal → verificar uno. Y de paso mirar las dos pantallas que cambian: **Cuenta**
  con sus dos pilotos (y con GitHub arriba, que es lo que no puede leerse mal) y el **diálogo de
  lanzamiento** diciendo «con Claude Code (modelo X)» (D-783).
- **F14 — el reverso del caso de aceptación: una máquina SIN Claude Code.** Que la opcionalidad se
  cumple está fijado por test —Cuenta informa y no alerta, Ajustes solo ofrece Copilot, nada se
  degrada—, pero conviene verlo con los ojos en una máquina que no tenga el CLI: abrir Cuenta y que
  la fila gris de «Claude Code · opcional» no se lea como una tarea pendiente, y abrir Ajustes y
  comprobar que el selector de proveedor ni aparece (D-784).
- **F14 — Métricas con dos proveedores dentro, vista.** Que el azulejo de coste enseñe **una línea
  por casa y ningún total** está probado por consulta; falta verlo en la ventana, a 1366×768 y en
  los dos temas, con un hub que tenga sesiones de las dos (D-780).
- **F14 — las firmas de fallo de Claude Code que aún no hemos visto.** El clasificador se apoya en
  los errores reales que devolvió el CLI durante la verificación más las formas conocidas de nombrar
  lo mismo. Lo que no case sale como desconocido con su crudo delante, que es lo correcto; cada vez
  que aparezca uno nuevo en un log, su firma se añade — igual que se hace con la de Copilot
  (D-707, D-778).

- **F12 — la calibración de severidad, contra la clave del banco.** Los criterios ya están
  implantados y viajan en el prompt de cada unidad (D-754), pero lo que prueba que calibran es
  **re-auditar el banco de pruebas y comparar el resultado contra la clave**: una crítica sembrada,
  una crítica reportada, y los off-by-one y las desreferencias nulas en alta. Ningún test puede
  hacer eso — mide el juicio del modelo, no el nuestro. Si vuelve a salir inflada, lo que se toca
  son los ejemplos y las reglas de desempate, que están en un solo fichero.
- **F12 — las capturas del pulido, en los dos temas.** Los cinco puntos de D-758 con la aplicación
  viva: el resumen de sesión agrupado por clase, el titular de una pasada de reconciliación que
  confirma sin aportar, los dos indicadores de deriva alineados en la fila del inventario, el aviso
  amarillo de re-anclaje entero, y ese mismo aviso reconociendo un arreglo de Atalaya. Las causas de
  los dos de layout están fijadas en tests sobre el XAML real, pero lo que se ve —los colores y el
  aire— solo se ve mirando.
- **F12 — el aviso de cierre de ciclo, visto al cerrarse uno de verdad.** El texto y su enlace están
  probados (D-757); falta llegar al 100 % en una aplicación real y ver el banner aparecer, leerlo
  entero a 1366×768 y que «Ver el informe del cierre» aterrice en el informe correcto.
- **F12 — la deriva de un silencio por patrón retirado, en el hub real.** Retirar un patrón devuelve
  a activo lo que solo él tapaba (D-756), incluidos los silencios anteriores a F12 que se enganchan
  por el texto del ejemplar. En el hub real no hay todavía ningún patrón con varios hallazgos
  detrás, así que la migración solo se ha ejercitado en tests.

- **BUGFIX-VERSION — el zip publicado de la 1.0.3, con ojos.** El camino de release está verificado
  reproduciéndolo en local y el workflow se vigila a sí mismo, pero el token de `gh` de esta máquina
  no ve el repositorio y no se ha podido descargar el artefacto real. Una línea:
  `gh release download v1.0.3 --repo Applied-Advanced-Solutions-AAS/Atalaya` y mirar el
  `ProductVersion` del exe (D-730).
- **BUGFIX-REDONDEO — verlo en la ventana.** Los porcentajes están verificados contra los datos
  reales por consulta (0,2 % en Métricas y en las dos tarjetas); falta abrir la aplicación y verlo,
  y comprobar de paso que el sliver de los roscos se distingue a los dos tamaños (D-726).
- **BUGFIX-CIERRE — la limpieza vista en la ventana.** La autocuración está verificada contra una
  COPIA del hub real (dos claims sueltos liberados, XBLAST dejando de decir «auditando ahora»);
  falta abrir la aplicación con el estado colgado y ver que se limpia sola con su aviso, y que las
  dos pantallas fallidas se cierran y desaparecen del rail (D-720).
- **BUGFIX-CUOTA — el circuito de cuota, visto en la aplicación viva.** El banner se ha renderizado
  con los textos reales en los dos temas y a dos anchos, y la geometría está medida por tests a tres
  tamaños; falta verlo DENTRO de la ventana con una sesión de verdad, y —cuando vuelva a haber
  cuota— el corte a mitad de barrido de punta a punta (D-713).
- **BUGFIX-CUOTA — los textos del proveedor que aún no hemos visto.** La taxonomía se apoya en el
  único error real que hay en los logs más las formas conocidas de nombrar lo mismo. Lo que no case
  sale como desconocido con su crudo delante, que es lo correcto; cada vez que aparezca uno nuevo en
  un log, su firma se añade al clasificador (D-707).
- **F9.2 — la siembra, con los ojos del usuario.** Cerrar un ciclo sobre un clon con deriva real y
  ver que el inventario del siguiente sale sembrado: las cambiadas en pendientes, las limpias en
  auditadas con su ancla, la arreglada conservando su Verificar. Que el panel del ciclo y la tarjeta
  del portafolio cuadren con él, y que la frase del cierre —«Cerrado con N cambiadas… y M sin
  verificar»— se lea entera en la pantalla de cierre a 1366×768 (D-705).
- **F9 — el caso de aceptación de la deriva, con los ojos del usuario.** El panel del ciclo ya se ha
  visto en la ventana, en los dos temas y a dos anchos (D-698), y el inventario de XBLAST enseña sus
  2 cambiadas de verdad. Lo que falta es el **circuito con datos reales**: tras un pull con cambios,
  «Seleccionar cambiadas», auditar y ver los hallazgos nuevos; y el inverso del bucle — arreglar con
  el agente, commitear, comprobar que sale «arreglada, pendiente de verificar» y no «cambiada», y
  que verificar la devuelve a «sin cambios» (D-694, D-695). No hay todavía ningún arreglo con huella
  en el hub, así que ese medio circuito no ha podido ejercitarse fuera de los tests. En la misma
  pasada: la fila del inventario con sus dos indicadores a 1366×768 y en los dos temas.
- **F9 — el umbral de tres arreglos, sin datos detrás.** `MaxOwnFixesBeforeReaudit` está razonado,
  no medido: uno sería no dejar arreglar nada y diez sería no mirar nunca (D-686). Se revisará
  cuando haya uso real y se sepa cuál de las dos molesta.
- **F9 — la deriva de un clon muy atrasado.** Auditado hace más de 1.500 commits y con ocho sesiones
  distintas, el cálculo tarda ~6,4 s sobre xblast (D-688). Va fuera del hilo de UI y se cachea, así
  que no bloquea, pero si alguien vive en ese caso habrá que acotar el recorrido y decirlo.

- **H9 — verificación humana con asiento real.** El flujo interactivo está verificado por el
  usuario con una sesión de verdad; falta cerrar el circuito con el **descarte** (que el clon
  vuelva byte a byte) y la revisión visual de la vista de arreglo con textos largos, en los dos
  temas y a 1366×768 (D-569).
- **H9.1 — que una sesión fix real sobre XBLAST diga «0 errores nuevos»** con los preexistentes
  aparte (D-581). El resolutor de ámbito ya está medido contra ese clon; falta la sesión entera
  con asiento. En la misma sesión: comprobar que el agente ya no sale a buscar tests (§3) y que la
  pantalla de cierre aterriza sola con la ventana pequeña y mucha conversación (D-588).

- **H9.2 — el caso de aceptación de Métricas, con los ojos del usuario.** El cuadre está hecho y
  cubierto por tests (D-589…D-598), pero **ningún test renderiza**: falta abrir Métricas y ver (a)
  el gasto de los arreglos de hoy sumado, (b) las resoluciones de hoy en el día de hoy, (c) el eje
  llegando a hoy en todas las gráficas. En la misma pasada: el tooltip del cubo semanal en los dos
  temas y la columna «Tipo» del registro a 1366×768 (D-599).
- **H9.2 — el huso horario, sin observar en producción.** El corte por día local está probado,
  pero no hay ni un dato en el hub entre las 00:00 y las 02:00 locales que lo haya ejercitado de
  verdad (D-599). Se verá solo, con el uso.

- **F7 — el caso de aceptación de las directivas, con una app spec-driven de verdad.** La
  funcionalidad está entera y cubierta por tests (D-600…D-614), pero **ningún test renderiza** y no
  se ha ejercitado contra un repositorio real: falta que el compañero del equipo dé de alta su app,
  vea qué le propone el escaneo, active lo suyo, lance una auditoría de una clase y compruebe en el
  informe qué directivas viajaron, y que un arreglo asistido salga con el estilo de su casa
  (D-615). En la misma pasada: la fila del panel de directivas a 1366×768 y en los dos temas.
- **F7 — los topes del escaneo, sin medir contra una colección grande.** `MaxCandidates` (400) y
  `MaxCandidateBytes` (1 MB) son números elegidos a priori. Se revisarán cuando haya un repositorio
  con una colección de skills de verdad delante (D-615).

## Despliegue al equipo

- Dar permiso de **write** al equipo en `atalaya-hub`.
- ~~`dist` **self-contained**~~ — resuelto en F8: lo que se reparte es el zip de la Release, que
  se publica siempre self-contained y no exige runtime en la máquina destino. `publish.ps1` sigue
  siendo framework-dependent por defecto, que es lo correcto para desarrollo.
- ~~**Sellar la versión en el build**~~ — resuelto en F8: `Directory.Build.props` fija
  `<Version>` y el workflow de release la pisa con la del tag, así que cada binario dice de qué
  tag salió. Además, una copia vieja avisa sola de que hay una nueva.
- **Firma de código: pedir certificado a IT.** Los ejecutables no van firmados, así que el primer
  arranque de cada zip descargado enseña el aviso de SmartScreen («Más información → Ejecutar de
  todas formas»). Se documenta en MANUAL y en README, pero es fricción en cada onboarding y la
  clase de aviso que enseña a la gente a ignorar avisos. Un certificado de firma de código —EV o
  estándar con reputación— lo quita de raíz: hay que pedírselo a IT y añadir el paso de firma al
  workflow de release (D-624).
- Acompañar los primeros onboardings y recoger la fricción.
- Conversión del coste a **euros**: medir ~10 unidades contra el panel de consumo de Copilot antes
  de poner un número en la interfaz. Ahora que las verificaciones registran su gasto (D-590), la
  medición tiene que incluirlas: hasta H9.2 el total del panel solo contaba auditorías y arreglos.

## Aplazado a decisión

- **Integración con Microsoft Planner.** El prompt de F6.2 está listo; falta el registro de la
  aplicación en Entra ID y decidir el momento.

## Aparcado hasta que la realidad lo pida

- **H9 ampliado**: mejoras sobre la sesión interactiva, según lo que pida el uso real.
- **Descargas diferenciales**, si los 221 MB por versión molestan. Hoy cada actualización baja el
  paquete entero, que es lo mismo que ya se bajaba a mano. La medición está hecha: 221 MB por
  Release, y de una versión a la siguiente cambian unos pocos MB. Velopack lo resolvería —sus
  deltas funcionan, se probaron— pero traía consigo cambiar el formato del paquete, un segundo
  origen de la versión y borrar el `appsettings.deploy.json` del despliegue en cada actualización
  (F11, D-736). **Se decide cuando alguien se queje del tiempo de descarga**, y no antes.

- **Migrar el diff a DiffPlex** si el artesanal falla en los casos finos —cambios intra-línea,
  ficheros grandes, encodings—. Decidido de antemano y sin debate (D-552).
- **Pasadas con «lentes» por pilar**, solo si los barridos siguen dejando hallazgos. F17 trajo la
  versión de ciclo entero (las temáticas); una lente por pasada dentro de un ciclo General sigue
  aparcada.
- **Temáticas personalizadas.** El catálogo de F17 es cerrado a propósito: una temática es un
  encargo que el auditor tiene que poder cumplir sin interpretar, y una libre sería un prompt
  suelto con nombre de lupa. Si el equipo pide una que no está —«accesibilidad», «i18n»—, se
  añade al catálogo de la casa con sus dos listas, versionada como las demás; editable en el hub
  solo si eso empieza a pasar a menudo.
- **Válvula de escape en un ciclo temático** (p. ej. «las críticas de seguridad se reportan
  siempre»). Se descartó en F17 a favor de la regla limpia (D-825); si el uso real enseña que
  una crítica se quedó sin reportar por estar fuera de lupa, es una opción de la temática y no
  un cambio de la regla.
- **`HubMergePolicy`**: riesgo teórico de pérdida de alias en un merge. Sin síntoma observado, y el
  backfill lo recuperaría.
- **Limpieza de los campos vestigiales del fingerprint** en los JSON antiguos.
- **Catálogo de reglas editable en el hub**, solo si su mantenimiento se vuelve frecuente.
- **Verify sobre hallazgos medidos**: mensaje «Confirmado: N LOC ≥ umbral». Cosmético, caso de
  esquina.

## Cerrado

- **R1 · El guardarraíl del prefijo estable no medía las lupas** — el defecto que M1 destapó (D-909)
  y que llevaba abierto desde F17: el techo de F19 se comprobaba solo con General, la única lupa sin
  bloque `ENFOQUE DEL CICLO`, mientras cualquier ciclo temático real se pasaba. Se tomaron los dos
  caminos por orden: primero **apretar el bloque** —549 → 451 tokens, sin tocar las dos listas del
  catálogo ni la regla dura de D-825—, y como Seguridad seguía 36 tokens por encima, **subir el techo
  a 3.600** con las seis medidas escritas (D-911). Y lo que de verdad estaba roto: el test recorre
  ahora `ThemeCatalog.All`, así que una temática nueva se mide sola.

- **F21 · Cortar en `unit_done` sin perder las cuentas** — la llamada de cortesía que F19 midió y no
  pudo quitar (D-865) y que F20 valoró en cerca del 70 % de la entrada de una pasada (D-873) ya no
  se paga. Las cuentas por llamada existían antes del evento final, había que **pedirlas**
  (`--include-partial-messages`): el `message_delta` que cierra un mensaje trae su consumo
  definitivo y **cuadra al token** con el agregado del CLI (D-878). El corte no es matar el proceso
  —eso seguiría perdiendo el consumo del modelo auxiliar, que solo existe en el evento final
  (D-879)—: se **retiene la respuesta de `unit_done`**, lo que impide que el CLI llegue a mandar la
  petición siguiente, y se le pide que se interrumpa, con lo que su evento final llega igual
  (D-880). Suelo de 2 llamadas por pasada → **1**; sobre el escenario de F19, **−67 % de escritura
  de caché** y 6 de 6 pasadas cortadas (D-881). Y si las cuentas no están, **no se corta**: la
  pasada paga su vuelta y el informe dice por qué. Copilot no tenía esta llamada desde F14, porque
  allí `unit_done` es `IsTerminal` (D-883).
- **F20 · El 60 % de la factura era reescribir el prefijo en cada pasada** — el coste se reparte
  ahora por concepto en credits, no en tokens, porque escribir caché cuesta doce veces leerla y dos
  cifras parecidas costaban trece veces distinto (D-871). El diagnóstico de por qué el prefijo no se
  reutiliza entre procesos: la lectura es exactamente la misma cifra en las tres pasadas de una
  unidad —cero reutilización, frontera fija en el bloque del propio CLI—, lo que descarta por
  construcción que se cuele nada nuestro y lo deja como límite del proveedor (D-872). Separar
  lectura de escritura llamada a llamada destapó además que la llamada de cortesía escribe el
  razonamiento de la anterior y es la más cara de la pasada (D-873). La conversación compartida se
  implementó, se midió y se cayó por cobertura (D-874) — y al medirla apareció el agujero que sí
  entra: una pasada en la que el auditor no llama a ninguna herramienta ya no cuenta como seca, así
  que dos turnos mudos no pueden volver a cerrar una unidad que nadie ha barrido (D-875).

- **F19 · Menos llamadas por unidad, sin tocar la cobertura** — primero el desglose, que era el
  trabajo: una pasada son tres llamadas y la tercera no hace nada (D-861). La hipótesis de que las
  pasadas compartían conversación era falsa y se comprueba en el código y en la medida: cada pasada
  ya arrancaba limpia desde F4.1/F14 (D-862). Lo que entra es una regla de prompt —entregar
  veredictos, hallazgos, ubicaciones y cierre en un solo turno, con `unit_done` la última (D-863)—,
  medida a 3,5 → 2,0 llamadas por unidad y −43 % de entrada con los mismos hallazgos, y 2,33 de
  media sobre seis unidades (D-864). Matar el proceso al cerrar la unidad se implementó, se midió y
  se descartó: quitaba la misma llamada pero dejaba la sesión declarando 43 tokens de salida donde
  se consumieron 51.451 (D-865). Y los guardarraíles: techo de llamadas por pasada que dice cuál de
  los dos techos saltó (D-866) y un test que impide pagar las llamadas de menos con un prefijo de
  más (D-867).

- **F18 · Saber a dónde va cada token** — el banco de medida reutiliza el código de producción y
  deja el escenario repetible (D-850), y la lección de método: lo determinista es la PRIMERA
  llamada, no el total (D-851). El orden del prompt, auditado en las dos casas, ya era correcto y
  queda fijado con test —prefijo idéntico entre unidades y pasadas, nada de la unidad dentro, y las
  432 combinaciones idénticas carácter a carácter a la implementación anterior (D-852)—. La palanca
  del system prompt del CLI se midió y salió **neutra**: misma clave de caché, y no hay corte entre
  el prefijo y el código; no entra, y queda desarmada para poder repetir la medida (D-853). La de
  Copilot existe, está nombrada y no se toca sin asiento con el que medir (D-854). Y la
  instrumentación: composición por bloque, consumo por pasada, reparto por fase y duración, todo
  derivado y sin un fichero nuevo en el hub (D-855), con el termómetro de caché declarado como
  suelo y no como predicción (D-856). Lo medido y no cambiado, escrito para que no se vuelva a
  proponer (D-857); la tabla del protocolo con el veredicto de cada cambio (D-858).

- **F17.2 · La cinta deja el calendario: los ciclos son capítulos** — la forma sale de la
  naturaleza del dato: eventos escasos y de duración dispar necesitan orden, no calendario
  (D-845). Una fila por app, bloques de ancho fijo en secuencia con rótulo y fechas, color por
  temática, bloque partido por periodos, huecos contados a partir de una semana (D-846), filas
  alineadas por el final y arranque en el último capítulo (D-847), filtro de periodo por
  pertenencia con aviso de lo que deja fuera (D-848); medido con datos desiguales y capturado en
  los dos temas (D-849).

- **F17.1 · La cinta de ciclos: que diga la verdad y se pueda leer** — el diagnóstico primero: el hub
  guardaba bien la temática, pero el ciclo solo guardaba la última y la cinta colgaba un tramo de
  los hallazgos medidos de una app que nadie había auditado (D-838). El ciclo guarda ahora su
  historial de temáticas con autor y fecha, y la cinta lo pinta partido (D-839); la fila es
  indivisible, con el nombre en columna fija (D-840); sin apertura ni sesión no hay tramo, y la app
  sin ciclos tiene su fila rotulada (D-841); la vista arranca en hoy, la escala es honesta y los
  nombres van con elipsis media (D-842). Y el método: los escenarios de verificación de una
  gráfica incluyen el caso desigual y el vacío (D-843).

- **F17-RETOQUE · El pie que decía los tokens dos veces** — el criterio común del consumo dice
  llamadas → coste → tokens una sola vez en las dos casas (D-835), y el pie es una `FooterLine`
  que o cabe o se abrevia con acceso al detalle, nunca truncada: ceden tokens, luego coste, las
  llamadas nunca (D-836); medido a 1124, 658 y 441 px y visto en los dos temas (D-837).

- **F17 · Ciclos temáticos: cada ciclo elige su lupa** — catálogo cerrado de seis temáticas junto
  a la rúbrica, con qué busca y qué no reporta cada una, y la regla dura: fuera de la lupa no se
  reporta nada (D-824, D-825). El hallazgo lleva la temática del ciclo que lo detectó, lo anterior
  es General, y se filtra en Hallazgos y se lee en la ficha, la tarjeta y el panel (D-826). La
  reconciliación acotada: un ciclo temático juzga solo lo suyo, lo demás envejece y un veredicto
  fuera de lupa se rechaza con error tipado (D-827). El diálogo en el alta, tras el cierre y desde
  el panel, con el cierre heredando y una sola mecánica para cambiar de lupa con trabajo hecho
  (D-828, D-829). El modelo preferido, compartido y avisado sin bloquear (D-830). Y la cinta de
  ciclos en Métricas, con su paleta medida y su honestidad con el pasado (D-831…D-833).

- **F16-RETOQUE · El pie que no contaba y la cabecera que se pisaba** — dos defectos del primer uso
  real, los dos de medición y presentación. **El pie** se quedaba en «0 llamadas · sin tokens
  registrados» con el agente trabajando: el consumo solo se leía del evento que cierra el turno, y
  con Claude un arreglo entero cabe en UN turno porque `ask_user` bloquea dentro de la herramienta.
  Ahora viaja llamada a llamada, descontando los eventos que repiten el mismo mensaje, y se cuadra
  al cerrar (D-817). De paso se cierra lo que D-816 dejó abierto: de las tres cifras del CLI manda
  **`modelUsage`**, que es la única que reproduce su propio coste al último decimal (D-818); y
  aparecieron dos pérdidas —la caché escrita del arreglo guardada como cero, y las llamadas que no
  cabían en ninguna parte para una sesión sin unidades—. **La cabecera** superponía «Pausar» sobre
  «Volver al hallazgo»: un `Grid` sin columnas con dos paneles dentro no reparte, superpone. Nace
  `PageHeader` y lo usan las dos pantallas que tenían el patrón (D-819). 27 tests nuevos, 1.808 en
  total (D-820).

- **F16 · El arreglo asistido con Claude Code, y la cosecha de su estreno** — `IAssistedFixProvider`
  baja al vocabulario común y **Claude Code arregla**, con el **mismo contrato observable**: las
  mismas cuatro herramientas —ahora con nombre y descripción compartidos por los dos drivers, así
  que no pueden divergir (D-805)—, las mismas precondiciones, la misma pantalla y los mismos frenos.
  La conversación viaja por `--input-format stream-json`, verificado contra el CLI real: el
  `session_id` sobrevive entre turnos y `total_cost_usd` viene **acumulado** mientras `usage` es del
  turno, así que el coste de un turno es una resta (D-806). El régimen de permisos sigue siendo el
  de Atalaya y el del CLI se neutraliza por cuatro vías —sin herramientas propias, lista blanca
  cerrada, sin poder preguntar ni autorizar, y sin cargar los ajustes de la máquina, donde viven los
  hooks— (D-807); y el servidor MCP pasa a atender en paralelo porque `ask_user` espera a una
  persona (D-808). Queda escrita la **doctrina de verificación entre casas**: se verifica con el
  proveedor activo, la regla del instrumento distingue auditor de medida y no obliga a repetir
  modelo (D-809). Más la cosecha del estreno: **un solo criterio de coste** en pie e informe, sin
  nombrar un SDK que aquí no existe (D-810); el **proveedor visible** en los cuatro sitios (D-811);
  el **tope del barrido a 6**, que restaura las cuatro pasadas productivas que D-755 se llevó al
  endurecer la parada, con la alternativa de las ubicaciones descartada y por qué (D-812); el
  **callejón del verify** —«mismo commit» no prueba que el fichero no haya cambiado, y un arreglo
  sin commitear es justo ese caso— reproducido y arreglado (D-813); y **verificar deja constancia**,
  con evento que apunta a su sesión e informe propio que dice qué código se le enseñó (D-814).
  42 tests nuevos, 1.781 en total (D-815).

- **BUGFIX-ARRANQUE · La 1.1.3 no arrancaba** — muerte antes de la ventana, en cualquier máquina.
  `v1.1.3` era exactamente el rango F14, y la causa estaba en dos líneas suyas que por separado eran
  inofensivas: un segundo constructor «de comodidad» en `ModelResolver` y `ConnectionChecker`, y el
  registro de `IAuditorProvider` en el contenedor. Juntas, dos firmas igualmente satisfacibles y un
  `ActivatorUtilities` que no elige entre iguales: doce de ochenta y ocho servicios sin resolver,
  `MainWindow` entre ellos. El arreglo **quita el constructor sobrante** en vez de marcar el bueno
  con un atributo: las alternativas funcionan y dejan viva la clase de fallo (D-798). Los 1.661
  tests no podían verlo —el compilador sí sabe desempatar constructores, y ningún test le pedía nada
  al contenedor (D-799)—, así que el remedio es de otra naturaleza: **`Atalaya.exe --selfcheck`**
  monta el contenedor de verdad, resuelve **todos** los servicios registrados y devuelve 0 o 1
  (D-800), y el workflow lo ejecuta **sobre el zip recién comprimido**, antes de crear la Release
  (D-801). De paso, un arranque que falla deja de morir en silencio: lo dice y lo escribe en el log
  (D-802). 5 tests nuevos, 1.739 en total (D-803). **Falta publicar la 1.1.4**, arriba.

- **BUGFIX-SYNC · El updater contra carpetas sincronizadas** — la actualización 1.1.2 → 1.1.3
  abortó contra un `.atalaya-anterior` residual con Atalaya instalada bajo OneDrive. El aborto fue
  limpio y honesto, y eso no se ha tocado; lo que estaba mal era todo lo demás, porque el caso es
  **el entorno corporativo normal**: Escritorio y Documentos redirigidos a OneDrive. Ahora cada
  mover/borrar del relevo **se reintenta unos segundos** —los bloqueos de un cliente de
  sincronización se sueltan solos (D-792)—, un respaldo residual que no se deja retirar **se
  esquiva con un nombre libre** en vez de bloquear la actualización (D-793), y lo que no se pudo
  borrar **deja de olvidarse**: queda apuntado y cada arranque lo reintenta, que es lo que
  probablemente dejó el residuo de este fallo (D-794). El diagnóstico **nombra al culpable y
  receta** —pausar la sincronización o mover la carpeta—, y el banner lo avisa antes de pulsar sin
  bloquear nada (D-795). MANUAL matizado en «Empezar» y «Actualizar» (D-796). 33 tests nuevos
  sobre directorios de verdad con bloqueos simulados, 1.735 en total (D-797). **Falta la
  verificación humana**, arriba.

- **F15 · El coste, en AI credits** — GitHub factura desde el 1 de junio de 2026 en **AI credits**
  (1 = 0,01 $) consumidos **por tokens** a las tarifas de API de cada modelo; las peticiones premium
  —lo que Atalaya llamaba «unidades SDK»— están retiradas. Antes de calcular nada se verificó cómo
  cuenta cada proveedor sus tokens, porque contar la caché dos veces o ninguna desviaría todos los
  costes sin síntoma: **Copilot la incluye en la entrada y Claude Code la excluye**, y la prueba
  definitiva fue reproducir con nuestra fórmula el coste que el propio CLI de Claude Code calcula,
  exacto al sexto decimal (D-785). De ahí salió que **Claude Code usa caché de una hora**, al doble
  de la entrada, contra la de cinco minutos que publica GitHub — un 44 % de desviación por una
  tarifa de caché, y la razón de que una tarifa pueda atarse a un proveedor. Las tarifas son
  **configuración compartida del hub**, editables desde Métricas, con su fecha y sus promocionales
  anotados (D-786). El modelo se lee del registro de cada sesión y **jamás se asume**: sin él, o sin
  tarifa, el coste sale «no aplicable» y el agregado se marca **parcial** con su recuento (D-787).
  Los tokens siguen siendo el hecho primario y **no se ha migrado un solo fichero**: el coste se
  deriva al leer, así que el histórico entero se reexpresa solo — un test lo fija sobre una sesión
  legada, comprobando que da 8 y no los 25 que llevaba escritos (D-788). Con los dos proveedores, la
  misma unidad pero distinto significado: factura contra equivalente API, y el total único solo
  cuando todo el periodo es de la misma naturaleza (D-789). El guarda de ids de modelo se afina otra
  vez en vez de aflojarse, con un test que impide usar la tabla de tarifas como atajo para poblar el
  selector (D-790). 44 tests nuevos, 1.702 en total (D-791).

- **F14 · Segundo proveedor de auditoría: Claude Code local** — Atalaya deja de depender de una sola
  bolsa de cuota. `ICopilotAgent` se convierte en **`IAuditorProvider`** en un ensamblado propio, y
  Copilot pasa a ser su primera implementación sin cambiar una línea de comportamiento; el pipeline
  entero —reconciliación, veredictos, evidencia, huella, informes— sigue **por encima** de la
  interfaz, así que añadir una casa no cambia resultados, solo quién los propone (D-775). El
  arreglo asistido se queda en `IAssistedFixProvider`, de Copilot, y que el compilador lo exija
  impide desviar un arreglo hacia quien no sabe hacerlo. El proveedor elegido se **relee** de los
  ajustes en cada consulta, no se captura (D-776). El transporte es un **servidor MCP propio por
  stdio** con un relé de veinte líneas en medio —el CLI lanza los servidores como hijos suyos y
  Atalaya ya está corriendo— sobre una **tubería con nombre**, no un puerto, y con las **mismas
  tools que ve Copilot palabra por palabra**, que es lo que hace comparables a las dos casas
  (D-777). Todo lo del CLI se comprobó ejecutándolo: el prompt va por **stdin** (en Windows es un
  `.cmd` y `cmd.exe` reinterpretaría el código de dentro), la config MCP va a **fichero**, y
  **`subtype` miente** — manda `is_error`. Y la trampa: con el servidor MCP caído el CLI termina
  «con éxito» y sin herramientas, lo que se leería como una unidad limpia; ahora se para, porque
  «no hay defectos» y «no se pudo mirar» no pueden verse igual (D-778). Los modelos se ofrecen por
  **alias de familia**, que no caducan como caducó el `gpt-5` a mano (D-779). El coste se registra
  **con su unidad pegada** y no se mezcla: Claude Code informa tarifa de lista que su suscripción no
  cobra por llamada, así que Métricas enseña una línea por casa y **ningún total** cuando hay dos
  (D-780). Cuenta enseña los dos proveedores con **GitHub arriba y sin sustituir** —identidad,
  autoría y hub lo necesitan siempre—, Ajustes elige, y el diálogo de lanzar **nombra al juez**
  (D-781). Y una regla que corta de raíz el riesgo de esto: **Claude Code es opcional, siempre** —
  Copilot sigue siendo el único requisito del equipo, y quien no lo instale no ve aviso, ni
  exigencia, ni merma; la fila de Cuenta informa de un extra y jamás reclama, y Ajustes no ofrece lo
  que no está (D-784). Escribir la cobertura destapó tres defectos que habrían roto toda sesión en
  el arranque, y un cuarto que reventaba Cuenta sin cuenta conectada (D-782, D-784). 80 tests
  nuevos, 1.665 en total, más una verificación de punta a punta contra el CLI real (D-783).

- **F12 · La cosecha del banco de pruebas** — el ciclo completo recorrido sobre un repositorio con
  defectos sembrados de severidad conocida. Lo estructural aguantó; lo que salió fueron defectos de
  **juicio y de lectura**, ninguno con un test rojo que mirar. Una no-respuesta del verificador se
  anotaba como **Confirmado**, alimentando «Veces confirmado» con la ausencia de evidencia: ahora
  tiene desenlace propio, «No concluyente», que no toca ningún contador y propone el paso siguiente
  (D-751); su causa raíz era que a la verificación se le enseñaba **solo la línea anclada**, y pasa a
  enseñarse el **símbolo que la contiene** (D-752). La caché de la deriva no se enteraba de las
  resoluciones porque F9.1 añadió la cobertura al cálculo y no a la clave — regla escrita: la clave
  incluye todas las entradas, o el evento invalida (D-753). La escala de severidad estaba inflada e
  **invertida** —siete críticas donde había una, y la de verdad en alta—: criterios con ejemplos y
  reglas de desempate, en un solo sitio versionado (D-754). El barrido paraba con **una** pasada
  seca y ahora pide **dos seguidas**, con el techo mandando (D-755). El silencio por patrón pasa a
  ser **derivado**: retirar el patrón devuelve a activo, gratis, lo que solo él tapaba, y el
  individual se conserva (D-756). El cierre de ciclo deja de ser invisible: aviso discreto con sus
  números y enlace al informe (D-757). Y cinco arreglos de lectura: el resumen agrupado por clase,
  la pasada que cuenta lo que confirmó, los indicadores alineados, el aviso que cabe y el aviso que
  reconoce el arreglo propio (D-758). 56 tests nuevos, 1.539 en total (D-759).

- **BUGFIX-AVISO · El aviso anunciaba una versión que no existe** — el banner decía «Atalaya 1.0
  disponible» con la v1.0.4 publicada. No era una recaída de BUGFIX-VERSION: el chequeo leyó bien
  el tag y decidió bien —el log lo demuestra—, y lo que fallaba era un **segundo formateador**
  (`SemanticVersion.Short`) que dejaba la 1.0.4 en «1.0», un número inexistente que además se lee
  como 1.0.0 (D-745). Se retira: una versión se escribe entera y en un solo sitio, con un barrido
  que impide que nazca otro camino, igual que el de las URLs de D-728 (D-746). El aviso dice ahora
  **las dos** versiones —«Tienes la 1.0.3 · disponible la 1.0.4»—, porque un número solo, sin nada
  con lo que contrastarlo, se lee como verdadero: eso es lo que dejó pasar el defecto durante una
  release entera (D-747). Y en un build local el aviso sale, informativo y sin botón, con el motivo
  escrito (D-748). Verificado a ojo contra la v1.0.4 real (D-749).

- **F11 · Actualizar desde la propia app** — el aviso de versión gana un botón **«Actualizar a
  X.Y.Z»**: descarga el zip de la Release con el token de cuenta que ya hay, verifica su
  **SHA-256** (que el workflow publica junto al paquete), lo descomprime aparte y solo entonces
  cede el relevo a un ejecutable auxiliar que sustituye la carpeta y relanza Atalaya. Nada
  automático: no descarga en segundo plano, no instala al arrancar, no se ofrece con una sesión en
  curso ni en un build local (D-737, D-738). **Velopack se evaluó de verdad** —contra una Release
  privada real, no sobre la documentación— y se descartó por lo que le hacía al paquete, no por lo
  que no sabía hacer (D-736). Los datos del usuario ya estaban fuera de la carpeta desde el primer
  día, así que no hubo nada que migrar; lo que sí vive dentro y ahora se conserva es el
  `appsettings.deploy.json` del despliegue (D-735, D-739). La sustitución son renombrados en el
  mismo volumen, con vuelta atrás automática si falla a mitad y la versión anterior guardada hasta
  que la nueva arranca (D-740). Probado de punta a punta con dos paquetes reales y una Release
  privada de verdad (D-743).

- **BUGFIX-VERSION · «Acerca de» decía 1.0.0 y enlazaba a un 404** — un build local se estampa ahora
  desde `git describe` como `1.0.3-dev+<sha>` y se lee como «build local», así que no puede
  confundirse con una release; el workflow sigue siendo el único que produce un número limpio
  (D-727). Los dos enlaces salen de `appRepoUrl` —el manual derivado de él— y sin ese ajuste no se
  enseña ninguno, con su explicación (D-728). El chequeo de versión NO estaba roto: estaba dentro
  de su ventana de 24 h, y el log lo demuestra; lo que se ha definido es que un build local compare
  con su versión BASE (D-729). El camino de release, verificado reproduciéndolo (D-730).
- **BUGFIX-REDONDEO · El redondeo no puede inventarse un 0 % ni un 100 %** — 3 unidades de 1.335 se
  enseñaban como «0 %»: un número que miente por redondeo y que borra el trabajo hecho. Ahora hay UN
  formateador (`PercentText`) con la regla «el redondeo nunca crea un extremo falso», precisión
  adaptativa y `< 0,1 %` / `> 99,9 %` para los huecos, con los extremos decididos por los enteros y
  no por la división (D-721, D-722). Lo usan Métricas, los roscos y sus tooltips, el Portafolio, los
  informes y hasta el progreso de clonado, y un test impide que vuelvan a nacer formateos sueltos
  (D-723). El rosco dibuja dos grados de suelo para que un tramo real no se confunda con uno vacío
  (D-724). Verificado con los datos reales: 0,2 % donde antes decía 0 % (D-726).
- **BUGFIX-CIERRE · Cerrar lo fallido, y que el Portafolio deje de mentir** — los claims son la
  fuente de «auditando ahora» y solo los soltaba el cierre ordenado, así que un fallo dejaba la
  tarjeta mintiendo y el fichero del claim en el hub para siempre; ahora hay un solo liberador al
  que llaman los tres finales posibles, y el arranque suelta además los claims de esta máquina que
  quedaron sueltos sin marca detrás (D-714, D-715, D-717). El margen de silencio pasa a decidirlo
  quien lee, 30 minutos, sin borrar nada de nadie (D-716). Y una pantalla terminal se puede
  **Cerrar**: se archiva, sale del rail y no borra historia — que no es lo mismo que «Descartar
  todo», y con ficheros tocados se pregunta (D-718).
- **BUGFIX-CUOTA · Sin créditos no es sin asiento, y los errores se leen** — la cuota agotada se
  clasificaba como «sin asiento» porque «quota» vivía dentro del detector del asiento: causa falsa y
  remedio opuesto. Ahora hay UN clasificador (`CopilotFailure`) con seis diagnósticos, cada uno con
  su mensaje y su remedio, el más específico primero, y lo que no se reconoce enseña el error crudo
  del proveedor con su Request ID en vez de proponer una causa (D-706, D-707, D-708). La cuota a
  mitad de barrido ya no tira el trabajo pagado: se corta ahí, se cierra ordenadamente y el resumen
  lo cuenta (D-709). Y el aviso de error pasa a tener fila propia —se solapaba con el cuerpo—,
  seleccionable, copiable y con el crudo plegable acotado (D-710).
- **F9.2 · La deriva se cobra en la frontera del ciclo** — empezar un ciclo pasa a ser **sembrarlo**:
  la auditada sin deriva conserva su estado y su ancla, la cambiada y la que no tiene historial
  nacen pendientes perdiendo la marca, y la arreglada pendiente de verificar conserva estado y
  acción (D-700). Sin clon con el que comparar, todo pendiente, que es la misma regla aplicada a lo
  que no se puede demostrar (D-701); «Reiniciar ciclo» sigue siendo el gesto explícito de mirarlo
  todo y no se siembra (D-702). Y el cierre no maquilla: dice qué queda envejecido con los dos
  números separados, y calla cuando están a cero (D-703).
- **F9.1 · La verificación cierra el ciclo, y el panel se ordena** — un arreglo propio cuyo hallazgo
  queda resuelto por verificación (o por medida) pasa a estar **cubierto**: sus commits dejan de
  contar y la unidad vuelve a «sin cambios» sin re-auditar. El umbral de tres cuenta solo los no
  cubiertos. Todo DERIVADO de hechos que ya vivían en el hub —la huella dice qué hallazgo arreglaba,
  el hallazgo dice cómo se resolvió—, sin un solo campo nuevo (D-695, D-696). Y el panel del ciclo
  pasa de lista corrida a tres bloques —Ciclo · Deriva · Gobernanza— con la rama como subtítulo del
  suyo y colapso a una línea cuando no hay deriva; visto en la ventana, en los dos temas y a dos
  anchos (D-697, D-699). El manual cuenta UN solo flujo —auditas, arreglas, verificas— y deja la
  deriva donde está: como anotación del inventario, no como paso (D-698).
- **F9 · Auditar lo que ha cambiado** — la deriva, derivada del historial local y nunca persistida:
  qué clases han cambiado desde que se auditaron, con su conteo de commits y su fecha, y los cinco
  estados honestos cuando el historial no coopera (commit ausente, reescrito, clon por detrás, rama
  no por defecto, árbol sucio). Guardarraíl anti-bucle que reconoce los arreglos de la propia
  aplicación **por el contenido que dejaron** —Atalaya no commitea (D-556), así que no hay hash de
  commit que guardar— y los aparta como «arreglada — pendiente de verificar» hasta tres. Indicador
  ortogonal, filtro propio y «Seleccionar cambiadas» en el Inventario; indicador clicable en el
  Portafolio; «Resolver por código eliminado» con atribución y el commit del borrado como evidencia;
  y `trigger: deriva` en la sesión, solo guardado. Medido sobre xblast (925 unidades, 3.621
  commits): **311 ms** en el caso normal, tras eliminar el diff de árbol a árbol que costaba 9,8 s
  por grupo (D-688).

- **F10.3 · Retirada del Mapa de calor** — la vista, su modelo, su agregador, la rampa magma, el
  treemap, las tarjetas, la fórmula de «Atención», los pesos de severidad y la franja de densidad
  que el mapa había puesto en el inventario salen del producto: con cobertura baja no informaba, y
  el problema de legibilidad reaparecía en módulos grandes y en aplicaciones sin jerarquía
  (D-680…D-682). Se retiran con ella las tres entradas en vuelo que pedían aceptarla y la de los
  umbrales. Lo entregado en F10…F10.2b se queda escrito aquí abajo y en DECISIONS: es historia.

- **F10.2b · La barra de mandos** — envuelve por grupos en vez de recortar (la columna estrella era
  el defecto, no los anchos), cuatro grupos separados por función, anchos medidos contra el control
  real, y la frase de la cabecera describiendo el nivel que se está viendo.
- **F10.2 · Módulos primero** — el nivel 1 pasa de 925 celdas grises a ~22 tarjetas de módulo con
  cobertura, densidad y severidades, ordenadas por **Atención** (`0,6 × riesgo + 0,4 × ignorancia`,
  documentada y explicada en el tooltip de cada tarjeta); el treemap se queda dentro de un módulo,
  con gris plano; termómetro de la aplicación en la cabecera, filtro «solo auditadas», layout en
  fila estrella sin huecos al maximizar y exportación PNG del nivel 1. Cargar la vista baja de
  27-31 ms a **14,8 ms**; ampliar a un módulo de 598 unidades, 2,7 ms (0,3 cacheado).
- **F10.1c · Los módulos, sin el nombre de su aplicación** — `XBLASTCore` se rotula `Core` en las
  bandas del mapa y `Documents` sale entero, con la regla explicada en una frase en la cabecera.
  Cálculo de vista por aplicación (nombre o slug, sin distinguir mayúsculas, ≥3 caracteres, sin
  partir palabra, resto ≥2 y sin dejar dos bandas rotuladas igual); el nombre completo, intacto en
  tooltip, tabla, inventario, migas, PNG exportado y hub. En el clon real: 21 de 22 módulos
  acortados. Sustituye el criterio de prefijo común, que en XBLAST no llegaba a activarse.
- **F10.1 · Rampa, textos y tabla del mapa** — rampa magma (violeta→ámbar) de claridad monótona
  en un único recurso compartido por treemap, leyenda, tabla e inventario, con la tinta verificada
  por paso; cero textos cortados en seco (se mide, se acorta por el medio o no se escribe) y
  agregados «+N unidades» que no esconden ni tranquilizan; y una tabla con las cabeceras alineadas
  como sus columnas, cifras tabulares, franja de densidad por fila y **un solo scroll**.
- **F10 · Mapa de calor (V9)** — treemap de dos niveles con el área por LOC y el color por densidad
  de deuda ponderada (Crítica 10 · Alta 5 · Media 2 · Baja 1, en `DebtWeights`), escala secuencial
  de un solo tono con umbrales fijos en la leyenda, tratamiento propio e irrenunciable para lo **no
  auditado** (gris tramado = densidad desconocida, nunca el paso frío), tooltips, zoom con migas,
  navegación a hallazgos y a auditar, tabla equivalente ordenable y exportación PNG con título,
  leyenda y pie. Medido contra el clon real de xblast (925 unidades): agregar 18–24 ms, cargar la
  vista 27–31 ms, repintar 23 ms.
- **Banner del snippet según el estado del hallazgo** — F6.7: un hallazgo resuelto ya no enseña el
  aviso ámbar de código cambiado.
- **F6.4 · Identidad visual** — icono `.ico` de la aplicación, logotipo Maxam (claro y negativo) en
  bienvenida, Cuenta y pie de informes, y ventana «Acerca de».
- **F6.5 · Roscos de severidad por aplicación** en Métricas, en la fila bajo cobertura.
- **H9 · «Arreglar con agente»** — F6.9: sesión interactiva sobre el clon local con narración,
  elicitación, snapshots y descarte. Cierre y arreglos en F6.10.
- **F8.1 · Política de formato** — Atalaya escribe números y fechas en es-ES (la app es
  monolingüe en español y sus informes se comparten), con la frontera «texto para personas → es-ES,
  datos para máquinas → invariante» cubierta por tests desde culturas hostiles. Arregla el único
  test que tumbó el estreno del release.
- **F8 · Distribución por GitHub Releases** — versión única en `Directory.Build.props` inyectada
  desde el tag, workflow de release (tag o disparo manual) con tests, publish self-contained, zip
  y Release idempotente, y el aviso de versión nueva en la app con el token de cuenta ya existente.
- **F7 · Directivas del proyecto** — detección por catálogo, panel de gestión con ámbitos,
  prioridad y presupuesto visible, registro merge-friendly en el hub (el contenido se queda en el
  repo de cada app), las directivas informando auditoría, verify y los dos flujos de arreglo con la
  jerarquía declarada, `criterio.directivas` y la traza en el informe.
- **H9.1 · Volver al hallazgo y compilación con línea base** — camino de vuelta en los cuatro
  puntos, ámbito de compilación por proyecto, delta contra línea base por commit y proyectos de
  C++ fuera del veredicto con nota. Más la situación de tests resuelta por la aplicación antes de
  abrir la sesión, y el aterrizaje en la pantalla de cierre.
