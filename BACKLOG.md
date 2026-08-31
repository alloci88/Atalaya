# Backlog Atalaya

Lo que queda por hacer, y lo que se decidió no hacer todavía. Vive en el repo y se mantiene al día
igual que `MANUAL.md` y `DECISIONS.md` (norma **N-4**): cada fase mueve a «Cerrado» lo que entrega
y apunta lo que deja pendiente. Un backlog que solo ve una persona no es del equipo.

Última revisión: 2026-08-31 (F9.2 — la deriva se cobra en la frontera del ciclo).

## En vuelo

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
- **Actualización asistida (Velopack) — nivel 3.** Hoy el aviso lleva al navegador y el usuario
  descarga y reemplaza la carpeta (nivel 2). El nivel 3 sería que Atalaya se actualizara ella
  misma: Velopack sobre las mismas Releases de GitHub, con delta y reinicio. **No se hace todavía
  a propósito**: exige cambiar la forma del paquete —de carpeta descomprimible a instalador con
  su propio directorio gestionado—, y eso solo compensa cuando el reemplazo manual moleste de
  verdad. Se decidirá cuando el equipo haya vivido dos o tres actualizaciones y sepamos si duele
  (D-623).
- **Migrar el diff a DiffPlex** si el artesanal falla en los casos finos —cambios intra-línea,
  ficheros grandes, encodings—. Decidido de antemano y sin debate (D-552).
- **Pasadas con «lentes» por pilar**, solo si los barridos siguen dejando hallazgos.
- **`HubMergePolicy`**: riesgo teórico de pérdida de alias en un merge. Sin síntoma observado, y el
  backfill lo recuperaría.
- **Limpieza de los campos vestigiales del fingerprint** en los JSON antiguos.
- **Catálogo de reglas editable en el hub**, solo si su mantenimiento se vuelve frecuente.
- **Verify sobre hallazgos medidos**: mensaje «Confirmado: N LOC ≥ umbral». Cosmético, caso de
  esquina.

## Cerrado

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
