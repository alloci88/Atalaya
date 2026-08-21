# DECISIONS.md — Atalaya

Registro de decisiones tomadas en zonas **[LIBERTAD]** o ante ambigüedades no
bloqueantes del prompt de construcción. Las decisiones **[NO NEGOCIABLE]** del
prompt no se repiten aquí salvo para anclar un detalle de implementación.

## Toolchain / entorno

- **D-000 — .NET 8 SDK ausente en la máquina de build.** Al arrancar solo estaba
  instalado el SDK 5.0.408, pero el *runtime* .NET 8 (`Microsoft.NETCore.App
  8.0.16` y `Microsoft.WindowsDesktop.App 8.0.16`) sí. Se instaló el SDK 8 con
  `winget install Microsoft.DotNet.SDK.8`. Prerrequisito duro para §11
  ("compila y pasa tests").

## Targets

- **D-001 — TFM por proyecto.** El prompt fija `net8.0-windows` como "forma".
  Solo `Atalaya.App` necesita el desktop pack de Windows, así que:
  - Librerías (`Domain`, `Storage`, `Inventory`, `Copilot`, `ImportV4`) → `net8.0`
    (portables, tests rápidos, sin acoplar el dominio a Windows).
  - `Atalaya.App` → `net8.0-windows` con `<UseWPF>true</UseWPF>`.
  Ningún requisito material se rompe: el binario final es .NET 8 sobre Windows.

- **D-002 — ULID propio en vez del paquete NuGet.** §2 permite "el paquete `Ulid`
  o implementación propia testeada". `Atalaya.Domain` debe ser *dependency-free*;
  para no introducir NuGet en el dominio se implementa un ULID propio (Crockford
  base32, timestamp 48 bits + 80 bits aleatorios) con tests de round-trip,
  orden y monotonía. Ver `Ids/Ulid.cs`.

## Serialización

- **D-003 — Valores de enum en el "wire".** Los enums del dominio usan nombres
  C# PascalCase (`Severity.Critica`). El mapeo al formato exacto en disco del §2
  (`"critica"`, `"lotes"`, `"criterio"`…) se hace con convertidores en
  `Atalaya.Storage`, no con atributos en el dominio, para mantener `Domain` puro.

## H2 — Storage / sync

- **D-004 — LibGit2Sharp 0.31.0** fijado. Rebase real (`repo.Rebase.Start/Continue`)
  para el bucle `fetch → rebase → push` del §3; la resolución de conflictos por
  categoría (claims/inventory/findings) se implementa como funciones puras en
  `HubMergePolicy` y se aplica sobre el índice durante el rebase.
- **D-005 — Enum wire-values vía convertidores en Storage** (no atributos en Domain):
  `EnumJsonConverter<T>` con mapa explícito. Valores exactos del §2
  (`critica`, `falso-positivo`, `severityChanged`…). JSON con LF + newline final
  para que dos escritores produzcan bytes idénticos.
- **D-006 — Nombre de fichero de silences/claims** = parte hex tras `sha256:`
  (los `:` son ilegales en rutas Windows). El fingerprint/unitHash completo vive
  dentro del JSON.
- **D-007 — Credenciales / polling / cola offline**: el `HubSyncService` ya opera
  offline (deja commits locales, `Health` ámbar) y acepta un `CredentialsHandler`.
  El timer de polling (60 s) y el Windows Credential Manager se cablean en la capa
  App (H4/H5), que es donde viven el hilo de UI y los ajustes.

## H3 — Inventory

- **D-008 — Enum de dominio `Stack` → `TechStack`.** Colisionaba con
  `System.Collections.Generic.Stack<T>` (implicit usings) en todos los proyectos
  que hicieran walks de directorios. Renombrado a `TechStack`; la propiedad
  `AppConfig.Stack` conserva el nombre. Wire-value sin cambios.
- **D-009 — LOC = número de líneas** (código+comentarios+blancos); un `\n` final
  no infla el conteo. `contentHash` = SHA-256 de bytes. "grande" = LOC o chars
  sobre umbral. El hallazgo auto de unidad grande tiene título constante (el LOC
  va en la descripción) para que el fingerprint dedupe entre ciclos.
- **D-010 — Detección de rename** por igualdad de `contentHash` con ruta distinta;
  el estado auditado se arrastra. "grande" recién calculado gana sobre el estado
  arrastrado.

## H4 — App shell

- **D-011 — WPF-UI (Lepo) 3.0.5** como librería de tema (frente a ModernWpfUI):
  mantenimiento activo, Fluent real, `FluentWindow` + `ApplicationThemeManager`
  para claro/oscuro conmutable. Justifica "nada de gris-WinForms".
- **D-012 — Navegación VM-first** con `DataTemplate DataType` mapeando cada page-VM
  a su vista; `NavigationService` resuelve VMs desde el Generic Host (DI).
- **D-013 — Config máquina-local** (`settings.json`, `machines.json`) fuera del hub;
  PAT cifrado con **DPAPI** (`ProtectedData`, CurrentUser). Sin almacén propio.
- **D-014 — `GlobalUsings.cs`** con `global using System.IO;` porque el pase de
  compilación de XAML (`_wpftmp`) no hereda los implicit usings del SDK. Los
  proyectos de test que solo referencian la app quitan `UseWPF` por el mismo motivo.
- **D-015 — Dashboards calculados** (`PortfolioQuery`) desde datos primarios; nada
  de dashboards en disco (mejora 8). `TimeProvider` inyectable para tests.

## H5 — Copilot

- **D-016 — `GitHub.Copilot.SDK` 1.0.11** (fijado). El paquete EXISTE (namespace
  `GitHub.Copilot`). Verifiqué la superficie real por reflexión: el prompt describía
  una 0.1.x simplificada; la 1.0.11 difiere:
  - Permisos: no hay `OnPermissionRequest` en el cliente sino en
    `SessionConfig.OnPermissionRequest = Func<PermissionRequest, PermissionInvocation,
    Task<PermissionDecision>>` (2 args, `Task`, como decía §6.2). `PermissionDecision.Reject/ApproveOnce`
    en `GitHub.Copilot.Rpc`.
  - Tools: `SessionConfig.Tools` es `ICollection<AIFunctionDeclaration>`; `CopilotTool.DefineTool`
    devuelve `AIFunction` (Microsoft.Extensions.AI 10.2.0).
  - Uso/coste: evento `AssistantUsageEvent.Data` (`AssistantUsageData`: InputTokens/OutputTokens/Cost/Model).
    Aislado en `UsageAdapter` (§6.3); campos leídos con `Convert.*` por si cambia el tipo numérico.
  - Varios miembros son `[Experimental]` (GHCP001) → opt-in con `NoWarn` y encapsulados.
- **D-017 — Seam `ICopilotAgent` + fake obligatorio** (§11). `FakeCopilotAgent` ejercita
  todo el pipeline sin asiento; los tests end-to-end de lotes/implícita/silencio lo usan.
  `RealCopilotAgent` compila contra 1.0.11 pero su ruta de ejecución necesita asiento
  Copilot (no disponible en CI): verificado en compilación, no en runtime.
- **D-018 — `read_signatures`** es heurístico (líneas que parecen firmas) en H5;
  extracción Roslyn para C# queda como mejora. Se declara en notas de sesión cuando falta.

## H6 — Gobernanza

- **D-019 — AvalonEdit 6.3** para el snippet de V4 (`SyntaxHighlighting="C#"`). Como
  `TextEditor.Text` no es DP bindable, se refleja `Snippet` del VM por code-behind.
- **D-020 — V3 agrega hallazgos de TODAS las apps** (o filtra por una); orden
  severidad×confianza×frescura. Silenciar/asignar/verify en línea y masivo; atajos
  s/a en la vista. "Abrir en editor" vía `EditorLauncher` (VS por defecto, `code -g`).
- **D-021 — Re-anclaje por snippetHash** (`SnippetAnchor`): mismo hash en la línea o
  búsqueda en el fichero; si no se ancla → `needsReview`, nunca "resuelto" (§5.4).
- **D-022 — El prompt de arreglo** se copia al portapapeles y se guarda en `comments/`
  con `kind="fix-prompt"`. Arreglar nunca resuelve; el estado resuelto solo por las
  vías 1–3 (§5.7).

## H7 — Ciclos e informes

- **D-023 — Ascenso de confianza en cierre**: se promueven a `alta` los hallazgos
  `activo` con confianza `media` (interpretación de "confirmados en el ciclo"), vía
  `Confirm(Cierre, cycleClose:true)`. `baja` no asciende en cierre.
- **D-024 — Guardia de concurrencia del cierre**: `TryCloseCycle` hace pull, y solo
  cierra si sigue viendo `CurrentCycle==esperado` y 0 pendientes; si otro ya avanzó,
  desiste. El push de `app.json` (remote-wins) y el merge de inventario hacen el
  doble-cierre convergente/idempotente.
- **D-025 — ESTADO.md** de cortesía se escribe solo-lectura en el clon si
  `app.exportStatusMd`; es el ÚNICO fichero que Atalaya escribe en el repo auditado.

## H8 — Importador, métricas, empaquetado

- **D-026 — ImportV4 tolerante**: parseo por bloques (heading + bullets `clave: valor`),
  normalización de acentos/case, fallback de pilar por prefijo de ID; nunca falla la
  importación entera por una entrada corrupta (se registra en el log). Fixture
  `CodeAudit/` con 10 hallazgos, 2 silencios, ciclo a medias; probado sin pérdida (§11).
- **D-027 — Gráficas V6 en WPF puro** (barras) frente a LiveCharts2/ScottPlot
  **[LIBERTAD]**: evita dependencias nativas (SkiaSharp) no verificables en el entorno
  de build headless. `MetricsQuery` (el cálculo, con `TimeProvider` inyectable) es
  agnóstico a la librería y está testeado; cambiar a LiveCharts2 es sustituir la vista.
- **D-028 — Empaquetado: `dotnet publish` win-x64** (framework-dependent o self-contained)
  vía `scripts/publish.ps1`, en lugar de MSIX **[LIBERTAD]**: sin firma ni certificados,
  reproducible en cualquier máquina con el SDK. Verificado: produce `dist/Atalaya.exe`.

## Post-entrega — Robustez de autenticación Copilot

- **D-029 — Chequeo de auth real + pantalla de ayuda (§6.1).** El error crudo del SDK
  ("session was not created with authentication info or custom provider") aparecía al
  auditar cuando el CLI `copilot` no había hecho login. Ahora:
  - `ICopilotAgent.CheckAsync` usa `CopilotClient.GetAuthStatusAsync().IsAuthenticated`;
    `RealCopilotAgent` falla rápido con `CopilotAuthenticationException` (texto de ayuda
    `CopilotHelp.NotAuthenticated`) en vez del error crudo, y mapea errores de sesión que
    parezcan de auth.
  - V5 muestra ese texto; **Ajustes** tiene un botón **"Comprobar Copilot"** para verificar
    el login sin lanzar una auditoría.
  - Recordatorio de diseño: el login de Copilot NO ocurre dentro de Atalaya; es un
    `copilot` + `/login` único por máquina (el verde de la barra es solo git/hub, no Copilot).

## H9 — Arreglo integrado supervisado (opcional, NO entregado)

- El *feature flag* `enableAssistedFix` existe en Ajustes y el generador de prompt de
  arreglo (§5.7, vía 4→2) está entregado y probado. El **arreglo integrado supervisado**
  (rama `fix/{displayId}` + permission handler por-fichero + diff aprobado + sin push)
  queda como trabajo futuro; el prompt indica que H9 "no bloquea la entrega del resto".
