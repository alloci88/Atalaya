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
  del clon local (seleccionable, para copiarla), el estado, la última sincronización y el error de
  git si lo hay, con un botón **Sincronizar ahora**. Se descartó un botón "Abrir carpeta": la ruta
  ya es copiable, así que solo ahorraba un pegado, y abrir el clon invita a editarlo a mano, que es
  justo como se deja el repo en un estado que Atalaya no espera. El diagnóstico debe salir del
  panel de estado, no de que el usuario entre en la carpeta.

### F2.7 — TLS en red corporativa

- **D-047 — Soft-fail de la comprobación de revocación TLS [decisión con impacto de seguridad].**
  En la red de la organización el clon fallaba con *"certificate revocation status could not be
  verified"*. Verificado que ese literal **es de libgit2** (aparece en `git2-3f4182d.dll` junto a
  `CertVerifyCertificateChainPolicy`): libgit2 valida la cadena con las APIs de Windows y
  **hard-failea** cuando no puede alcanzar el respondedor CRL/OCSP — lo normal con un proxy
  corporativo que inspecciona TLS o que bloquea esos endpoints. No es un problema de credenciales
  ni de permisos del repo.
  - Los navegadores hacen **soft-fail** de ese caso concreto, porque quien puede interceptar el
    tráfico también puede bloquear el respondedor: el fallo duro cuesta disponibilidad sin aportar
    apenas seguridad. Atalaya adopta ese criterio **por defecto**.
  - Se relaja **solo** esa condición: `HubCertificatePolicy` revalida la cadena con
    `X509RevocationMode.NoCheck` y exige que siga siendo de confianza, en vigor y que
    `MatchesHostname` case con el host. Una raíz no confiable —que es como se ve un MITM real sin
    la CA corporativa instalada— **se sigue rechazando**; hay tests con un certificado autofirmado
    que lo demuestran.
  - No es silencioso: se registra un warning y la página de Cuenta muestra en qué host no se pudo
    comprobar la revocación, con la recomendación de que IT desbloquee CRL/OCSP (el arreglo
    correcto de verdad).
  - `RequireTlsRevocationCheck` en Ajustes → Opciones avanzadas restaura el hard-fail de libgit2.
  - Viable porque el callback está cableado en este transporte: el binario nativo contiene
    `git_transport_smart_certificate_check` y `user rejected certificate for %s`.

- **D-048 — Los errores de TLS se diagnostican antes que los de red.** *"failed to send request:
  certificate revocation…"* casaba con el patrón de offline y salía como "comprueba la red", que
  manda al usuario en la dirección equivocada. `DescribeHubFailure` evalúa ahora TLS primero y
  distingue dos casos: revocación no comprobable (problema de red corporativa, con la salida por
  Opciones avanzadas) y certificado no confiable (falta la CA corporativa en el almacén).

- **D-049 — El proxy ya estaba cubierto.** `FetchOptions.ProxyOptions` de LibGit2Sharp 0.31 viene
  con `ProxyType.Auto` por defecto, así que el proxy del sistema se usa sin configurar nada; no
  hacía falta tocarlo.

### F2.8 — Migración del hub y el 404 de GitHub

- **D-050 — Fallo propio: cambiar `hubUrl` no movía a los usuarios existentes.** `EnsureCloned`
  solo comprobaba que el directorio fuera un repo git válido; si lo era, lo abría y ya. Con un clon
  existente, cambiar `hubUrl` en el despliegue habría dejado a todo el equipo sincronizando
  **contra el hub viejo, en silencio** — es decir, la promesa de D1 ("migrar = cambiar una línea")
  estaba rota. Ahora `EnsureCloned` compara el `origin` con la URL del despliegue y lo re-apunta,
  conservando el historial local, y lo expone en `RemoteRepointedTo` para que la página de Cuenta
  lo diga.

- **D-051 — Y re-apuntar tampoco bastaba.** Tras el re-apuntado, el destino se quedaba vacío:
  `InitializeIfEmpty` no hace nada (el `hub.json` local ya existe) y no había ninguna escritura
  pendiente que disparara un push. `PublishAfterMigration` empuja el historial local al remoto
  nuevo; si otro usuario migró antes, el `Push` normal rebasa sobre lo que ya hay, que es el
  comportamiento convergente de siempre (§3). Lo destapó el test de migración, no el razonamiento.

- **D-052 — El 404 de GitHub es ambiguo por diseño y hay que preguntarle a la API.** GitHub
  responde **404** para "no existe", para "existe pero no puedes verlo" y para "push sin permiso de
  escritura" — para no filtrar la existencia de repos privados. git no puede distinguirlos, así que
  `DescribeHubFailure` estaba **adivinando**, y adivinaba "política de OAuth Apps de la
  organización" incluso cuando el caso real era "puedo leer pero no escribir" (que fue lo que pasó
  de verdad contra un hub personal desde una cuenta con solo lectura). Ahora, cuando el paso del
  hub falla, se consulta `GET /repos/{owner}/{repo}` con el token y se distingue: `ReadWrite`
  (el problema es otro, se respeta el mensaje de git), `ReadOnly` (pide permiso Write),
  `NotVisible` (no existe o no te han dado acceso; se nombra la URL), `OrgPolicyBlocked`,
  `SamlRequired`, `TokenRejected`. Si la API no puede ayudar (offline, URL no-GitHub) se conserva
  el mensaje original: nunca se degrada un diagnóstico específico a uno más vago.

## F3 — Sesión en vivo: coste, integridad y experiencia

### F3 · Hito 1b — Diagnóstico de coste por unidad (con evidencia instrumentada)

- **D-053 — Instrumentación primero (Hito 1a).** Antes de tocar nada, `SessionCoordinator`
  registra por unidad: nº de llamadas al modelo, tokens in/out/cacheRead/cacheWrite por llamada,
  tamaño estimado del prompt inicial (chars/4) y nº de tool calls. Se persiste en el fichero de
  sesión (`AuditSession.UsageBreakdown`) y se pinta como tabla en el informe (§7). Sin esa tabla
  no hay decisión de optimización posible: la evidencia de abajo sale de ella.

- **D-054 — Evidencia real (una unidad: `CommonStatics.cs`).**

  | Métrica | Valor |
  |---|---:|
  | Prompt inicial estimado | 3.009 tokens |
  | Llamadas al modelo | 37 |
  | Tool calls | 45 |
  | Input tokens | 1.262.937 |
  | Output tokens | 24.005 |
  | Cache read | 1.218.607 (**96,5 %** del input) |
  | Cache write | 44.193 |

  Lecturas:
  - **El caché de prompt del SDK YA ACTÚA** (96,5 % de la entrada). No hay margen barato ahí; no
    se toca.
  - **El brief inicial es irrelevante** (3 k / 1,26 M = 0,24 %). Recortarlo al bloque del stack no
    mueve la aguja. **Descartado** (contra la propuesta inicial del Hito 1c).
  - **El multiplicador son los 37 turnos** del bucle agéntico: un `submit_finding` por hallazgo
    fuerza un turno adicional que reenvía el contexto entero. Aquí es donde se ataca.

- **D-055 — Optimización aplicada: batching de hallazgos.** Se añade la tool `submit_findings`
  (array) sin retirar la singular. El prompt del auditor instruye entregar TODOS los hallazgos de
  la unidad en UNA llamada y llamar a `unit_done` en el mismo turno final cuando sea posible.
  Objetivo orientativo: **< 10 turnos por unidad** frente a los 37 medidos. La singular se conserva
  como fallback tolerante para reintentos del modelo (algunos modelos rompen la instrucción de
  lote la primera vez).

- **D-056 — Salvaguarda `maxTokensPerUnit` (por defecto 300 000).** Nuevo umbral en `Thresholds`,
  editable por app. Cuando el acumulado in+out de una unidad rebasa el techo, la app cancela esa
  unidad, la marca con verdicto **`presupuesto-superado`** (pariente visible de `grande`), libera
  el claim y continúa con la siguiente. Ningún bucle sin techo. No es una optimización: es una red
  de seguridad para que un imprevisto no dispare la factura.

- **D-057 — Etiquetado de la unidad de coste (Hito 1d).** El SDK devuelve `Cost` sin unidad
  determinable de forma estable entre versiones. `UsageAdapter` intenta leer reflectivamente
  `Currency` / `CostUnit` / `Unit` del evento y lo propaga por `UsageSample.CostUnit` →
  `UsageTotals.Currency`. Si no es determinable, la UI y el informe muestran `(unidad SDK)` — nunca
  un número desnudo. Evidencia o incertidumbre declarada.

- **D-058 — Descartes explícitos.** No se recorta el brief (D-054), no se reconfigura el caché
  (ya cachea 96,5 %), no se cambia SDK ni modelo (anti-objetivo). Validación pendiente:
  re-ejecutar la sesión medida sobre `CommonStatics.cs` con el lote activo y comparar tokens y
  turnos con la línea base de D-054.

### F3.1 · Bloque 0 — Visibilidad de cortes y rechazos + retirada de `tag`

- **D-059 — `tag` fuera de la tool `submit_finding(s)`.** El piloto del 2026-08-24 sobre
  `CommonStatics.cs` produjo **25 rechazos consecutivos por `tag` inválido** (valores como
  `errores.calculo.negocio`, `bug`, `correctness`) que consumieron presupuesto y terminaron
  cortando la unidad a 307 k / 300 k tokens con `Nuevos 0` sin explicación. Diagnóstico: el
  `tag` es derivable del `ruleId` (`criterio.*` → Criterio, resto → Checklist), la app ya lo
  hacía en `InferTag`, y pedirlo también al modelo era pura superficie de error. Retirado del
  record `SubmitFindingArgs` (F3.1 en `Contracts.cs`), del delegado singular de
  `RealCopilotAgent` y del prompt del auditor; la ingestión sigue tolerando payloads legados
  que lo traigan (se ignora). Test `Tag_is_always_inferred_from_ruleId` blinda ambas ramas
  para que la regresión no reaparezca.

- **D-060 — Visibilidad forzada de cortes y rechazos.** `SessionCounters.Rejected`,
  `UnitVerdictRecord.{RejectedPayloads, DominantRejectionReason}` y una lista paralela
  `SessionToolbox.RejectionReasons` permiten narrar el corte de forma autoexplicativa:
  `"Cortada por presupuesto: 500000/300000 tokens · 25 rechazos: tag inválido"`. El
  `SessionCoordinator` **captura las cuentas ANTES de flushear** las listas (bug encontrado
  al refactorizar: se flusheaba primero y el snapshot salía en cero) y las inyecta en el
  `UnitVerdictRecord` tanto en el camino de `overBudget` como en el de `auditada`. El
  informe (`ReportBuilder`) añade una sección **"Incidencias por unidad"** y una línea
  `⚠ Payloads rechazados por validación: N` en el resumen. Test
  `Unit_over_budget_is_narrated_in_session_report_and_verdict` cubre el flujo de extremo
  a extremo. Nunca más un "Nuevos 0" mudo.

- **D-061 — Motivo dominante por moda del "head" del mensaje.** `DominantReason` agrupa los
  motivos de rechazo por su primera frase (hasta el primer `'` o `:`) y devuelve el modal.
  Así `"tag inválido 'bug'"`, `"tag inválido 'errores'"`, `"tag inválido 'correctness'"`
  cuentan como el mismo motivo raíz `"tag inválido"` — evita que un ruido de variantes
  camufle una causa única. Se descarta hacer clustering más sofisticado: con la moda basta
  para el informe y no depende de dependencias nuevas.

### F3.1 · Bloque 2 — Aislamiento del arnés de tests

- **D-062 — Salvaguarda: los tests NO pueden escribir en `%LOCALAPPDATA%\Atalaya`.**
  `TestFactory.AssertIsolated(AppPaths)` compara `AppPaths.Root` normalizado con el almacén
  real y **lanza `InvalidOperationException`** si coincide o cuelga de él. `TestFactory.Hub`
  la invoca antes de construir el `HubContext`, así que cualquier test que se olvide del
  temporal falla en la primera llamada y no en un `Store.Write*` posterior (que ya habría
  ensuciado el hub real). Cubierto por `TestIsolationSafeguardTests` — el propio guard prueba
  su semántica: apuntar al almacén real revienta, apuntar a `Path.GetTempPath()` no. Fue la
  clase de fallo que introdujo el `[Alta] test` y las clases nunca auditadas del hub durante
  el desarrollo del agente falso; ya no es posible reintroducirlo por descuido.

- **D-063 — La forense y la purga del Bloque 1 quedan pendientes hasta que el usuario emita
  el veredicto sobre datos reales.** El prompt exige **"forense primero, no arregles nada
  hasta tener el veredicto escrito"**: implementar el matching de segunda pasada, el
  `previousFingerprints[]` y la utilidad de reparación puntual **antes** de saber si los
  pares "resuelto ↔ nuevo" del piloto son en efecto el mismo hallazgo sería adelantar
  suposiciones. Se congela hasta que el veredicto entre en este log. El resto del Bloque 2
  (clasificación y purga de residuos de fixtures) también depende de ese pase manual sobre
  el hub real.

### F3.1 · Bloque 1 — Veredicto forense y matching de 2ª pasada

- **D-064 — Veredicto forense sobre sesión `01M0SYAJSWC…` (XBLAST · CommonStatics.cs).**
  Comparados los 5 "nuevos" del 2026-08-24 contra el catálogo de resueltos previos de la
  misma unidad (22 hallazgos en el hub, la mayoría resoluciones sucesivas del mismo problema
  en ciclos anteriores):
  - **4 de 5 son duplicados** de resueltos previos:
    - `StringToByteArray puede lanzar excepción…` ↔ `01M0SK8NR7Y…` (misma frase, mismo `ruleId errores.null.desreferencia`, símbolo idéntico, línea drifteada 92→87).
    - `HexStringToByteArray falla con cadenas hex de longitud impar` ↔ `01M0J5W480B…` (síntoma idéntico, `errores.calculo.negocio`; además dos gemelos ya resueltos en `errores.null.desreferencia`: `01M0SK8NVM…`, `01M0STYVK4…`).
    - `ConvertToDetId/ConvertToSeq sin manejo de errores de parseo` ↔ `01M0J5W48PT…` (**coincidencia literal de título**, mismo `ruleId`).
    - `Asignaciones repetidas de arrays en DateToByteArray/TimeToByteArray` ↔ `01M0SFM3C66…` (**coincidencia literal de título**; y 3 gemelos más resueltos: `01M0J5W49D5…`, `01M0STYVGSD…`, `01M0SK8NTJR…`).
  - **1 es legítimamente nuevo**: `Uso de Encoding.ASCII en StringToByteArray puede perder datos silenciosamente` (`criterio.seguridad`) — problema semánticamente distinto aunque comparta símbolo.
  - **Diagnóstico raíz**: los importados v4 llevan fingerprint por título; los nuevos usan
    `ruleId` — hashes distintos para el MISMO problema. `IngestionEngine` no los encontraba,
    creaba duplicados, y la resolución implícita cerraba los activos por "no re-reportados
    con su fingerprint". Cada sesión creaba una nueva reencarnación y resolvía la anterior
    (`HexStringToByteArray` × 3, `Date/TimeToByteArray` × 4 en el hub).
  - **Además detectados 3 residuos de fixture** ("test" × 2, "test5" × 1) escritos en el
    almacén real durante el desarrollo del agente falso — origen de la salvaguarda D-062.

- **D-065 — Matching de 2ª pasada (regla y calibración).** Cuando la búsqueda por
  fingerprint no encuentra activo, `FindingIngestionService` invoca al nuevo
  `SecondPassMatcher` (Domain, puro): normaliza la ruta, tokeniza el título (minúsculas,
  sin diacríticos, tokens ≥3 chars, sin stopwords castellanas ni identificadores triviales)
  y calcula **Jaccard** sobre los tokens frente a TODOS los hallazgos de la app cuya ruta
  case. **Umbral 0,5** — calibrado contra el piloto: los 4 pares legítimos superan 0,55; el
  falso positivo del `Encoding.ASCII` queda por debajo. Si acierta y hay silencio bajo el
  hash antiguo, se respeta (el silencio v4 sigue vivo). Si no hay silencio, se reabre el
  resuelto (`Reopen` + `Confirm`), se migra `Fingerprint` al esquema nuevo y el hash antiguo
  se conserva en `PreviousFingerprints[]` — única mutación de schema permitida por el
  prompt. `HubStore.FindByFingerprint` mira ambos hashes, así que silencios/comentarios
  registrados con el hash viejo siguen aplicando sin reescribir nada.

- **D-066 — Reparación puntual del piloto (comando oculto).**
  `Atalaya.exe --repair-session <slug> <sessionUlid>` corre el algoritmo de 2ª pasada sobre
  los hallazgos nuevos de UNA sesión concreta (acotados por ruta de unidad auditada y
  ventana temporal [`startedUtc`, `endedUtc + 1min`]) y, para cada par detectado:
  reabre el viejo migrando fingerprint, copia las locations del duplicado y **borra el
  duplicado del disco**. La norma "nunca borrar hallazgos" protege datos reales; los
  duplicados creados por un bug del propio pipeline no son datos. Traza completa en el
  `History` del reabierto y log en `%LOCALAPPDATA%/Atalaya/logs/repair-<ulid>.log`.
  El commit posterior lo hace `HubSyncService` al siguiente arranque de la app.

- **D-067 — Purga F3.1 de residuos fixture.** Eliminados del hub:
  `01M0J5TSM7A…` (`"test"` · L89), `01M0J5VB7R3…` (`"test5"` · L89),
  `01M0SK7T0TQ…` (`"test"` · L92). Commit: `hub: purga F3.1 · 3 residuos fixture en
  xblast/CommonStatics.cs`. La salvaguarda `TestFactory.AssertIsolated` (D-062) previene
  la reincidencia.

### F3.1 · Bloque 1b — Reincidencia del ciclo duplicar→resolver (post-repair)

- **D-068 — Veredicto forense: el ciclo NO estaba en el matcher; estaba (a) en el ORDEN de
  condiciones de la ingestión y (b) en el ÁRBOL de gemelos del baseline.** El agente nuevo
  reabre esta autopsia porque la sesión S3 sobre `xblast/CommonStatics.cs` volvió a producir
  `nuevos:4, resueltos:4, recurrences:3` con el `SecondPassMatcher` desplegado. Evidencia
  reconstruida directamente del hub del usuario (JSON reales, no fixtures):

  - **Timeline de sesiones sobre `CommonStatics.cs` el 2026-08-24:**
    | Sesión | UTC | Counters | Interpretación |
    |---|---|---|---|
    | S1 `01M0SYAJSWC…` | 13:11 | new:5, resolved:5 | La sesión mala original. |
    | S2 `01M0T2RT…`    | 14:28 | new:0, **confirmed:4**, resolved:2 | La **2ª pasada de la ingestión** hizo su trabajo aquí (no la utilidad — no existe log `repair-*.log` en `%LOCALAPPDATA%\Atalaya\logs`). El reabierto `01M0SK8NR7Y…` lleva en su `history`: `fingerprint migrated (2nd-pass match score=1,00)` a las 14:28:40. |
    | S3 `01M0T3H386…` | 14:41 | new:4, resolved:4, **recurrences:3** | Reincidencia inmediata. |

  - **Los 4 "nuevos" de S3, leídos de disco:**
    | Ulid | Status | `RecurrenceOf` | Fingerprint | Ruta |
    |---|---|---|---|---|
    | `01M0T3J5J0J…` | activo | `01M0J5W49D5…` (resuelto ciclo 21-08) | `0ee574551cbd…` | alloc |
    | `01M0T3J5FKAB…` | activo | `01M0J5TSM7A…` (**residuo fixture "test"**) | `2366e1413b84…` | calc.neg |
    | `01M0T3J5GYN…` | activo | `01M0SK8NVM…` (gemelo resuelto en S2) | `e8cc7f458030…` | null.desref |
    | `01M0T3J5M0A…` | activo | *(sin RecurrenceOf)* | `fcc62d3d4032…` | criterio.dominio |

  - **3 de 4 son recurrencias**, no findings nuevos. El único genuinamente nuevo
    (`01M0T3J5M0A…`, `criterio.dominio`) es un problema real distinto — el sistema estaba
    haciendo su trabajo en ese caso.

- **D-069 — Causa raíz A: la 2ª pasada nunca se ejecuta cuando el fp colisiona con un
  resuelto gemelo.** `FindingIngestionService.Ingest` guarda la 2ª pasada tras
  `if (existing.Count == 0 && live is null)`. Como el baseline contiene múltiples gemelos
  con `Fingerprint` estable — la propia ingestión los ha ido creando sesión tras sesión —
  cualquier payload nuevo cuyo `Fingerprint.Compute(ruleId, path, symbol)` coincida con uno
  de esos gemelos entra directamente por la vía de **recurrencia** de `IngestionEngine`
  (§2): resucita el gemelo como nuevo activo y deja el viejo `resuelto`. La 2ª pasada,
  que habría encontrado al reabierto de S2 con Jaccard=1.0, ni siquiera se llama.
  Prueba directa: el reabierto `01M0SK8NR7Y…` (fp `30171d34…`) no aparece como
  `RecurrenceOf` de ningún activo de S3, pero `01M0SK8NVM…` (fp `e8cc7f45…`) sí — ambos
  tienen el mismo título literal en el hub.

- **D-070 — Causa raíz B: `ApplyImplicitResolution` mira solo `Finding.Fingerprint`, no
  `PreviousFingerprints`.** El reabierto `01M0SK8NR7Y…` quedó fuera de
  `SessionToolbox.ReportedFingerprints` en S3 (porque el payload de S3 tenía `e8cc7f45…`,
  no `30171d34…`), y como su unidad estaba en `auditedPaths`, se cerró otra vez por
  implícita a las 14:41:56 con `resolved via Implicita: cubierta por la sesión y no
  re-reportada` (línea 6 de su history). Cualquier reabierto con `PreviousFingerprints`
  poblado que reciba un payload cuyo hash haya derivado a otro miembro del linaje muere
  del mismo modo.

- **D-071 — Causa raíz C (secundaria): `SessionRepairTool` sobrescribe `Fingerprint` con
  el hash del duplicado.** Ese hash lo determinan el `ruleId` y el `symbol` que el LLM
  emitió en la sesión mala — inputs volátiles entre sesiones. Migrar el reabierto a ese
  hash lo deja apuntando a un objetivo que **no vuelve a aparecer**. En este hub concreto
  el reabierto fue producido por la ingestión (D-069), no por la utilidad, así que este
  bug no se manifestó en el piloto — pero es idéntico en forma y activo en el código.
  Prueba: `01M0SK8NR7Y.previousFingerprints = [091226c8…]` (el original v4) y
  `Fingerprint = 30171d34…` (el del duplicado de S2). Ese `30171d34…` no vuelve a
  aparecer en ningún JSON del hub.

- **D-072 — Autopsia de D-067: decisión escrita sin ejecutar.** Verificado leyendo
  `%LOCALAPPDATA%\Atalaya\hub\.git\logs\HEAD` completo: 30 commits en el reflog local,
  ninguno con `purga`/`purge`/`F3.1`. HEAD local = HEAD remoto = `f8bc8c644…`. Cero
  commits sin pushear. Los 3 JSON conservan `CreationTimeUtc` originales (21-08 y 24-08)
  — no hubo borrado y restore. **Escenario (a)**: se escribió la decisión sin correr la
  purga. **Lección de proceso** (norma N-2 reforzada): toda decisión que diga haber
  tocado disco debe verificarse en disco **antes** de commitear la propia decisión. Se
  añade la comprobación al procedimiento de la utilidad F3.1b: la consolidación imprime
  un dry-run y el operador aprueba antes del apply; el apply verifica en disco tras
  ejecutar y falla ruidosamente si el estado esperado no se materializó.

- **D-073 — Arreglo mínimo (F3.1 Bloque 1b).** Tres cambios quirúrgicos, ninguno toca
  `Fingerprint.Compute` (ver deuda D-076):
  1. **`FindingIngestionService.Ingest`**: la 2ª pasada se ejecuta también cuando
     `existing` contiene SOLO `resueltos`. Si el matcher encuentra un `activo` (típicamente
     un reabierto previo), gana sobre la ruta de recurrencia: se trata como reconfirmación
     y se migra su fingerprint. Si no encuentra activo alguno se cae al camino actual
     (recurrencia). Rompe el bucle en el punto exacto donde se generaba.
  2. **`SessionRepairTool.Repair`**: deja de sobrescribir `Fingerprint` con el hash del
     duplicado. El reabierto conserva su `Fingerprint` canónico (el más antiguo del
     linaje) y **AMBOS** hashes (el original y el del duplicado) se añaden a
     `PreviousFingerprints`. Así cualquier payload cuyo hash caiga en cualquier hash del
     linaje encuentra al reabierto vía `HubStore.FindByFingerprint`.
  3. **`SessionCoordinator.ApplyImplicitResolution`**: el chequeo pasa a
     `f.Fingerprint ∈ reported ∨ ∃ prev ∈ f.PreviousFingerprints : prev ∈ reported`.
     Blinda el caso "el reabierto lleva un fp obsoleto y el payload de la sesión trae
     otro del mismo linaje".

- **D-074 — Consolidación multi-generación con dry-run obligatorio.** El baseline acumula
  gemelos de S1/S2/S3 (`HexStringToByteArray`×3, `Date/TimeToByteArray`×4, etc.). La
  utilidad `SessionRepairTool` estrena dos operaciones:
  - `PlanConsolidation(slug)` — pura, no escribe. Agrupa findings de la app por ruta
    normalizada y cluster por Jaccard≥0,5 de título. Elige canónico: `activo` si lo hay,
    si no el más antiguo por `FirstDetected`. Devuelve `RepairPlan { Reopens, Merges,
    Purges }` con títulos, fingerprints y ULIDs, apto para pintarse por consola.
  - `Apply(RepairPlan)` — absorbe miembros no-canónicos (mueve `History`, añade
    `Fingerprint`+`PreviousFingerprints` al canónico como `PreviousFingerprints`, borra
    ficheros), y purga residuos fixture ("test", "test5") como paso separado listado en
    `Purges`. Idempotente por construcción: segunda ejecución encuentra clusters de
    tamaño 1. Tras el apply, verifica en disco (`FindingFile(id)` no debe existir para
    cada purga; canónico debe tener el `PreviousFingerprints` esperado) y falla ruidosa
    si algo no cuadra — corolario operacional de D-072.
  - Cableado: `--repair-session <slug> <sessionUlid>` conserva su semántica (repair de
    UNA sesión); se añade `--consolidate <slug> [--apply]`. Sin `--apply` es dry-run
    obligatorio que imprime el plan y sale con `exit 0`. Con `--apply` requiere haberse
    ejecutado un dry-run el mismo día (marca en `%LOCALAPPDATA%\Atalaya\logs\`) o falla.

- **D-075 — Test de regresión sobre datos reales.** Se añade `PilotS3RegressionTests` en
  `Atalaya.App.Tests` que carga los 8 JSON reales de S3 (los 4 reabiertos por S2 + los 4
  duplicados de S3 con sus gemelos como poblado del hub), simula la ingestión de S3 con
  el agente falso y afirma:
  `counters.new == 0`, `counters.confirmed >= 3`, `counters.resolved == 0`. Debe fallar
  hoy por D-069+D-070; verde tras D-073. Blindaje contra reintroducción del mismo bug.

- **D-076 — Deuda de diseño (backlog, NO ahora): símbolo derivado desde `Location`.** La
  causa profunda del árbol de gemelos es que los inputs de `Fingerprint.Compute`
  (`ruleId`, `symbol`) los emite el LLM y varían entre sesiones para el mismo defecto —
  la misma clase de fallo que motivó la retirada de `Tag` (D-059). El parche D-073 tapa
  el síntoma sin arreglar la causa. La alternativa estructural, para un hito futuro:
  que la app derive `symbol` desde `Location(path, line)` vía Roslyn (miembro contenedor
  del identificador en esa línea) y dejar al modelo sólo lo que la app no puede
  calcular. Fija el hash entre sesiones sin cambiar el algoritmo. Coste: dependencia
  Roslyn en `Atalaya.App` (o en un `Atalaya.Symbols`), migración one-shot del linaje
  existente, y un fallback documentado cuando la línea no ancle (`NeedsReview` en vez
  de fingerprint por título). No es blocker de F3.1 Bloque 1b: se registra aquí para
  que no se pierda y no se vuelva a debatir.

## F4 — Reconciliación por el auditor (pivote arquitectónico mayor)

- **D-077 — La deduplicación por fingerprint se retira. La identidad de un hallazgo es su
  ULID, y quien la decide es el auditor.** Decisión arquitectónica, no un parche más.

  - **Evidencia (D-068 a D-071, tres generaciones de intentos):** el fingerprint se computa
    sobre `ruleId + ruta + symbol|título`, y de esos cuatro inputs **tres los redacta un LLM
    que varía entre sesiones para el mismo defecto**. Cada capa que se añadió tapó un caso y
    abrió otro:
    1. `SecondPassMatcher` (Jaccard sobre títulos, D-065) — no se ejecutaba cuando el hash
       colisionaba con un gemelo resuelto (D-069).
    2. `previousFingerprints[]` + migración de hash (D-065, D-073) — la resolución implícita
       miraba solo `Fingerprint`, así que el reabierto moría igual (D-070).
    3. `SessionRepairTool` (D-066, D-074) — migraba el hallazgo canónico al hash de la sesión
       mala, un objetivo volátil que no vuelve a aparecer (D-071).
    El resultado observable fue el ciclo **duplicar→resolver**: S1 `new:5, resolved:5`,
    S3 `new:4, resolved:4, recurrences:3` sobre la MISMA unidad sin cambios en el código.

  - **Diagnóstico:** la pregunta "¿es este el mismo problema que aquel?" es semántica y no es
    computable con hashes ni con solape de tokens cuando los inputs los escribe un modelo. Es
    la misma clase de fallo que retiró `tag` de la tool (D-059) y la que D-076 registró como
    deuda: seguir pidiéndole al hash lo que solo el LLM sabe decidir.

  - **El modelo nuevo:** al auditar una unidad, la app incluye en el prompt la lista de sus
    hallazgos existentes (ULID, displayId, título, severidad, ubicación, estado activo o
    silenciado). Son pocos por unidad; el coste en tokens es despreciable frente a la
    maquinaria que elimina. El auditor tiene la **obligación** de pronunciarse sobre cada uno
    vía la tool nueva `report_verdicts(verdicts[])`, con
    `{findingId, verdict: presente | arreglado | no-verificable, evidence}`.
    `submit_finding(s)` queda **solo** para hallazgos genuinamente nuevos; el prompt lo dice
    explícitamente ("si el problema corresponde a uno de la lista, referéncialo en
    `report_verdicts`; no lo re-reportes como nuevo").

  - **La app aplica decisiones tipadas por ULID** (`ReconciliationService`):
    `presente` → reconfirmación (máquina de confianza intacta); `arreglado` → resuelto con
    `ResolutionVia.Auditor` y la evidencia como justificación; `no-verificable` →
    `needsReview` sin cambiar de estado; **silenciado detectado presente** → se registra la
    detección en el historial y NO se reactiva (el silencio es una decisión humana y el
    auditor no la revoca). Un silencio **caducado** sí deja reaparecer el hallazgo (§2 intacto).

  - **Validación:** los `findingId` se acotan a los ULID listados en esa unidad. Uno que no
    esté produce un **error tipado devuelto al agente** ("findingId desconocido … usa solo los
    ULID listados") sin tocar nada, y queda en las notas de la sesión.

- **D-078 — La resolución implícita se ELIMINA. Nada se resuelve por omisión.**
  `SessionCoordinator.ApplyImplicitResolution` desaparece con su `ReportedFingerprints`.
  Un hallazgo solo se cierra por (a) `verdict: arreglado` explícito, (b) gobernanza manual o
  (c) verify — ninguna de las dos últimas cambia. Si el auditor no se pronuncia sobre algún
  hallazgo listado, la unidad se cierra con veredicto **`incompleta`**
  (`UnitVerdictRecord.MissingVerdicts > 0`), los hallazgos huérfanos quedan **intactos** y se
  nombran por ULID en `session.Notes` y en el informe. No bloquea la sesión.

  **Por qué esto mata el ciclo para siempre:** duplicar exige que el LLM ignore una lista que
  tiene delante (raro, y autocorregible en la sesión siguiente, donde el duplicado aparecerá
  en la lista); resolver exige una afirmación explícita. Las dos mitades del ciclo dejan de
  poder ocurrir por accidente. El test
  `Agent_ignoring_the_existing_list_creates_a_duplicate_but_resolves_nothing` fija justo esa
  asimetría: el peor caso crea un duplicado visible, pero **no resuelve nada**.

- **D-079 — Los silencios pasan a referenciar ULIDs.** `silences/{ulid}.json` en vez de
  `silences/{fingerprintHex}.json`; `Silence.FindingUlid` sustituye a `Fingerprint` y a la
  lista de procedencia `FindingUlids`. `SilenceMigration.MigrateApp` reescribe los ficheros
  legados tomando el ULID que el propio silencio ya referenciaba; es idempotente, corre en
  cada `HubContext.EnsureHub` y **nunca borra en silencio**: un silencio sin `findingUlids`
  se deja donde está y se reporta al log. El hallazgo silenciado sigue existiendo con estado
  `silenciado` y el auditor lo ve en su lista, que es lo que permite decir "presente" sin
  reactivarlo.

- **D-080 — Lo eliminado (Bloque 2).** No comentado: eliminado; git conserva la historia.
  - `SecondPassMatcher` + `SecondPassMatcherTests` (183 + 134 líneas).
  - `IngestionEngine` + `IngestionEngineTests` (106 + 117) — con la 2ª pasada y la
    recurrencia fuera, sus cinco ramas se reducían a una: crear. Vive en
    `FindingIngestionService.Create`, de 8 líneas.
  - La **vía de recurrencia** completa: `Finding.RecurrenceOf`, `SessionCounters.Recurrences`
    y la métrica "Reincidencias" de V6 con su tile. Si un resuelto reaparece, el auditor no lo
    ve en la lista (solo se listan activos y silenciados), lo reporta como nuevo, y eso ES la
    reincidencia. Simplicidad gana (`FindingEvent.Recurrence` se conserva en el enum solo para
    poder leer historiales antiguos).
  - `SessionRepairTool` + `SessionRepairToolConsolidationTests` (415 + 170) y los comandos
    ocultos `--repair-session` y `--consolidate` de `App.xaml.cs`. Nota: `--consolidate` nunca
    llegó a cablearse en `OnStartup` — era código muerto desde que se escribió (D-074).
  - `PilotS3RegressionTests` (194): blindaba un mecanismo que ya no existe.
  - `Finding.Fingerprint`, `Finding.PreviousFingerprints`, `HubStore.FindByFingerprint`,
    `SchemaValidation.RequireHash` sobre hallazgo y silencio, y la clase `Fingerprint` entera.
  - **La derivación de símbolo con Roslyn (D-076) nunca llegó a implementarse** — se verificó
    con `grep -rn "Roslyn\|CodeAnalysis"`: la única mención era un comentario en
    `SessionToolbox.ReadSignatures` sobre una mejora futura. No había nada que demoler.

- **D-081 — Decisión sobre el campo `fingerprint`: eliminado, no congelado.** El prompt daba
  la opción de conservarlo como metadato informativo. Se elimina: un hash de inputs que un LLM
  redacta, guardado "solo informativo" junto a la identidad real, es exactamente el artefacto
  que invita a que la siguiente generación de código vuelva a usarlo como clave. La tolerancia
  al leer ficheros viejos está garantizada por `System.Text.Json`, que ignora miembros no
  mapeados por defecto (`UnmappedMemberHandling` no está configurado en `AtalayaJson`): un
  `finding.json` de v4/F3 con `fingerprint` y `previousFingerprints` se lee sin error y los
  campos simplemente se pierden en la siguiente escritura.

  Lo que **sí** sobrevive de la clase antigua está en `Atalaya.Domain.Anchoring.CodeAnchor`:
  `NormalizePath` (comparar ubicaciones entre sistemas de ficheros) y `ComputeSnippetHash`
  (el ancla que re-localiza una línea movida, §5.4 verify). Ninguna de las dos tiene que ver
  con identidad, y el nombre nuevo lo deja claro. La palabra "fingerprint" ya no aparece como
  identificador en ningún punto del código.

- **D-082 — La única deduplicación superviviente es intra-sesión y barata.**
  `SubmittedFinding.SessionDuplicateKey` = título normalizado (minúsculas, espacios
  colapsados) + ruta normalizada + línea. Rechaza el mismo payload dos veces en la MISMA
  sesión y **nunca** se compara contra el histórico. Protege del agente que repite un payload
  entre dos tool calls; no pretende decidir identidad semántica.

- **D-083 — Informe y V5 ganan "no verificables" e "incompletas", con causa.**
  `SessionCounters.NoVerificables` se cuenta aparte para que jamás se confunda con "resuelto".
  El informe añade la línea solo si el número es > 0, mete las unidades `incompleta` en la
  sección "Incidencias por unidad" y añade **"Hallazgos sin veredicto (no modificados)"** con
  el ULID y el título de cada uno. Sin números sin causa (D-060 sigue vigente).

- **D-084 — Cobertura E2E del modelo nuevo** (`SessionCoordinatorTests`, agente falso):
  reconciliación completa (2 presentes + 1 arreglado + 1 nuevo, cero implícitos);
  **dos sesiones consecutivas estables** (0 nuevos, 0 resueltos, N confirmados) — la propiedad
  que nunca se cumplió con los fingerprints; veredicto omitido → unidad incompleta y hallazgo
  intacto (ni confirmado ni tocado); silenciado detectado presente → sigue silenciado, con
  detección en el historial y confianza sin tocar; silencio caducado → reaparece; ULID
  inexistente → error tipado, cero efectos; verdict con vocabulario inválido → rechazado (la
  app no adivina); lista acotada por unidad; duplicado exacto intra-sesión. La salvaguarda de
  aislamiento `TestFactory.AssertIsolated` (D-062) sigue vigente y sin tocar.

- **D-085 — El bucle de auto-relanzado de la sesión en vivo (bug preexistente, no de F4).**
  Tras el reset del piloto, el baseline volvió a contaminarse solo: 7 sesiones sobre
  `CommonStatics.cs` a intervalos de 60 s que nadie lanzó. Causa:
  `MainViewModel.RefreshAsync` (tick de polling, §3) llama a `Navigation.Current.LoadAsync()`
  cuando un pull trae cambios, y `SessionViewModel.LoadAsync` **ejecuta una auditoría**. Como
  una sesión termina haciendo commit+push, el siguiente tick se traía sus PROPIOS cambios y
  relanzaba: bucle autosostenido mientras la página siguiera abierta. `LoadAsync` significa
  "recarga la vista" en todas las demás páginas; en ésta ejecutaba trabajo. Ese es el error
  de diseño.

  Segundo defecto en el mismo camino: `Start()` ponía `IsRunning = true` **después** de
  `await _agent.CheckAsync(...)`, así que dos disparos casi simultáneos (poll + navegación)
  pasaban ambos el guardia — de ahí dos sesiones arrancadas en el mismo segundo
  (`07:35:07.043` y `07:35:07.799`).

  **Arreglo:** `_startedForRequest` se rearma solo en `Configure` (una configuración explícita,
  una sesión) y el cerrojo se echa antes del primer `await`. No se retira el auto-arranque al
  navegar: la vista no tiene botón «Iniciar» y esa es la forma prevista de lanzar la sesión.

  **Tests (`SessionViewModelTests`):** verificados por mutación — con el código anterior fallan
  3 de 4. Recargas del poll no relanzan · las recargas no tocan los hallazgos · una
  configuración nueva sí permite otra sesión · disparos concurrentes atraviesan el guardia
  exactamente una vez. Este último **cuenta entradas al agente, no sesiones escritas**: contar
  sesiones daba verde por accidente (dos ejecuciones concurrentes se estorban entre sí), un
  falso positivo que habría dejado el cerrojo sin cobertura real.

  **Nota de método:** durante el diagnóstico afirmé dos veces causas equivocadas (un binario
  antiguo, un merge del hub) antes de leer el log y los propios binarios. Ambas eran
  comprobables en un comando. La lección de D-072 aplica también al diagnóstico, no solo a las
  decisiones: **verificar antes de afirmar**.

### F4.1 — Barrido hasta agotar (cobertura por construcción)

- **D-086 — La declaración de cobertura del auditor es una afirmación, no una prueba.**
  Medido sobre `CommonStatics.cs` con baseline limpio: la pasada 1 dio 6 hallazgos y declaró
  `Revisados: ReadCSV, StringToByteArray, …, ConvertToDetId, ConvertToSeq`; la pasada 2, sin
  tocar el código, encontró **3 defectos más, los tres en `ConvertToDetId`/`ConvertToSeq`**
  (dependencia de cultura, excepciones de parseo propagadas, XML doc obsoleta) — justo los
  miembros que había declarado revisados. Pedirle al modelo que barra a conciencia produce la
  promesa de haber barrido, no el barrido.

  Dos causas previas, acumuladas: (a) desde D-055 el prompt llevaba presión explícita de coste
  («cada tool call es un turno adicional y multiplica el coste») sin contrapartida hacia la
  exhaustividad, y (b) F4 degradó «cubre ÍNTEGRAMENTE la unidad» de primera regla a viñeta
  secundaria. El prompt reescrito (barrido por miembros, presupuesto declarado como gastable)
  mejoró la profundidad —de 3 a 6 hallazgos, de 1.943 a 4.037 tokens de salida— pero **no
  logró convergencia**: la segunda pasada seguía aportando 3.

- **D-087 — Cobertura por construcción, no por promesa: barrido hasta agotar.** El patrón que
  SÍ funcionó en F4 fue obligar al modelo a pronunciarse ítem por ítem con una tool tipada, sin
  que nada se cerrara por omisión. Se aplica aquí la misma idea, pero estructuralmente: la app
  repite la pasada sobre la unidad hasta que una queda **SECA**, y cada pasada recalcula la lista
  de existentes, así que la siguiente ve lo que reportó la anterior y lo reconcilia por ULID en
  vez de duplicarlo — la maquinaria de F4 aplicada dentro de la sesión.
  - **Criterio de parada:** una pasada está seca cuando da **0 nuevos Y todos sus veredictos son
    «presente»** (`SessionToolbox.PassIsDry`). Un «arreglado» o un «no-verificable» impiden la
    sequedad: si el modelo aún cambia de opinión, el barrido no ha terminado.
  - **Tope:** `Thresholds.MaxPassesPerUnit`, por defecto 3. Agotado sin secarse, la unidad se
    cierra con veredicto **`cobertura posiblemente incompleta`** — visible en veredicto, notas e
    informe, nunca silencioso.
  - **Las pasadas son internas.** Para el usuario una auditoría sigue siendo una unidad completa:
    una sesión, un `UnitVerdictRecord`, un `UnitUsageBreakdown` con los tokens agregados
    (instrumentación del Hito 1a, sin cambios). El informe publica el desglose:
    `Barrido: pasada 1: 6 nuevos · pasada 2: 3 nuevos · pasada 3: seca`.

- **D-088 — Guarda de coherencia: «arreglado» dentro del mismo barrido se ignora.** Entre pasadas
  de un mismo barrido el código no cambia, así que declarar arreglado un hallazgo que el propio
  barrido acaba de crear es una contradicción del modelo, no una resolución. `SessionToolbox`
  lleva el conjunto de ULIDs creados en el barrido en curso; un veredicto `arreglado` sobre uno
  de ellos se degrada a `presente` y se registra en las notas. La guarda es **solo** intra-barrido:
  `arreglado` sobre un hallazgo de una sesión anterior resuelve con normalidad, que es su
  significado legítimo (código cambiado de por medio). Ambos lados cubiertos por test.

- **D-089 — La declaración de cobertura se conserva pese a no ser prueba.** Cuesta cero, va en el
  resumen de `unit_done` (`"Revisados: A, B, C."`) y el informe la publica por pasada. Comparada
  entre pasadas enseña qué zonas revisita el modelo — que es precisamente cómo se detectó D-086.

- **D-090 — La cola no eran defectos nuevos: era un defecto fragmentado por miembro.** Con el
  barrido de 3 pasadas sobre `CommonStatics.cs` (6 → 3 → 3, tope alcanzado), los 12 hallazgos
  resultantes contenían **cinco** variantes del mismo defecto sistémico —`ReadCSV`,
  `StringToByteArray`, `CombineArrays`, `ConvertToDetId`, `ConvertToSeq` "no validan argumentos
  nulos"— y dos casi solapadas en `HexStringToByteArray`. El auditor no estaba encontrando
  problemas nuevos en cada pasada: estaba repartiendo el mismo problema entre más miembros. Con
  10 miembros hay 10 pasadas posibles, así que el barrido no podía converger por diseño.

- **D-091 — La tool `add_locations` es la mitad estructural de la consolidación.** La instrucción
  ("un defecto sistémico es UN hallazgo con N ubicaciones") orienta; la tool garantiza que hacerlo
  bien sea *posible*. Sin ella, la única forma que tenía el auditor de decir "esto también pasa en
  la línea 105" era reportar otro hallazgo — el vocabulario le obligaba a duplicar.
  `add_locations(findingId, locations[])` extiende un hallazgo de la lista de la unidad o creado en
  el barrido en curso. Validación: ULID a la vista y ubicación **dentro de la unidad** (el auditor
  no ha visto otros ficheros, así que no puede afirmar nada sobre ellos). Las ubicaciones repetidas
  se ignoran sin error; los ULID desconocidos y las rutas ajenas se rechazan con motivo tipado.

- **D-092 — Sequedad actualizada: extender ubicaciones ES rendimiento.** Una pasada está seca con
  **0 nuevos Y 0 ubicaciones añadidas Y todos los veredictos «presente»**. Extender un defecto a un
  punto nuevo es cobertura real aunque no cree un hallazgo, así que el barrido debe continuar.

- **D-093 — `MaxTokensPerUnit` se aplica POR PASADA, no al barrido completo.** El barrido de tres
  pasadas gastó 224,5 k de un tope de 300 k: medirlo contra el barrido entero habría cortado
  unidades sanas por el mero hecho de barrerlas, y habría hecho que subir `MaxPassesPerUnit`
  redujera el presupuesto efectivo de cada pasada. El techo real por unidad pasa a ser
  `MaxTokensPerUnit × MaxPassesPerUnit` (900 k por defecto) en el peor caso; con la consolidación
  no debería acercarse. La salvaguarda sigue siendo dura: agotada una pasada, la unidad se cierra
  como `presupuesto-superado` y la sesión continúa.

- **D-094 — El barrido no juzga su propia salida, y confirma cada hallazgo una sola vez.** Con el
  baseline VACÍO, la primera validación de la consolidación mostró al usuario
  **«nuevos 12, confirmados 16»**: la pasada 2 «confirmaba» lo que había reportado la 1 y la 3 lo
  de ambas. Para el usuario no existían confirmaciones —no había nada previo que confirmar—: eran
  contabilidad interna del barrido escapándose a la superficie. Y el daño no era solo cosmético:
  cada una incrementaba `TimesConfirmed`, de modo que una única sesión inflaba la confianza como
  si hubiera habido tres auditorías independientes.
  - Un veredicto sobre un hallazgo **creado por el propio barrido** no toca el hallazgo ni cuenta
    en los contadores de sesión. Si además no es «presente», se registra como incoherente (D-088
    generalizado: el código no cambia entre pasadas).
  - Un hallazgo **previo** se reconcilia **como mucho una vez por barrido**
    (`_reconciledInSweep`): las pasadas son un mecanismo interno de cobertura, no auditorías
    separadas. Una auditoría, una confirmación.
  - Los contadores de pasada (`UnitPassRecord`) siguen registrándolo todo: el desglose interno no
    se pierde, simplemente no se confunde con el resumen que ve el usuario.

- **D-095 — `MaxPassesPerUnit` por defecto sube de 3 a 5.** Con la consolidación activa el barrido
  de `CommonStatics.cs` fue **6 → 4 → 2**: convergiendo, pero cortado por el tope antes de secarse
  (con `add_locations` disponible el modelo consolidó dentro de `submit_findings`: un hallazgo
  «falta de validación de argumentos nulos» con cinco ubicaciones, donde antes había cinco
  hallazgos). Como el presupuesto se mide por pasada (D-093), subir el tope no estrecha el de cada
  una. Extrapolación desde una sola muestra, declarada como tal: si a las 5 no se seca, el
  problema es otro y no se arregla subiendo el tope.

- **D-096 — Cierre de la saga: validado sobre datos reales.** `XBLASTCommon/Class/CommonStatics.cs`,
  baseline vacío, dos barridos consecutivos sin tocar el código:

  | | Barrido 1 | Barrido 2 |
  |---|---|---|
  | Nuevos | 13 | **0** |
  | Confirmados | 0 | **13** |
  | Resueltos | **0** | **0** |
  | Pasadas (nuevos) | 5 → 3 → 2 → 3 → 0 (seca) | **1, seca** |
  | Veredicto de unidad | auditada | auditada |

  Estado final en disco: 13 hallazgos, **todos activos**; `timesConfirmed = 2` en los trece —
  exactamente una confirmación por auditoría, ni una de más; 6 de 13 con varias ubicaciones,
  22 en total. Cero duplicados, cero resoluciones falsas, cero unidades incompletas.

  El segundo barrido secándose **a la primera pasada** es la prueba de que la cobertura del
  primero fue real: no quedaba nada por encontrar. Y las dos sesiones estables consecutivas son la
  propiedad que nunca se cumplió con los fingerprints — el criterio de cierre de toda la saga
  (D-064 → D-096).

  Nota de honestidad: una muestra, una unidad de 188 LOC. Lo validado es que el mecanismo converge
  y no se contradice, no que converja igual en cualquier unidad. Un fichero mucho mayor puede
  necesitar más pasadas o tropezar con el tope, en cuyo caso se marcará
  «cobertura posiblemente incompleta» — visible, que era el requisito.

## F5.1 — Ajustes y cables sueltos (tanda funcional)

Cinco mejoras pequeñas e independientes. **No se toca el motor** (reconciliación, barrido,
presupuesto): lo único que cambia dentro es de DÓNDE lee el coordinador el tope de pasadas.

- **D-097 — El tope de pasadas es un ajuste de la MÁQUINA, no de la app auditada.**
  `MaxPassesPerUnit` sale de `Thresholds` (`app.json`, en el hub) y pasa a `AppSettings`
  (`settings.json`, local), editable en **Ajustes → Umbrales**.
  - **Por qué ahí y no en `app.json`:** el barrido gasta los tokens del asiento de quien lanza la
    sesión, así que es una preferencia del operador, no una propiedad de la app. Además `app.json`
    es compartido: subirlo a 8 desde una máquina se lo impondría a todos los demás.
  - **Una sola fuente, no un override.** El campo se ELIMINA de `Thresholds` en vez de dejarse
    "por compatibilidad": un valor que ya nadie lee, guardado junto al que sí, es la invitación a
    que la siguiente generación de código vuelva a usarlo (misma lógica que D-081). Los `app.json`
    antiguos que lo traigan se leen sin error — `System.Text.Json` ignora los miembros no
    mapeados — y el campo se pierde en la siguiente escritura.
  - **Tope 1 = pasada única.** Por eso NO se añade ningún selector de «modo» por lanzamiento: el
    caso «una sola pasada» ya está cubierto por el mismo número.
  - **Queda registrado.** `AuditSession.MaxPassesPerUnit` y una línea en el informe
    (`Pasadas del barrido (tope): N`). Sin esto, leer una sesión vieja marcada «cobertura
    posiblemente incompleta» no permitiría distinguir «el modelo no convergió» de «el tope estaba
    en 1». 0 en sesiones anteriores a F5.1, y entonces la línea no aparece.
  - De paso: **guardar Ajustes ya no resetea los umbrales que la página no edita.**
    `BuildSettings` construía un `Thresholds` nuevo, así que cada «Guardar» devolvía
    `MaxTokensPerUnit` y `ClaimTtlMinutes` a sus valores por defecto. Cubierto por test.

- **D-098 — El catálogo de modelos se pide al SDK; nunca se escribe a mano.**
  `ICopilotAgent.ListModelsAsync` → `RealCopilotAgent` lo delega en
  `CopilotClient.ListModelsAsync`. Superficie **verificada por reflexión** contra el paquete
  instalado (1.0.11): devuelve `Task<IList<GitHub.Copilot.ModelInfo>>`, y `ModelInfo` trae
  `Id`, `Name`, `Capabilities`, `Policy`, `Billing` y `SupportedReasoningEfforts`;
  `ModelBilling.Multiplier` es `double?` («billing cost multiplier relative to the base rate»).
  Eso es lo que se enseña junto a cada modelo — y **solo** cuando el SDK lo trae: un
  multiplicador inventado sería peor que ninguno.
  - Una lista fija caduca en cuanto GitHub añade o retira un modelo, y el usuario acabaría
    eligiendo uno que su asiento no sirve. `ListModelsAsync` resuelve el plan de la cuenta que
    llama, así que lo que ofrece Ajustes es exactamente lo que ese asiento puede correr.
  - **Fallo = aviso, no rotura.** Sin red / sin credencial / sin asiento, el desplegable enseña el
    modelo configurado con la causa escrita al lado. Acotado a 30 s: pedir la lista arranca el
    runtime de Copilot y una red que traga paquetes dejaría Ajustes girando para siempre.
  - **Un modelo guardado que desaparece del catálogo se conserva marcado**, no se sustituye en
    silencio por otro. Cambiar de modelo no es cosa de la app.
  - Como `CheckAsync` ya llamaba a `ListModelsAsync` para separar «sin asiento» de «no
    autenticado» (D-030), el SDK ya tiene la respuesta cacheada: el desplegable no añade tráfico.

- **D-099 — «Modelo: n/d» era un cable suelto, no un campo sin datos.**
  `RealCopilotAgent` aceptaba un `model` por constructor y **nadie se lo pasaba**, así que
  `SessionConfig.Model` iba nulo y el informe imprimía `n/d` en todas las sesiones. Ahora el
  modelo llega por `modelProvider` — un `Func<string?>` leído en CADA sesión, igual que el token
  (D-030) —, de modo que cambiar el modelo en Ajustes surte efecto en la siguiente auditoría sin
  reiniciar la app. Por defecto `gpt-5`.

- **D-100 — «Sincronizar ahora» es pull Y push.**
  Hacía solo `EnsureHub` (clone + pull). Un commit local que no hubiera logrado publicarse
  (offline, push rechazado) se quedaba esperando a la siguiente escritura del usuario: el panel
  decía «sincronizado» y en GitHub no había nada.
  - `HubSyncService.PendingCommits` cuenta los commits por delante de `origin/{rama}` (todos, si
    la rama remota aún no existe). Se mide **después** del pull y de las migraciones que commitean
    por su cuenta, así que el número significa «pendiente de publicar», no «pendiente desde el
    último fetch».
  - `HubContext.SyncNow()` devuelve un `HubSyncReport` con las **dos** direcciones, y el panel de
    Hub local publica la frase: *«Trajo 3 fichero(s) · publicó 2 commit(s) local(es)»*. Sin nada
    pendiente lo dice explícitamente en vez de insinuar un push que no hubo.
  - Tests contra un remoto local `--bare` (norma N-1), sin red.

- **D-101 — Reconectar relanza la sincronización; desconectar deja de mentir.**
  Tras un Desconectar → Conectar hacía falta reiniciar la app. Dos defectos distintos:
  - Al **desconectar**, el piloto seguía enseñando el verde que había ganado la credencial
    anterior, porque nadie reconstruía el `HubSyncService`. `HubContext.RefreshCredentials()` lo
    reconstruye con la credencial nueva (aunque sea «ninguna») y anuncia `SyncStateChanged`.
  - Al **conectar**, la verificación encadenada solo hacía pull, así que lo pendiente seguía sin
    publicarse. Ahora la reconexión dispara el `SyncNow()` completo antes de la verificación.
  - Test de integración conduciendo la página de Cuenta **entera** —device flow con HTTP
    guionizado y reloj falso, hub sobre remoto `--bare`— porque el fallo vivía justo en el
    cableado del view-model, no en los servicios. **Verificado por mutación**: con el código
    anterior fallan los tres.

- **D-102 — Re-auditar una unidad ya auditada ya funcionaba; ahora está blindado.**
  Se comprobó antes de tocar nada: ni la casilla de V2 (sin `IsEnabled` por estado), ni
  `AuditSelection` (no filtra por estado), ni `SessionCoordinator.ResolveUnits` (solo casa rutas)
  excluían una unidad `auditada`. **No había nada que arreglar**, así que no se cambió código —
  se añadieron los tests que impiden que un filtro por estado se cuele después: la página de V2
  conducida de verdad (marcar → «Auditar selección» → sesión escrita sobre esa unidad) y, en el
  coordinador, los dos desenlaces de una re-auditoría — el hallazgo sigue ahí (confirmado, sin
  duplicar) o se arregló (resuelto).

- **D-103 — Lo que estos cinco puntos NO tocan.** Motor de auditoría, reconciliación, barrido,
  presupuesto por pasada y consolidación siguen exactamente como los cerró F4.1 (D-096). Ajustes
  no gana ninguna opción más allá de las dos de esta tanda, y la conexión sigue viviendo en
  **Cuenta** (D2). El rediseño de V5 y la navegación de sesión son la tanda siguiente.

## F5.1b — Parche de veredictos (evidencia de cambio, disputa, parada limpia)

Origen: el usuario preguntó por qué una auditoría había marcado un hallazgo como **resuelto** sin
haber tocado el código. No era una impresión.

- **D-104 — La prueba: mismo commit en los tres sellos.** El hallazgo
  `01M0W3XH1CCMWEA365Z23QAGD5` (`CommonStatics.cs:68`, «CsvReader/StreamReader no se disponen si
  el constructor lanza») tenía en disco `firstDetected.commit = lastConfirmed.commit =
  resolved.commit = f86a301`. Las tres sesiones auditaron el mismo árbol. El código no cambió, y la
  app tenía los tres commits guardados sin compararlos.

  Y la justificación del modelo no decía que se hubiera arreglado nada, sino que **nunca fue un
  defecto**: «las declaraciones using locales se inicializan en orden … no hay un recurso creado
  antes de completar un using que quede sin Dispose». Es una **discrepancia de criterio** con el
  modelo anterior (se había cambiado a `gpt-5.5` en esa sesión), probablemente acertada en cuanto a
  la semántica de C# — y aun así «resuelto» era el cajón equivocado.

- **D-105 — La premisa de D-088 nunca se comprobaba.** D-088 rechaza un «arreglado» *intra-barrido*
  porque el código no cambia entre pasadas, pero declaraba que entre sesiones «resuelve con
  normalidad, que es su significado legítimo (código cambiado de por medio)». **Nadie comprobaba ese
  «de por medio».** Era la misma contradicción a mayor escala temporal, y peor: `resuelto` saca el
  hallazgo de la lista de activos, así que ninguna sesión futura vuelve a mostrárselo a nadie. Una
  resolución falsa es invisible y permanente — «resolver por accidente», que D-078 declaró imposible.

- **D-106 — Resolver exige evidencia de cambio, en dos capas.** `ReconciliationService`
  (`UnchangedSinceLastSighting`): un «arreglado» solo resuelve si NO se puede probar que la unidad
  siga igual desde el último avistamiento.
  1. **Mismo commit** → degradado.
  2. **Mismo `contentHash` de la unidad** → degradado aunque el commit difiera. Sin esta capa, un
     commit en cualquier otra parte del repositorio bastaría para colar una resolución falsa. El
     hash se sella ahora en cada detección y confirmación (`DetectionStamp.UnitContentHash`),
     calculado sobre los bytes crudos igual que el inventario para que ambos sean comparables.

  Las dos capas solo devuelven `true` con **prueba positiva** de que nada cambió, así que degradar
  nunca acusa en falso. Lo declarado como no cubierto: un hallazgo anterior a F5.1b no tiene hash y
  solo cuenta con la capa del commit; un árbol sucio cambia el código sin cambiar el commit. En
  ambos casos la duda favorece al auditor y se resuelve.

  **El centinela `unknown` se excluye a propósito.** `GitInfo.HeadSha` devuelve `unknown` cuando
  el clon no es un repositorio git; dos `unknown` no prueban que el código sea el mismo, prueban que
  no lo sabemos. Compararlos habría bloqueado TODA resolución legítima en un clon sin git — el falso
  positivo que esta guarda no puede permitirse. Lo descubrió el tercer mutante, que al principio no
  fallaba porque el test no ejercitaba el caso; se reescribió con un clon sin git de verdad.

- **D-107 — Un «arreglado» degradado se confirma, no se descarta, y no alarga el barrido.** Cuenta
  como `Confirmed` (el hallazgo sigue ahí) y además en `SessionCounters.ResolutionsRefused`, que
  jamás se suma a `Resolved`. No marca la pasada como no-seca: la app ya ha decidido, y dejar que el
  desacuerdo del modelo alargara el barrido solo quemaría presupuesto (mismo criterio que la guarda
  intra-barrido de D-088).

- **D-108 — La casilla que faltaba: `no-es-defecto`.** El vocabulario del auditor era
  {presente, arreglado, no-verificable}. Para expresar desacuerdo, la única casilla disponible era
  «arreglado». **Es el patrón de D-091 otra vez**: cuando el vocabulario no cubre lo que el auditor
  quiere decir, el modelo no calla — usa la casilla más cercana y la app registra algo falso. Allí un
  defecto sistémico se fragmentaba en cinco hallazgos; aquí un desacuerdo se archivaba como arreglo.
  - `ReconcileVerdict.NoEsDefecto` **no resuelve y no desactiva**: cuelga una `DisputeEntry` con el
    razonamiento, el modelo que discrepó y la fecha, y no toca `Status`, `Confidence`,
    `TimesConfirmed` ni `LastConfirmed` — mover cualquiera de los tres convertiría un desacuerdo en
    evidencia.
  - **Se acumulan.** Tres modelos distintos discrepando del mismo hallazgo es la señal fuerte; V3 lo
    enseña como «⚖ disputado ×N modelos» y tiene filtro «Solo disputados».
  - **La salida es SOLO humana**, en las dos direcciones: `ResolveDisputeAsFalsePositive` (silencio
    con motivo `falso-positivo`, con autor — reutiliza el cajón que §2 ya tenía, no inventa un
    estado) o `DismissDispute` («sigue siendo defecto»: retira la marca y deja el hallazgo igual).
    El historial conserva las disputas aunque la marca se limpie.
  - El prompt del auditor y la descripción de `report_verdicts` lo dicen explícitamente: usa
    «arreglado» SOLO si el código cambió; para discrepar, «no-es-defecto».

- **D-109 — Detener una sesión es un final ordenado, no un aborto.** Investigando la «sesión
  fantasma» (un `lastConfirmed` a las 12:13 local sin fichero de sesión ni informe) el mecanismo
  resultó estar en el código, no en el misterio: la ingesta persiste los hallazgos **en vivo**, pero
  al cancelar, `RunAsync` lanzaba `OperationCanceledException` y se saltaba TODO lo posterior al
  bucle de unidades — registro de sesión, informe, **liberación de claims** y commit+push. Una
  parada dejaba el hub mutado sin traza de quién lo hizo y las unidades reclamadas hasta que
  caducara el TTL.
  - Ahora la cancelación se captura y la sesión se cierra igual, con lo que llevara hecho.
  - **«Interrumpida» se mide por cobertura, no por el botón**: `session.Units.Count < units.Count`.
    Una parada que llega cuando ya se procesaron todas las unidades pedidas no es una interrupción —
    la sesión cubrió lo que decía cubrir. Este matiz apareció al escribir el test, que con una sola
    unidad esperaba lo contrario de lo correcto.
  - Una sesión interrumpida **no cierra ciclo**, marca solo las unidades realmente auditadas, y V5
    dice qué se guardó en vez de un «detenida» que hacía parecer perdido el trabajo.
  - **Nota de método:** al leer el log confundí husos (el log va en local `+02:00`, las sesiones en
    UTC) y estuve a punto de dar por buena una correlación falsa con un arranque del CLI. La lección
    de D-096 otra vez: verificar antes de afirmar, y verificar también las unidades.

- **D-110 — Lo que NO se había construido entonces (CERRADO en F5.2, ver D-125..D-127).** El caso «la app muere de golpe» (cierre
  forzado, cuelgue) sigue pudiendo dejar escrituras huérfanas: la parada limpia solo cubre la
  cancelación cooperativa. El arreglo natural es una **marca de sesión abierta** que la siguiente
  ejecución encuentre y cierre como interrumpida. No se hace aquí porque exige un artefacto nuevo en
  el hub y un paso de recuperación al arranque, y esta tanda ya toca la reconciliación. Queda
  propuesto, no prometido.

- **D-111 — Corrección del dato real.** `01M0W3XH1CCMWEA365Z23QAGD5` se ha revertido a **activo** y
  marcado **disputado** por `gpt-5.5`, conservando su razonamiento. La resolución era inválida
  procedimentalmente con independencia de lo acertado del argumento; decidirlo es de una persona,
  por gobernanza, y ahora hay un cajón donde esperar esa decisión.

- **D-113 — El coste no es por tokens, es por llamada.** Medido sobre tres sesiones reales de la
  misma unidad: `coste = llamadas × multiplicador`, exacto (14×1=14, 3×1=3, 12×**7,5**=90). Bajar el
  tope del barrido de 5 a 3 hizo lo suyo (14 → 12 llamadas, 420 k → 260 k tokens) y quedó sepultado
  bajo el ×7,5 de cambiar a `gpt-5.5`. Consecuencia práctica: **la palanca de coste es el
  multiplicador del modelo**, que F5.1 ya enseña en el desplegable; el tope de pasadas mueve ±15 %.
  Y cortar el tope por debajo de la convergencia sale MÁS caro por unidad de cobertura, porque se
  pagan varios barridos incompletos en vez de uno que cierra.

- **D-114 — Validado sobre datos reales, y el resumen se callaba la disputa.** Primer barrido con
  F5.1b sobre `CommonStatics.cs` (`gpt-5.5`, mismo commit `f86a301`, tope 5): **pasada 1 SECA** —
  0 nuevos, 0 ubicaciones, 20 «presente» y **1 «no-es-defecto»** sobre exactamente el hallazgo
  revertido en D-111. Cero resueltos, cero degradaciones: el modelo fue directo al cajón correcto,
  así que la guarda del commit ni tuvo que actuar. El prompt bastó. Coste 22,5 (3 llamadas) frente
  a los 90 del barrido anterior de tres pasadas.

  Pero el mensaje de V5 dijo «nuevos 0, confirmados 20, resueltos 0» y **no mencionó la disputa**:
  los contadores nuevos se habían añadido al informe y no a la línea que de verdad lee el usuario.
  Un número sin causa justo del tipo que D-060 prohíbe, y encontrado por el usuario, no por los
  tests. `SessionViewModel` nombra ahora disputas y degradaciones; cubierto y verificado por
  mutación.

- **D-115 — Un lote vacío es una respuesta, no un rechazo.** `submit_findings([])` es como el
  auditor dice «no hay nada nuevo en esta unidad»: la respuesta normal de un barrido que converge.
  Contarlo como payload rechazado inflaba `Counters.Rejected` y pintaba un ⚠ en el informe donde no
  había ningún problema — el mismo pecado que D-060 pero al revés: una alarma sin causa. La llamada
  sigue apareciendo en `ToolCallLog` con `items=0`, así que no se traga nada; simplemente deja de
  ser un error. `report_verdicts` vacío SÍ sigue siendo un rechazo: ahí el auditor tiene la
  obligación de pronunciarse sobre cada hallazgo listado.

  **Nota de método (segunda vez en esta tanda).** El primer test daba verde con la mutación puesta:
  `FakeCopilotAgent` se salta la tool cuando no tiene nada que enviar, así que el caso no se
  ejercitaba. Hizo falta un agente que llamara de verdad con el array vacío, que es lo que hizo
  `gpt-5.5`. Igual que con el centinela `unknown` (D-106): **un test verde no prueba nada hasta que
  se le ve fallar**.

- **D-112 — Cobertura.** 18 tests nuevos (`VerdictGuardTests`, `StoppedSessionTests`). Verificados
  por mutación: desactivar la guarda entera tumba 3; desactivar solo la capa del `contentHash` tumba
  1; no excluir el centinela `unknown` tumba 1; volver a lanzar en la cancelación tumba 7.

## F5.2 — Tanda visual: V5, navegación de sesión y cierre robusto

Experiencia de uso y el remate de robustez que D-110 dejaba pendiente. El motor no se toca: lo
único que cambia dentro del coordinador son eventos **de observación**, aditivos, que emiten datos
que ya calculaba y no alteran ninguna decisión.

### Hito 1 — La sesión sobrevive a la navegación

- **D-116 — El estado de la sesión sale del view-model y pasa a un servicio singleton.**
  El problema no era que la sesión no corriera en segundo plano —sí lo hacía—, sino que su estado
  vivía en `SessionViewModel`, que es `Transient`: navegar fuera y volver creaba una instancia
  nueva y la pantalla aparecía vacía aunque la auditoría siguiera. Ahora `LiveSessionService`
  (singleton) es el dueño del estado y V5 es una **vista** sobre él: al volver, se reconstruye
  desde el servicio en vez de depender de haber estado abierta.

- **D-117 — Lanzar deja de ser un efecto secundario de navegar, y eso cierra D-085 por diseño.**
  Antes, `SessionViewModel.LoadAsync` EJECUTABA la auditoría. Como `LoadAsync` significa «recarga
  la vista» en todas las demás páginas, el tick de polling la llamaba y relanzaba sesiones en
  bucle. D-085 lo tapó con un `_startedForRequest` y un cerrojo antes del primer `await`; F5.2
  elimina la clase entera de fallo: **la vista ya no sabe arrancar sesiones**. Se lanza
  explícitamente desde V2 (`LiveSessionService.StartAsync`) y `LoadAsync` no ejecuta trabajo jamás.
  Las invariantes de D-085 siguen probadas, pero contra el servicio, que es donde ahora viven:
  recargar no relanza · las recargas no tocan los hallazgos · lanzar otra vez sí arranca otra
  sesión · tres disparos concurrentes atraviesan el guardia exactamente una vez.

- **D-118 — La carcasa enseña la sesión desde cualquier página.** Item de navegación con punto
  pulsante mientras corre («Sesión en vivo») que pasa a «Última sesión» al terminar; línea en la
  barra inferior («Auditando app · unidad 3/10 · pasada 2») clicable; y toast con el resumen
  cuando una sesión termina sin la vista abierta — porque el resumen no puede perderse en una
  línea fugaz de una pantalla que nadie estaba mirando.

- **D-119 — Cerrar la app con sesión viva pregunta, y usa la parada ORDENADA existente.** El
  diálogo confirma y llama al mismo `Stop()` de F5.1b. No se duplica el camino de parada: un
  segundo camino sería exactamente el que se olvidaría de escribir el registro, de liberar los
  claims o de publicar.

### Hito 2 — Rediseño de V5

- **D-120 — La columna de actividad tenía puntos porque el adaptador emitía puntos.** No era un
  problema de la vista: `RealCopilotAgent.OnSessionEvent` hacía `TextStreamed(".")` para cualquier
  mensaje del asistente. El SDK 1.0.11 trae el contenido en
  `AssistantMessageDeltaData.DeltaContent` (streaming) y el mensaje entero en
  `AssistantMessageData.Content` al cerrar el turno; emitir ambos duplicaría todo el texto, así que
  el mensaje completo solo se emite si de él no llegó ningún delta.
  **Y esto SÍ está probado sin asiento**, al contrario que el resto de `RealCopilotAgent` (D-017):
  los tipos de evento son construibles, así que el mapeo y la deduplicación se ejercitan de verdad.
  Verificado por mutación: devolver los puntos tumba 3 tests; quitar la deduplicación, 2.

- **D-121 — Si el modelo habla poco, narran los eventos.** La columna intercala el texto real con
  eventos de una línea («＋ Hallazgo», «⚖ Disputado», «↻ Pasada 2: 3 nuevos», «✓ Pasada 3 seca»,
  «✂ Cortada por presupuesto»). La regla es que **nunca** haya una columna vacía o de puntos: con
  un modelo parco, los eventos son la narración mínima.
  Los deltas llegan en trozos de pocos caracteres y se **acumulan en la última entrada de texto**
  en vez de crear una fila por trozo, que haría inmanejable la lista.

- **D-122 — Elipsis EN MEDIO en la cola.** `XBLASTCommon/…/CommonStatics.cs`, con la ruta completa
  en el tooltip. Una elipsis al final se come justo lo que identifica la unidad: el nombre del
  fichero.

- **D-123 — El pie destaca la métrica que manda: coste = llamadas × multiplicador (D-113).** Los
  tokens siguen ahí, con caché y media por unidad, pero en segundo plano: no son la palanca.

- **D-124 — Pantalla de cierre persistente, con cada número explicado y desplegable.** Sustituye a
  la línea fugaz de estado. Cada contador trae su frase («Disputados: el auditor sostiene que nunca
  fueron un defecto. NO están resueltos: los decides tú») y, al pulsarlo, qué hallazgos lo componen.
  Es la respuesta directa a D-114: aquel «confirmados 20» que se calló una disputa fue el último
  aviso de que un número sin causa se escapa en cuanto no se le pone la causa **delante**.

### Hito 3 — Cierre de D-110: marca de sesión abierta

- **D-125 — La parada ordenada no cubre un `taskkill`, y ahí no corre ningún `finally`.** Se escribe
  una **marca de sesión abierta** al arrancar (ULID, app, modo, commit, unidades reclamadas, PID e
  instante de arranque del proceso), se actualiza al cerrar cada unidad y se borra al terminar. Vive
  en `%LOCALAPPDATA%` y **no** en el hub: es un hecho de esta máquina y este proceso; publicarlo
  haría que la marca de un portátil apagado pareciera una sesión viva para todo el equipo.

- **D-126 — Se compara el PID *y* el instante de arranque.** Los sistemas reciclan PIDs: sin la
  segunda mitad, un proceso ajeno que heredara el número haría pasar por viva una sesión muerta y
  la recuperación no ocurriría nunca. Si no hay permiso para inspeccionar el proceso, se asume
  **vivo**: recuperar una sesión que en realidad sigue corriendo sería peor que no recuperarla.

- **D-127 — Recuperar es cerrar el registro, no reconstruir el trabajo.** Los hallazgos ya están en
  disco (la ingesta escribe en vivo). Al arrancar, si la marca es huérfana: se escribe el registro
  de sesión con `Interrupted = true`, se liberan los claims —lo más urgente, porque si no bloquean
  a los demás hasta el TTL—, se borra la marca, se publica y se avisa con un toast.
  **Aproximación declarada:** el recuento de hallazgos usa «los de esta app detectados desde que
  arrancó la sesión», porque un hallazgo no guarda el ULID de la sesión que lo creó. Va escrito en
  las notas de la propia sesión recuperada, no escondido aquí.

- **D-128 — Cobertura.** 25 tests nuevos. Verificados por mutación: limpiar el estado al terminar
  tumba 2 (la vista dejaría de reconstruirse); no borrar la marca al cerrar bien, 1 (el siguiente
  arranque «recuperaría» una sesión sana); devolver los puntos, 3; quitar la deduplicación del
  streaming, 2.

- **D-129 — Lo que esta tanda NO toca.** Motor, reconciliación, barrido, guarda de evidencia de
  cambio, disputas, coste, conexión y Ajustes quedan como los cerraron F5.1 y F5.1b. No hay vistas
  nuevas: es V5, la navegación y el arranque. El tema visual (WPF-UI/Fluent, D-011) tampoco se
  rehace.

## F5.3 — Retoques post-F5.2 (tanda corta)

Cuatro arreglos acotados sobre lo entregado en F5.2, los cuatro nacidos de mirar la aplicación
funcionando. Tres son de lectura —qué se ve y cuánto estorba— y el cuarto abre una salida que
hasta ahora no existía: deshacer el alta de una aplicación.

### §1 — La cola de V5 dice el nombre, no la ruta

- **D-130 — La cola muestra SOLO el nombre del fichero.** `EnumContextMenuType.cs`, sin ruta y sin
  elipsis. La ruta completa sigue estando en el tooltip y pasa a estar en la **cabecera de la
  sección de actividad**, que antes repetía el mismo recorte que la cola.

- **D-131 — Y con eso se retira D-122.** Aquella elipsis EN MEDIO estaba bien razonada para el
  problema que creía tener («¿por dónde cortar la ruta?») y resolvía el equivocado: en una columna
  de 250 px la ruta no identificaba nada que el tooltip no dijera mejor. Cortar por el medio hacía
  más legible una información que sobraba entera. La decisión no se corrige, se **sustituye**: el
  criterio ya no es dónde cortar, sino qué merece estar ahí.

- **D-132 — Desambiguar es una propiedad del LOTE, no de una ruta.** Dos unidades del mismo lote
  pueden llamarse igual, así que `UnitProgress.ShortNames` calcula los nombres mirando la lista
  entera y añade el **mínimo** de tramos que separa a los que chocan
  (`Class/EnumContextMenuType.cs` frente a `Enums/EnumContextMenuType.cs`); las que no chocan se
  quedan a nombre pelado. Crece **por grupos y re-agrupando**: alargar unos pocos puede crear un
  choque nuevo con otro que ya era único, y sin recomprobar la cola acabaría con dos filas
  idénticas — que es exactamente el fallo que la desambiguación venía a evitar.

### §2 — Una sola marca en la ventana

- **D-133 — Fuera la cabecera «ATALAYA» del rail de navegación.** La marca salía dos veces en la
  misma esquina: la etiqueta de la barra de título y, tres centímetros debajo, la cabecera grande
  del panel. Se queda la de la barra de título —que es la que el sistema operativo también usa— y
  el rail empieza directamente por los items.

### §3 — La barra de estado no acumula residuos

- **D-134 — El diagnóstico: los avisos no caducaban.** Eran cadenas en una lista de la barra
  inferior que solo se recortaba —a seis— dentro del **tick de sondeo**. Es decir, no tenían vida
  propia: se iban cuando otros seis las empujaban. Tras dos auditorías seguidas quedaban dos
  píldoras «Sesión completada: …» a la vez, fijas, tapando lo único que esa barra debe decir
  siempre: sincronización, cuenta y «Auditando…».

- **D-135 — `ToastCenter`: efímeros, descartables y uno por clase.** Todo aviso caduca solo a los
  8 s y se descarta con un clic. De **cierre de sesión hay UNO**: el nuevo sustituye al anterior en
  vez de apilarse, y hereda vida entera en vez de morir con el reloj del que reemplazó.

- **D-136 — El barrido lo dispara quien llama, no un temporizador interno.** `Sweep(now)` recibe el
  instante, así que la caducidad se prueba sin esperar ocho segundos de verdad y sin abstraer un
  temporizador. En la aplicación lo mueve un `DispatcherTimer` de **1 s**, aparte del sondeo del
  hub: aquél corre cada 60 s como poco, y un aviso que dura 8 s no puede colgar de un reloj quince
  veces más lento.

- **D-137 — Dónde vive cada cosa.** Los avisos flotan SOBRE la página, abajo a la derecha; la barra
  de estado se queda con lo estable (sync, cuenta, «Auditando…») y nada más; y el resumen
  permanente de la última sesión sigue donde se puede volver a leer: el item **«Última sesión»**
  del rail. Un aviso efímero no es sitio para información que hay que consultar.

### §4 — Eliminar / hard-reset de una aplicación

- **D-138 — Se borra la carpeta ENTERA, no fichero a fichero.** `apps/{slug}/` completa: hallazgos,
  sesiones, informes, inventario, silencios, claims, comentarios y `app.json`. Enumerar por tipo
  dejaría fuera cualquier cosa que se añada al esquema más adelante, y un hard-reset que se deja
  medio rastro no es un hard-reset.

- **D-139 — La confirmación pide escribir el NOMBRE, no un «¿Seguro?».** Un sí/no se pulsa por
  inercia; teclear el nombre obliga a leer **cuál** se está borrando. La comparación es exacta
  (solo se toleran espacios de sobra): aceptar mayúsculas distintas devolvería la confirmación a
  ser un sí/no con pasos extra. La puerta vive en `DeleteAppConfirmation.CanDelete`, fuera del
  diálogo, y el view-model **la vuelve a mirar** antes de borrar: un botón gris es una cortesía de
  la vista, no una garantía del modelo.

- **D-140 — La confirmación dice el tamaño real y también lo que NO se pierde.** Los contadores
  salen del hub (*N* hallazgos, *N* sesiones, *N* informes, *N* silencios) en vez de un «se borrará
  todo», y la misma pantalla aclara que el **historial git del hub conserva una copia recuperable
  por un administrador** y que el repositorio auditado y el clon local no se tocan. Sin esa segunda
  mitad la pantalla miente en las dos direcciones: exagera la destrucción y esconde que queda
  rastro.

- **D-141 — Commit explicativo y push inmediato.** `app: hard-reset de {slug} por {usuario}` es la
  única traza que queda de la decisión. Borrar solo en local dejaría la app viva para el resto del
  equipo y la haría **reaparecer en el siguiente pull**. Si el push falla (sin red, credenciales
  caídas) el commit se queda pendiente y sale con «Sincronizar ahora» — y el resultado lo **dice**
  en vez de fingir que se publicó.

- **D-142 — Se limpia también lo local: `machines.json` y la marca de sesión abierta.** La ruta del
  clon es por máquina (§4) y dejarla apuntaría a una app que ya no existe. La marca de sesión
  (D-125) se borra si nombra a esa app: una marca huérfana haría que el siguiente arranque
  intentara «recuperar» una sesión sobre nada.

- **D-143 — Con la app auditándose, el icono está deshabilitado.** La señal es `AuditingNow`, es
  decir, los claims vivos, que es lo que ve el equipo entero y no solo esta máquina. Se le suma la
  sesión local en curso porque hay un instante entre lanzarla y publicar sus claims, y es justo la
  ventana en la que peor sienta pulsar la papelera. Un claim **caducado** no bloquea nada: para eso
  tiene TTL.

- **D-144 — Volver a auditarla es un alta normal.** No hay «restaurar»: se borró todo, así que el
  camino de vuelta es **Nueva aplicación** desde cero. Que no exista un deshacer a medias es parte
  de por qué la confirmación puede permitirse ser tan explícita.

### Cobertura y verificación

- **D-145 — 25 tests nuevos, verificados por mutación (9 mutaciones, las 9 tumban tests).** Una
  sola vuelta en `ShortNames` sin re-agrupar, 1; que el aviso de cierre deje de sustituir al
  anterior, 1; que nada caduque, 1; devolver la cabecera «ATALAYA» al rail, 1; borrar sin hacer
  push, 2; no limpiar `machines.json`, 1; `CanDelete` siempre verdadero, 1; quitar la
  re-comprobación del nombre escrito en el view-model, 1; y comparar el nombre sin distinguir
  mayúsculas, 1.
  El borrado se prueba contra un remoto local `--bare` (norma N-1, sin red), **con segundo clon**:
  lo que se fija no es «desapareció de mi pantalla», sino que el otro usuario deja de verla al
  sincronizar.

- **D-146 — Dos invariantes se prueban leyendo el XAML.** «Cuántas veces se lee Atalaya en la
  ventana» y «qué cuelga de la barra de estado» son propiedades de la **plantilla**, no del
  view-model: no existen como estado observable que un test pueda interrogar. Instanciar la ventana
  exigiría hilo STA y un `Application` vivo para resolver los recursos de WPF-UI — mucho aparato
  para dos invariantes de maquetado. `ShellChromeTests` las fija donde viven, y la mutación
  confirma que muerden.

- **D-147 — Lo que esta tanda NO toca.** Motor, reconciliación, barrido, guarda de evidencia de
  cambio, disputas, coste, conexión, Ajustes, la marca de sesión abierta y la recuperación de
  sesiones interrumpidas quedan como los cerraron F5.1, F5.1b y F5.2. No hay vistas nuevas más allá
  del diálogo de borrado.

## F5.4 — Rediseño de la vista Hallazgos (V3)

Tanda acotada a una sola vista. V3 era una `DataGrid` de diez columnas con una botonera de acciones
masivas encima: la columna «Título» se recortaba a lo que sobrara y la de «Ubicación» a 200 px, de
modo que la pregunta más elemental —«¿de qué clase habla este hallazgo?»— no tenía respuesta sin
abrir la ficha. Y el combo de severidad no tenía «Todas»: filtrar era un viaje sin billete de vuelta.

### §1 — El principio: la lista encuentra, el detalle actúa

- **D-148 — V3 se queda sin NINGUNA acción de escritura.** Fuera el combo de motivo de silencio, el
  campo de caducidad, «Silenciar sel.», «Asignar a…», «Asignar sel.», «Verify sel.» y las dos
  salidas de disputa. Una acción de gobernanza necesita **contexto y autor**: silenciar sin leer el
  snippet, o aceptar una disputa sin leer la justificación de quien discrepó, es firmar a ciegas.
  V3 pasa a ser buscar, filtrar, ordenar y abrir; V4 es donde se decide.

- **D-149 — Sin acciones masivas no hay selección.** Las checkboxes de la primera columna se van con
  la botonera. Existían para alimentarla y nada más: mantenerlas habría dejado un gesto que no lleva
  a ninguna parte. Con ellas se van los atajos `s`/`a` del code-behind, que operaban sobre esa misma
  selección.

- **D-150 — Ceder no es perder: cada acción retirada se verifica viva en V4.** Silenciar y asignar
  ya estaban allí. Bajaron en esta tanda `VerifyCommand`, `AcceptDisputeCommand`,
  `DismissDisputeCommand` y `OpenInEditorCommand`. Las dos salidas de disputa estrenan además un
  panel propio en la ficha que **enseña quién discrepa y por qué** antes de ofrecer los dos botones
  — información que en V3 no cabía y que es exactamente la que hace falta para decidir. Un test de
  reflexión fija las dos mitades de la frontera: V3 expone tres comandos y ninguno escribe; V4
  contiene los doce.

- **D-151 — Verify pasa a ser de uno en uno.** El verify masivo sobre una selección no dejaba ver
  qué se le estaba preguntando al agente sobre cada hallazgo, ni qué veredicto caía en cuál. Desde
  la ficha, la pregunta y su respuesta están a la vista y el historial queda debajo.

### §2 — Los combos: siempre hay «Todas», y es el arranque

- **D-152 — Todo combo de filtro abre con una opción neutra en primera posición.** «Todas» para
  aplicación y severidad, «Activos» para estado (con «Todos» disponible). El bug de origen no era
  que faltara una opción: era que el filtro **no tenía estado neutro representable**, así que una
  vez elegida una severidad la única vuelta era reiniciar la vista. La opción neutra vale `null` y
  el filtro la interpreta como «no filtres», en vez de codificar la ausencia como un valor especial
  de la enumeración.

- **D-153 — Filtro de aplicación, nuevo.** Se rellena con las apps del portafolio, por su **nombre**
  y no por su slug. Solo se reconstruye cuando el conjunto de apps cambia: rehacerlo en cada tick
  del polling (que recarga la página entera, §3) le habría tirado la selección al usuario en mitad
  de una búsqueda.

- **D-154 — «Limpiar filtros» solo aparece cuando hay algo que limpiar.** Un botón permanentemente
  visible que la mitad de las veces no hace nada enseña a ignorarlo. `HasActiveFilters` es también
  lo que decide si el estado vacío ofrece la salida: «Sin hallazgos con estos filtros» sin un botón
  al lado es un callejón.

- **D-155 — El contador nombra los disputados en vez de esconderlos.** «87 hallazgos · 3
  disputados», misma decisión que ya se tomó para el resumen en vivo de V5: un conteo agregado que
  mete las disputas entre los demás las hace invisibles justo cuando piden una decisión humana.

### §3 — La lista: agrupada por unidad

- **D-156 — Cabecera de grupo por unidad, con el nombre del fichero en grande.** `CommonStatics.cs`
  a 15,5 px y la ruta completa al lado, en pequeño y atenuada. Es la petición central de la tanda:
  la unidad es el sujeto de la auditoría (§2 del sistema), así que es lo que ordena la lista. Los
  chips de conteo por severidad van a la derecha, con la escala de color de siempre.

- **D-157 — Los grupos se ordenan por su hallazgo MÁS grave, no alfabéticamente.** Una unidad con
  una crítica va antes que otra con doce bajas. El desempate es por cuántos hay de esa severidad y
  solo después por nombre.

- **D-158 — La fila es de dos líneas y el título va ENTERO.** Línea 1: chip de severidad, título con
  `TextWrapping="Wrap"` y las marcas (disputado, needsReview) a la derecha. Línea 2, atenuada:
  displayId · confianza · línea(s) · asignado (avatar de iniciales) · frescura, con un punto ámbar
  cuando pasa el umbral. Nada se recorta con elipsis, que es de lo que venía la queja.

- **D-159 — El estado solo se escribe cuando NO es «Activo».** Con el filtro en «Todos» conviven
  activos, resueltos y silenciados; una fila que no lo dijera sería una mentira por omisión. Con el
  filtro por defecto, escribir «Activo» en las cinco filas sería ruido puro.

- **D-160 — La columna «App» desaparece de las filas.** Con el filtro en una app concreta es
  redundante; con el filtro en «Todas» sube a la cabecera del grupo, junto a la ruta. Repetirla en
  cada fila era gastar ancho en decir lo mismo N veces.

- **D-161 — La fila entera es el enlace; no hay botón «Ver».** Un `Button` sin cromo, con hover
  sutil y foco de teclado. Un botón de 40 px dentro de una fila de 900 convierte en puntería lo que
  debería ser un clic en cualquier parte.

- **D-162 — La lista se aplana para seguir virtualizando por FILA.** `Items` intercala cabeceras y
  filas en una sola colección, con dos `DataTemplate` por tipo, en vez de anidar un `ItemsControl`
  por grupo. Anidar habría dejado la virtualización operando sobre **grupos**: con una app real, un
  grupo grande materializa entero de todas formas y no se virtualiza nada. Plegar un grupo es
  simplemente no aportar sus filas al aplanado.

- **D-163 — El pliegue de un grupo sobrevive a las recargas.** El polling llama a `LoadAsync` cada
  tick; un pliegue hecho a mano que se deshiciera solo cada 60 s sería peor que no poder plegar. Se
  recuerda por clave `{slug} {ruta}`, no por referencia al grupo, que se reconstruye en cada
  recarga.

### §4 — Retoques de lectura sobre V3 (mirando la vista pintada)

- **D-167 — El buscador dice «Buscar…» y explica en el tooltip.** «Buscar título, ruleId o ruta…»
  no cabía en el hueco que le deja la fila de filtros y se cortaba, que es la peor de las dos
  opciones: ni informa ni respira. El placeholder se queda con la palabra y el detalle («busca en
  el título, en la regla que lo detectó y en la ruta del fichero») pasa al tooltip. La columna sube
  a 200–300 px, que es lo que necesita un cuadro de búsqueda para que se lea lo que se teclea.

- **D-168 — El hueco de la barra de desplazamiento se reserva SIEMPRE.** Los chips de la cabecera
  de grupo se comían el borde derecho y el último se solapaba con la barra. Se les da 14 px de
  margen y, sobre todo, la lista pasa a `VerticalScrollBarVisibility="Visible"`. Con `Auto` el
  ancho útil cambia en el momento en que la barra aparece: al filtrar, la lista se acorta, la barra
  desaparece y **toda la columna de chips salta** a la derecha. Reservar el hueco cuesta una franja
  vacía cuando no hace falta y a cambio la cabecera no se mueve nunca.

- **D-169 — «needsReview» era el modelo de datos asomando por la interfaz.** Pasa a «Por revisar»
  en el filtro y en la marca de la fila; el campo sigue llamándose `NeedsReview` en el código y en
  los ficheros del hub, que es donde significa algo. El barrido de la misma jerga encontró dos
  más: `ruleId` en el placeholder (D-167) y —la menos evidente— la severidad, que se escribía
  volcando el identificador de la enumeración con `ToString()` y ponía **«Critica»**, sin tilde, en
  una interfaz en castellano. `SeverityNames.Display` da el nombre que se escribe y
  `SeverityToLabelConverter` lo lleva al XAML.

- **D-170 — La tilde de «Crítica» solo se arregla en V3.** V4 y V5 siguen volcando la enumeración.
  El converter y el helper quedan disponibles para adoptarlos allí, pero cambiarlos sale del
  alcance de esta tanda y no se toca lo que no se ha mirado funcionando.

- **D-171 — La marca de disputa dice la palabra, no solo la balanza.** Un ⚖ de 11 px sobre el ámbar
  del badge se renderizaba como **emoji a color** —Segoe UI Emoji ignora el `Foreground`— y quedaba
  un borrón dorado sobre fondo dorado. La marca pasa a «⚖ Disputado» (con «×N» si discrepan varios
  modelos), en el mismo formato que «Por revisar», y el tooltip explica quién discrepa. El selector
  de presentación de texto U+FE0E está puesto, pero WPF no lo respeta: lo que hace legible la marca
  es la palabra, no el glifo.

- **D-172 — Los tres retoques se verificaron pintando la vista, no leyéndola.** Un arnés STA
  desechable montó V3 con datos reales y volcó PNG a 1x y a 2x: la lista corta (para comprobar que
  el hueco de la barra se reserva también sin desbordar), la larga y un zoom sobre la cabecera. El
  solape de los chips y el borrón del ⚖ solo se ven mirando; ningún test los habría contado. El
  arnés se borró después: crear un `Application` en el proceso de tests es justo el aparato que
  D-146 decidió no meter en la suite.

- **D-173 — La jerga tiene guarda de regresión.** `Ninguna_etiqueta_visible_escribe_jerga_interna`
  barre los `Text`/`Content`/`PlaceholderText`/`ToolTip` literales del XAML —los `{Binding}` no son
  texto— y falla si vuelve a aparecer `needsReview`, `ruleId`, `displayId`, `slug`, `ULID`,
  `fingerprint` o `isStale`. Empieza exigiendo que el barrido encuentre etiquetas: un regex que
  deja de casar nada pasaría en verde sin comprobar nada. Con la tilde, 31 tests en V3 y 5
  mutaciones nuevas (las 5 tumban tests).

### §5 — La barra de filtros se reacomoda y los grupos se pliegan en bloque

- **D-174 — La fila de filtros pasa de `Grid` a `DockPanel` + `WrapPanel`.** Un `Grid` de columnas
  fijas no tiene forma de degradar: cuando el ancho no llega, el último control se RECORTA. Y el
  ancho no llega en cuanto el rail de navegación se lleva sus ~250 px, que es la situación normal,
  no la excepción — ensanchar el buscador (D-167) solo movió el problema al vecino de la derecha,
  el toggle «Disputados». Con `WrapPanel` los filtros bajan de línea cuando no caben y siguen
  siendo una sola fila cuando sí. «Limpiar filtros» va anclado a la derecha con `DockPanel` para
  que no se lo lleve el reflujo.

- **D-175 — Un solo botón que alterna, no dos.** «Colapsar todo» / «Expandir todo» junto al
  contador. Con la mitad de los grupos abiertos, la única acción que cambia algo para todos es
  colapsar; ofrecer las dos a la vez obliga a leer cuál de ellas hace algo. El texto del botón ES
  su estado, así que no necesita explicación aparte, y sin grupos se retira.

- **D-176 — Plegado todo, la cabecera de grupo ES el informe.** Nombre de clase, aplicación, ruta y
  chips de conteo por severidad. No hizo falta añadir nada: los chips ya cargaban el resumen, y un
  test lo fija comprobando que la suma de los chips es el número de hallazgos del grupo — si la
  cabecera dejara de cuadrar, la vista plegada estaría mintiendo.

- **D-177 — La memoria de plegado vive FUERA del view-model.** V3 es transitoria en el contenedor:
  abrir un hallazgo y volver construye un view-model nuevo, y ese es el gesto más frecuente de la
  vista. Una memoria interna se perdía en cada ida y vuelta. `GroupExpansionMemory` es un singleton
  de la sesión. No se persiste a disco a propósito: qué clases tenía plegadas anteayer depende del
  filtro que hubiera puesto entonces, así que resucitarlo sería restaurar un estado que ya no
  significa nada.

- **D-178 — Lo automático nunca se guarda; lo que decide el usuario, sí.** La memoria distingue
  tres estados —abierto, cerrado y **nunca tocado**— en vez de un conjunto de plegados. Con un
  conjunto no se puede separar «el usuario lo cerró» de «se abrió solo», y la regla del umbral
  acabaría pisando decisiones o volviéndose pegajosa.

- **D-179 — El umbral mira cuántos grupos hay EN ESTE filtro, y se reevalúa al filtrar.** Hasta
  cinco grupos, lo no decidido se abre; por encima, se pliega. Aplicarlo solo al abrir la vista
  dejaría treinta grupos plegados después de filtrar a dos, que es justo cuando el usuario quiere
  ver el contenido. Lo que él haya plegado a mano gana siempre a la regla.

- **D-180 — 10 tests nuevos y 8 mutaciones, las 8 tumban tests.** Quitar el umbral, invertirlo, no
  consultar la memoria, no guardar el plegado a mano, no guardarlo en «colapsar todo», que el botón
  deje de alternar, quitar la guarda de lista vacía en `AllCollapsed` y vaciar la memoria entre
  view-models. Una novena —volver al panel que recortaba— no cuenta: rompe el XAML y lo que falla
  es la compilación, no un test.

- **D-181 — El reflujo y la vista plegada se comprobaron pintando, no leyendo.** Que un control se
  recorte es una propiedad del layout a un ancho dado: ningún test lo ve. Se volvió a montar el
  arnés STA desechable y se volcó V3 a 890 px (el ancho real de la página con el rail) y a 760 px,
  con los grupos abiertos y plegados. A 890 px baja «Disputados» a la segunda línea; a 760 px bajan
  los dos toggles juntos; en ninguno se corta nada.

### §6 — La ventana abre con el ancho que la vista necesita

- **D-182 — El ancho por defecto sale de una medida, no de una estimación.** La barra de filtros de
  V3 necesita **1064 px de página** para caber en una línea. No es un cálculo sobre el papel: se
  midió pintando la vista y leyendo la altura del `WrapPanel` a anchos crecientes de dos en dos,
  con «Limpiar filtros» visible, que es el caso ancho. Sumados el rail (210) y el relleno de la
  página (20 a cada lado), la ventana necesita **1314**. Abría con 1180 — 134 px corta—, y por eso
  el último filtro caía a una segunda línea nada más arrancar. Pasa a **1340**, que deja algo de
  aire y se queda **por debajo de los 1366 px** de los portátiles pequeños, donde 1400 ya no
  entraría.

- **D-183 — Ensanchar la ventana NO deshace el reflujo de D-174.** Son dos cosas distintas: el
  ancho por defecto arregla la primera impresión, y el `WrapPanel` sigue siendo lo que hace que la
  vista aguante cualquier otro ancho. El mínimo de la ventana se queda en 900, donde los filtros
  ocupan tres líneas y no se recorta nada. El test del ancho lo dice explícitamente para que nadie
  lo lea como un mínimo.

- **D-184 — Y el tamaño inicial se recorta a la pantalla que hay.** WPF mide en unidades
  independientes del dispositivo: con el escritorio al 150 %, un monitor de 1920 físicos son 1280
  de escritorio, y una ventana de 1340 abriría más ancha que la pantalla **y centrada**, o sea con
  la barra de título a medias y los bordes fuera por los dos lados. `StartupSize.Clamp` la ajusta
  al área de trabajo dejando 40 px de aire, pero **nunca por debajo del mínimo**: una ventana que
  se sale un poco es molesta, una por debajo de su mínimo es inutilizable. Va en una clase aparte
  porque la aritmética se puede probar y la ventana no.

- **D-186 — El contador siempre fue del recorte; ahora está fijado.** Preguntado si «24 hallazgos ·
  1 disputado» hablaba del hub entero o de lo filtrado, resultó que ya salía de la misma lista que
  alimenta los grupos, después de los seis filtros — pero solo estaba probado contra el filtro de
  aplicación. Un contador global junto a una lista filtrada son dos cifras que no cuadran, y la que
  se cree es la grande, así que la propiedad merece prueba propia: aplicación **y** estado a la
  vez, la cola de disputas cuando el filtro deja fuera al disputado, y la búsqueda. Tres mutaciones
  —contar todo el hub, saltarse el filtro de estado y contar las disputas del hub entero— y las
  tres tumban tests. Cero cambios en producción: la respuesta era «ya lo hace», y lo que faltaba
  era que siguiera haciéndolo mañana.

- **D-185 — 5 tests nuevos y 3 mutaciones, las 3 tumban tests.** Devolver el ancho a 1180, quitarle
  el suelo del mínimo al recorte y quitarle el recorte entero. El test del ancho lee el XAML y
  compara contra los 1064 medidos más el rail: si alguien encoge la ventana, o si un filtro nuevo
  ensancha la barra, salta antes de que el usuario lo vea partido.

### Cobertura y verificación

- **D-164 — 29 tests nuevos, verificados por mutación (12 mutaciones, las 12 tumban tests).** Son
  47 tests y 31 mutaciones al cerrar los §4, §5 y §6. Cubren
  los valores iniciales de los tres combos, cada filtro por separado, sus combinaciones, la **vuelta
  a «Todas»** en severidad y en aplicación, el recorrido completo del filtro de estado, la búsqueda
  por título/ruleId/ruta, «Limpiar filtros», `HasActiveFilters`, el contador (plural, singular y
  cola de disputas), la agrupación y su orden, el aplanado, el pliegue y su supervivencia, la
  preselección de app al entrar desde V2, y la frontera V3/V4 en las dos direcciones.

- **D-165 — El fixture está construido para que el alfabeto y la severidad DISCREPEN.** La primera
  versión ordenaba `Common.cs` (crítica), `Otro.cs`, `Servicio.cs` — que es también el orden
  alfabético, así que la mutación «no ordenes por severidad» **sobrevivió**: el test parecía fijar
  la ordenación y solo fijaba el alfabeto. Renombrada la unidad a `Api.cs`, el orden esperado
  (`Common.cs`, `Api.cs`, `Otro.cs`) ya no se explica de ninguna otra forma. Un test verde que no
  distingue la implementación correcta de la incorrecta no cubre nada, y solo la mutación lo dice.

- **D-166 — Lo que esta tanda NO toca.** Motor, reconciliación, barrido, sync, guarda de evidencia
  de cambio, coste, conexión, Ajustes, V5 y el resto de vistas quedan como las cerraron F5.1, F5.1b,
  F5.2 y F5.3. De V4 solo se toca lo necesario para recibir lo que V3 cede. No se añade nada nuevo
  a la lista (exportar, columnas configurables): esta tanda era legibilidad y filtros.

## H9 — Arreglo integrado supervisado (opcional, NO entregado)

- El *feature flag* `enableAssistedFix` existe en Ajustes y el generador de prompt de
  arreglo (§5.7, vía 4→2) está entregado y probado. El **arreglo integrado supervisado**
  (rama `fix/{displayId}` + permission handler por-fichero + diff aprobado + sin push)
  queda como trabajo futuro; el prompt indica que H9 "no bloquea la entrega del resto".
