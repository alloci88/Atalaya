# Backlog Atalaya

Lo que queda por hacer, y lo que se decidió no hacer todavía. Vive en el repo y se mantiene al día
igual que `MANUAL.md` y `DECISIONS.md` (norma **N-4**): cada fase mueve a «Cerrado» lo que entrega
y apunta lo que deja pendiente. Un backlog que solo ve una persona no es del equipo.

Última revisión: 2026-08-28 (F7 — directivas del proyecto).

## En vuelo

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
- `dist` **self-contained** (`pwsh scripts/publish.ps1 -SelfContained`): hoy se publica
  framework-dependent y exige el runtime .NET 8 en la máquina destino.
- **Sellar la versión en el build** (verificado al cerrar H9): ni `Atalaya.App.csproj` ni
  `Directory.Build.props` fijan versión, así que «Acerca de» dice «Versión 1.0.0» en todos los
  binarios y no distingue un `dist` de otro. Con varias copias repartidas por el equipo, eso es
  lo que separa «no tienes lo último» de «hay un fallo».
- Acompañar los primeros onboardings y recoger la fricción.
- Conversión del coste a **euros**: medir ~10 unidades contra el panel de consumo de Copilot antes
  de poner un número en la interfaz. Ahora que las verificaciones registran su gasto (D-590), la
  medición tiene que incluirlas: hasta H9.2 el total del panel solo contaba auditorías y arreglos.

## Aplazado a decisión

- **Integración con Microsoft Planner.** El prompt de F6.2 está listo; falta el registro de la
  aplicación en Entra ID y decidir el momento.

## Aparcado hasta que la realidad lo pida

- **H9 ampliado**: mejoras sobre la sesión interactiva, según lo que pida el uso real.
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

- **Banner del snippet según el estado del hallazgo** — F6.7: un hallazgo resuelto ya no enseña el
  aviso ámbar de código cambiado.
- **F6.4 · Identidad visual** — icono `.ico` de la aplicación, logotipo Maxam (claro y negativo) en
  bienvenida, Cuenta y pie de informes, y ventana «Acerca de».
- **F6.5 · Roscos de severidad por aplicación** en Métricas, en la fila bajo cobertura.
- **H9 · «Arreglar con agente»** — F6.9: sesión interactiva sobre el clon local con narración,
  elicitación, snapshots y descarte. Cierre y arreglos en F6.10.
- **F7 · Directivas del proyecto** — detección por catálogo, panel de gestión con ámbitos,
  prioridad y presupuesto visible, registro merge-friendly en el hub (el contenido se queda en el
  repo de cada app), las directivas informando auditoría, verify y los dos flujos de arreglo con la
  jerarquía declarada, `criterio.directivas` y la traza en el informe.
- **H9.1 · Volver al hallazgo y compilación con línea base** — camino de vuelta en los cuatro
  puntos, ámbito de compilación por proyecto, delta contra línea base por commit y proyectos de
  C++ fuera del veredicto con nota. Más la situación de tests resuelta por la aplicación antes de
  abrir la sesión, y el aterrizaje en la pantalla de cierre.
