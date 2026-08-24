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

## H9 — Arreglo integrado supervisado (opcional, NO entregado)

- El *feature flag* `enableAssistedFix` existe en Ajustes y el generador de prompt de
  arreglo (§5.7, vía 4→2) está entregado y probado. El **arreglo integrado supervisado**
  (rama `fix/{displayId}` + permission handler por-fichero + diff aprobado + sin push)
  queda como trabajo futuro; el prompt indica que H9 "no bloquea la entrega del resto".
