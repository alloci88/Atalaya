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

## F2 — Rediseño del setup y la conexión

### F2.1 — Verificación de la superficie real (antes de tocar UI)

- **D-030 — Superficie de auth del SDK 1.0.11 (verificada contra el paquete instalado).**
  Comprobado por reflexión sobre `GitHub.Copilot.SDK.dll` (net8.0) y su XML de documentación:
  - **Opción de token**: `CopilotClientOptions.GitHubToken` (`string`). La doc del paquete dice
    literalmente *"When provided, the token is passed to the runtime via environment variable.
    This takes priority over other authentication methods."*
  - **Flag de usuario logueado**: `CopilotClientOptions.UseLoggedInUser` (`bool?`). *"Default: true
    (but defaults to false when GitHubToken is provided)."* Aun así lo ponemos **explícito a
    `false`** cuando hay token de cuenta, para no depender de un default.
  - **Estado de auth**: `CopilotClient.GetAuthStatusAsync()` → `GetAuthStatusResponse`
    (`IsAuthenticated`, `AuthType` ∈ {user, env, gh-cli, hmac, api-key, **token**}, `Host`,
    `Login`, `StatusMessage`). Es la comprobación **más barata**: no crea sesión.
  - **Comprobación de asiento**: `GetAuthStatusAsync` solo dice si hay credencial. Para separar
    *"sin asiento"* de *"no autenticado"* se usa `ListModelsAsync()`, que la doc marca como
    cacheada tras la primera llamada y que sí ejercita el *entitlement* de Copilot. Sigue siendo
    más barato que crear y desechar una sesión.

- **D-031 — El CLI ya viene embebido; lo forzamos explícitamente.** Los targets del paquete
  (`build/GitHub.Copilot.SDK.targets`) descargan el CLI en build-time y lo copian a
  `$(OutDir)runtimes/{rid}/native/copilot.exe`, registrándolo además como `ContentWithTargetPath`
  para que fluya por *project reference* hasta `Atalaya.App` (verificado: el binario está en
  `bin/Debug/net8.0-windows/runtimes/win-x64/native/`). El SDK lo resuelve solo
  (`CopilotClient.GetBundledCliPath` → `AppContext.BaseDirectory/runtimes/{rid}/native/copilot.exe`,
  invocado por reflexión en la sonda) y `Connection == null` ⇒ `RuntimeConnection.ForStdio(null)`
  ⇒ *bundled runtime*. Aun así pasamos la ruta **explícita** vía `CopilotCliLocator.ResolveBundled()`:
  así el requisito "siempre el binario embebido, nunca el PATH" queda **testeado**, y un despliegue
  roto (sin `runtimes/`) se ve en el log en vez de degradar en silencio.
  **Consecuencia: `npm install -g @github/copilot` deja de ser requisito.**

- **D-032 — Device flow: solicitud de código verificada en vivo; la autorización, no.** La OAuth App
  ya está registrada (prerrequisito humano cumplido) y su `client_id` **`Ov23liC0Kt139QXJauYV`**
  va en `appsettings.deploy.json`. Verificado contra GitHub de verdad:
  `POST https://github.com/login/device/code` con ese `client_id` y los tres scopes devuelve
  **200** con `{device_code, user_code, verification_uri, expires_in: 899, interval: 5}` — o sea,
  el id es válido, **Enable Device Flow está activo** y la respuesta tiene exactamente la forma que
  parsea `RequestCodeAsync`.
  Lo que **no** está verificado end-to-end es la segunda pata: teclear el código en
  `github.com/login/device`, autorizar y recibir el `gho_`. Eso exige una persona delante de un
  navegador. La máquina de estados de esa pata (pending → slow_down con backoff → success,
  caducidad, denegación, device flow deshabilitado) sí está cubierta por tests con el endpoint
  OAuth simulado y reloj/espera inyectados.

### F2.2–F2.4 — Diseño

- **D-033 — OAuth App, no GitHub App [LIBERTAD].** El prompt deja libre la elección. Se elige
  **OAuth App con device flow**: los `gho_` no caducan por defecto, así que no hay que implementar
  ni almacenar *refresh tokens* (los `ghu_` de GitHub App caducan a las 8 h y obligarían a un
  refresco transparente). Menos piezas, menos estado, mismo resultado. La migración a la
  organización se resuelve re-registrando la app allí (o aprobándola) y cambiando el `client_id`
  en el fichero de despliegue. Si algún día hace falta GitHub App, el cambio queda confinado a
  `GitHubDeviceFlow` + `GitHubAccountService`.

- **D-034 — `appsettings.deploy.json` embebido *y* en disco.** El JSON se embebe como recurso
  (default de fábrica) **y** se copia junto al ejecutable (`CopyToOutputDirectory`). Resolución:
  disco → embebido; un override corrupto no puede dejar la app inservible. El `client_id` **no es
  secreto** (device flow no usa client secret), así que va en claro: no se incumple el anti-objetivo
  de "no incrustar secretos".

- **D-035 — Cuenta es una *página*, no una ventana modal.** Coherente con D-012 (navegación
  VM-first): entra en el rail, en el clic del avatar de la barra de estado y en el primer arranque,
  sin gestionar una ventana aparte ni su ciclo de vida.

- **D-036 — Precedencia de credencial: cuenta > PAT > gestor de credenciales del SO.**
  `HubContext.CredentialSource` lo expone (y lo testean los tests). Ambas rutas comparten forma:
  `UsernamePasswordCredentials { Username = "x-access-token", Password = token }` — la que ya
  usaba el PAT contra GitHub, así que el token de cuenta entra sin tocar `HubSyncService`.
  `HubContext` reconstruye el `HubSyncService` cuando cambia la credencial o la identidad, para que
  conectar/desconectar/cambiar de cuenta surta efecto sin reiniciar la app.

- **D-037 — Identidad de commits derivada del perfil.** Nombre = `name` (o `login`); email = el
  público si existe, si no el `noreply` `{id}+{login}@users.noreply.github.com`. Así basta el scope
  `read:user` (no hace falta `user:email`) y nunca se filtra un email privado. El formulario manual
  de nombre/email desaparece de Ajustes; los valores antiguos sobreviven como *fallback* para
  usuarios sin cuenta conectada.

- **D-038 — Migración silenciosa de usuarios pre-F2.** Al arrancar, una vez:
  - si el `hubRepoUrl` guardado **coincide** con el del despliegue → se descarta (ese usuario pasa
    a seguir el despliegue);
  - si **difiere** → se conserva como `hubUrlOverride` en Opciones avanzadas, porque un equipo con
    otro hub no puede romperse al actualizar;
  - el PAT y la identidad git **no se tocan**: siguen funcionando como hasta ahora.
  Un usuario con PAT y sin cuenta **no** ve la pantalla de bienvenida: va directo al portafolio.

- **D-039 — Expiración/revocación.** Cualquier consumidor que vea un 401 llama a
  `GitHubAccountService.NoteFailure`; la cuenta queda "requiere reconexión", la barra de estado lo
  muestra en ámbar junto al avatar y la página de Cuenta lo explica. Un pull correcto lo limpia.
  Se reconocen tanto el 401 de la API REST como el mensaje típico de LibGit2Sharp
  (*"too many redirects or authentication replays"*).

- **D-040 — Diagnósticos, no errores crudos.** `ConnectionHelp` centraliza los textos (política de
  OAuth Apps de la org, SAML SSO, sin asiento de Copilot, sin red, sin client id) y la tabla del
  README usa exactamente los mismos. `AgentReadiness` gana `AgentProblem` para que "sin asiento"
  no se confunda nunca con "no autenticado" — eran el mismo mensaje antes de F2.

- **D-041 — El fallback antiguo se conserva, no se borra.** Sin token de cuenta, el adaptador de
  Copilot vuelve a `UseLoggedInUser = true` (credenciales del CLI en la máquina) y el texto de
  ayuda `CopilotHelp.NotAuthenticated` sigue existiendo. Igual con el PAT en Opciones avanzadas.
  Anti-objetivo explícito del prompt.

### F2.6 — Ajustes post-entrega (sincronización y diagnóstico)

- **D-042 — Un pull fallido dejaba de verse.** `HubSyncService.Pull` traga los errores de git a
  propósito (operar offline es un estado normal, §3 / D-007), así que "no había nada que traer" y
  "el pull reventó" eran indistinguibles desde fuera. Ahora expone `LastError` (null tras un pull
  correcto) y la comprobación **Acceso al hub** exige `Health == Green` **y** una marca de
  sincronización: antes se ponía en verde solo con que `EnsureHub` no lanzara excepción, que es
  justo lo que producía "3 comprobaciones en verde, piloto ámbar y última sincronización: nunca".
  Si el pull falla, el paso se pone en rojo con el texto real de git y la página de Cuenta lo
  muestra.

- **D-043 — Regresión propia de F2: el hub vacío ya no se montaba.** Al sacar la conexión de
  Ajustes desapareció el botón "Conectar / crear hub", que era el único sitio que escribía
  `hub.json` y hacía el primer push en un hub recién creado. Restaurado dentro de
  `HubContext.EnsureHub` (`InitializeIfEmpty`), y solo con la sincronización sana, para no
  empujar nunca encima de un pull roto. `OrganizationName` sale de `organizationLogin` si está
  configurado, y si no del perfil de la cuenta. Cubierto con un remoto `--bare` local: clonar →
  pull → `hub.json` escrito y presente en el remoto, con la identidad derivada del perfil y sin
  generar commits nuevos al repetir la comprobación.

- **D-044 — El piloto se actualiza por evento, no por polling.** `HubContext.SyncStateChanged` se
  emite tras cada pull y tras montar el hub; `MainViewModel` se suscribe y marshalea al dispatcher
  (los pulls corren en `Task.Run`). Antes, al conectar, `SyncAccount` leía la salud **antes** de
  que el clon terminara y el indicador se quedaba ámbar hasta el siguiente tick de 60 s.

- **D-045 — El login de Copilot sale del perfil, no del SDK.** Autenticando por token,
  `GetAuthStatusAsync` devuelve `authType: "token"` sin `Login`, y el mensaje quedaba en "Copilot
  autenticado como: ?". `RealCopilotAgent` recibe ahora un `loginProvider` (el login ya guardado
  en `GitHubAccountService`) y solo cae a `status.Login` en el camino heredado del CLI, que es
  donde ese valor sí existe.

- **D-046 — El estado de sync es diagnosticable sin logs.** La página de Cuenta muestra la ruta
  del clon local (seleccionable), el estado, la última sincronización y el error de git si lo hay,
  con botones **Sincronizar ahora** y **Abrir carpeta**.

## H9 — Arreglo integrado supervisado (opcional, NO entregado)

- El *feature flag* `enableAssistedFix` existe en Ajustes y el generador de prompt de
  arreglo (§5.7, vía 4→2) está entregado y probado. El **arreglo integrado supervisado**
  (rama `fix/{displayId}` + permission handler por-fichero + diff aprobado + sin push)
  queda como trabajo futuro; el prompt indica que H9 "no bloquea la entrega del resto".
