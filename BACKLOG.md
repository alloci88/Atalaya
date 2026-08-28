# Backlog Atalaya

Lo que queda por hacer, y lo que se decidió no hacer todavía. Vive en el repo y se mantiene al día
igual que `MANUAL.md` y `DECISIONS.md` (norma **N-4**): cada fase mueve a «Cerrado» lo que entrega
y apunta lo que deja pendiente. Un backlog que solo ve una persona no es del equipo.

Última revisión: 2026-08-28 (H9.1).

## En vuelo

- **H9 — verificación humana con asiento real.** El flujo interactivo está verificado por el
  usuario con una sesión de verdad; falta cerrar el circuito con el **descarte** (que el clon
  vuelva byte a byte) y la revisión visual de la vista de arreglo con textos largos, en los dos
  temas y a 1366×768 (D-569).
- **H9.1 — que una sesión fix real sobre XBLAST diga «0 errores nuevos»** con los preexistentes
  aparte (D-581). El resolutor de ámbito ya está medido contra ese clon; falta la sesión entera
  con asiento.

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
  de poner un número en la interfaz.

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
- **H9.1 · Volver al hallazgo y compilación con línea base** — camino de vuelta en los cuatro
  puntos, ámbito de compilación por proyecto, delta contra línea base por commit y proyectos de
  C++ fuera del veredicto con nota.
