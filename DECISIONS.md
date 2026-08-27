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

## F5.5 — Rediseño de la ficha de hallazgo (V4)

Tanda acotada a una sola vista, la que recibió en F5.4 todo lo que V3 cedió. La ficha era una
columna única de novecientos píxeles de ancho y varias pantallas de alto: para llegar a gobernanza
o al historial había que hacer un scroll enorme, el estado de las acciones se escribía al fondo
—donde nadie mira, y donde encima se quedaba pegado— y el snippet era un recorte de siete líneas
renumeradas desde 1 debajo de un hallazgo que vive en la línea 412.

### §1 — Dos columnas: la ficha deja de ser un pasillo

- **D-187 — Principal al 60 % y lateral al 40 %, cada una con su scroll.** Izquierda: cabecera,
  descripción/impacto/recomendación, código, historial y comentarios. Derecha: metadatos, acciones
  y gobernanza. Que la lateral tenga scroll propio es lo que hace que gobernanza siga en pantalla
  por muy largo que sea el hallazgo — es toda la «adherencia» que hacía falta y no necesita ningún
  truco de scroll pegajoso. En 1080p las tres tarjetas de la lateral entran sin desplazar nada.

- **D-188 — El colapso a una columna MUEVE el panel, no lo duplica.** Por debajo de 860 px de
  página la lateral no cabe sin estrangular a la principal, así que baja al final de la primera
  conservando el orden. La alternativa —dos árboles en el XAML con visibilidades cruzadas— es la
  manera segura de que uno de los dos se quede sin arreglar la próxima vez que alguien toque la
  gobernanza. El umbral se fija en un test contra los números de la carcasa: la ventana por
  defecto (1340) va a dos columnas, el mínimo (900) a una.

- **D-189 — Cada bloque en su tarjeta, con el envoltorio de V3.** Mismo `CardBackgroundFill`,
  mismo borde, mismo radio que la barra de filtros de V3, para que las dos vistas se lean como la
  misma aplicación y no como dos productos.

### §2 — La cabecera son chips, y el ruleId baja a metadatos

- **D-190 — Una sola fila de badges: severidad, confianza, estado, disputa y «Por revisar».** El
  estado lleva color semántico —azul activo, verde resuelto, gris silenciado— con los mismos tres
  colores que ya usan el indicador de sync y la cola de V5. La confianza se escribe entera
  («Confianza media»): un chip que pone «Media» al lado de otro que pone «Alta» no dice de qué
  habla ninguno de los dos.

- **D-191 — El ruleId NO se elimina: se etiqueta.** Es la regla del checklist que motivó el
  hallazgo y desde F5.4 se puede buscar por ella en V3, así que borrarlo habría cortado el puente
  entre la lista y la ficha. Lo que se quita es que flote como texto suelto bajo el título: pasa a
  ser el primer campo de metadatos, con su tooltip, en monoespaciada y junto a displayId,
  aplicación, unidad, origen, primera detección, última confirmación, veces confirmado y commit
  anclado.

### §3 — El código que se enseña es el que hay AHORA

- **D-192 — El recorte es el miembro completo, extraído con Roslyn.** Cuatro líneas antes y tres
  después cortaban la firma por la mitad y dejaban fuera el `using` o el `return` que explicaban el
  hallazgo. Ahora se enseña el método, el constructor o el accesor entero.

- **D-193 — Parser y no contar llaves, y por eso hay tests de llaves falsas.** Contar llaves se
  equivoca con las que viven dentro de cadenas, comentarios, literales de carácter, cadenas crudas
  e interpolaciones anidadas, y ninguno de esos casos es exótico en el código que audita Atalaya.
  El parser además **tolera ficheros que no compilan**: el árbol sale igual con nodos de error, así
  que un fichero a medio editar sigue dando su miembro. El coste es una dependencia grande
  (`Microsoft.CodeAnalysis.CSharp`) en una aplicación de escritorio; se acepta porque la
  alternativa es una heurística que falla en silencio y enseña el trozo equivocado.

- **D-194 — Números de línea del FICHERO, con un margen propio.** AvalonEdit numera siempre desde 1
  el documento que le des, así que debajo de un hallazgo de la línea 412 escribía «1..7»: números
  que no sirven para nada y que contradicen la ubicación que la propia ficha muestra al lado. El
  margen nuevo pinta `PrimeraLinea + n - 1` y marca en ámbar la línea del hallazgo; la banda de
  fondo la pinta un *background renderer*, y el marcador del margen existe aparte porque una línea
  en blanco no tiene banda que resaltar.

- **D-195 — Tres desenlaces del ancla, y se distinguen a propósito.** *Anclado* (la línea sigue
  diciendo lo mismo), *Movido* (el mismo código apareció en otro sitio: se enseña la posición
  nueva, no la vieja) y *Cambiado* (no aparece: aviso y «Verificar ahora»). Meterlos en el mismo
  saco convertía un simple desplazamiento de líneas en una alarma, y un cambio real en un silencio.

- **D-196 — Sin clon no se enseña un snippet guardado, porque no existe.** El prompt pedía mostrar
  «la copia anclada al commit», pero el modelo **no guarda el snippet**: de la ubicación solo se
  conserva su `snippetHash`, que es un ancla y no una copia (`Location`, §2). Inventar código
  plausible sería exactamente lo que §3 prohíbe, y añadir el texto al modelo era ampliar la tanda
  al almacén. Se dice lo que se sabe: «Sin clon local en esta máquina: no hay código que mostrar.
  El hallazgo quedó anclado a {ruta}:{línea} en el commit {sha}».

- **D-197 — El panel de código lleva su propia superficie oscura en los dos temas.** Las
  definiciones de AvalonEdit están pensadas para papel blanco —azul marino, negro, verde oscuro— y
  Atalaya abre en tema oscuro, así que el coloreado de fábrica dejaba las palabras clave
  invisibles. En vez de escribir y mantener una definición nueva, se **aclara** la que ya hay:
  cada color sube de luminosidad conservando su tono. Un bloque de código con fondo propio es una
  convención que se lee igual de bien en tema claro.

### §4 — Gobernanza: secciones con nombre y controles que se explican

- **D-198 — Cuatro sub-secciones tituladas, cada una con su línea de ayuda.** Silenciar, Severidad,
  Resolución manual y Disputa. Un `WrapPanel` con seis controles seguidos no dice cuál va con cuál:
  el campo de notas parecía del silencio o de la asignación según dónde hubiera reflujo.

- **D-199 — La caducidad deja de ser un numérico huérfano.** «Caducidad (días) — 0 = permanente»,
  con tooltip. Un `PlaceholderText` que decía «Caduca (días)» desaparecía en cuanto se escribía
  algo, así que el campo con un 30 dentro no decía ni qué eran esos 30 ni qué pasaba con el 0.

- **D-200 — Los motivos de silencio se escriben, no se declaran.** «Falso positivo», no
  `FalsoPositivo`. Mismo escape del modelo a la interfaz que la severidad en F5.4: el combo volcaba
  los identificadores de la enumeración de C#.

- **D-201 — La resolución manual llega PLEGADA y con su advertencia.** Es la acción excepcional de
  la tarjeta —cierra un hallazgo sin que nadie haya mirado el código— y desplegada compite
  visualmente con silenciar y verificar, que son las del día a día. El expander se abre solo si
  falta la justificación, que es cuando hay algo que enseñar ahí dentro.

- **D-202 — Lo que no aplica no se pinta.** «Reabrir» solo si está resuelto; «Des-silenciar» solo
  si está silenciado; «Silenciar» solo si no lo está; «Resolver a mano» solo si no está resuelto; y
  la sección de disputa **solo si hay disputa**. Un botón siempre presente que falla al pulsarlo
  enseña la regla a base de errores.

- **D-203 — La asignación sale de la VISTA, no del modelo.** Decisión del usuario: no se usa. El
  campo sigue en `Finding`, el comando sigue en el view-model y su test sigue verde — lo único que
  desaparece son los dos controles. Recuperarla es volver a ponerlos, no reescribir la gobernanza.
  Un test lee el XAML y comprueba que ya no están; otro comprueba que la acción sigue viva.

### §5 — El historial es una línea de tiempo, no un párrafo cortado

- **D-204 — Icono por tipo de evento, fecha, autor y texto COMPLETO con wrap.** Y del más reciente
  al más antiguo: lo último que le pasó al hallazgo es lo que explica en qué estado está ahora.

- **D-205 — Los eventos hablan castellano.** `FindingEvent` es una enumeración de C#: volcarla con
  `ToString()` escribía «SeverityChanged» en una interfaz en castellano. Traducir es trabajo de la
  vista; el modelo se queda como está.

- **D-206 — Plegar no es truncar.** Los textos largos —las justificaciones de disputa— se limitan a
  dos líneas de ALTURA con un «ver más» que las abre enteras; el texto nunca se recorta en el
  view-model. Truncarlo escondía justo lo que hay que leer para decidir. Un evento que ya cabía no
  se pliega: si no, el «ver más» aparecía en entradas de una frase.

### §6 — Toasts, y ningún estado que se quede pegado

- **D-207 — `StatusMessage` desaparece de la ficha.** Ya no existe la propiedad: no hay dónde dejar
  un mensaje colgado. Toda acción avisa por el `ToastCenter` de F5.3, que caduca solo a los 8 s.

- **D-208 — Abrir el editor tiene tope de tiempo.** `Process.Start` parece instantáneo y no lo es:
  resolver `devenv` por el PATH, levantar el shim `code.cmd` o caer en el manejador del sistema
  puede bloquear el hilo varios segundos, y con una unidad de red desconectada, indefinidamente.
  Ese era el «Abriendo en el editor…» que se quedaba para siempre. Ahora o abre, o falla a los 10
  segundos, pero termina. El tope vive en una función aparte, con la llamada al sistema inyectada,
  para poder probarlo sin depender de que haya un editor instalado.

### Cobertura y verificación

- **D-209 — 52 tests nuevos.** 14 del extractor de límites y 38 de la ficha. Los del extractor
  cubren el método entero, la firma, el método de expresión, la propiedad por su accesor, el
  constructor, la función local ganando al método que la hospeda, el fichero que no compila, el
  caso de las llaves falsas (cadena, comentario, carácter y cadena cruda), el fichero que no es C#,
  la línea fuera de todo miembro, la ventana recortada por los dos lados, la línea fuera del
  fichero, el método gigantesco y el fichero vacío.

- **D-210 — Los de la ficha prueban el CUÁNDO, no solo el QUÉ.** Visibilidad condicional de las
  cuatro sub-secciones (disputa, reabrir, silenciar/des-silenciar, resolver a mano), la disputa que
  se retira sola al cerrarla, las dos validaciones de justificación, el silencio con y sin
  caducidad, la reclasificación que no escribe una entrada vacía, el historial traducido y del
  revés, el plegado de un texto largo y el no-plegado de uno corto, y los cinco estados del
  snippet: anclado, cambiado, movido, sin clon y fichero borrado.

- **D-211 — Lo que no tiene estado observable se lee del XAML.** Igual que en F5.3 (D-181): que la
  asignación ya no esté, que no quede ningún `StatusMessage`, que el ruleId no flote en la
  cabecera, que la resolución manual venga dentro de un `Expander` con su advertencia y que la
  caducidad lleve su etiqueta son propiedades de la PLANTILLA. Instanciar la vista pediría hilo STA
  y un `Application` vivo; leer el fichero las fija donde viven.

- **D-212 — El maquetado se verificó en el `dist`, NO con el arnés de F5.4.** La vista pintada a
  dos anchos —el gesto de D-181— se preparó pero **no se llegó a ejecutar**: la tanda se cerró
  publicando y comprobando la ficha en la aplicación real, con el hallazgo disputado. Lo que sí
  queda fijado en un test es la aritmética del umbral (la ventana por defecto va a dos columnas, el
  mínimo a una); que el reflujo no recorte nada a cada ancho concreto sigue siendo, de momento,
  verificación humana.

- **D-214 — Nada de barridos de mutación: no salen a cuenta.** F5.4 los usó a mano y bien; F5.5
  intentó automatizarlos —21 mutaciones, cada una recompilando la app entera— y costó **~20 minutos
  contra los ~30 segundos** de `build` + `test` de la solución completa. Es 40× la suite, y el
  cuello no es probar sino **recompilar**. Peor: al cancelarlo dejó una mutación inyectada en
  `SnippetReader` que hubo que revertir a mano y volver a compilar, o sea que la verificación se
  cobró otra ronda entera. La regla a partir de aquí: build y tests en cada cambio, y si alguna vez
  hace falta comprobar que un test concreto discrimina, **una** mutación puntual a mano sobre esa
  línea.

- **D-215 — Un arnés que bloquea un hilo del pool cuelga el testhost, no prueba nada.** El test del
  tope de tiempo del editor simulaba un arranque que no vuelve con un `TaskCompletionSource` que
  solo se liberaba en la línea **posterior** a la aserción. Aislado pasaba en milisegundos; en la
  suite entera dejaba el hilo tomado y `dotnet test` no terminaba nunca —diez minutos y subiendo—.
  La versión buena libera **siempre** (`ManualResetEventSlim` con `using` y `finally`) y además le
  pone su propio tope al arnés. Regla: un test que comprueba que algo no se cuelga no puede tener
  una sola ruta en la que él sí se cuelgue.

- **D-213 — Lo que esta tanda NO toca.** Motor, reconciliación, barrido, sync, guarda de evidencia
  de cambio, coste, conexión, Ajustes, V3 y V5 quedan como los cerraron F5.1, F5.1b, F5.2, F5.3 y
  F5.4. De la ficha se rehace la presentación entera; del modelo, nada — ni siquiera la asignación,
  que solo desaparece de la vista.

## F5.6 — Cinco defectos de la ficha (V4): diagnóstico y arreglo

Tanda de arreglos sobre la ficha entregada en F5.5. **No se rediseña nada**: se arregla lo que
hay. Los defectos 1 y 2 se diagnosticaron juntos porque el usuario sospechaba causa común; son
**dos causas distintas** y la relación entre ellas está demostrada abajo (D-221).

### El diagnóstico primero (N-2): qué se midió y con qué

- **D-216 — La evidencia salió del hub real, no de un caso inventado.** Se reprodujo el cálculo de
  `CodeAnchor.ComputeSnippetHash` fuera de la aplicación y se pasó sobre las **56 ubicaciones** de
  los 25 hallazgos reales de `xblast`, contra el clon en `C:\Users\alcil\MyProjects\X-BLAST`,
  parado en el mismo commit (`f86a301`) en el que se confirmaron. Es decir: **el código no ha
  cambiado**, así que el resultado correcto era «cero banners». Lo medido con el código de F5.5:

  | desenlace | ubicaciones |
  |---|---|
  | `Anclado` (el hash casa en la línea guardada) | 3 |
  | `Movido` (casa en otra línea) | 0 |
  | `Cambiado` (**banner**) | **53** |

  53 de 56 banners sobre código intacto. El defecto 1 queda reproducido y acotado antes de tocar
  una línea de producción.

### Defecto 1 — el banner de «el código ha cambiado» salía en todos

- **D-217 — Causa raíz: la normalización del hash es ASIMÉTRICA entre ingesta y presentación.**
  `ComputeSnippetHash` recorta cada línea **solo por la derecha** (`TrimEnd`). Y las dos puntas
  hashean textos distintos:
  - **Al ingerir** (`SessionToolbox.SubmitFinding` / `AddLocations`) se hashea `l.Snippet`, la
    cadena que **manda el LLM**, que llega **sin la sangría** del fichero.
  - **Al mostrar** (`SnippetReader.Locate`) se hashea `lines[loc.Line - 1]`, la línea **cruda del
    fichero**, con sus 8 o 12 espacios de sangría delante.

  Como `TrimEnd` no toca la sangría, ninguna línea de dentro de una clase de C# puede casar
  jamás. La prueba por fuerza bruta lo fija sin ambigüedad: los cuatro hashes auditados
  reproducen **exactamente** el recorte por los dos lados de una línea concreta del fichero, y de
  ninguna otra forma (ni línea cruda, ni bloque de varias líneas, ni bloque des-sangrado):

  | hash guardado | reproduce |
  |---|---|
  | `sha256:2523e49f…` | línea 172 **recortada** — `return uint.Parse(detId, NumberStyles.HexNumber);` |
  | `sha256:c7bb63b5…` | línea 183 **recortada** — `return ushort.Parse(seq);` |
  | `sha256:bfadfcf4…` | línea 80 **recortada** — `public static byte[] StringToByteArray(…)` |
  | `sha256:20afc325…` | línea 60 **recortada** — `public static List<T> ReadCSV<T>(…)` |

  Las 3 ubicaciones que sí casaban son las que caían en líneas sin sangría. No es «CRLF», no es
  «el ancla apunta a otra línea»: es que **un lado recorta la sangría y el otro no**.

- **D-218 — Y el mismo fallo tenía tumbado a `verify`.** `SnippetAnchor.TryAnchor`
  (`VerifyCoordinator`) hashea igual que la ficha, así que devolvía «no anclado» para todo y
  marcaba `needsReview` en cada hallazgo que se verificara. El defecto 1 no era solo cosmético.

- **D-219 — El arreglo es normalizar por los DOS lados, y no migra nada.** `ComputeSnippetHash`
  pasa a recortar cada línea entera (`Trim`) y a descartar las líneas en blanco de los extremos.
  Los hashes ya guardados **siguen siendo válidos**: se calcularon sobre un texto que ya venía sin
  sangría, y recortar por los dos lados un texto sin sangría da lo mismo que recortar por la
  derecha. Cero migración, cero reescritura del hub. El precio es que dos líneas idénticas con
  sangrías distintas colisionan; se paga eligiendo, entre las candidatas, **la más cercana a la
  línea guardada** en vez de la primera del fichero.

### Defecto 2 — la línea resaltada no era la del hallazgo

- **D-220 — Causa raíz: los números de línea del LLM son aproximados, y nadie los corregiía.** El
  caso del usuario, medido: el hallazgo «ConvertToDetId/ConvertToSeq propagan excepciones de
  Parse» guarda L167, que es `/// <param name="detId">…` — un comentario de documentación. El
  hash de esa misma ubicación reproduce la **L172**, `return uint.Parse(detId, …)`, que es
  literalmente el código del hallazgo. Sobre las 53 ubicaciones desviadas el desfase va de **+1 a
  +25 líneas**, casi siempre hacia abajo: el LLM cuenta sobre lo que leyó, no sobre el fichero.
  Anclar por número de línea crudo es, efectivamente, frágil.

- **D-221 — La relación entre el defecto 1 y el 2, demostrada.** Son **causas independientes** —
  una es normalización, la otra es el dato que emite el LLM— pero la primera **enmascaraba** el
  mecanismo que ya existía para arreglar la segunda: la búsqueda de «Movido» de `SnippetReader`
  recorre el fichero buscando el hash, y habría re-anclado sola. Con el hash simétrico, esa misma
  búsqueda resuelve **53 de las 56** ubicaciones a la línea correcta, y las 3 restantes ya estaban
  ancladas. Es decir: **arreglar el 1 arregla el 2 en el 95 % de los casos**, y no por casualidad
  sino porque el hash *es* el ancla buena. No se asume: se midió antes y después.

- **D-222 — El re-anclaje por símbolo es el plan B, no el plan A.** Se comprobó que es viable con
  la infraestructura de F5.5 (`MethodBoundary` ya sube por el árbol de Roslyn hasta el miembro que
  contiene una línea), pero **el hash es mejor ancla que el símbolo**: es exacto, no depende de que
  el título nombre bien el método y no se confunde con sobrecargas. El orden queda: hash en la
  línea guardada → hash en otra línea → **símbolo** → no localizado.

- **D-223 — El símbolo se PERSISTE, porque no estaba.** `submit_finding` ya recibía un `symbol` y
  `SubmittedFinding` lo llevaba, pero `Finding.CreateNew` lo tiraba: el modelo no tenía dónde
  guardarlo. Se añade `Finding.Symbol` (opcional, aditivo, no rompe el JSON existente). Para los
  hallazgos ya guardados, que no lo tienen, los candidatos salen del **título**: los
  identificadores que parecen nombres de miembro. Es peor ancla que el símbolo declarado, y por eso
  va detrás del hash.

- **D-224 — Nunca se resalta un comentario.** Cuando el ancla sale del símbolo (o de la línea
  guardada sin hash con el que contrastarla), la línea buena es la **primera línea de código
  ejecutable** del miembro; si no la hay, su declaración. Un comentario, un atributo, una llave
  suelta o una línea en blanco nunca son el resaltado. Esto es lo que convertía L167 en un
  resaltado que el usuario hacía bien en no creerse.

- **D-225 — Si el símbolo no aparece, se dice.** Estado nuevo `NoLocalizado`: se enseña el fichero
  alrededor de donde estaba, **sin resaltar ninguna línea**, con el aviso «no localizado» y el
  botón «Verificar ahora». Honestidad antes que precisión fingida: resaltar una línea al azar es
  peor que admitir que se perdió el rastro.

- **D-226 — El ancla se corrige también al PERSISTIR, no solo al pintar, y por eso el aviso
  vuelve a significar algo.** Dos sitios:
  - **Al ingerir.** `SessionToolbox` ya tiene el clon a mano (lo usa `read_signatures`), así que al
    aceptar un `submit_finding` o un `add_locations` se busca el snippet en el fichero y se guarda
    la línea **real**. Las detecciones nuevas nacen re-ancladas.
  - **Al abrir la ficha** (`AnchorRepair`). Y esto no es un adorno: **medido**, con el hash ya
    arreglado la ficha acertaba la línea pero la anunciaba —«se anotó en la 167 y su código está
    en la 172»— en **24 de los 25** hallazgos reales. Un aviso que sale siempre no avisa: era el
    banner del parte con otro texto. Y no había nada que anunciar, porque el código no se había
    movido — la línea nació torcida. Corrigiéndola en disco, la lectura dice «anclado» y calla.

  **Solo se corrige lo demostrable**, y son dos casos: (1) el hash aparece en otra línea del
  fichero —mismo texto letra por letra— y (2) la línea anclada no es código ejecutable, y se baja
  a la primera del miembro que sí lo es. Cuando el hash **no aparece** no se toca nada: eso sí es
  código cambiado, y ahí el aviso y el «Verificar ahora» son la respuesta. Re-anclar por símbolo
  en disco silenciaría para siempre el único caso que necesita una persona. Es idempotente: en
  cuanto la ubicación está bien no escribe, así que abrir la ficha dos veces no toca el hub.

- **D-236 — «Movido» deja de ser un aviso.** Era la mitad buena de D-195 y la mitad mala: separar
  un desplazamiento de un cambio real estaba bien, pero anunciar el desplazamiento no. Con el ancla
  corregida en disco, un código que solo se ha movido se sigue **en silencio** — si no, añadir un
  `using` al principio de un fichero encendería el aviso de todos sus hallazgos a la vez.

### Defecto 3 — «(sin alias todavía)» en todos los hallazgos

- **D-227 — Causa raíz: la asignación de alias NUNCA se llegó a cablear.** `DisplayId.Next` y
  `Finding.AssignDisplayId` existen, están escritos y no los llama **nadie**: cero llamadas en
  `src/`, cero en `tests/`. No es que no se persista ni que la ficha lea otro campo — la ficha lee
  `f.DisplayId`, que es el correcto. Es que el paso post-push del §2 se quedó sin implementar. La
  prueba directa está en el hub: los 25 hallazgos tienen `"displayId": null` y `app.json` guarda
  `"displayIdCounters": {}` — el contador nunca avanzó ni una vez.

- **D-228 — Se cablea donde decía la decisión antigua, y además se rellena hacia atrás.**
  `DisplayIdService` asigna alias a todo hallazgo sin él, en orden de ULID (que es orden de
  creación, así que la numeración sigue la historia real), avanzando el contador por pilar de
  `AppConfig.DisplayIdCounters`. Se invoca **tras el push de la sesión** —la regla del §2 contra
  colisiones concurrentes— y **al arrancar**, como backfill de lo ya existente. Es idempotente por
  construcción: solo mira los que tienen `DisplayId == null`, así que la segunda pasada asigna 0.

- **D-229 — El alias también entra en los informes.** Estaba en la ficha y en las listas, pero
  `ReportBuilder` escribía solo el título. Un informe que no nombra el hallazgo por su alias obliga
  a volver a la aplicación para saber de cuál habla.

### Defecto 4 — la rueda del ratón peleaba entre el snippet y la página

- **D-230 — Causa: `SnippetView` hereda de `TextEditor` (AvalonEdit), que consume SIEMPRE la
  rueda.** Su `ScrollViewer` interno marca el evento como tratado aunque ya esté en su tope, así
  que la página de debajo nunca se entera. Arreglo estándar de WPF: el interno solo se queda la
  rueda **mientras pueda desplazarse en esa dirección**; en el tope re-emite el `MouseWheel` al
  padre para que burbujee. La decisión de burbujear o no se saca a una función pura
  (`SnippetScroll.ShouldBubble`) para poder probarla sin hilo STA.

### Defecto 5 — «Resolución manual» se renderizaba cortada

- **D-231 — Causa raíz: `Padding="0,8,0,0"` en el `Expander`.** Se puso pensando en separar el
  **contenido** de la cabecera, pero la plantilla de `Expander` de WPF-UI 3.0.5 enlaza `Padding`
  a la **cabecera**, no al contenido. Resultado: la barra plegada pierde 8 px por arriba, el texto
  se descuelga contra el borde inferior y las esquinas redondeadas de arriba se aplastan — el
  «título a medias» del informe. Verificado renderizando el bloque fuera de la aplicación
  (`RenderTargetBitmap` sobre las mismas `ThemesDictionary`/`ControlsDictionary`) con y sin el
  atributo: quitándolo, la cabecera vuelve a estar centrada y completa. El separador del contenido
  se mueve al `Margin` del `StackPanel` de dentro, que es donde tenía que haber estado.

- **D-232 — Y la cabecera plegada se confundía con el combo de severidad.** Misma altura, mismo
  relleno sutil, mismo galón y a 12 px del `ComboBox` de encima: el informe la describe como «un
  combo suelto», que es exactamente lo que parecía. Se diferencia con lo que D-201 ya pedía y no
  llegó a estar: la cabecera lleva **su advertencia** («Resolver a mano — cierra el hallazgo sin
  auditar», con el símbolo de precaución y el color de precaución del tema) y estira a todo el
  ancho. Sigue siendo el mismo control con el mismo comportamiento; lo que cambia es que ahora se
  lee como lo que es.

- **D-233 — Los demás expanders están sanos.** Se revisaron los tres que quedan en la aplicación
  (`InventoryView` y los dos anidados de `SessionView`): ninguno fija `Padding`, así que ninguno
  tiene el síntoma. Un test lee el XAML y falla si alguien vuelve a poner `Padding` en un
  `Expander`, que es la forma barata de que esto no vuelva.

### Cobertura y verificación

- **D-239 — La regla de «cuándo se ofrece verificar» estaba escrita dos veces.** `SnippetPanel` y
  el view-model mantenían cada uno su lista de estados, y al añadir los dos nuevos solo se
  actualizó una: los avisos de `Reanclado` y `NoLocalizado` salían sin su botón. Ahora la lista
  vive una sola vez (`SnippetPanel.OffersVerify`) y la ficha la consulta.

- **D-234 — Lo que se prueba, y lo que se verifica a mano.** Con test: el hash estable ante CRLF,
  ante sangría y ante blancos de los extremos; el re-anclaje por hash a la línea correcta; el caso
  del usuario (línea en comentario → ancla al miembro, nunca al comentario); el símbolo
  desaparecido → `NoLocalizado` sin resaltado; el backfill de alias y su idempotencia; el
  re-anclaje en la ingesta; y la decisión de burbujeo de la rueda en sus cuatro esquinas. Sin test
  automático, con verificación humana documentada: que la rueda se sienta bien y que el expander
  se lea como expandible. El render del expander sí queda comparado en imagen (D-231).

- **D-237 — La verificación se hizo sobre el hub real, con el código compilado.** No con una
  reimplementación del algoritmo: una sonda llama a `SnippetReader`, `SymbolAnchor` y
  `DisplayIdService` de la aplicación, sobre **copias** del hub (`%LOCALAPPDATA%\Atalaya\hub`) y
  del clon de X-BLAST — los originales quedaron intactos, comprobado con `diff -r` y
  `git status`. Resultados:

  | comprobación | antes (F5.5) | después (F5.6) |
  |---|---|---|
  | ubicaciones ancladas | 3 / 56 | **56 / 56** |
  | hallazgos con aviso, código intacto | 24 / 25 | **0 / 25** |
  | resaltados sobre un comentario | 1 | **0** |
  | hallazgos con alias | 0 / 25 | **25 / 25** (BUG=15, MEJ=7, OPT=3) |
  | 2ª pasada del backfill | — | **0 asignados**, contador intacto |
  | 2ª pasada del re-anclaje | — | **0 correcciones**, cero escrituras |

  Y la prueba que el usuario pidió hacer a mano, hecha sobre los datos reales: editando **una**
  línea anclada (la 172 de `CommonStatics.cs`), salieron **2 avisos** — exactamente los dos
  hallazgos anclados a esa línea — y los otros 23 siguieron callados. Las dos mitades del criterio
  («cero banners sin tocar código» y «banner en ese y solo ese») quedan además fijadas en tests.

- **D-238 — El expander se comparó en imagen, antes y después.** Un arnés de render fuera de la
  aplicación (`RenderTargetBitmap` sobre las mismas `ThemesDictionary`/`ControlsDictionary`) pinta
  el bloque de gobernanza a 2,4×. Con `Padding`, la cabecera plegada sale con el texto descolgado
  contra el borde y las esquinas de arriba aplastadas; sin él, centrada y completa. Un detalle que
  el arnés enseñó de paso: la expansión **sí está animada** por el tema — las primeras capturas
  salían a medio camino hasta que se dejó correr el reloj de animaciones del dispatcher en vez de
  bloquear el hilo de UI. Es el mismo error de arnés que D-215, en otra forma.

- **D-240 — El guión de la verificación humana de los dos de interfaz.** Lo que no tiene estado
  observable se comprueba a mano, y el guión se escribe para que la próxima vez se repita igual:

  **Rueda (defecto 4).** Abrir un hallazgo cuyo miembro no quepa en los 320 px del panel —por
  ejemplo `CommonStatics.ReadCSV`— y otro que quepa de sobra. (a) Con el cursor **fuera** del
  panel, rodar de arriba abajo: la página baja del tirón. (b) Con el cursor **encima** del panel
  largo, rodar hacia abajo: se desplaza el código hasta su última línea y, **sin levantar el
  dedo**, sigue bajando la página. (c) Igual hacia arriba. (d) Con el cursor encima del panel
  **corto**, rodar en las dos direcciones: baja y sube la página, el panel nunca se queda el
  gesto. Lo que se busca es que no haya que apartar el ratón del código para poder seguir leyendo.

  **Expander (defecto 5).** En la columna de gobernanza, con el hallazgo activo y sin justificación
  pendiente: la sección «Resolución manual» enseña su título, su línea de ayuda y **una barra
  completa** con el símbolo de precaución, «Resolver a mano» en negrita, «— cierra el hallazgo sin
  auditar» y el galón a la derecha. Nada asoma por debajo. Al pulsarla se despliega **con la
  animación del tema** y aparecen la etiqueta «Justificación (obligatoria)», el campo y el botón
  rojo. Comprobar en los dos temas.

### Retoque posterior: el panel de código crece con el método

- **D-241 — `MaxHeight` en vez de `Height` en el panel de código.** Con el ancla ya arreglada se
  vio el efecto de al lado: `ConvertToDetId` ocupa cinco líneas y el panel medía 320 px fijos, así
  que dejaba **250 px de superficie vacía** entre el código y el borde, y separaba la tarjeta de
  «Código» de la de «Historial» con un hueco que no significaba nada. Con un tope en vez de un
  alto, el panel mide lo que mide el miembro y solo se planta al llegar a 320, que es donde
  aparece el scroll interno — el comportamiento de un método largo no cambia en nada.

  Se comprobó antes de cambiarlo, porque no era evidente: `SnippetView` hereda de `TextEditor`
  (AvalonEdit), que lleva su propio `ScrollViewer` dentro, y un control así puede perfectamente
  medirse a cero o a infinito cuando se le quita la altura. Renderizado fuera de la aplicación con
  el control **de verdad** (el arnés de D-238, ahora referenciando `Atalaya.App`): mide al
  contenido, y a partir del tope saca la barra. Probado en los tres casos que importan — método de
  cinco líneas, método de 28 y el degenerado de **una sola línea**, este último también con una
  línea más ancha que el panel: la barra horizontal de AvalonEdit se superpone y no aplasta el
  texto, así que no hace falta un `MinHeight` que reintroduciría el hueco en pequeño.

- **D-242 — El resto de la ficha ya lo hacía bien.** El historial (`MaxHeight=420`) y los
  comentarios (`MaxHeight=320`) ya usaban tope, y el campo de justificación usa `MinHeight`, que es
  lo correcto para un campo de escritura. El único alto fijo de la ficha era el del código. Un test
  lee el XAML y falla si vuelve a aparecer un `Height="..."` numérico en cualquier tarjeta —
  comprobado con una mutación puntual a mano (D-214) de que discrimina de verdad.

- **D-235 — Lo que esta tanda NO toca.** Motor de auditoría, reconciliación, barrido y sync quedan
  como estaban. De `SessionToolbox` se toca **solo** el re-anclaje al persistir (D-226) y de
  `SessionCoordinator` **solo** la llamada al alias tras el push (D-228), que es donde la decisión
  antigua decía que vivía. La ficha no se rediseña: cambian el ancla, un atributo del expander y
  el manejo de la rueda.

## F5.6 (V2) — Vista Inventario: selección, modos y coste visible

### §1 — Colapsar/expandir todo, compartido con Hallazgos

- **D-243 — La regla de plegado sale de V3 y pasa a ser de las dos vistas.** En F5.4 nació dentro
  de `FindingsViewModel`: la lista corta abre, la larga pliega, lo que el usuario decide a mano
  manda, y un solo botón alterna entre «Colapsar todo» y «Expandir todo». V2 necesitaba
  exactamente eso sobre sus módulos. Copiarla habría dejado **dos** reglas que se separan a la
  primera corrección —el patrón de D-239, donde una lista duplicada se quedó sin actualizar—, así
  que vive en `GroupCollapse` (Services) y las dos vistas la consultan. Los grupos implementan
  `ICollapsibleGroup` (`Key` + `IsExpanded`) y nada más: la lógica no sabe si el grupo es una
  unidad de V3 o un módulo de V2.

- **D-244 — La memoria de expansión sigue siendo una sola y de la sesión.**
  `GroupExpansionMemory` ya era singleton porque V3 es transitoria en el contenedor; V2 lo es
  igual, así que la comparten. Para que un módulo no pueda plegar el grupo homónimo de V3, la
  clave de V2 lleva prefijo: `inv {slug} {módulo}`. Sigue sin persistirse en disco por la razón
  de F5.4: qué había plegado anteayer depende del filtro de anteayer.

- **D-245 — Las tres propiedades que lee la cabecera se reenvían, no se duplican.**
  `AllCollapsed`, `ToggleAllLabel` y `HasGroups` viven en `GroupCollapse`; los dos view-models las
  exponen como pase directo y reemiten el aviso de cambio (`PropertyChanged` → `OnPropertyChanged`
  con el mismo nombre). Así el XAML de V3 no cambió ni una línea y los tests de F5.4 siguen
  midiendo exactamente lo mismo.

### §2 — Integral y Superficial salen del UI, no de los datos

- **D-246 — Por qué se retiran.** El barrido por lotes con reconciliación (F4.1) los dejó sin
  contenido propio: **Integral** es «seleccionar todo + lotes», y **Superficial** es
  `maxPassesPerUnit = 1`, que es un ajuste de la máquina (D-095), no un modo de auditoría. Un
  botón que no aporta una decisión distinta solo aporta una forma más de gastar sin querer.

- **D-247 — Se retira el LANZAMIENTO, no el valor.** Los botones y los comandos
  (`AuditIntegralCommand`, `AuditSuperficialCommand`) desaparecen; los valores del enum, el mapa
  JSON (`"integral"` / `"superficial"`), la máquina de confianza, el importador de V4 y las
  etiquetas en castellano se quedan **intactos**. Las sesiones históricas del hub los referencian
  y tienen que seguir cargando, contando en métricas y saliendo en informes: un dato que deja de
  leerse es un dato perdido, y aquí nada se borra (§0, mejora 4). Un test escribe a mano un
  fichero de sesión como lo escribía la versión antigua —modo retirado, sin `maxPassesPerUnit`,
  sin `usageBreakdown`— y comprueba las tres cosas: que carga, que suma en `MetricsQuery` y que
  `ReportBuilder` lo redacta entero.

- **D-248 — Atributo propio en vez de `[Obsolete]`.** `Obsolete` avisa en cada **uso**, y los usos
  que quedan son justamente los que deben seguir vivos (lectura JSON, confianza, importación). En
  `Atalaya.Domain` y `Atalaya.Storage` los avisos son errores de compilación (§11), así que
  marcarlos con `Obsolete` habría obligado a silenciarlo línea por línea — ruido que acaba
  escondiendo un aviso de verdad. Se declara con `[DeprecatedMode("…")]`, que no genera
  advertencias y **se puede consultar** (`AuditModes.IsDeprecated`), en vez de con una lista
  paralela de modos retirados que sería la primera en quedarse sin actualizar (D-239).

- **D-249 — No había prompts de auditor por modo que borrar.** Se comprobó: `Prompts.cs` no tiene
  recursos separados por modo; solo interpola `MODO: {mode}` en la cabecera del prompt de unidad.
  Como el UI ya no puede lanzar esos dos, esa interpolación nunca volverá a escribirlos.

- **D-250 — El motor no se toca.** `SessionCoordinator` conserva sus dos ramas de `Integral`
  (resolver unidades y cerrar ciclo). Ya no son alcanzables desde la interfaz, pero borrarlas
  entraba en el motor de auditoría, que esta tanda no toca, y no gana nada: una sesión reanudada
  o reimportada con ese modo sigue resolviéndose igual.

### §3 — Selección a escala (900 unidades)

- **D-251 — La selección deja de vivir en los nodos del árbol.** Era el defecto de fondo: buscar
  reconstruye `Modules` entero, y con él se iba lo marcado. Ahora la verdad es un conjunto de
  rutas en el view-model (`_selected`) y los nodos son su reflejo. De ahí salen las tres
  propiedades que antes no existían: **filtrar no deselecciona**, el contador dice el total del
  ciclo y no el de lo visible, y colapsar y expandir no tocan nada.

- **D-252 — La barra de selección solo aparece cuando hay algo marcado**, y dice el número.
  Lanzar auditorías cuesta dinero y con 900 unidades nadie las cuenta a ojo: «N unidades
  seleccionadas · Deseleccionar todo». El mismo número se repite en el resumen del ciclo.

- **D-253 — Casilla tri-estado por módulo, con `IsThreeState="False"`.** Es deliberado y no una
  errata: así el clic solo alterna marcar/desmarcar —el gesto que espera una persona—, mientras
  que un `null` puesto **desde el código** se sigue pintando como indeterminado. Con
  `IsThreeState="True"` habría que pasar por el estado intermedio en cada vuelta, que no significa
  nada cuando lo pulsa alguien. La casilla va **fuera** del botón que pliega: dos gestos, dos
  zonas. Bajo filtro actúa sobre las unidades del módulo que están a la vista, que es lo que se
  está mirando; la barra sigue diciendo la verdad del total.

- **D-254 — «Seleccionar pendientes» es simétrico y GLOBAL.** Si ya están todas marcadas, el botón
  pasa a «Deseleccionar pendientes» —el texto es su estado, como el de plegado (D-243)—. Actúa
  sobre **todas** las pendientes del ciclo, a la vista o no: es la acción global que hace pareja
  con «Deseleccionar todo», y para recortar a un trozo ya está la casilla del módulo. El tooltip
  lo dice con el número, para que bajo filtro no sorprenda.

- **D-255 — Lo seleccionado que desaparece del inventario deja de contar.** Un re-escaneo o un
  reset pueden llevarse una unidad por delante; el conjunto se poda contra el inventario vigente
  en cada reconstrucción. Un contador que cuenta fantasmas es exactamente el que hace gastar de
  más.

### §4 — Confirmación de lanzamiento con estimación de coste

- **D-256 — El número sale del gasto ya medido o no existe (N-2 aplicada al dinero).**
  `CostEstimator` promedia el coste por unidad de `AuditSession.UsageBreakdown` —el desglose que
  la instrumentación de Hito 1a guarda en cada fichero de sesión— sobre las **5 sesiones más
  recientes** de esa aplicación. No hay ninguna tabla de precios ni ninguna constante: si el hub
  no tiene una sola unidad con coste medido, la estimación **no existe** y el diálogo lo dice
  («Sin coste medido en el historial de esta aplicación»). Con una sola sesión, o con menos de
  tres unidades medidas, se usa igual pero declarada como «estimación con pocos datos», y el
  diálogo saca un aviso visible.

- **D-257 — El factor de pasadas se evita antes que se calcula.** Cada sesión registra su tope
  (F5.1). Se prefieren las sesiones medidas **con el mismo tope que el vigente**: entonces no hay
  nada que extrapolar y el factor es 1. Solo cuando no hay ninguna se escala por
  `topeVigente / topeObservado`, y el factor se enseña **en el desglose** (`× 6/2 pasadas`) para
  que se vea que es extrapolado — con la advertencia de que el escalado lineal sobreestima, porque
  el barrido se seca antes de agotar el tope. Si el historial no registra tope (sesiones anteriores
  a F5.1, `maxPassesPerUnit = 0`) no se escala nada y también se dice.

- **D-258 — El cálculo se enseña desglosado, no el total.** «47 unidades × ~18/unidad ≈ 846
  unidades SDK», con la procedencia debajo («Media de 12 unidades medidas en las últimas 3
  sesiones»). Un total suelto no se puede contrastar; el desglose sí. La unidad de coste es la que
  declaró el SDK y quedó guardada en la sesión (`usage.currency`), no una inventada; «unidades
  SDK» solo aparece cuando el SDK no declaró ninguna, igual que en los informes.

- **D-259 — Informa, no bloquea.** El botón de confirmar nunca se deshabilita por la estimación, ni
  siquiera cuando no hay número. La confirmación se pide **por encima de 3 unidades**
  (`Thresholds.ConfirmLaunchUnits`, configurable por app): el coste de un clic de más solo se
  justifica cuando el gasto es relevante, y una tanda de dos unidades no lo es. Cancelar deja la
  selección **intacta** — volver atrás no puede costar el trabajo de elegir.

- **D-260 — Quién pregunta se inyecta.** `IAuditLaunchConfirmer` + `AuditLaunchConfirmation`,
  mismo patrón que el borrado de aplicación (F5.3 §4): `InventoryViewModel` no depende de una
  ventana y los tests ejercitan el flujo entero —incluido «cancelar»— sin interfaz gráfica.

### §5 — El resumen del ciclo se explica solo

- **D-261 — «Grandes» sale del panel, el badge se queda.** Decisión del usuario: a nivel de resumen
  no aporta. El estado sigue visible como badge en la fila de cada unidad, que es donde de verdad
  se actúa sobre él. Lo que sí obliga es a tapar el agujero que deja: sin ese número,
  `Auditadas + Pendientes` ya no suma `Unidades` y el panel deja de cuadrar, que desconcierta más
  que la fila que se quitó. Así que el dato se muda al tooltip de «Unidades», y solo aparece si
  hay alguna: «2 son demasiado grandes para auditarlas de una vez: salen marcadas «Grande» en la
  lista, no cuentan como pendientes y no impiden cerrar el ciclo».

- **D-262 — «Ciclo 5» no significaba nada suelto, y la fecha se DERIVA.** El usuario no distinguía
  ciclo de sesión, y en el piloto —donde cada reset abrió ciclo nuevo— el número solo desconcertaba.
  Ahora se lee «Ciclo 5 · iniciado 12 ago 2026», con el tooltip que dice qué es un ciclo: «Una
  vuelta completa al inventario. Se cierra al auditar todas las unidades; los resets abren ciclo
  nuevo».
  <br>
  <b>De dónde sale la fecha.</b> `InventoryCycle` nunca tuvo fecha de creación, y añadírsela ahora
  no arreglaría el caso que importa: los ciclos que YA existen seguirían sin ella. Pero el dato
  está en el hub — tanto el cierre como el reset escriben su sesión con el `CycleN` del ciclo que
  **abren**, así que esa sesión ES el momento en que empezó. `CycleSummary.StartOf` lo busca ahí.

- **D-263 — Y cuando no se sabe, se dice (N-2, otra vez).** El ciclo 1 no lo abre nadie: nace con
  el primer escaneo, que no deja sesión. Tres respuestas posibles y tres textos distintos:
  «iniciado {fecha}» cuando hay evento de apertura; «activo desde {fecha}» cuando solo se puede
  inferir de la primera sesión registrada en él, y el tooltip añade «pudo abrirse antes»; y «Ciclo
  1» a secas cuando no hay nada, con «Este ciclo no registra cuándo se abrió». Fingir una fecha
  —la del fichero de inventario, por ejemplo, que un `pull` reescribe— habría sido peor que no
  darla.

- **D-264 — «Sesiones: 7» contaba algo que nadie podía explicar.** Contaba TODAS las sesiones de la
  aplicación: de todos los ciclos y de todos los tipos, cierres y resets incluidos. Ahora la
  etiqueta promete «Sesiones este ciclo» y el número lo cumple: solo el ciclo en curso, y solo
  **lanzamientos** — el cierre y el reset dejan sesión pero nadie los lanza, y contarlos rompería
  la frase que los explica. Las sesiones históricas en modo retirado (F5.6 §2) sí cuentan: fueron
  lanzamientos. El tooltip es literalmente la definición: «Cada lanzamiento de auditoría es una
  sesión. Los cierres de ciclo y los resets no cuentan».

- **D-265 — Cada dato del panel lleva una frase, y hay un test que lo exige.** El criterio: que se
  entienda a un compañero que abre la aplicación por primera vez. «Auditadas» dice que puedes
  volver sobre ellas a mano; «Pendientes», que es lo que queda para cerrar el ciclo;
  «Seleccionadas», qué se auditará al pulsar el botón. Un test recorre el bloque del panel en el
  XAML y falla si algún `TextBlock` de datos se queda sin `ToolTip` — es la forma barata de que el
  próximo dato que se añada no nazca mudo.

- **D-266 — Verificado en la vista real.** El arnés de D-268 vuelve a cargar `InventoryView` con el
  panel nuevo: cero avisos de enlace y el bloque se lee entero. De ahí salió un ajuste tipográfico:
  la línea del ciclo va en **SemiBold**, no en `Bold` — en negrita pesaba más que «Resumen del
  ciclo», su propio título.

### Cobertura y verificación

- **D-267 — Lo que queda probado.** Con test: el cálculo del coste en sus tres casos (con
  historial, sin historial y con factor de pasadas), la preferencia por el tope igual, el
  historial sin tope registrado y el tope absurdo; el plegado de V2 en sus cinco esquinas (lista
  corta, lista larga, botón que alterna, supervivencia a la recarga y clave sin colisión con V3);
  la selección entera (barra y contador, tri-estado en sus tres valores, deseleccionar todo,
  pendientes simétrico, **selección bajo filtro**, selección bajo colapso y poda tras un
  re-escaneo); y el diálogo (se pregunta por encima del umbral, no por debajo, el umbral se baja
  por app, cancelar no lanza y sin historial se pregunta igual pero sin número); y el panel lateral (la fecha del ciclo en sus tres procedencias, el recuento de lanzamientos del ciclo, el tooltip de «Unidades» con y sin grandes, y el barrido del XAML que exige un `ToolTip` por dato). Además, la
  retirada de modos: fixture de sesión antigua que carga, cuenta en métricas y se redacta; los
  comandos que ya no existen y la barra de acciones que quedó.

- **D-268 — La vista se comprobó cargándola de verdad, no leyendo el XAML.** Un arnés fuera de la
  aplicación (el de D-238, ahora referenciando `Atalaya.App`) instancia `InventoryView` y
  `AuditLaunchDialog` con los mismos `ThemesDictionary`/`ControlsDictionary`, escucha
  `PresentationTraceSources.DataBindingSource` y renderiza a PNG. Resultado: **cero avisos de
  enlace** en las tres pantallas. El arnés enseñó de paso un detalle que un test de texto no ve:
  `Run.Text` enlaza **TwoWay por defecto** —está pensado para `RichTextBox`—, así que los `<Run>`
  del resumen del ciclo exigen una propiedad con `set`; con el view-model real la tienen, pero un
  doble de solo lectura revienta ahí y no en el enlace que uno estaba mirando. Y una corrección
  que salió de la imagen: las filas de unidad necesitan margen derecho, porque la barra de
  desplazamiento cortaba el «🔒 alguien» de las reclamadas.

- **D-269 — Lo que se verifica a mano.** (a) Seleccionar un módulo entero con un clic y soltarlo
  todo con otro; (b) «Seleccionar pendientes» y su inverso, comprobando que el texto del botón
  cambia; (c) lanzar una selección de más de 3 unidades y leer la estimación antes de confirmar;
  (d) que las sesiones antiguas en modo retirado siguen visibles en Métricas y en sus informes.

### Retoque posterior: la cabecera de módulo era texto negro sobre fondo negro

- **D-270 — Causa raíz: un `Style TargetType="Button"` sin `BasedOn` no hereda del implícito de
  WPF-UI, y su `Foreground` cae al NEGRO de serie de WPF.** El usuario lo vio en la vista: las
  cabeceras de las que cuelgan las clases «apenas se ven». Medido en el render en vez de
  suponerlo: el texto de la cabecera salía a **(0,0,0)** sobre un fondo **(32,32,32)** — no es que
  contrastara poco, es que no había contraste. Las filas de unidad, en cambio, medían (255,255,255)
  porque cuelgan del `UserControl` y heredan el color del tema; la cabecera cuelga del botón que
  pliega, y ahí el estilo nuevo cortaba la herencia.

- **D-271 — El arreglo es del ESTILO, no de sus hijos.** `ModuleHeaderLink` declara
  `Foreground="{DynamicResource TextFillColorPrimaryBrush}"` —recurso del tema, no un blanco fijo,
  que en tema claro sería el mismo defecto al revés— y el `TextBlock` de la cabecera lo declara
  también, siguiendo la disciplina de V3. Comprobado en las dos direcciones con el arnés de D-268:
  la cabecera mide ahora (255,255,255) sobre (32,32,32) en oscuro, y oscura sobre claro en el tema
  claro, que el arnés renderiza en una segunda pasada.

- **D-272 — El mismo agujero estaba latente en V3.** `RowLink` (`FindingsView`) tampoco declaraba
  `Foreground`; no se notaba solo porque cada `TextBlock` de dentro se pone su propio color. Un
  hijo nuevo sin color habría reproducido el defecto exacto. Se le pone el `Setter` —hoy inerte
  ahí— y la regla se escribe una vez en un test: recorre las diez vistas y falla si algún `Style`
  de `Button` sin `BasedOn` no declara su `Foreground`. Comprobado que discrimina con una mutación
  puntual (D-214): quitando el `Setter` de V2, falla ese caso y solo ese.

## F5.7 — Vista Ajustes: limpieza, claridad y reset de fábrica

### §1 — Un solo ritmo de espaciado

- **D-273 — El desorden no era de márgenes, era de que no había regla.** Cada bloque de Ajustes
  llevaba su `Margin` puesto a ojo (`0,0,0,8`, `0,0,0,16`, `0,4,0,6`…) y las filas de umbrales
  eran `StackPanel Orientation="Horizontal"` con un `Width="200"` repetido en cada etiqueta. Con
  eso, cualquier control nuevo nacía descuadrado por defecto. Ahora la página son **cuatro
  secciones** —General, Auditoría, Sincronización y Zona peligrosa— y **toda** fila
  etiqueta+control es la MISMA rejilla (columna de 220 · resto) con el MISMO estilo `FieldRow`;
  la ayuda cuelga bajo el control, alineada con él. Los estilos (`SectionTitle`, `SectionBlock`,
  `FieldRow`, `FieldLabel`, `FieldHelp`) viven en los recursos de la vista: el margen lo pone el
  estilo, nunca la fila.

- **D-274 — Y hay un test que lo exige.** Recorre las filas del XAML y falla si alguna no usa la
  rejilla común, si a alguna le falta su línea de ayuda, o si una etiqueta o una ayuda se pone su
  propio `Margin` —que es exactamente por donde el espaciado se volvió a torcer la última vez—.
  Es la misma forma barata de D-265: que el próximo control no pueda nacer mudo ni descuadrado.

### §2 — Las dos retiradas

- **D-275 — «Habilitar arreglo asistido» era un interruptor conectado a nada.** Es el *feature
  flag* de H9 (arreglo integrado supervisado), que se decidió NO construir. Un control que el
  usuario puede mover y que no cambia ningún comportamiento es peor que no tenerlo: enseña una
  capacidad que la aplicación no tiene. **El control se va del UI; el flag `enableAssistedFix` se
  queda en la configuración** para cuando H9 exista, y `BuildSettings` deja de tocarlo — retirar
  un control no puede significar borrarle el valor a quien lo tuviera puesto.

- **D-276 — «Opciones avanzadas» tampoco tenía público.** Contenía el PAT de respaldo, el TLS
  estricto y el override de la URL del hub. El PAT existía para el escenario «la organización
  bloquea la OAuth App»: **ese escenario ya no existe**, porque la OAuth App es propiedad de la
  organización. Y el override de `hubUrl` es de desarrollo, así que su público sabe editar
  `appsettings.deploy.json` —donde sigue disponible— mejor de lo que sabe encontrar un expander.
  <br>
  <b>Lo que NO se toca.</b> El soporte de PAT sigue entero en el código: `SettingsService.GetPat`
  / `SetPat` y la cadena de credenciales de `HubContext` (D3). Se va la interfaz, no la capacidad.
  Un test lo fija en las dos direcciones: el XAML no puede volver a nombrarlos, y los métodos
  tienen que seguir existiendo.

### §3 — Que se entienda sin preguntar

- **D-277 — El criterio es el de D-265, aplicado a Ajustes: un compañero que abre la aplicación
  por primera vez.** Cada control lleva su línea de ayuda, con el mismo estilo y en el mismo
  sitio. «Frescura» dice a partir de cuántos días un hallazgo se marca como pendiente de revisión;
  «Editor preferido», qué botón usa ese ajuste; «Timeout de Copilot», qué se da por fallido.
  «Unidad grande» tenía etiqueta pero no ayuda, y ahora la tiene.

- **D-278 — «Polling» no era una palabra del usuario.** Se llama **«Sincronización del hub
  (segundos)»** y su ayuda dice lo que de verdad hace: cada cuántos segundos se buscan cambios de
  tus compañeros. El nombre viejo describía la técnica; el nuevo, la consecuencia.

### §4 — Feedback de acción = toast global (regla)

- **D-279 — La regla, escrita de una vez: el resultado de una acción se cuenta con un toast
  global (`ToastCenter`, F5.3), NUNCA con un texto incrustado al fondo de un panel.** El patrón
  había aparecido ya cuatro veces con el mismo defecto doble: no caduca —se queda pegado hasta la
  acción siguiente— y está donde el usuario no mira. En Ajustes era el caso extremo: «Ajustes
  guardados» salía justo debajo del botón que lo provocaba, pero **fuera de la pantalla**, así que
  guardar no daba ninguna señal.

- **D-280 — El barrido, con su lista.** Buscados por toda la aplicación los `StatusMessage`
  incrustados. Convertidos a toast: **Ajustes** («Ajustes guardados»), **V2 Inventario** (el
  texto al fondo del panel del ciclo: re-escaneo, reset de ciclo, «lanzamiento cancelado»,
  errores), **Importar v4** (validación, «Importando…», «Importación completada», errores) y
  **Nueva aplicación / onboarding** (validación, «Escaneando y registrando…», «Aplicación
  registrada», errores). Ya estaban convertidos de tandas anteriores V4 (F5.5 §6) y V5.

- **D-281 — Dos excepciones, razonadas y enumeradas en el test.** No todo texto de estado es
  feedback de acción. **Cuenta** narra el flujo de dispositivo —«pidiendo código», «introduce el
  código en github.com», «leyendo tu perfil»— y el usuario tiene que poder leerlo *mientras se va
  al navegador y vuelve*: un toast que caduca a los 8 segundos se lo llevaría justo cuando hace
  falta. **Sesión** enseña el estado VIVO de la auditoría en curso («Auditando…», «Deteniendo tras
  la unidad actual…»), que debe permanecer mientras dure. Las dos están comentadas en su XAML y
  listadas en `EmbeddedStatusSweepTests`, así que son una decisión y no un olvido: el test recorre
  las diez vistas, exige cero `StatusMessage` en las ocho restantes y **exige que las dos
  excepciones lo sigan teniendo** —si una deja de necesitarlo, hay que sacarla de la lista.

### §5 — Restablecimiento de fábrica (zona peligrosa)

- **D-282 — La zona peligrosa va al final y se ve que lo es.** Borde y fondo rojos sutiles, título
  en rojo y el botón en `Appearance="Danger"`. Separada de «Guardar» a propósito: lo último que
  puede pasar es que alguien busque el botón de guardar y encuentre este.

- **D-283 — La operación es ATÓMICA, y ese es su diseño entero.** Primero el hub, después lo
  local. El orden no es una preferencia: el peor estado posible no es «no se pudo resetear», es
  **«tu máquina limpia y el hub lleno»** —sin cuenta, sin ajustes y sin clon, quien lo pulsó ya no
  puede ni reintentarlo ni explicar qué pasó—. Por eso el borrado de `apps/` se commitea y se
  **publica** antes de tocar un solo byte local, y cualquier fallo en ese tramo (sin permisos, sin
  red, sin hub configurado) devuelve el clon a su commit anterior y aborta con el estado intacto y
  un toast que lo dice. El punto de retorno se toma **después de un pull**, para que sea el estado
  vigente del equipo y no un pasado que ya no existe.

- **D-284 — La marcha atrás necesitaba una primitiva nueva, deliberadamente aparte de `Push`.**
  `HubSyncService.HeadCommitSha` y `ResetHardTo(sha)`: devolver rama y árbol de trabajo al commit
  anterior. No se metió dentro de `Push` a propósito — solo quien sabe que su escritura es atómica
  puede pedir que se deshaga; para el resto de escrituras del hub, un push fallido que sale con el
  siguiente «Sincronizar ahora» es el comportamiento correcto (F5.3 §4).

- **D-285 — Qué se borra en local, y qué no.** Se van el clon del hub, `machines.json`,
  `settings.json` —donde vive también el PAT—, `auth.dat` (la cuenta se desconecta) y la marca de
  sesión abierta: dejarla huérfana haría que el siguiente arranque intentara «recuperar» una sesión
  sobre nada. **No** se van los logs de `%LOCALAPPDATA%/Atalaya/logs`, que son justamente lo que
  hace falta para investigar un reset que salió mal y no reconstruyen ningún estado. Ni, por
  supuesto, los repositorios auditados ni los clones de código.

- **D-286 — Y hacía falta poder cerrar el clon.** `HubContext.CloseSync()`: en Windows no se puede
  borrar el directorio del clon mientras LibGit2Sharp lo tiene abierto. Suelta los handles y olvida
  el servicio; la siguiente llamada a `EnsureSync` lo reconstruye desde cero, que es exactamente el
  estado de primer arranque. Análogo en ajustes: `SettingsService.ResetToDefaults()` borra el
  fichero **y** pone `Current` a los valores de fábrica — hacer solo una de las dos cosas deja los
  ajustes viejos vivos en memoria (y el primer `Save` los reescribe) o el fichero en disco para el
  arranque siguiente.

- **D-287 — La confirmación es a la altura del daño, y por eso NO es la del borrado de una app.**
  Allí se escribe el nombre de la app porque hay que confirmar CUÁL; aquí no hay cuál —son todas—,
  así que lo que se confirma es la naturaleza de la acción y la palabra es **RESET**, en mayúsculas
  y con comparación exacta. El diálogo enumera los números reales del hub (N aplicaciones, N
  hallazgos, N sesiones), dice en negrita que **afecta a todo el equipo y que los compañeros lo
  verán desaparecer en su próxima sincronización**, dice qué se borra en esta máquina, y recuerda
  lo que NO se pierde (el historial git conserva copia recuperable; el código auditado no se toca).
  Mismo patrón inyectable que D-260: `IFactoryResetConfirmer`, así que el flujo entero —incluido
  cancelar— se prueba sin abrir una ventana. Y el view-model **vuelve a mirar la puerta**
  (`CanReset`) antes de destruir nada: un «sí» sin la palabra escrita no borra.

- **D-288 — Después del reset, primer arranque.** La cuenta queda desconectada y Ajustes navega a
  «Cuenta», que es la pantalla de bienvenida (D4). No hace falta reiniciar la aplicación.

### Cobertura y verificación

- **D-289 — Lo que queda probado.** Del reset, contra un remoto `--bare` local (N-1, sin red): que
  vacía `apps/` entero y lo publica; que el commit dice quién lo hizo; que la máquina queda como
  recién instalada (clon, `machines.json`, `settings.json` y `auth.dat` fuera, cuenta desconectada,
  ajustes en fábrica); que los logs sobreviven; que un segundo usuario en su propio clon lo ve
  desaparecer al sincronizar; que un hub ya vacío no es un error; y —el caso que justifica el
  diseño— que **un push fallido aborta sin tocar nada**, con el clon en su commit de antes y la
  cuenta todavía conectada. Además: sin hub configurado se niega en vez de vaciar la máquina, la
  puerta de la palabra RESET en sus cuatro casos, el desglose contado sobre el hub, y el flujo
  desde Ajustes (cancelar, «sí» sin palabra, y el fallo contado por toast). De la vista: las cuatro
  secciones y su orden, la rejilla y el margen compartidos, la ayuda por control, el renombrado de
  «Polling», la ausencia del toggle de H9 y de las opciones avanzadas —con los métodos del PAT
  todavía en pie—, y la zona peligrosa en rojo con su botón. Y el barrido de §4 en las diez vistas.

- **D-290 — La vista se comprobó cargándola de verdad (arnés de D-268).** Instancia `SettingsView`
  con las mismas `ThemesDictionary`/`ControlsDictionary` de producción, escucha
  `PresentationTraceSources.DataBindingSource` y renderiza a PNG en las dos direcciones del tema:
  **cero avisos de enlace en oscuro y en claro**, y la página se lee entera —secciones, ayudas,
  «Guardar» y la zona peligrosa al final— sin que nada quede cortado. Un detalle del arnés que se
  anota para la próxima: `RenderTargetBitmap` **no captura el fondo de la `Window`**, así que el
  PNG salía transparente y un visor lo enseña blanco — que se lee como «el tema oscuro no se
  aplicó». El fondo hay que pintarlo en un `Border` dentro de la ventana.

- **D-291 — Lo que se verifica a mano.** (a) Leer Ajustes entero y entender cada control sin
  preguntar; (b) guardar y ver el toast **sin hacer scroll**; (c) el reset de fábrica sobre un hub
  de PRUEBA —nunca sobre el del piloto—: la aplicación queda en primer arranque y el repositorio
  aparece vacío en GitHub.

## F5.8 — Portafolio: estado de vinculación local y flujo «Vincular clon»

### §1 — El piloto de las tarjetas

- **D-292 — El hueco era de INFORMACIÓN, no de capacidad.** Las apps del hub salen en el
  portafolio de todo el mundo tras sincronizar, pero auditarlas exige el clon local en ESTA
  máquina —la ruta vive en `machines.json`, que es por-máquina (§4)— y eso no lo decía nada. El
  usuario descubría que no podía participar al intentarlo, y lo único que le ofrecía la aplicación
  era «Nueva aplicación», que es el gesto de dar de alta una app nueva, no el de unirse a una que
  ya existe. Cerrar el hueco es decirlo en la tarjeta y ofrecer ahí mismo el gesto correcto.

- **D-293 — Son TRES estados, no dos, y el tercero es el que más se ve.** Entre «lo tengo» y «no
  lo tengo» está el caso real: la carpeta se movió, se borró, o se reutilizó para otro repo. Se
  arregla de otra manera —reparar, no vincular— y por eso se dice de otra manera. `CloneLink`
  resuelve 🟢 *Vinculada*, 🔴 *Sin vincular* y 🟡 *Vinculada con problema*, y los tres llevan
  tooltip que dice qué pasa **y qué sigue siendo posible**: hallazgos, métricas e informes están
  accesibles siempre.

- **D-294 — La comprobación llega hasta el REMOTO, y ése es su punto.** El orden de
  `CloneLinkService.For` es el orden en que fallan de verdad: hay ruta → sigue ahí → es un repo →
  es el repo CORRECTO. La última es la que nadie mira y la que, sin ella, dejaría auditar el
  proyecto de al lado publicando sus hallazgos bajo el nombre de éste. Una app declarada sin
  `repoUrl` en el hub no se puede contrastar contra nada: se acepta el clon, pero no se finge que
  se ha verificado (N-2) — y no se inventa un problema donde no hay evidencia de uno.

- **D-295 — «¿El mismo repo?» tenía que ser UNA sola regla.** Ya existía dentro de
  `HubSyncService.SameRemote` para decidir si re-apuntar `origin` cuando el despliegue mueve el
  hub. Vincular un clon hace exactamente la misma pregunta, y dos implementaciones habrían acabado
  con una más estricta que la otra. Sale a `Atalaya.Storage.Sync.RemoteUrl` y `SameRemote` delega.
  <br>
  Lo que se amplió al sacarla: **la forma de clonar no distingue un repo de otro.**
  `https://host/org/repo.git`, `git@host:org/repo` y `ssh://git@host/org/repo` son el mismo
  repositorio, igual que una ruta local escrita con barras normales o invertidas; las credenciales
  embebidas en la URL son de quien clona, no del repo. Rechazar el clon de un compañero por haber
  clonado con SSH lo dejaría sin poder auditar por una diferencia de forma.

- **D-296 — El piloto es un `Ellipse` de color con su NOMBRE al lado, no un emoji.** La primera
  versión pintaba 🟢/🟡/🔴 como texto: medido en el render, WPF resuelve esos emoji por una fuente
  monocroma y el «piloto» salía gris. Es el patrón que la aplicación ya tenía para el indicador de
  sync (`SyncHealthToBrush` + `Ellipse`), así que se reutilizan la forma y los tres colores —
  `CloneLinkStateToBrush`—. Y el color **nunca va solo**: «Vinculada», «Sin vincular» y «Vinculada
  con problema» se escriben al lado, porque quien no distinga verde de rojo tiene que poder leerlo
  y porque «vinculada con problema» no se deduce de un ámbar.

- **D-297 — Se recalcula, no se cachea.** El estado sale del `LoadAsync` de la página, que es lo
  que corre al arrancar, al sincronizar (el tick de sondeo recarga la página viva) y al volver la
  ventana al primer plano. Ese último momento es el que importa: el usuario acaba de venir del
  explorador de archivos, que es donde se mueven y se borran las carpetas de las que el piloto
  habla. La regla de qué se recarga al enfocar vive en `ShellRefresh.ShouldReloadOnActivate` y su
  parte importante es lo que NO hace: solo Portafolio e Inventario. Recargar cualquier página al
  enfocar tiraría el comentario a medio escribir de una ficha, o los ajustes sin guardar.

### §2 — El flujo de vincular

- **D-298 — Dos caminos a la vista, no en un menú.** «Ya tengo el repo clonado» y «Clonarlo ahora»
  se eligen con dos radios visibles: quien no tiene el repo no debe tener que adivinar que la
  segunda opción existe. El segundo camino solo se ofrece si el hub sabe de qué URL clonar.

- **D-299 — La validación es OBLIGATORIA y su fallo es específico.** Antes de escribir nada en
  `machines.json` se comprueba que la carpeta exista, sea un repo git y que su `origin` sea el de
  la app. Cuando no coincide, el error enseña **las dos URLs** —la de la carpeta elegida y la del
  repo de la aplicación—, porque un «no coincide» a secas no se puede ni discutir ni arreglar
  (N-2: evidencia, no adivinanza). Y una validación fallida no deja rastro: vincular una carpeta
  equivocada en silencio es el fallo caro.

- **D-300 — Clonar usa la credencial de la CUENTA, la misma que el hub.**
  `HubContext.BuildCredentials` pasa a ser público en vez de duplicar la cadena de resolución del
  token (la cuenta gana al PAT, D3) en un segundo sitio donde acordarse de mantenerla. El destino
  que se elige es la carpeta CONTENEDORA y el diálogo dice dónde va a quedar el clon **antes** de
  crearlo; si esa ruta ya existe y no está vacía, se avisa en vez de mezclar.

- **D-301 — La deriva se mide por `contentHash`, no por el commit, y se OFRECE.** Al vincular se
  compara el inventario vigente contra los ficheros del clon: dos commits distintos con el mismo
  contenido de fuente no cambian nada de lo auditado, así que el commit sería un falso positivo
  constante. Si difieren, se dice —«tu clon está en un commit distinto al del último inventario»—
  con un botón de re-escanear al lado, y **no se re-escanea solo**: el inventario es del equipo, y
  ponerlo al día es una decisión, no un efecto secundario de vincular.

- **D-302 — El re-escaneo salió de `InventoryViewModel` a `InventoryRescanService`.** El diálogo
  de vincular lo necesita, y un segundo re-escaneo escrito aparte sería el que se olvidaría de
  reconciliar —arrastrar el estado auditado— o de publicar.

- **D-303 — «Nueva aplicación» ya no puede crear un duplicado.** Si el repo elegido corresponde a
  una app que ya está en el hub, el asistente lo detecta —al escribir la URL, y también al pulsar
  «Detectar stack», que además saca la URL del `origin` de la carpeta— y redirige al diálogo de
  vincular de la app que ya existe. La puerta se vuelve a mirar en `Create` y **antes** que la del
  hub: un botón gris es una cortesía de la vista, y «esta app ya existe» le sirve más al usuario
  que «conecta el hub» cuando las dos cosas son ciertas.

- **D-304 — El diálogo es un view-model, no una ventana.** Mismo reparto que
  `DeleteAppConfirmation` (D-260): la regla vive en `LinkCloneViewModel` y la vista la enlaza. La
  diferencia es que aquí la regla no es una puerta sino un flujo, así que se inyectan **dos**
  seams —`IFolderPicker` y `ILinkCloneDialog`— y el recorrido entero (elegir mal, leer el error,
  elegir bien, clonar, ver la deriva, re-escanear) se prueba sin abrir nada. El selector real es
  `Microsoft.Win32.OpenFolderDialog`, que viene con WPF en .NET 8: no hace falta arrastrar WinForms
  para pedir una carpeta.

### §3 — Solo lectura coherente

- **D-305 — Se deshabilita lo que LANZA o LEE el clon; nada más.** En V2: «Auditar selección» y
  «Re-escanear», con su motivo en el tooltip y una barra que explica el estado y trae el acceso
  directo a vincular. Siguen enteros el árbol, los filtros, la selección, «Reset ciclo» —que
  trabaja sobre el inventario, no sobre el código— y «Ver hallazgos». En la ficha de un hallazgo
  se deshabilita **solo** «Verificar ahora», que re-ancla contra los ficheros y le pregunta al
  agente. La **gobernanza no depende del clon**: silenciar, cambiar severidad, disputar, comentar
  y resolver a mano siguen funcionando, y el snippet ya enseñaba la copia anclada con su aviso
  cuando no hay código vivo (F5.5 §3).

- **D-306 — Y la puerta está en el MODELO, no solo en el XAML.** `LaunchSession` y `Rescan`
  comprueban `CanAudit` antes de nada: un botón gris es una cortesía de la vista; auditar sin clon
  escribiría hallazgos sobre un código que no está. Un test lo fuerza ejecutando el comando a mano.

### Cobertura y verificación

- **D-307 — Lo que queda probado.** Los tres estados uno a uno: sin ruta (🔴), ruta que ya no
  existe, carpeta que dejó de ser repo, repo sin `origin`, y repo con OTRO remoto (🟡, con las dos
  URLs en el mensaje), más el clon por SSH del mismo repo, que es 🟢. De la validación: que la
  carpeta equivocada se rechaza y **no** deja rastro en `machines.json`, y que la correcta lo
  registra y pone el piloto en verde. Del diálogo: rechazo con error específico y acierto a
  continuación sin estado pegado; que reparar arranca en la ruta rota; y que «Clonarlo ahora»
  termina en 🟢 con el código en disco, clonando contra un repo local (N-1, sin red), además del
  destino ocupado. De la deriva: que avisa cuando los hashes difieren, que calla cuando coinciden,
  y que re-escanear desde el diálogo deja el inventario con el hash del clon. De la redirección:
  detección por URL escrita, por forma SSH, por el `origin` de la carpeta al detectar el stack, y
  que `Create` no da de alta nada. De V2: que el inventario se abre y se recorre sin clon pero no
  lanza ni re-escanea, que la carpeta movida pide *reparar* y no *vincular*, y que con clon válido
  no hay barra ninguna. Y en las vistas: que los dos botones de auditar están atados a `CanAudit`
  **con su motivo**, y que la tarjeta pinta piloto, etiqueta, tooltip y botón condicionado.

- **D-308 — Las vistas se comprobaron cargándolas de verdad (arnés de D-268/D-290).** El diálogo
  nuevo y las dos páginas se instancian con los diccionarios de producción, escuchando
  `PresentationTraceSources.DataBindingSource`: **cero avisos de enlace** en los tres estados. De
  ahí salió D-296 —el emoji gris— que ninguna aserción sobre el texto del XAML habría detectado.
  <br>
  Un efecto lateral del piloto: los tests que auditan necesitan ahora un clon que sea un repo git
  de verdad con su `origin`, no una carpeta suelta. `TestFactory.MakeClone` lo construye, y es lo
  correcto: un test que audita tiene que partir del mismo estado del que parte un usuario que
  puede auditar.

- **D-309 — Lo que se verifica a mano.** (a) Renombrar la carpeta del clon de xblast → la tarjeta
  pasa a 🟡 con «Reparar vínculo…» al volver a la ventana; repararla → 🟢. (b) Con otra app de
  prueba sin clon: 🔴, inventario en solo lectura y navegable, y «Clonarlo ahora» terminando en 🟢.
  (c) Abrir «Nueva aplicación» con la URL de una app que ya existe y comprobar que redirige en vez
  de crear el duplicado.

## H9 — Arreglo integrado supervisado (opcional, NO entregado)

- El *feature flag* `enableAssistedFix` sigue en la configuración (`settings.json`) y el generador
  de prompt de arreglo (§5.7, vía 4→2) está entregado y probado. **Desde F5.7 §2 (D-275) el
  interruptor NO está en Ajustes**: no estaba conectado a nada y enseñaba una capacidad que la
  aplicación no tiene. El **arreglo integrado supervisado** (rama `fix/{displayId}` + permission
  handler por-fichero + diff aprobado + sin push) queda como trabajo futuro; cuando se construya,
  el flag ya está ahí y el control se vuelve a poner.

## F5.9 — Métricas (V6): de placeholder a panel de mando

### §1 — «Importar v4» sale del menú y entra en el asistente

- **D-310 — Era un problema de SITIO, no de capacidad.** El importador funcionaba; lo que estaba
  mal es que ocupase un destino permanente de la navegación. Importar el baseline de una app es
  una acción de **una-vez-por-app** que se hace justo al darla de alta: quien la necesita está,
  por definición, en «Nueva aplicación», y quien no la necesita —todos los días a partir del
  segundo— la tenía delante para siempre. Con las demás aplicaciones de la empresa todavía por dar
  de alta, el gesto va a seguir haciendo falta: por eso no se retira, se **reubica** al camino por
  el que ya se pasa. El item del rail, la página `ImportView` y su view-model desaparecen;
  `ImportService` y `V4Importer` se quedan enteros.

- **D-311 — El asistente la busca, pero no la importa solo.** `V4Baseline.Find` mira la raíz del
  clon elegido (y acepta que el usuario haya señalado directamente la propia `CodeAudit/`, que es
  el error fácil de cometer cuando se sabe distinguir). Encontrarla **propone**: rellena la ruta y
  marca la casilla. Importar sigue siendo una decisión que se ve venir, igual que detectar deriva
  al vincular ofrece re-escanear y no re-escanea solo (D-301). Y si el baseline vive fuera del
  repo auditado, se señala a mano con el mismo `IFolderPicker` de F5.8. La detección es por
  contenido —basta uno de `BASELINE.md`, `LOTES.md`, `SILENCIADOS.md`, `HISTORICO.md`—, no por el
  nombre de la carpeta: una `CodeAudit/` vacía no es un baseline.

- **D-312 — Se importa ANTES de escanear, y el inventario se reconcilia.** Es el orden que
  importa y el único que conserva las dos mitades. El baseline v4 trae su `app.json` —con el ciclo
  en el que se quedó el sistema anterior— y su inventario con **qué unidades estaban auditadas**;
  el escaneo trae **qué ficheros hay hoy en el clon**, con sus hashes de contenido. Importar
  después habría pisado el escaneo con una lista de ficheros de otra época; escanear y no
  reconciliar habría tirado justo lo que se venía a rescatar. El inventario final es
  `Rescanner.Reconcile(importado, escaneado)`, la misma reconciliación del re-escaneo (D-302), no
  una segunda escrita aparte. Y `ImportService.Import` recibe `push: false`: el alta publica **una
  vez** al final. Dos commits para un mismo gesto no cuentan dos cosas, cuentan la misma a medias.

### §2 — Las reglas de visualización, y por qué son reglas

- **D-313 — Un solo eje Y por gráfica, sin excepción.** No es una preferencia estética: un eje
  secundario deja que quien dibuja **elija la escala** con la que se leen dos series, y con eso se
  puede hacer que cualquier par de líneas se crucen donde uno quiera. `AxisScale` calcula UNA
  escala por gráfica y la comparten todas sus series. La gráfica del flujo es la que tenía la
  tentación —barras de nuevos/resueltos y línea de activos acumulados— y no cae en ella: las tres
  son **conteos de hallazgos**, así que comparten el eje aunque las barras salgan pequeñas al lado
  de la deuda acumulada. Que salgan pequeñas *es el dato*: dice que la rotación semanal es chica
  comparada con lo que hay abierto. `ChartPlot` no tiene ninguna propiedad de segundo eje, que es
  la forma más sólida de no tenerlo. Las marcas van en números redondos (1-2-5 por década): un eje
  que llega a 137 con marcas cada 34,25 es exacto e ilegible.

- **D-314 — El color de una app sale de un HASH de su slug, no de su posición.** Repartir la
  paleta por el orden de la lista habría hecho que dar de alta una aplicación nueva —algo que va a
  pasar con cada app de la empresa— repintase a todas las que van detrás. El índice sale de
  **FNV-1a**, nunca de `string.GetHashCode`, que está aleatorizado por proceso y habría dado
  colores distintos en cada arranque: exactamente lo que la regla prohíbe. Las colisiones se
  resuelven en orden ordinal de slug sobre el **portafolio completo**, así que filtrar el panel no
  reparte nada — un filtro que oculta apps deja a las demás del color que tenían, y el rosco de
  xblast, su línea de coste y su punto en el registro de sesiones son del mismo color.

- **D-315 — Seis colores, y la séptima app en adelante es «Otras».** Descontados los cuatro tonos
  de severidad y los tres de estado (verde/ámbar/rojo), lo que queda libre deja de distinguirse en
  una línea de 1,6 px mucho antes de la décima serie. En la gráfica de coste se nombran las seis
  que más consumieron **en el periodo** y el resto se suma en «Otras», que va en gris y **a
  trazos**: un agregado de varias aplicaciones no puede leerse como una app más solo por tener
  color. La elección de cuáles se nombran depende del periodo; el color de cada una, no.

- **D-316 — Las severidades son colores de ESTADO y quedan reservadas.** Crítica, alta, media y
  baja significan lo mismo en toda la aplicación —chips del portafolio, badges de hallazgos,
  informes—, así que ninguna serie puede usarlos: una app pintada de rojo se lee como «crítica».
  Para poder **comprobarlo** en vez de dejarlo escrito en un comentario, los cuatro salieron de
  dentro de `SeverityToBrushConverter` a `SeverityPalette`, que ahora es el único sitio donde
  viven; el convertidor lee de ahí, así que no pueden divergir. Los roscos de cobertura tampoco
  los usan: van con el color de la app y dos neutros, porque un rosco de cobertura no habla de
  gravedad y pintar «pendiente» de rojo diría que lo pendiente es crítico.

- **D-317 — Dos temas son dos PASOS elegidos, no un flip.** Cada color de serie tiene su valor
  para fondo claro y otro para fondo oscuro. Invertir la luminosidad del mismo color da, sobre
  negro, un tono lavado que no se distingue del vecino. Los colores de serie se **aclaran** en
  oscuro (una línea tiene que destacar sobre el fondo) y los rellenos neutros del rosco hacen lo
  contrario (ahí lo que se busca es un fondo apagado contra el que resalte el tramo auditado): son
  dos reglas distintas porque son dos trabajos distintos, y el test las comprueba por separado. El
  tema vigente se lee del ajuste que la propia aplicación usa para aplicarlo; cambiarlo exige ir a
  Ajustes, y volver a Métricas recarga la página, así que no hace falta escuchar ningún evento.

- **D-318 — Ninguna cifra sin datos detrás.** Un tile que escribe «0,0 días» sobre cero
  resoluciones, o «0 unidades SDK» sobre cero sesiones, no está diciendo cero: está diciendo una
  medida que nadie ha tomado. Los agregados que pueden no existir son **nulos**, no cero
  (`CostInPeriod`, `CostPerAuditedUnit`, `HasCycleData`), y la vista escribe «—» con la frase que
  dice **cuándo se activarán**. Es la regla N-2 aplicada a un panel: declarar la procedencia, o
  declarar que no la hay.

### §3 — Qué enseña el panel, y qué dejó de enseñar

- **D-319 — Las tres preguntas mandan sobre el contenido.** ¿Cómo estamos? (activos por severidad,
  cobertura del ciclo, roscos). ¿Avanzamos? (resueltos con delta, flujo de hallazgos). ¿Cuánto
  cuesta? (coste del periodo, coste en el tiempo, registro de sesiones). Lo que no responde a
  ninguna de las tres no está. Cuatro tiles, cuatro gráficas: la sexta gráfica no habría añadido
  una respuesta, habría repartido la atención entre más sitios.

- **D-320 — «Hallazgos activos» NO se filtra por periodo, y todo lo demás sí.** Es un estado de
  hoy —cuánta deuda hay abierta— y recortarlo por la ventana temporal daría un número más pequeño
  que la deuda real, que es la peor clase de error en un panel que alguien mira para decidir. Los
  resueltos, el coste y las gráficas sí son del periodo, y el delta de resueltos se compara contra
  el periodo **inmediatamente anterior de la misma longitud**, no contra un mes fijo.

- **D-321 — El burndown reconstruye los activos a la fecha de cada tramo.** Repetir el estado de
  hoy en todos los cubos habría dado una recta horizontal que no responde a nada. Cada cubo cuenta
  los hallazgos **creados antes de su cierre y todavía sin resolver en ese momento**, que es lo
  único que contesta «¿la deuda baja o sube?». La gráfica anterior —una barra por semana, sin
  acumulado— no lo contestaba.

- **D-322 — La cobertura se mide sobre lo AUDITABLE, no sobre el total.** Las unidades grandes
  están excluidas por definición: nadie las va a auditar en este ciclo, así que contarlas en el
  denominador daría una app «al 70 %» que en realidad ya no tiene nada pendiente. Es la misma
  cuenta que el progreso de la tarjeta del portafolio, y a propósito: la misma aplicación no puede
  enseñar dos porcentajes distintos en dos pantallas. El rosco sí las **dibuja**, como tercer
  segmento neutro, porque existir existen y el usuario tiene que saber cuántas son.

- **D-323 — El registro de sesiones era el hueco de verdad.** Quién auditó qué, cuándo, cuántas
  unidades, con qué saldo de hallazgos y a qué coste no se veía **en ninguna parte** sin ir al hub
  a leer JSON. Va acotado a 25 líneas —el panel no puede convertirse en otra lista infinita— y
  cada línea abre el informe markdown de esa sesión, que es su detalle. Si esa sesión no dejó
  informe, se dice; un clic que no hace nada se lee como un fallo.

- **D-324 — Lo que se retira, y a dónde va.** El **«% criterio»** mide la calidad del AUDITOR —qué
  proporción de lo que encuentra es criterio y no defecto duro—, no el estado del código: es un
  diagnóstico que solo se puede interpretar junto a la sesión que lo produjo, y ahí sigue, en el
  informe de cada una. El **«tiempo medio a resolución»** no vuelve hasta que haya resoluciones
  reales que promediar; hasta entonces sería el «0,0 días» de D-318. Cuando las haya, vuelve como
  tile con datos.

- **D-325 — Render WPF propio, no LiveCharts2.** Ambas opciones eran MIT y ninguna se descartó por
  licencia. Pesaron tres cosas. (a) **Las reglas del §2 son nuestras**: color por identidad,
  severidades reservadas, un solo eje, dos pasos por tema. Ninguna librería las trae; con
  cualquiera de ellas habría que imponerlas una a una **sobre** sus valores por defecto, que es
  más trabajo que dibujarlas y además deja la puerta abierta a que un valor por defecto se cuele.
  (b) **Tooltips**: el `ToolTip` nativo de WPF ya hereda el tema de la aplicación, la tipografía y
  el comportamiento del sistema; un lienzo Skia dibuja el suyo y hay que replicar los tokens del
  tema a mano — que es justamente lo que la regla de «legible en ambos temas» quería evitar. (c)
  **Coste de integración**: `LiveChartsCore.SkiaSharpView.WPF` arrastra binarios nativos de
  SkiaSharp a `dist`, y las cuatro formas del panel son geometría trivial (polilínea, barras
  agrupadas, arcos). `ChartPlot` y `DonutRing` son dos ficheros y cero dependencias nuevas.

- **D-326 — El cursor es una banda invisible por columna, no un adorno.** Cada tramo del eje X
  lleva un rectángulo transparente que cubre **todo el alto** del área de dibujo. Es lo que
  permite que el tooltip salga apuntando a cualquier altura de la columna —sin tener que acertarle
  a una línea de 1,6 px— y lo que permite enseñar **todas las series de ese tramo juntas**, con su
  color y su valor, en vez del valor suelto de la que se haya acertado. Un tramo sin actividad lo
  dice: un tooltip en blanco se lee como un fallo del programa.

### §4 — Datos: se calcula todo, se guarda nada

- **D-327 — La caché es de la LECTURA y vive en memoria.** Cada render tocaría todos los
  hallazgos, todas las sesiones y todos los inventarios de todas las apps, y el panel se re-agrega
  con cada cambio de filtro, que es un gesto de un clic. Se cachea el volcado de ficheros por
  aplicación —nunca el agregado en disco—: la norma dice que en el hub solo hay datos primarios,
  así que un fichero de agregados sería exactamente lo que no se puede añadir. Se invalida con el
  evento de sync del hub, que es el único momento en que esos ficheros cambian por debajo. El
  toggle «Acumulado» no re-agrega nada: es una forma de **leer** los mismos datos, y se calcula
  sobre lo que ya está en memoria. Y la agregación corre fuera del hilo de UI: el panel no puede
  congelar la ventana mientras cuenta.

### Cobertura y verificación

- **D-328 — Lo que queda probado.** De §1: que el item, la página y su view-model no existen y que
  el servicio sí; que un clon con `CodeAudit/` lo detecta y lo propone marcado; que el alta trae
  los hallazgos del baseline **y** deja el inventario con los ficheros del clon; que desmarcar la
  casilla da de alta sin importar nada; que sin `CodeAudit/` el asistente se comporta exactamente
  igual que antes; que se puede señalar una carpeta de fuera del repo; y que una carpeta que no es
  v4 se rechaza diciendo qué le falta. De §2, una por una: que el color de una app no depende del
  proceso ni del orden de la lista, que filtrar no repinta a nadie, que seis apps reciben seis
  colores distintos aunque colisionen sus hashes, que **ningún** color de serie es uno de
  severidad y que el convertidor de severidad lee del sitio único, que cada color tiene dos pasos
  y en qué dirección va cada familia, que el eje termina en número redondo y no divide por cero,
  que el flujo son barras y línea sobre un solo eje, que hay leyenda desde la segunda serie, y que
  sin datos los tiles escriben «—» con su frase de activación. De §3 y §4: el desglose por
  severidad sin recorte de periodo, el delta contra el periodo anterior, el coste y su media por
  unidad, la cobertura excluyendo grandes y cuadrando con el rosco, el reparto por tramos, la
  agrupación en «Otras» sin perder una sola unidad, la reconstrucción de activos a la fecha de
  cada cubo, el orden y el tope del registro de sesiones, los tres rangos con su grano, «Todo»
  arrancando en el dato más antiguo y pasando a meses por encima del año, que la caché existe y se
  puede tirar, y —el que protege la norma— que **agregar no escribe ni un fichero en el hub**,
  comprobado con una foto del árbol antes y después de recorrer los ocho filtros.

- **D-329 — Las vistas se comprobaron cargándolas de verdad (arnés de D-268/D-290/D-308).**
  `MetricsView` y `OnboardingView` se instancian con los diccionarios de producción, escuchando
  `PresentationTraceSources.DataBindingSource`, y se renderizan a PNG en los dos temas: **cero
  avisos de enlace**. De ahí salieron tres defectos que ninguna aserción sobre el texto del XAML
  habría detectado. El primero: el `ContentPresenter` de la plantilla por defecto de `Button`
  **no estira su contenido**, así que la rejilla de columnas de cada fila del registro de sesiones
  se encogía al ancho de su texto y dejaba de cuadrar con la de la cabecera — el nombre de la app
  y quién la auditó salían pegados («Nóminamaria»). Se arregla con plantilla propia, y quien
  redefine la plantilla de un botón se queda con su color (F5.6 §1). El segundo: la barra de
  desplazamiento de WPF-UI se pinta **encima** del contenido, y el interruptor «Acumulado» quedaba
  debajo de ella; el `ScrollViewer` le reserva su hueco, el mismo que la cabecera y los filtros
  para que el panel no tenga dos bordes derechos.

- **D-331 — Un `Style` sin `BasedOn` SUSTITUYE al estilo implícito de WPF-UI, no lo extiende.** El
  tercer defecto del render, y el que más se veía: `Style x:Key="Block" TargetType="ui:Card"` se
  puso solo para compartir un margen entre los cuatro bloques de gráficas, y al no llevar
  `BasedOn` se llevó por delante la plantilla entera de la tarjeta — fondo, borde **y relleno**.
  El síntoma era que el interruptor «Acumulado» y la última cifra de la fila tocaban la barra de
  desplazamiento; la causa no era un margen sino que esos bloques habían dejado de ser tarjetas.
  Es exactamente la misma trampa que F5.6 §1 encontró en los `Button` (un `Style` sin `BasedOn`
  hereda el `Foreground` negro de serie de WPF) y se cierra igual: `ImplicitStyleTests` barre
  **todas** las vistas y la carcasa exigiendo `BasedOn` en cualquier `Style` cuyo `TargetType` sea
  un control de WPF-UI. Quien redefine un estilo hereda la responsabilidad de todo lo que ese
  estilo traía, no solo de lo que quería cambiar.
  <br>
  El barrido, al nacer, encontró **un caso anterior**: `SideAction` en `FindingDetailView.xaml`
  redefine solo alineación y margen de un `ui:Button` y se lleva la plantilla igual. F5.9 tenía
  prohibido tocar otras vistas, así que no se arregla aquí: queda en la lista de deuda del propio
  test, con su motivo escrito, para que se vea en vez de para que se olvide. El test **falla** si
  alguien arregla ese estilo y no saca la entrada, así que la lista no puede pudrirse.

- **D-332 — «Crít», no «Crítica», en los chips del tile.** Son cuatro chips en el ancho de un
  cuarto de fila y «Crítica» los parte en dos líneas. La abreviatura no es nueva: es la que ya usa
  la tarjeta del portafolio, así que el usuario la ha visto antes y el panel no inventa una
  segunda forma de escribir lo mismo.

- **D-330 — Lo que se verifica a mano.** (a) Cambiar el selector de aplicación y el de periodo y
  ver reaccionar los cuatro tiles y las cuatro gráficas; (b) pasar el ratón por la gráfica de
  coste y leer el tooltip con cada app y su valor, y activar «Acumulado» para ver la curva
  ascendente; (c) pulsar un rosco y comprobar que abre el inventario de ESA aplicación; (d) leer
  el panel entero en tema claro y en tema oscuro; (e) dárselo a alguien que no lo haya visto y
  comprobar que lo entiende sin explicación.

## F5.10 — Silencio con alcance: hallazgo concreto o regla en toda la app

### §1 — El modelo, y por qué es por-aplicación y no del hub

- **D-333 — Excluir una regla es silenciar con otro alcance, no otra cosa.** `RuleExclusion` lleva
  exactamente los mismos campos que `Silence` —motivo obligatorio, notas, autor, fecha, caducidad
  opcional— y comparte hasta el enum de motivos. No se inventó un vocabulario nuevo a propósito:
  las dos son la misma decisión humana («esto no quiero verlo») tomada sobre objetos de distinto
  tamaño, y darles disciplinas distintas habría hecho que la barata pareciera la seria. La
  caducidad se comporta igual que la del silencio, con la misma frase escrita en el mismo sitio:
  **caducada = inexistente a efectos de filtrado**, y la UI la sigue listando como «caducada —
  revisar» porque una decisión que venció no es un error, es algo que alguien tiene que volver a
  mirar.

- **D-334 — Por-aplicación, y por construcción.** La ruta es
  `apps/{slug}/rule-exclusions/{ruleId}.json`: el slug es parte del camino, así que no existe la
  forma de escribir una exclusión global aunque alguien quisiera. No es una comodidad de
  implementación — es el invariante que sostiene el concepto. `mejoras.localizacion.*` sobra en una
  app sin requisitos de i18n y es contractual en la de al lado, y el coste de equivocarse es
  asimétrico y silencioso: **lo que no se reporta no se ve**, así que una ceguera global no la
  descubre nadie leyendo un informe. El grano por-módulo tampoco se hizo (anti-objetivo): si algún
  día hace falta, se verá con uso real.

- **D-335 — Un `ruleId` se valida ANTES de convertirlo en nombre de fichero.** Las reglas del
  catálogo son identificadores seguros, pero `criterio.<área>` acepta un sufijo libre que viene del
  modelo (`RuleCatalog.IsValid` deja pasar cualquier cosa tras `criterio.`), y un sufijo libre que
  acaba en una ruta es cómo se sale de un directorio sin querer. `HubPaths.RequireSafeRuleId`
  exige `[A-Za-z0-9._-]+` y lanza en cuanto no lo es — en voz alta, no devolviendo `false`:
  escribir la exclusión en otro sitio significaría que no suprime nada donde se la busca, que es el
  peor fallo posible para esta pieza (silencioso y en la dirección insegura).

### §2 — Estructural, no cosmética

- **D-336 — La exclusión actúa en la INGESTIÓN, no en la presentación.** Un hallazgo entrante cuyo
  `ruleId` esté excluido no crea ni reactiva nada: se cuenta como detección suprimida y se le
  devuelve al auditor el motivo con nombre y apellidos. Filtrar al pintar habría dejado el hub
  creciendo con hallazgos que nadie iba a ver nunca — deuda invisible que reaparece en cuanto se
  levanta el filtro, y que mientras tanto infla el baseline y el coste de cada reconciliación. La
  guarda va **antes** de validar pilar y severidad: preguntarse si el pillar está bien escrito en
  algo que se va a tirar es trabajo para nadie.

- **D-337 — Suprimir NO es rechazar, y por eso tiene contador propio.** `Counters.Rejected` significa
  «el agente mandó un payload malo» y pinta un ⚠ en el informe. Aquí el payload era perfecto y el
  auditor hizo su trabajo: la app es la que ha decidido que esa regla no aplica. Meterlo en el
  mismo saco habría acusado al auditor de un fallo que no cometió y, peor, habría hecho que el ⚠
  del informe dejara de significar algo. `SuppressedByRule` va aparte, con su línea en el resumen y
  su sección nombrando **qué** se suprimió — porque un contador sin detalle no permite decidir si
  la exclusión sigue teniendo sentido, que es la única razón para volver a mirarla.

- **D-338 — Una pasada que solo suprime queda SECA.** El barrido de F4.1 repite hasta que una pasada
  no aporta nada. Si una supresión contara como aportación, una app con una regla excluida y un
  auditor tozudo barrería la unidad hasta agotar el tope de pasadas produciendo cero hallazgos y
  gastando el presupuesto entero. Lo que se suprime no es trabajo pendiente.

- **D-339 — La regla excluida se RETIRA del brief; las áreas de criterio, nunca.** Es la mitad
  barata de la exclusión: no se pide lo que se va a tirar. Pero `criterio.*` no es una lista de
  comprobación sino juicio profesional libre, y retirarla del brief sería decirle al auditor «no
  pienses en seguridad», que no es lo que pidió quien excluyó una regla. Un `criterio.*` excluido
  explícitamente **sí** se suprime en la ingestión: la exclusión se respeta como filtro de entrada,
  no como venda en los ojos. Las dos mitades leen el MISMO `RuleExclusionSet`, congelado al
  arrancar la sesión: si el brief y la ingestión pudieran discrepar, el auditor gastaría tokens
  buscando algo que la app tira, o al revés.

- **D-340 — Un pilar que se queda sin reglas desaparece entero.** Una cabecera «PILAR MEJORAS»
  seguida de nada se lee como un fallo del programa, no como una decisión.

### §3 — Los existentes: la pregunta que no se puede contestar por omisión

- **D-341 — Silenciar los N hallazgos que ya existen es una decisión SEPARADA, y se pregunta.**
  Excluir previene el futuro; qué hacer con lo que ya está es otra pregunta, y contestarla por
  defecto en cualquiera de los dos sentidos sería decidir por el usuario: silenciarlos siempre borra
  deuda real de un plumazo, no silenciarlos nunca deja una lista que ya nadie va a mirar. El
  diálogo dice cuántos son y qué pasa con cada respuesta. Son **tres** salidas y no dos porque
  «cancelar» no es «no silenciarlos»: quien abre la pregunta y descubre que hay 40 hallazgos
  activos puede querer echarse atrás de la exclusión entera, y con dos botones tendría que excluir
  para luego des-excluir. Con cero hallazgos activos no se pregunta: un diálogo que dice «hay 0,
  ¿los silencio?» es un clic sin contenido.

- **D-342 — Cada hallazgo silenciado en masa se lleva su fichero y su entrada de historial.** No hay
  un «silencio de grupo»: son N silencios normales, cada uno con su motivo, su autor y su fecha,
  distinguibles uno a uno y levantables uno a uno. Lo único que se añade es la **procedencia**:
  `Silence.ByRuleExclusion` guarda el `ruleId` que lo originó, y con eso la ficha puede decir
  «Silenciado por exclusión de regla (`{ruleId}`, por `{autor}`)» en vez de dejar creer que alguien
  miró ese caso concreto y decidió sobre él. Es procedencia, no semántica: suprime, caduca y se
  levanta igual que cualquier otro, así que el silencio por hallazgo no cambia (anti-objetivo).

- **D-343 — Des-excluir NO des-silencia en cascada.** La regla vuelve al brief y sus hallazgos
  vuelven a poder reportarse, pero los que se silenciaron en masa siguen silenciados. Cada uno de
  esos silencios fue una decisión registrada con autor y motivo, y algunos habrán sido revisados a
  mano desde entonces; deshacerlos todos de golpe tiraría también esos. Se levantan desde su ficha,
  como cualquier otro silencio.

- **D-344 — Reconciliación sin cambios en las tools.** Un hallazgo silenciado por exclusión sigue
  siendo `Silenciado`, así que `ExistingForUnit` ya se lo enseña al auditor y éste se pronuncia sin
  re-reportarlo. Los suprimidos en ingestión no existen, así que no aparecen. No hizo falta tocar
  `report_verdicts` ni el resto del vocabulario del agente.

### §4 — La interfaz

- **D-345 — La consecuencia va DEBAJO de cada opción, no en un tooltip.** La diferencia entre los
  dos alcances no es un matiz —una oculta un caso, la otra apaga una regla en toda la aplicación— y
  quien las confunde no se entera hasta la auditoría siguiente. Las dos frases viven en el
  view-model (`SilenceScopeOption.Consequence`), no en el XAML, porque son la parte que hay que
  poder comprobar. El alcance arranca siempre en «solo este hallazgo», que es el gesto de todos los
  días y el que no puede equivocarse por inercia; y el botón cambia de nombre («Silenciar» /
  «Excluir la regla») porque no hace lo mismo en los dos.

- **D-346 — La gestión vive en el panel del ciclo del Inventario.** «Reglas excluidas: N ·
  Gestionar» va ahí y no en Ajustes porque **es un dato de la aplicación**, no de la máquina, y
  porque condiciona la lectura de todo lo que tiene al lado: una cobertura del 100 % con tres
  reglas excluidas no significa lo mismo que una sin ninguna. El contador cuenta las **vivas**; las
  caducadas se cuentan aparte y el tooltip pide revisarlas, porque significan cosas distintas —una
  viva suprime, una caducada solo pide una decisión. Se abre sin clon: es gobernanza, y la
  gobernanza se lee y se edita sin tener el código delante (F5.8 §3).

- **D-347 — Editar la caducidad te hace su autor.** Cambiar cuánto más dura una exclusión es una
  decisión nueva; firmarla con el nombre de quien la creó haría que el registro mintiera. El motivo
  y las notas se conservan: lo que se está cambiando es el plazo, no la razón. Poner 0 días la
  devuelve a permanente, que es además la forma de revivir una caducada sin volver a escribirlo todo.

### Cobertura y verificación

- **D-348 — Lo que queda probado.** Del modelo: que la exclusión es un fichero por regla bajo la
  app, que un `ruleId` con separadores de ruta no llega a escribirse, y que una caducada no filtra
  pero se sigue viendo. De la ingestión: que suprime y no crea hallazgo, que el informe lo cuenta y
  dice cuáles, que lo no excluido sigue entrando en la misma sesión, que la caducidad reactiva la
  regla, que un `criterio.*` excluido se suprime igual, y que una pasada que solo suprime queda
  seca. Del brief: que la regla excluida no viaja en el prompt, que el criterio sí, que un pilar
  vacío no deja cabecera huérfana y que sin exclusiones el brief es **byte a byte** el de antes. De
  los existentes: que el silencio en masa deja entrada de historial y fichero de silencio por
  hallazgo —con la regla escrita dentro—, que sin él siguen activos, que un silenciado por regla
  sigue en la lista del auditor, y que des-excluir devuelve la regla al juego sin des-silenciar. De
  la UI: las dos consecuencias escritas, las tres salidas del diálogo, que sin activos no se
  pregunta, que la ficha nombra la exclusión y que un silencio normal sigue diciendo lo de siempre;
  y de la gestión, listar con estado, des-excluir, editar caducidad y que una app no ve las de
  otra. Y **el que protege el concepto entero**: excluir en una app no suprime nada en la de al
  lado, con dos apps auditadas en el mismo test.

- **D-349 — Las vistas se comprobaron cargándolas de verdad (arnés de D-329).** `FindingDetailView`
  con el selector de alcance, `RuleExclusionsDialog` y `ExcludeRuleDialog` se instancian con el
  tema y los convertidores de producción escuchando `PresentationTraceSources.DataBindingSource`:
  **cero avisos de enlace**. El selector es un `ItemsControl` de `RadioButton` con `IsChecked`
  enlazado a `IsSelected` en dos direcciones; el view-model también empuja hacia los radios cuando
  el alcance se fija desde código, y las dos vías convergen porque el setter generado no reemite
  cuando el valor no cambia.

- **D-351 — El botón de la sección tiene una puerta por alcance, no una para las dos.** Su
  visibilidad colgaba de `CanSilence` (`Status != Silenciado`), que es correcto para «solo este
  hallazgo» y falso para «esta regla en toda la aplicación»: excluir la regla de un hallazgo YA
  silenciado sí hace algo, y es además el camino natural — alguien silencia un falso positivo, ve
  que se repite por toda la app y vuelve a esa misma ficha a apagar la regla entera. Con la puerta
  compartida se encontraba el selector de alcance pintado y **ningún botón que pulsar**, que es
  peor que no ofrecer la opción. `CanApplySilence` la separa: alcance de regla siempre, alcance de
  hallazgo solo si no está silenciado.

- **D-350 — Lo que se verifica a mano.** Excluir una regla desde un hallazgo real de xblast
  eligiendo «toda la aplicación» y marcando silenciar los existentes; auditar esa clase y comprobar
  que el informe trae «Suprimidos por regla» y que ninguno renace; des-excluir desde «Gestionar» y
  comprobar que vuelven a poder reportarse.


## F5.12 — Silencio por patrón (sustituye a F5.10-alcance-regla y cancela F5.11)

### §0 — Lo que se cancela, y por qué

- **D-352 — La exclusión por regla y el afinado de catálogo quedan DESCARTADOS.** F5.10 entregó dos
  alcances de silencio: el hallazgo y la **regla del catálogo**. F5.11 iba a partir las reglas en
  trozos más finos para que excluir una no apagase de más. Las dos se retiran a la vez porque
  compartían el mismo error: convertían silenciar en **mantenimiento de taxonomía**. El alcance de
  una exclusión lo decidía la granularidad del catálogo, la granularidad del catálogo la decidía
  quien lo escribió, y afinar el alcance obligaba a reescribir el catálogo — un trabajo continuo,
  sin dueño, que además cambia el significado de las reglas ya escritas en hallazgos antiguos. El
  sustituto es coherente con la arquitectura que este proyecto ya eligió en F4: **las preguntas
  semánticas las contesta el LLM en el momento de auditar**, no un diccionario mantenido a mano.
  «¿Este hallazgo es del mismo tipo que aquel?» es exactamente una de esas preguntas.

- **D-353 — El catálogo de reglas deja de ser superficie de gobernanza.** Queda como **metadato
  informativo**: búsqueda, métricas y «qué busca» en la ficha. Su granularidad deja de importar
  porque ya no decide qué se calla, y por eso F5.11 no se ejecuta: no hay nada que partir. El brief
  vuelve a viajar entero (`PillarBrief.For(stack)` perdió el parámetro de exclusiones), lo que
  además retira la maquinaria de «un pilar que se queda sin reglas desaparece» — sin exclusiones,
  ningún pilar se vacía nunca.

- **D-354 — La maquinaria de F5.10 se RECICLA, no se tira.** El commit `a3aaed3` había construido
  las piezas correctas sobre el concepto equivocado, y casi todas mapean 1:1 al patrón: el selector
  de alcance con su consecuencia escrita, la disciplina de gobernanza compartida con el silencio
  (motivo, notas, autor, caducidad), el contador propio en la sesión y su sección en el informe, el
  panel del ciclo en el Inventario, la regla de que una pasada que solo suprime queda **seca**, y la
  procedencia escrita en el silencio del hallazgo. Lo único que cambia de sitio es **dónde ocurre la
  supresión**: antes en la ingestión, ahora en el auditor.

### §1 — El modelo

- **D-355 — El alcance lo define una FRASE, no un identificador.** `PatternSilence` lleva un
  `exemplar` («bloques catch vacíos que ocultan excepciones»), obligatorio y no vacío, más los
  mismos campos que un silencio. La frase **es** el alcance: viaja en el prompt de cada unidad y es
  lo único que el auditor lee para decidir. De ahí que la validación de esquema la exija —un patrón
  sin frase produciría supresiones que nadie podría explicar— y de ahí que afinar el alcance sea
  **reescribir una línea de texto** en vez de negociar la granularidad de un catálogo.

- **D-356 — Carpeta propia, `apps/{slug}/pattern-silences/{ulid}.json`, y no dentro de
  `silences/`.** El prompt planteaba una entrada de tipo patrón dentro de `silences/`, con un
  `scope`. No se hizo, por tres razones concretas: (a) la clave de un silencio es el ULID del
  **hallazgo** y la de un patrón es la suya propia, así que compartir carpeta obliga a todo lector
  de `silences/` —`ListSilences`, `TryReadSilence` y la **migración de F4**, que enumera cada
  fichero del directorio— a conocer un discriminador para siempre; (b) un hallazgo puede estar
  silenciado por sí mismo **y** ser el origen de un patrón, y los dos ficheros no pueden llamarse
  igual; (c) un fichero por patrón bajo la app es lo mismo que hacía `rule-exclusions/`, que es lo
  que se recicla. El invariante que importaba —**por-aplicación por construcción**, el slug en la
  ruta— se conserva intacto.

- **D-357 — El id corto (`P-1`) existe para el prompt, y no es identidad histórica.** Un ULID de 26
  caracteres por patrón en cada prompt es ruido que el modelo copia mal; un `P-3` no. Se reparte
  como el mayor existente más uno, sobre los patrones vivos **y** caducados, sin contador en
  `app.json`. Si se borran todos y se crea otro vuelve a ser `P-1` — y nada miente, porque **el
  informe guarda el ejemplar al lado del id** (`PatternSuppressionTally` lleva los dos). Editar el
  ejemplar **no** cambia el id: un informe viejo tiene que seguir nombrando lo mismo.

- **D-358 — El ejemplar se propone automáticamente y se edita antes de confirmar.**
  `ExemplarDraft.Propose` quita del título lo que ata la frase a un sitio concreto: el símbolo del
  hallazgo, los tokens con punto o guion bajo, los que llevan mayúscula interna, y lo que va entre
  paréntesis o comillas invertidas; con el conector que los introducía («…en `ReadCSV`» pierde
  también el «en»). Si el recorte se lo come todo devuelve el título tal cual: una caja vacía es
  peor punto de partida que una frase demasiado concreta. **No es matching y no pretende serlo** —
  es un borrador, y el diálogo lo enseña en una caja de texto precisamente porque lo que se espera
  es que el usuario lo pula. La generalización semántica de verdad («catch vacío en ReadCSV oculta
  errores de parseo» → «bloques catch vacíos que ocultan excepciones») la hace la persona, en dos
  segundos, mirando la frase.

### §2 — Dónde vive la supresión

- **D-359 — La supresión ocurre en el AUDITOR, y en la ingestión no queda ningún filtro.** El prompt
  de cada unidad lleva los patrones vivos («TIPOS DE PROBLEMA SILENCIADOS…»), la instrucción de no
  reportar lo que corresponda a uno de ellos, y la de declararlo en `unit_done`. `SubmitFindingCore`
  perdió su guarda: **no hay comparación de textos, ni hashes, ni parecidos calculados**. Es el
  anti-objetivo central de F5.12 y también el que más tentación da de romper, porque «solo un
  `Contains`» siempre parece barato — y es exactamente la película de F5.10.

- **D-360 — Coste del fallo asumido y escrito: un hallazgo de más, visible.** La supresión es juicio
  del modelo y no es determinista. Si un día se le escapa, el hallazgo entra con normalidad y el
  usuario lo silencia —individualmente, o afinando el ejemplar—. Ese es el peor caso, y es
  reversible en un clic. El peor caso del camino contrario (una taxonomía que decide en silencio qué
  no se ve) no lo descubre nadie leyendo un informe.

- **D-361 — `unit_done` gana un argumento estructurado, no un texto que haya que parsear.**
  `suppressedByPattern: [{patternId, count}]`. Es el ÚNICO canal por el que una supresión entra en
  los contadores, así que se valida lo justo y **no se tira nada**: un `count` que no suma no se
  cuenta pero deja su rastro en la traza de tools, y un `patternId` que no corresponde a ningún
  patrón vivo **se cuenta igual**, marcado como «el auditor citó un patrón que no existe». Tragarse
  cualquiera de los dos dejaría una supresión invisible, y ningún número de esta aplicación aparece
  sin causa (D-060). Si el modelo no declara nada, la supresión existió y no se contó: coste
  asumido, muy por debajo del de mantener una taxonomía.

- **D-362 — Una pasada que solo suprime sigue quedando SECA.** Se conserva tal cual de D-338. Si una
  supresión declarada contara como aportación, una app con un patrón y un auditor tozudo barrería la
  unidad hasta agotar el tope de pasadas produciendo cero hallazgos. Lo que se calla no es trabajo
  pendiente.

- **D-363 — Cada patrón acumula cuánto ha suprimido.** `PatternSilence.Suppressions` y
  `LastSuppressionUtc` se suman al cerrar la sesión, y la gestión los enseña en una frase («Ha
  suprimido 12 detección(es), la última el …»). Es el único dato con el que se puede decidir si un
  patrón sigue mereciendo la pena o si se puso por un susto puntual: un contador a cero tras varios
  ciclos no es un error, pero es lo primero que hay que mirar. La escritura nunca tumba una sesión
  ya publicada — el informe conserva el dato aunque el contador falle.

### §3 — Los existentes: la pregunta que ya no existe

- **D-364 — El diálogo de tres salidas de F5.10 desaparece; el hallazgo origen se silencia con el
  patrón, sin preguntar.** Aquel diálogo ofrecía «los N hallazgos activos de esta regla», una
  población que **solo existía porque existía la taxonomía**: sin `ruleId` como criterio no hay forma
  no arbitraria de enumerar «los que son de este tipo», y calcularla con parecidos de texto sería
  reintroducir por la puerta de atrás justo lo que D-359 prohíbe. Lo que sí se sabe con certeza es
  que el hallazgo desde el que se crea el patrón **es** de ese tipo: el usuario acaba de mirarlo y
  decir «esto no lo quiero ver más». Silenciar el tipo y dejar activo el caso que lo motivó sería
  incoherente, así que se silencia con él, con la procedencia escrita
  (`Silence.ByPatternExemplar`), y no se pregunta — no es una decisión aparte como lo eran los 40
  hallazgos de D-341. Los demás se silencian uno a uno desde su ficha, o desaparecen solos en la
  siguiente auditoría. Sobre un hallazgo **ya** silenciado el patrón no reescribe su silencio: la
  decisión previa era suya, con su motivo y su autor.

- **D-365 — La procedencia guarda el TEXTO del ejemplar, no el id del patrón.** `ByRuleExclusion`
  llevaba un `ruleId` que seguía significando algo aunque la exclusión se retirara.
  `ByPatternExemplar` lleva la frase porque un id colgando de un fichero que ya no existe no explica
  nada, y la ficha tiene que poder decir «silenciado al silenciar el patrón "…"» un año después de
  que alguien des-silenciara el patrón.

- **D-366 — Des-silenciar un patrón NO des-silencia en cascada.** Igual que D-343: el ejemplar deja
  de viajar en el prompt y el auditor vuelve a reportar problemas de ese tipo, pero cada silencio de
  hallazgo fue una decisión registrada con autor y motivo, y algunos se habrán revisado a mano desde
  entonces. Se levantan desde su ficha.

- **D-367 — Reconciliación sin cambios en las tools.** Un hallazgo silenciado por un patrón sigue
  siendo `Silenciado`, así que `ExistingForUnit` se lo enseña al auditor y éste se pronuncia sin
  re-reportarlo. El prompt lo dice explícitamente: un tipo silenciado **no exime de reconciliar**.
  No hizo falta tocar `report_verdicts`.

### §4 — La interfaz

- **D-368 — El ejemplar se edita EN la ficha, bajo su opción de alcance; no hay modal.** F5.10
  abría un diálogo modal porque tenía una pregunta que hacer. Aquí no queda ninguna: la frase se ve
  y se pule en el mismo formulario donde ya están el motivo, las notas y la caducidad, debajo del
  radio que la activa (`SilenceScopeOption.HasExemplar`). El botón cambia de nombre con el alcance
  («Silenciar» / «Silenciar este tipo») y `CanApplySilence` conserva la puerta separada de D-351:
  silenciar el **tipo** de un hallazgo ya silenciado sí hace algo, y es el camino natural. Se
  retiran `ExcludeRuleDialog`, `IExcludeRuleConfirmer` y `ExcludeRuleConfirmation`, y con ellos una
  dependencia del constructor de `FindingDetailViewModel`.

- **D-369 — La consecuencia sigue debajo de cada opción, y nombra al juez.** «Las auditorías de
  {app} dejarán de reportar problemas de este tipo. **El juicio de similitud lo hace el auditor.**»
  La segunda frase no es un detalle de implementación: es lo que explica por qué a veces se colará
  un hallazgo de un tipo silenciado, y sin ella ese caso se leería como un fallo del programa.

- **D-370 — La gestión vive donde vivía, y el contador cuenta lo mismo.** «Patrones silenciados: N ·
  Gestionar» en el panel del ciclo del Inventario, por la razón de D-346: es un dato de la
  aplicación y condiciona la lectura de todo lo que tiene al lado. Vivos y caducados se cuentan por
  separado. La gestión lista ejemplar, origen, autor, caducidad, estado y **supresiones acumuladas**,
  y ofrece las tres operaciones: reescribir el ejemplar, cambiar la caducidad y des-silenciar.
  Editar la caducidad o el ejemplar te hace su autor (D-347): las dos son decisiones nuevas.

- **D-371 — La ficha dice si este hallazgo fue el ORIGEN de un patrón.** «Origen del patrón
  silenciado "…" (P-2, por …)», y si está caducado lo añade. Sin esto, un hallazgo silenciado por su
  propio patrón parece silenciado porque sí.

- **D-372 — El tope de 50 avisa, no bloquea.** La lista viaja en cada prompt de unidad y con decenas
  de patrones sigue siendo despreciable frente al contenido de la unidad, así que el tope no es un
  límite de coste: es el **síntoma** de que el silenciado se está usando como taxonomía, que es lo
  que F5.12 vino a evitar. La gestión lo dice («considera consolidar…») y se sigue. Sin más
  ingeniería.

### §5 — Migración y retirada

- **D-373 — `RuleExclusion` se ELIMINA; lo escrito se migra.** No queda deprecado-legible: un modelo
  sin escritor es un artefacto de taxonomía esperando a confundir a alguien.
  `RuleExclusionMigration` lee los ficheros legados como JSON crudo —sin necesitar el tipo—, escribe
  un patrón por cada uno con la **descripción de la regla del catálogo** como ejemplar
  (`"{Title}: {Look}"`; el `ruleId` si la regla ya no está), conserva motivo, notas, autor, fecha y
  caducidad, deja la procedencia escrita en las notas, borra el origen solo después de haber escrito
  el destino y retira el directorio vacío. Es idempotente y corre en cada apertura del hub, como la
  de D-064; en cuanto no queda ninguna no hace nada. Un fichero ilegible se deja donde está y se
  reporta — nunca se borra nada en silencio. Los ids cortos respetan los patrones que ya hubiera en
  la app. Se retiran también `HubPaths.RequireSafeRuleId` y sus rutas: con la clave siendo un ULID
  generado por la app, no hay texto del modelo que pueda acabar en una ruta.

### Cobertura y verificación

- **D-374 — Lo que queda probado.** Del modelo: que el patrón es un fichero por patrón bajo la app,
  que sin ejemplar no se escribe, que los ids cortos no se repiten, que uno caducado no filtra pero
  se sigue viendo, y que el borrador del ejemplar quita los nombres propios y cae al título cuando
  no queda nada. Del prompt: que el ejemplar y su id llegan a la unidad con la instrucción de
  declarar lo suprimido, que el catálogo sigue entero, que la caducidad lo retira, que sin patrones
  el prompt es **exactamente** el de antes, y que des-silenciar lo saca del prompt siguiente. De los
  contadores: que lo que el auditor declara se cuenta, se nombra en la sesión y sale en el informe
  con su desglose por patrón, que sin supresiones el informe no habla del asunto, que un id
  inventado se cuenta y se marca, que un `count` cero no se cuenta pero deja rastro, que una pasada
  que solo suprime queda seca, y —el que protege D-359— que **un hallazgo reportado pese al patrón
  entra con normalidad**. Del trabajo: que cada patrón acumula lo suyo entre sesiones. De la
  gobernanza: que el hallazgo origen se silencia con su procedencia escrita, que sobre uno ya
  silenciado no se reescribe nada, que des-silenciar no des-silencia en cascada, y que **el silencio
  individual sigue intacto** (anti-objetivo). De la UI: las dos consecuencias escritas, el ejemplar
  propuesto y editable, el botón que cambia de nombre, la puerta por alcance, la línea de origen del
  patrón, y que un silencio normal sigue diciendo lo de siempre. De la gestión: listar con estado y
  trabajo, reescribir el ejemplar sin cambiar el id y que el cambio llegue al prompt, des-silenciar,
  caducidad, el aviso de tope, y que una app no ve los patrones de otra. De la migración: la
  conversión con la descripción de la regla, la caducidad conservada, el `ruleId` como respaldo, el
  directorio retirado, la idempotencia, los ids sin choque, el fichero ilegible intacto y que migrar
  una app no toca a la de al lado. Y **el que protege el concepto entero**: silenciar en una
  aplicación no calla a la de al lado.

- **D-375 — Lo que se verifica a mano.** Silenciar como patrón un `catch` vacío de xblast desde su
  ficha, puliendo el ejemplar propuesto; auditar una clase distinta que tenga OTRO `catch` vacío y
  comprobar que no aparece como hallazgo y que el informe dice «Suprimidos por patrón: 1» nombrando
  el patrón; abrir «Gestionar» y ver que el contador de trabajo del patrón subió; des-silenciarlo y
  comprobar que en la siguiente auditoría el hallazgo reaparece.


## F5.13 — Lanzamiento descontrolado y frenos de emergencia (incidente del 2026-08-26)

### §0 — Qué se buscó, y qué se encontró

- **D-376 — Los tres bugs se reprodujeron ANTES de tocar nada, y dos de ellos no existían.** El
  parte describía tres fallos combinados: una selección de una clase que auditó un módulo entero,
  un botón «Detener» ausente y un item de navegación «Sesión en vivo» desaparecido. Antes de
  cambiar una línea se montó un arnés que ejercita el camino real —marcar en el árbol, lanzar con
  el agente falso, leer la sesión escrita en el hub— y midió lo siguiente:

  | Qué se midió | Resultado |
  |---|---|
  | Casilla del módulo con una hija marcada | `null` (indeterminado), **nunca** `true` |
  | Contador de la barra | 1 |
  | Unidades de la sesión escrita en el hub | 1 — exactamente la marcada |
  | `SessionViewModel.IsRunning` con sesión viva | `true`, y `StopCommand` ejecutable |
  | `MainViewModel.HasSession` con sesión viva | `true`, y `ShowSessionCommand` navega a V5 |

  Es decir: **el tri-estado no propagaba mal, el lanzamiento no expandía grupos, y los dos frenos
  estaban en su sitio y funcionando** en la capa de view-model. La hipótesis del parte («el padre se
  marca al marcar una hija y el lanzamiento lo expande») quedó descartada con evidencia, no por
  opinión. Lo que sí se encontró está en D-377; lo que no se encontró, en D-380.

### §1 — Bug 1: la causa real estaba en el gesto, no en la contabilidad

- **D-377 — `IsThreeState="False"` sobre una casilla que el view-model lleva a `null` convierte un
  clic de DESHACER en «seleccionar el módulo entero».** La casilla del módulo se declaraba
  `IsThreeState="False"` a propósito —para que el clic alternara entre marcar y desmarcar sin pasar
  por el estado intermedio— mientras `RefreshCheckState` le empujaba `null` en cuanto había
  selección parcial. Pero `ToggleButton.OnToggle` de WPF, con tres estados desactivados, resuelve un
  clic **desde indeterminado** como `IsChecked = true`. La secuencia completa, que es exactamente la
  que describe el parte:

  1. El usuario marca UNA clase. El módulo pasa a indeterminado — que a ojo **se lee «marcado»**, y
     por eso el parte dice «el nodo padre se marcó también».
  2. El usuario pulsa la casilla del módulo para deshacer lo que cree haber hecho sin querer.
  3. WPF manda ese clic a `true` → `OnIsCheckedChanged` seleccionaba **todas** las unidades del
     módulo.
  4. «Auditar selección» lanza el módulo entero. El «1 unidad seleccionada» que el usuario recuerda
     es del paso 1, antes del gesto que lo multiplicó.

  El contador nunca mintió: decía la verdad en cada instante. Lo que fallaba era que **el gesto de
  corrección hacía lo contrario de corregir**, y en la dirección cara. Ahora la regla la decide
  `ModuleNode` y no `ToggleButton`: si hay algo marcado en el módulo, el clic lo quita; si no hay
  nada, lo marca entero. La dirección segura es la de quitar, y además es la que espera quien pulsa
  para deshacer.

- **D-378 — El nodo de grupo AVISA, no toca la selección.** `ModuleNode.OnIsCheckedChanged` mutaba
  directamente `unit.IsSelected` de sus hijas y confiaba en que el eco llegara al conjunto del
  view-model. Funcionaba, pero repartía la propiedad de la selección entre dos sitios. Ahora el nodo
  publica `SelectionRequested(module, select)` y el dueño del conjunto —el view-model— aplica el
  cambio y refresca el tri-estado. Un solo dueño es lo que hace estructuralmente imposible que el
  árbol y el contador digan cosas distintas. Y `IsChecked` queda como lo que siempre debió ser: un
  **reflejo** de las unidades, escrito solo por `RefreshCheckState`, nunca el origen de nada.

- **D-379 — `SelectedUnits()` es LA lista, y la consumen los tres.** No había una recolección
  paralela de grupos —eso se descartó midiendo— pero sí **dos expresiones de la misma verdad**: el
  contador leía `_selected.Count` y `AuditSelection` volvía a derivar la lista desde `_allUnits`.
  Dos derivaciones que hoy coinciden son dos derivaciones que mañana pueden no coincidir, y la
  diferencia se paga en tokens. Ahora hay un solo método —unidades-hoja marcadas que siguen en el
  inventario vigente— y lo consumen el contador de la barra, el diálogo de confirmación y la
  petición que va al coordinador. Un grupo no tiene ruta: no puede entrar aunque alguien lo intente.

- **D-380 — La salvaguarda de última línea vive en el COORDINADOR, y aborta antes del primer token.**
  `SessionRequest` lleva ahora `ConfirmedUnits`: cuántas unidades vio y aceptó el usuario.
  `GuardAgainstUnconfirmedScope` se ejecuta antes del sello, antes de publicar claims y antes de
  cualquier llamada al agente, y compara contra **las dos** listas —la que llegó y la que de verdad
  se va a auditar tras resolverla contra el inventario—. Si alguna supera lo confirmado, lanza
  `LaunchMismatchException` y no hay sesión, ni claims, ni gasto. Null significa «nadie declaró un
  N» y no comprueba nada, para no romper los caminos que no vienen de la barra de selección.
  <br>Es deliberadamente **redundante** con D-379: la corrección de fondo es que contador y lista
  sean el mismo método; esto es lo que garantiza que el próximo desajuste —venga de donde venga— se
  pague en un mensaje de error y no en una factura.

### §2 — Bugs 2 y 3: no hubo regresión, y eso también hay que escribirlo

- **D-381 — No existe el commit culpable del bug 3, porque no hubo regresión.** El parte pedía
  identificar qué cambio se llevó por delante el item «Sesión en vivo» de F5.2. Se buscó y **no
  aparece**: `git log -S 'ShowSessionCommand' -- MainWindow.xaml` devuelve un único commit, el
  propio `8971961` (F5.2) que lo introdujo, y ningún commit posterior lo ha tocado. De los cuatro
  commits que han modificado `MainWindow.xaml` desde entonces, el único que borró un item de
  navegación es `25c8f87`, y lo que borró fue **«Importar v4»** —el vecino de abajo en el rail—, no
  el de sesión. El botón «Detener» tiene una historia todavía más corta: está en
  `SessionView.xaml` desde `f0ad0c2` (v1) y ningún commit lo ha quitado ni ocultado; F5.2 y F5.3
  rediseñaron a su alrededor sin tocarlo.
  <br>Los tests de D-382 confirman que hoy los dos frenos funcionan: con sesión viva,
  `MainViewModel.HasSession` es true y `ShowSessionCommand` navega a V5, y `SessionViewModel`
  ofrece `StopCommand` ejecutable. **Se deja escrito que no se encontró la causa** en vez de
  inventar una: un parte de regresión sin regresión reproducible es información, y taparlo con un
  arreglo cosmético habría dejado el problema real —sea cual sea— sin buscar. Las dos hipótesis que
  quedan vivas y que este repositorio no puede descartar: que la ejecución fuera un binario anterior
  a F5.2, o que la sesión que el usuario veía la hubiera lanzado una instancia distinta de Atalaya
  (el estado de sesión es de proceso, no del hub: otra instancia auditando no enciende el rail de
  ésta). Si el episodio se repite, esa es la primera pregunta que hay que contestar.

- **D-382 — Los dos frenos pasan a ser invariantes testeados, en sus DOS capas.** La lección del
  episodio no es cuál fue la causa: es que **ningún freno de emergencia tenía un solo test**, así
  que podían desaparecer en silencio y nadie se enteraría hasta estar delante de una sesión que no
  se puede parar. Se prueban ahora por partida doble, porque se pueden caer por cualquiera de las
  dos vías y la que se cae sin ruido es justamente la que no compila: (a) el **estado observable** —
  con sesión viva el view-model ofrece el acceso y el mando, y `ShowSessionCommand` navega de verdad
  a V5—; y (b) la **plantilla** — que `MainWindow.xaml` pinta el item enlazado a `HasSession`,
  `SessionNavLabel` e `IsSessionRunning`, y que la cabecera de `SessionView.xaml` pinta «Detener»
  atado a `IsRunning`. El aviso de que el freno se activó (`StatusMessage`) también se comprueba: una
  parada que no se anuncia parece que no ha hecho nada.

- **D-383 — El acceso a V5 se DERIVA del estado, ya no depende de cazar un evento.** `MainViewModel`
  sincronizaba la cuenta en su constructor pero la sesión no: `HasSession` colgaba únicamente de
  recibir un `Changed` del servicio. En la práctica funciona —la carcasa nace antes que cualquier
  sesión— pero es una dependencia temporal innecesaria en algo cuyo trabajo es ser el camino de
  vuelta cuando algo va mal. Ahora la carcasa se sincroniza también al construirse. No es la causa
  de nada de lo reportado; es quitarle al freno la única forma que tenía de nacer apagado.

### §3 — Corrección de D-377: el fallo estaba en el DIBUJO, no en el gesto

- **D-386 — D-377 acertó el síntoma y se quedó corto en la causa. La casilla del módulo no
  «parecía» marcada: se pintaba EXACTAMENTE igual que una marcada.** Tras entregar §1, el usuario
  volvió con el mismo parte: «cuando checkeo una clase hija se sigue marcando la clase padre». La
  primera versión razonaba que el indeterminado *se lee* como marcado y arreglaba el gesto que salía
  de esa confusión; pero no había mirado la plantilla. Al descomprimir el BAML de
  <c>Wpf.Ui.dll</c> 3.0.5 aparecen dos disparadores sobre <c>IsChecked</c> en el estilo de
  <c>CheckBox</c>, y **los dos pintan el mismo fondo**:

  | `IsChecked` | Fondo de `ControlBorderIconPresenter` | Glifo de `ControlIcon` |
  |---|---|---|
  | `null` | `CheckBoxCheckBackgroundFillChecked` | `Subtract16` (un guion) |
  | `true` | `CheckBoxCheckBackgroundFillChecked` | `Checkmark48` (la marca) |

  Es decir: en WPF-UI 3.0.5 una casilla indeterminada es **una casilla de acento maciza**, idéntica
  en color y relleno a una marcada; lo único que la distingue son unos pocos píxeles de glifo
  dentro. En un árbol denso de módulos eso no es «se lee como marcado»: **es** marcado a todos los
  efectos de quien mira. El usuario tenía razón las dos veces, y la segunda con más precisión que
  el primer diagnóstico.

- **D-387 — La casilla del módulo pasa a ser de DOS estados; la selección parcial se dice con
  palabras.** No se puede corregir el relleno desde fuera —los disparadores de la plantilla lo fijan
  por <c>TargetName</c>, y eso gana a cualquier estilo derivado—, así que la solución no es pelearse
  con la plantilla sino **dejar de mandarle un valor que no sabe dibujar**. La casilla se enlaza
  ahora a <c>ModuleNode.IsAllSelected</c> (<c>true</c> si y solo si TODAS las unidades lo están) en
  modo OneWay, y el clic llega por <c>ToggleModuleCommand</c>. Lo que el guion pretendía comunicar
  —y nunca comunicó— se escribe al lado del nombre del módulo: «3 de 12 seleccionadas», visible solo
  con selección parcial. Es estrictamente más información que un guion, y no se puede confundir con
  un relleno.
  <br><c>IsChecked</c> **se conserva** como <c>bool?</c>: es la semántica correcta del grupo y es lo
  que los tests interrogan. Lo que deja de hacer es viajar a la vista. La distinción es la que vale
  la pena recordar: *el modelo puede tener tres estados; el control solo sabe dibujar dos.*

- **D-388 — Y con eso la regla del clic vuelve a la estándar, que ahora sí se puede leer del
  dibujo.** D-377 había hecho que un clic desde indeterminado LIMPIARA, porque con una casilla que
  mentía sobre su estado la dirección segura era la de quitar. Con la casilla diciendo la verdad esa
  excepción sobra y estorba: quien pulsa una casilla vacía espera llenarla. La regla es la de
  cualquier casilla de dos estados —si no está todo marcado, marca el módulo entero; si lo está, lo
  limpia— y vive en un solo sitio (<c>ModuleNode.RequestToggle</c>), llegue el gesto por el comando
  de la vista o por escribir <c>IsChecked</c> desde código. La protección del gasto no depende ya de
  la dirección del gesto: la dan el contador —que lee la misma lista que se lanza (D-379)—, el
  diálogo de confirmación y la salvaguarda del coordinador (D-380).

- **D-389 — La lección de método, que es la que más cuesta.** El primer diagnóstico se apoyó en un
  arnés de view-model que medía bien y probaba lo que decía probar — y aun así apuntó al sitio
  equivocado, porque el fallo vivía una capa más abajo, en una plantilla de terceros que nadie había
  leído. Un test verde sobre el estado observable **no** dice que la pantalla enseñe ese estado. Lo
  que cerró el caso no fue razonar mejor: fue abrir el DLL del proveedor y mirar qué pinta cada
  disparador. Cuando el parte de un usuario contradice un test que pasa, el que se está midiendo mal
  es el test, no el usuario. Queda como invariante en `LaunchScopeTests` que la vista se enlaza a
  <c>IsAllSelected</c> y **nunca** a <c>IsChecked</c>, para que nadie vuelva a mandarle a esa casilla
  un valor que no sabe dibujar.

### Cobertura y verificación

- **D-384 — Lo que queda probado.** Del alcance: que una hija marcada deja el grupo en
  indeterminado y audita esa unidad y solo esa —extremo a extremo, con el agente falso y leyendo la
  sesión escrita—; que un grupo marcado entrega sus hijas y **nunca** el grupo; que la casilla del
  módulo **no se pinta marcada** con una hija marcada (D-386) y que la vista se enlaza a
  `IsAllSelected` y nunca a `IsChecked`; que la selección parcial se dice con palabras y solo cuando
  es parcial; que el gesto marca el módulo entero o lo limpia, también desde selección parcial; que
  el contador, el diálogo de confirmación y la sesión dicen el mismo
  número; y que filtrar la vista no cambia la lista de lanzamiento. De la salvaguarda: que el
  coordinador aborta con `LaunchMismatchException` si la lista supera lo confirmado —sin sesión y
  sin claims—, que lo confirmado exacto pasa sin estorbo, que sin N declarado no molesta, y que el
  camino real la arma pasando el N. De los frenos, lo de D-382.

- **D-385 — Lo que se verifica a mano.** Marcar una clase → la barra dice «1 unidad seleccionada»,
  **la casilla del módulo se queda vacía** y a su derecha aparece «1 de N seleccionadas»; pulsar la
  casilla del módulo → se marca el módulo entero y la nota desaparece; volver a pulsarla → se vacía;
  dejar solo la clase y lanzar → el diálogo, si aparece, dice 1 y V5 dice «Unidad 1 de 1»; navegar a
  Portafolio y volver por el item pulsante del rail; comprobar que «Detener» está en la cabecera y
  que al pulsarlo la sesión se cierra con informe parcial y los claims liberados.


## F5.14 — La narración de V5 marcaba como disputado lo que no lo era

### §1 — La causa: un conversor de cadenas alimentado con un contador

- **D-390 — `NotEmptyToVisibilityConverter` consideraba «lleno» el cero, y la insignia ⚖ colgaba de
  un `Count`.** El parte: durante la primera pasada la columna de hallazgos de V5 marcaba con ⚖
  —«disputado»— todo lo que entraba, mientras el resumen final decía 0 disputados y los hallazgos
  persistidos no tenían ninguna disputa. La sospecha del parte apuntaba a tres sitios; era el
  segundo. `SessionView.xaml` pintaba la insignia así:

  ```xml
  Visibility="{Binding Disputes.Count, Converter={StaticResource NotEmptyToVisibility}}"
  ```

  y el conversor decía:

  ```csharp
  => value is string s ? (!string.IsNullOrWhiteSpace(s) ? Visible : Collapsed)
      : value is not null ? Visible : Collapsed;
  ```

  `Disputes.Count` es un **int**. No es cadena, no es nulo → **Visible, siempre**. Un hallazgo con
  cero disputas encendía la insignia igual que uno con tres. El motor nunca se equivocó: lo que
  mentía era una rama de conversor escrita para «cualquier objeto» en un conversor cuyo nombre
  promete «no vacío».

- **D-391 — Se arregla el CONVERSOR, no el enlace.** Cambiar la vista a otro conversor habría dejado
  la mina puesta para el siguiente que usara este bien. Ahora «vacío» se decide por lo que el valor
  ES: cadena en blanco, número a cero, colección sin elementos, `false` o nulo. Los otros trece usos
  del conversor son todos de cadena y siguen comportándose exactamente igual — la rama que cambia es
  la que nunca debió existir. El enlace de la insignia se conserva tal cual, y queda fijado en un
  test: es el enlace correcto, con la semántica correcta detrás.

- **D-392 — El `switch` de la narración está limpio: no había ninguna etiqueta huérfana.** Se
  revisó, porque el parte lo sospechaba con razón —una rama por defecto graciosa habría explicado el
  mismo síntoma—. Los ocho tipos que el motor emite (`nuevo`, `ubicaciones`, y los seis
  `ReconcileOutcome` en minúsculas) tienen su caso, y **no hay `default`**: un tipo desconocido no
  narra nada en vez de narrar cualquier cosa. Se deja un test que fija el repertorio de glifos, para
  que añadir un suceso sin su caso se vea como un glifo inesperado y no como una etiqueta mentirosa.

### §2 — Lo que apareció al revisar el resto de la narración

- **D-393 — «Presente» no se narraba en el caso más común: la pasada seca.** El cierre de pasada
  compone una coletilla con los veredictos («· veredictos: 3 presente») y la añadía **solo** a la
  rama de la pasada con aportación. Una pasada seca —cero nuevos, todo confirmado como presente, que
  es el desenlace normal de una unidad ya auditada— se narraba «Pasada 1 seca — unidad completa» y
  nada más. Tres reconfirmaciones de trabajo real leídas como «aquí no ha pasado nada». No era una
  etiqueta que mintiera, pero es de la misma familia (D-060): un dato sin causa visible. La coletilla
  va ahora en las dos ramas. «Presente» sigue sin línea propia por hallazgo —sería una fila por
  hallazgo y por pasada— y eso es deliberado: se narra **agregado**, que es lo que se puede leer.

- **D-394 — Las ubicaciones añadidas ya se narraban bien**, con su ⊕ y sin contarse como hallazgo
  nuevo. Queda con test para que siga así.

### §3 — El invariante que faltaba

- **D-395 — La narración en vivo y la sesión escrita son el mismo suceso contado dos veces, así que
  sus cuentas tienen que cuadrar.** Es la misma lección de F5.13 (D-379) en otra pantalla: dos
  cálculos paralelos de la misma verdad acaban divergiendo, y el que el usuario ve primero fue el
  falso. El test corre una sesión de verdad con el agente falso a través de `LiveSessionService` y
  afirma, sobre la misma ejecución, que **cada glifo aparece tantas veces como dice su contador**
  (`＋`=New, `⚖`=Disputed, `✔`=Resolved, `⚠`=ResolutionsRefused) y que **cada línea del resumen en
  vivo coincide con la sesión persistida en el hub**, no con lo que la narración creyó ver.
  <br>Una excepción, documentada porque si no parece un fallo: la línea «Confirmados» cuenta
  `Counters.Confirmed`, que suma también los «arreglado» degradados (D-337 bis), mientras su detalle
  solo nombra las reconfirmaciones — los degradados tienen su propia línea y su propio detalle. Las
  demás líneas sí cumplen `Count == Details.Count`, y así queda probado.

- **D-396 — Lo que queda probado.** Que una sesión sin disputas no narra ninguna —ni glifo, ni línea
  de resumen, ni insignia—; que el conversor esconde el cero, la colección vacía, el `false` y el
  nulo, y sigue tratando las cadenas como siempre; que la vista enlaza la insignia al recuento de
  disputas del propio hallazgo; que con una disputa real, un «arreglado» degradado y una
  reconfirmación los glifos cuadran uno a uno con los contadores; que el resumen en vivo cuadra con
  la sesión escrita; que «presente» se narra agregado en el cierre de pasada también cuando la
  pasada es seca; que extender ubicaciones no narra un hallazgo nuevo; y que no se pinta ningún
  glifo fuera del repertorio conocido.

- **D-397 — Lo que se verifica a mano.** Auditar una unidad limpia y comprobar que **ningún**
  hallazgo entrante lleva ⚖ mientras la sesión corre, y que el cierre dice 0 disputados; repetir la
  auditoría sin tocar el código y comprobar que la pasada seca dice «· veredictos: N presente» en vez
  de solo «unidad completa».


## F5.15 — El arranque que murió mudo, y el literal que lo mató

### §1 — Por qué la interfaz se quedó zombi

- **D-398 — Un fallo dejaba la sesión en un estado que la interfaz leía como «no hay nada que
  enseñar».** `LiveSessionService` solo conocía dos estados terminales: corriendo
  (`IsRunning`) y terminada (`HasFinished`). Cuando `CreateSessionAsync` reventó con «Model gpt-5 is
  not available», el `finally` puso `IsRunning = false` y `HasFinished` se quedó en false. De ahí en
  cascada, todo lo que el parte describe:

  | Superficie | Cuelga de | Con el fallo |
  |---|---|---|
  | Item «Sesión en vivo» del rail | `HasSession = IsRunning \|\| HasFinished` | **desaparece** |
  | Botón «Detener» | `IsRunning` | **desaparece** |
  | Pantalla de cierre (la ÚNICA que pinta `StatusMessage`) | `ShowSummary`, que exige `HasFinished` | **no se pinta** |
  | Reloj | `EndedUtc` ya sellado | **congelado en 00:02** |

  El mensaje de error existía —`StatusMessage` se escribía en el `catch`— y **no había ni una
  superficie que lo enseñara**. No fue un error tragado por un `catch` vacío: fue un error escrito
  en una propiedad huérfana. Es la peor variante, porque en el código parece que está resuelto.

- **D-399 — Y retro-explica el «bug 3» de F5.13.** El parte anterior decía que el item de navegación
  había desaparecido y no había forma de volver a la sesión, y la investigación concluyó —con razón—
  que no había ninguna regresión: el item llevaba sin tocarse desde F5.2 y los tests lo probaban. Lo
  que faltaba era esto: **el item no desapareció por un cambio, desapareció porque la sesión había
  fallado**, y una sesión fallida no contaba como sesión. La conclusión «no hay commit culpable» era
  correcta; la causa estaba un nivel más abajo y hacía falta el log de las 12:20:06 para verla. Se
  deja escrito porque cierra un cabo que quedó abierto: D-381 no se equivocaba, se quedaba corto.

- **D-400 — «Fallida» es un tercer estado terminal, con su panel y su toast.** `HasFailed`,
  `FailureMessage` y `FailureOffersModelChange` en el servicio; `HasSession` los incluye, así que el
  rail sigue ofreciendo el camino de vuelta; V5 gana un panel propio colgado de `ShowFailure`
  —`!IsRunning && HasFailed`, que no depende de `HasFinished`— con el mensaje, la aclaración de que
  no se ha gastado nada y el atajo a Ajustes; y un evento `Failed` que la carcasa saca por toast,
  porque quien lanza una auditoría se va a otra pantalla y un error que solo vive en V5 es un error
  que nadie lee. Todos los caminos de fallo pasan por un único método `Fail(...)`, así que no puede
  volver a existir uno que deje el estado a medias.

- **D-401 — El modelo rechazado tiene excepción propia porque tiene REMEDIO propio.**
  `CopilotModelUnavailableException` frente a la genérica: el mensaje nombra el modelo y la vista
  ofrece «Elegir modelo en Ajustes». Un error genérico no puede ofrecer ese enlace, y sin el enlace
  el usuario no sabe que la cura está a dos clics. El SDK no tipa este fallo, así que se reconoce por
  el texto (`LooksLikeModelUnavailable`), exigiendo las dos piezas —que mencione un modelo **y** que
  lo declare no disponible— para no confundirlo con cualquier mensaje que nombre un modelo de pasada.
  Queda cubierto por una tabla de casos, positivos y negativos.

### §2 — El literal que caducó

- **D-402 — Un id de modelo es un dato del proveedor con fecha de caducidad; no puede vivir como
  constante.** `AppSettings.CopilotModel` nacía con `"gpt-5"` escrito a mano. El día que GitHub lo
  retiró, **toda máquina con ajustes vírgenes nació rota**: la primera auditoría moría en
  `session.create`. El valor por defecto pasa a ser **vacío** = «pregúntaselo al runtime».

- **D-403 — `ModelResolver` decide contra la lista real, antes de crear nada.** Al lanzar: si no hay
  modelo elegido, o el guardado ya no figura entre los de la cuenta, se elige uno disponible, **se
  guarda** (para no resolverlo dos veces ni obligar a arreglarlo dos veces) y se avisa por toast. Si
  el guardado sigue vivo, no se toca nada ni se molesta a nadie. Si no se pudo preguntar pero hay un
  modelo configurado, se sigue con él —puede ser válido y el fallo estar en la red—; si no hay
  ninguno, la sesión **no arranca** y lo dice: dejar que el runtime la rechace después solo cambia un
  aviso claro por un fallo feo.

- **D-404 — Elige el PRIMERO que lista el runtime, y no es pereza.** Cualquier preferencia por
  nombre —«los gpt antes que los claude», «evita los mini»— vuelve a meter literales que envejecen
  igual que el que causó el parte. El orden de `ListModelsAsync` es el del propio proveedor para esa
  cuenta: es el único criterio que no caduca. Y como la elección se enseña y se cambia en Ajustes con
  dos clics, equivocarse cuesta un clic, no una avería.

- **D-405 — Y queda un test que impide la recaída: ningún id de modelo en el código de producción.**
  Barre `src/` buscando cadenas con forma de id (`"gpt-`, `"claude-`, `"o3`, `"o4-`, `"gemini-`). Mira
  el **código, no los comentarios**: la documentación tiene que poder contar que el literal era
  `gpt-5` y qué pasó el día que lo retiraron —esa memoria es justamente lo que evita repetirlo—; lo
  que no puede volver es una cadena que el programa ejecute. El test viejo se llamaba
  «El_modelo_por_defecto_es_el_que_se_usa_hoy» y afirmaba `"gpt-5"`: su propio nombre contenía el
  bug, y estaba en verde mientras la aplicación se rompía.

### §3 — El cierre lento

- **D-406 — El agente ya no puede comerse el presupuesto de cierre, pero NO se ha probado que fuera
  él.** El parte apuntaba a que «Cierre: se agotó el tiempo al liberar el host» era el mismo zombi.
  Se revisó el camino de fallo y **no deja nada colgando**: `CreateSessionAsync` lanza antes de crear
  la sesión, así que no hay sesión que liberar; el `finally` borra la marca de sesión abierta; el
  `CopilotClient` es uno por proceso y su vida no depende de que una sesión saliera bien. **No hay
  evidencia de que un arranque fallido cause el cierre lento**, y se deja escrito en vez de declarar
  arreglado lo que no se ha reproducido. Lo que sí se hace es acotar al agente: `DisposeClientAsync`
  espera 5 s como mucho al runtime y sigue. `App.OnExit` reparte 10 s entre todo lo que hay que
  soltar; el agente no puede quedarse con el presupuesto entero. Un cierre lento es molesto; uno que
  no termina deja un Atalaya zombi sondeando el hub (D-086). Si el aviso persiste, ya no es el
  agente, y el siguiente sitio donde mirar es el timer de sondeo.

### Cobertura y verificación

- **D-407 — Lo que queda probado.** Del fallo: que un arranque fallido marca la sesión como fallida
  y la deja VISIBLE —con `HasSession` en true, o sea con camino de vuelta—, con su mensaje nombrando
  el modelo y ofreciendo Ajustes; que V5 lo pinta y retira «Detener»; que la plantilla declara el
  panel, su enlace y su atajo; que un fallo que no es de modelo se ve pero no ofrece un remedio que
  no aplica; que no queda marca de sesión abierta ni reloj corriendo; y que lanzar otra vez limpia el
  fallo anterior. Del modelo: que sin configurar se elige uno y se guarda; que uno retirado se
  sustituye nombrando los dos; que uno válido se deja en paz sin avisar; que sin lista y sin modelo
  se falla diciéndolo y sin inventarse nada; que sin lista pero con modelo se sigue; el circuito
  entero de una máquina virgen que resuelve y audita; y que sin ningún modelo utilizable no se
  arranca. Del reconocimiento: siete mensajes, positivos y negativos, y el caso anidado.

- **D-408 — Lo que se verifica a mano.** Poner en Ajustes un modelo inventado, lanzar una auditoría,
  y comprobar que sale toast, que V5 enseña el panel rojo con el nombre del modelo y el botón a
  Ajustes, que el item del rail SIGUE ahí para volver, y que no se ha auditado nada. Después, borrar
  el modelo de los ajustes (dejarlo vacío) y lanzar: debe elegirse uno solo, avisarlo por toast, y
  auditar con normalidad.


## F5.16 — Ciclo de vida de los hallazgos medidos, y el forense del «desaparecido»

### §0 — Forense: no desapareció, y nada lo borró

- **D-409 — Veredicto: el hallazgo estaba entero, activo y marcado «por revisar». No hubo borrado
  físico.** Se leyó el fichero real del hub
  (`apps/xblast/findings/01M0YVAJ268VBNCFDKA7ZQ3K8F.json`, alias **MEJ-0037**) y dice, literalmente,
  `"status": "activo"`, `"resolved": null`, `"needsReview": true`. Su historial es la secuencia
  completa del incidente:

  | UTC | Evento | Detalle |
  |---|---|---|
  | 2026-08-26 10:54:45 | `detected` | detected via Lotes — 2.983 LOC sobre umbral 1500 |
  | 2026-08-26 10:59:24 | `reopened` | verify: no verificable |
  | 2026-08-26 11:02:21 | `reopened` | verify: no verificable |

  Y el inventario confirma el resto: en el ciclo 1 la unidad medía **2.983 LOC / grande**; en el
  ciclo 2, tras el pull y el re-escaneo, mide **978 LOC / pendiente**, y ha aparecido a su lado
  `ExtensionMethodsNumerics.cs` con 1.703 LOC. O sea: el compañero la troceó, el inventario se
  enteró y **el hallazgo no**. Al usuario le pareció que había desaparecido porque estaba marcado
  «por revisar» y porque lo que sí había desaparecido era su motivo. **La DoD contemplaba «si hubo
  borrado físico, corregir el código que borraba»: no lo hubo, y no hay tal código que corregir.**

- **D-410 — Los dos «no verificable» no fueron un fallo del verificador: fue el instrumento
  equivocado.** «Verificar ahora» ancla el hallazgo por su fragmento —línea 1 del fichero— y le pide
  a un LLM que juzgue el enunciado «esta unidad tiene 2.983 LOC, por encima del umbral». Desde una
  línea no se puede contar un fichero, así que el modelo contestó lo único honrado que podía
  contestar. El daño no fue el veredicto sino su efecto: `no verificable` marca `needsReview`, y el
  hallazgo quedó pidiendo una revisión humana que nadie podía resolver mirando código.

- **D-411 — El barrido completo del hub, porque un caso nunca es un caso.** Cruzando los 37
  hallazgos de tamaño contra el inventario vigente aparecieron **cuatro** activos cuya unidad ya no
  es grande —MEJ-0005 (556 LOC), MEJ-0024 (968), MEJ-0026 (557) y MEJ-0037 (978)— y **una** unidad
  grande sin hallazgo ninguno (`ExtensionMethodsNumerics.cs`, 1.703 LOC). No era un hallazgo
  descolgado: era el ciclo de vida entero que no existía.

- **D-412 — La reparación la hace el mecanismo, no una edición a mano.** El hub es un clon de git
  compartido con el equipo: editar sus JSON desde fuera de la aplicación es exactamente la clase de
  escritura silenciosa que este proyecto no admite —sin autor, sin historial, sin commit explicable—.
  MEJ-0037 se repara **en el primer re-escaneo con esta versión**, por el camino que además impide
  que vuelva a pasar. Se comprobó en seco contra los datos reales: resolverá los cuatro con su
  medida, limpiará el `needsReview` de MEJ-0037 y creará el de `ExtensionMethodsNumerics.cs`.

### §1 — La regla

- **D-413 — Cada hallazgo se verifica con el INSTRUMENTO que lo detectó.** Es la regla general que
  este incidente destapó. Los hallazgos de «unidad demasiado grande» no los encuentra el auditor:
  los calcula la aplicación comparando LOC y caracteres contra el umbral de la app (§4, mejora 7).
  Lo que detecta una medida se verifica midiendo, se resuelve midiendo y se reabre midiendo — nunca
  preguntando a un modelo, nunca por omisión, y siempre con el número escrito en el historial.
  `UnitMeasure.MeasuredRuleIds` es la lista de reglas medidas; hoy tiene una, y el mecanismo es de la
  clase y no de la regla, así que añadir la siguiente es añadirla ahí y nada más.

- **D-414 — La condición de «grande» vive en UN sitio.** `UnitMeasure` está junto a
  `InventoryScanner` y cuenta líneas con `CountLinesOf`, el mismo método que el escáner. Si el
  instrumento que crea el hallazgo y el que lo resuelve contaran distinto, una unidad podría salir de
  «Grandes» en el inventario y quedarse con su hallazgo activo — que es literalmente la mitad de este
  parte. Y el umbral es **LOC o caracteres**: por eso la frase nombra el criterio que de verdad
  decide, y una unidad de 1.269 líneas pero muy pesada se confirma diciendo «N caracteres ≥ umbral
  60000 (1269 LOC)» en vez de mentir con un número correcto.

### §2 — El ciclo de vida

- **D-415 — El re-escaneo actualiza el inventario Y sus hallazgos, en el mismo gesto y con un solo
  push.** `InventoryRescanService` llama a `MeasuredFindingService.Reconcile` sobre **el inventario
  que acaba de escribir** —no volviendo a leer el disco—, así que las dos listas no pueden discrepar
  ni por una carrera ni por un umbral leído dos veces. Por debajo del umbral → resuelto con evidencia
  medida (`re-escaneo {fecha}: N LOC < umbral M, commit del clon X`); por encima y sin hallazgo →
  creado; por encima y ya resuelto → **reabierto el mismo**, nunca uno nuevo.

- **D-416 — Una unidad que no está en el inventario NO se da por resuelta.** Puede haberse movido,
  renombrado o excluido, y «no la he medido» no es «ya no es grande». Se deja intacta. Es la misma
  disciplina que `SnippetAnchor`: «no localizado» nunca se confunde con «resuelto» (§5.4).

- **D-417 — Nace en modo LOTES, y el dominio lo impuso.** El primer intento creó los hallazgos con
  `AuditMode.Verify` y `ConfidenceMachine.ForNew` lo rechazó en voz alta: «Mode 'Verify' never
  creates new findings». Tenía razón — verificar comprueba lo que hay, no inventa— y además el modo
  correcto es el que ya usaba el escaneo inicial, que es lo que da a estos hallazgos su confianza
  media. Queda anotado porque es un buen ejemplo de una guarda del dominio ganándose el sueldo en el
  primer test.

- **D-418 — La resolución tiene vía propia: `ResolutionVia.Medida`.** No es `Verify` porque no hubo
  veredicto de nadie: hubo un número. Quien lea el hallazgo dentro de un año tiene que poder
  distinguir «un auditor dijo que estaba arreglado» de «la aplicación lo contó».

- **D-419 — Y se NARRA.** El re-escaneo devuelve `RescanOutcome` con el detalle, y el toast lo dice:
  «2 unidades salieron de Grandes; sus hallazgos se resolvieron». Una resolución silenciosa es
  indistinguible de un borrado — es literalmente lo que hizo pensar al usuario que su hallazgo había
  desaparecido. Sin cambios, no se anuncia nada.

### §3 — «Verificar ahora» sobre un hallazgo medido

- **D-420 — Se desvía antes de llegar al agente, y no gasta un token.** `VerifyCoordinator` reparte
  por clase de hallazgo: lo medido va a `MeasuredFindingService.Verify`, que lee el fichero del clon
  y responde con el número —«Confirmado: 2000 LOC ≥ umbral 1500» o «Resuelto: 300 LOC < umbral
  1500»—. **«No verificable» deja de existir para esta clase.** Un confirmado refresca la última
  confirmación sin tocar la confianza (verify nunca asciende, §0); un resuelto sella la medida.
  Queda probado con un agente que lanza excepción si alguien lo llama.

- **D-421 — Sin clon se dice, y no se toca nada.** «No se puede medir sin el clon local —
  vincúlalo». La incertidumbre se declara, como siempre. Y lo importante: **NO marca `needsReview`**.
  Ensuciar el hallazgo por no poder medirlo sería repetir el error que abrió el parte.

- **D-422 — Una medida limpia el `needsReview` anterior.** La marca significaba «el instrumento
  equivocado no supo verificar esto»; cuando la medida responde —en cualquiera de los dos sentidos—
  esa duda ya no existe, y dejarla puesta manda al usuario a revisar a mano algo que la aplicación
  acaba de contar. El historial conserva las dos entradas: que se dudó y que se midió.

- **D-423 — Y el botón dice lo que hace: «Medir ahora».** Con su línea de ayuda («No consulta al
  auditor ni gasta tokens»). Llamarlo igual que a una verificación por LLM hace esperar una llamada
  al modelo y una espera larga donde solo hay una lectura de fichero.

### Cobertura y verificación

- **D-424 — Lo que queda probado.** El caso real, reconstruido entero: un hallazgo de tamaño con dos
  «no verificable» encima cuya unidad baja a 978 LOC queda **resuelto vía `Medida`**, con
  «978 LOC < umbral 1500» en la justificación y sin `needsReview`. Del re-escaneo: que resuelve por
  debajo del umbral y lo narra; que crea el de una unidad nueva que lo supera; que **reabre el mismo**
  cuando vuelve a crecer, sin duplicar; que sin cambios no anuncia nada; que una unidad ausente del
  inventario no se da por resuelta; y que no toca los hallazgos del auditor. De la verificación: que
  confirma con el número, que resuelve con el número, que una unidad grande **por caracteres** se
  nombra por caracteres, que sin clon dice cómo arreglarlo sin ensuciar nada, que una unidad que ya
  no está no se da por resuelta, y que un resuelto que volvió a crecer se reabre. Del desvío: que un
  hallazgo medido **no llega al agente** y que uno del auditor sigue llegando. De la ficha: que la
  acción se llama «Medir ahora» cuando mide.

- **D-425 — Lo que se verifica a mano.** Abrir xblast y pulsar «Re-escanear». El toast debe decir
  «4 unidades salieron de Grandes; sus hallazgos se resolvieron · 1 unidad nueva supera el umbral:
  hallazgo creado». Después: **(a)** MEJ-0037 (`ExtensionMethods.cs`) figura en Resueltos con
  «978 LOC < umbral 1500» y sin la marca «Por revisar»; **(b)** abrir un hallazgo de una unidad que
  sigue siendo grande —por ejemplo `UgUtils.cs`, 4.836 LOC— y pulsar «Medir ahora»: responde
  «Confirmado: 4836 LOC ≥ umbral 1500», nunca «no verificable»; y **(c)**
  `ExtensionMethodsNumerics.cs` (1.703 LOC) aparece con su hallazgo recién creado.

## F6.1 — Resoluciones en el tiempo: la segunda gráfica de línea del panel

Tras la demo, el equipo pidió ver **deuda saldada** con la misma forma con la que ya ve el
gasto: una línea por aplicación sobre el eje temporal, justo debajo de la de coste, para
poder leer las dos en pareja («esto costó, esto saldó»).

- **D-426 — La gráfica cuenta EVENTOS de resolución, no el neto.** Un hallazgo que se
  resolvió, se reabrió y se volvió a resolver saldó deuda **dos veces**, y las dos veces
  fueron trabajo hecho. Restar del pasado lo que se reabre convertiría esta gráfica en el
  burndown, que ya existe (la línea «Activos al cierre» de *Flujo de hallazgos*) y responde
  a otra pregunta. Dos gráficas que respondieran lo mismo con distinto dibujo serían una de
  más.

- **D-427 — Y por eso la fuente es el HISTORIAL, no el campo `resolved`.** `Finding.Reopen`
  pone `Resolved` a null: el sello solo conoce la resolución **vigente**, así que contar por
  él borraría del eje todas las resoluciones que alguna vez se reabrieron — exactamente los
  casos que D-426 quiere ver. `MetricsQuery.ResolutionEvents` recorre las entradas
  `FindingEvent.Resolved` del historial, que son inmutables y están todas. El sello queda de
  **reserva** para hallazgos sin historial (los importados de V4): sin ella, un hub migrado
  dibujaría una gráfica vacía teniendo resoluciones.

- **D-428 — Cuentan las cuatro vías, sin distinguir.** Veredicto del auditor con evidencia,
  resolución manual de gobernanza y medida de la aplicación (`ResolutionVia.Medida`, D-418)
  aportan lo mismo al mismo punto. La gráfica mide deuda saldada, no de quién fue el mérito;
  quien quiera la vía la tiene en la ficha del hallazgo, que es donde se puede leer con su
  justificación.

- **D-429 — El tile «Resueltos en el periodo» y la gráfica pueden NO coincidir, y está
  bien.** El tile cuenta estado de hoy (hallazgos que hoy están resueltos con su sello en el
  periodo); la gráfica cuenta eventos. Un hallazgo resuelto y reabierto suma 1 punto a la
  gráfica y 0 al tile. Son dos preguntas distintas —«cuántos están cerrados» y «cuánto se
  cerró»— y cuadrarlos a la fuerza exigiría mentir en una de las dos. El test lo fija
  explícitamente para que nadie lo «arregle» dentro de seis meses.

- **D-430 — Se extrajo el genérico en vez de duplicar, en las tres capas.** El encargo decía
  «reutiliza el componente». `ChartPlot` ya era agnóstico, pero el camino del dato no:
  - `CostPoint` → **`SeriesPoint`**: un punto del eje X con lo que aportó cada app. El tipo
    no sabe qué mide.
  - `CostSeries(...)`/`CostPoints(...)` → **`TopSeries(scope, totalOf)`** y
    **`Points(scope, buckets, series, hasOthers, valueOf)`**: los cubos, el reparto de las
    seis con nombre propio y el agrupado en «Otras» se hacen UNA vez, y quien llama solo
    aporta de dónde sale el número.
  - En el view-model, `RebuildCostChart` cedió su cuerpo a **`LineChart(...)`**, que arma las
    series con su color de identidad, su leyenda que nombra y el acumulado opcional.
  El beneficio es concreto: el día que una app cambie de color, o que «Otras» deje de ir a
  trazos, no hay dos sitios que acordarse de tocar. Y es lo que hace **gratis** el requisito
  de que una app tenga el mismo color en las dos gráficas — no se comprueba, se deriva.

- **D-431 — «Acumulado» es un interruptor PROPIO (`CumulativeResolutions`), no el de coste.**
  Cada gráfica es una tarjeta con su cabecera. Un interruptor en la tarjeta de arriba que
  cambiara la forma de la de abajo sería un mando a distancia: el usuario que lo pulsa está
  mirando la gráfica que tiene al lado. Mismo gesto, misma etiqueta, ámbito de su tarjeta.

- **D-432 — Sin resoluciones se escribe por qué, y no es «sin datos».** «— · aún no hay
  resoluciones en este periodo» aparece en lugar de un eje mudo, y **no** enciende el estado
  vacío del panel entero: puede haber hallazgos activos y sesiones con coste, y decir que no
  hay nada que medir sería falso. Es la misma regla que la nota de la gráfica de coste.

- **D-433 — MANUAL.md no existe en este repositorio.** La DoD pedía una línea en su
  «§ Métricas»; no hay tal fichero ni lo ha habido (`git log` no registra ningún borrado), y
  la documentación de usuario vive en `README.md`. La línea se ha puesto ahí, en «Flujos»,
  junto al resto de vistas. Queda anotado para que la próxima tanda no lo busque otra vez —
  o cree el manual a conciencia, que es una tarea con su propio tamaño y no un renglón.

- **D-434 — Lo que queda probado.** Del agregador: que reparte por semana y por aplicación
  con el mismo grano y las mismas etiquetas de eje que la de coste; que **una reapertura no
  resta** y la segunda resolución cuenta aparte (con el tile marcando 1 al lado de los 2
  puntos, D-429); que cuentan las tres vías vivas; que obedece el filtro de aplicación y el
  de rango —incluida la resolución que se cae de la ventana de 4 semanas—; que sin
  resoluciones no hay gráfica pero tampoco panel vacío; que con más de seis apps agrupa en
  «Otras» sin perder ni una; y que un resuelto **sin historial** cuenta por su sello. Del
  panel: que una app lleva el **mismo color** en las dos gráficas, que el acumulado de
  resoluciones sube y **no toca** la serie de coste, y que la nota de vacío está escrita.
  De la vista: tres `ChartPlot` y ni una más, y la de resoluciones **entre** la de coste y la
  de cobertura.

## F6.3 — Informes (V7): buscar, leer y descargar lo que las auditorías escribieron

Los informes existían desde §7 y no había forma de verlos. El único camino era pulsar
«Ver informe» en la última sesión, que los lanzaba a la aplicación que el sistema
asociara al `.md` — es decir, fuera de Atalaya y en markdown crudo. Lo escrito estaba,
y era ilegible.

### §1 — La lista

- **D-435 — La fuente son los FICHEROS de `reports/`, no la lista de sesiones.** Es la
  decisión que ordena todo lo demás. Enumerar sesiones habría dado una lista con filas
  que no llevan a ninguna parte —una sesión que reventó antes de escribir su informe— y
  sin las que sí llevan: los informes que trae el importador de v4 no tienen sesión
  ninguna. La sesión se usa para ENRIQUECER lo que se sabe de cada fichero, nunca para
  decidir si aparece. `ReportsQuery.All()` recorre `ListReports(slug)` y busca la sesión
  por el nombre del fichero, que es el ULID de la sesión cuando lo hay.

- **D-436 — Sin sesión detrás, lo que se enseña es lo que el informe declara de sí
  mismo.** `ReportHeader.Parse` lee el `# título` y los campos `- **Fecha**`,
  `- **Autor**` y `- **Modo**` de las primeras treinta líneas — no el fichero entero,
  que para una lista de doscientos informes sería leerlos todos para pintar una tabla.
  Lo que no declare se queda a **null**, y la fila escribe «—». Escribir «0 unidades»
  sobre un informe que nunca dijo cuántas procesó es inventarse una medida (D-318).

- **D-437 — La fecha dice de dónde sale.** Cuatro procedencias, en este orden: la de su
  sesión, la que el propio informe escribe, la que lleva dentro el ULID de su nombre y,
  en último término, la del fichero en este clon. `ReportDateSource` viaja con la fila y
  el tooltip lo dice. Ordenar una lista por una fecha sin saber cuál de las cuatro es
  sería ordenarla por algo que el usuario no puede juzgar (N-2).

- **D-438 — Tres tipos, y el que no se sabe NO se hace pasar por sesión.** El cierre de
  ciclo escribe un **consolidado**; el reset es **operaciones**; lo demás —lotes, verify
  y los modos retirados que siguen vivos en los informes antiguos— es **sesión**. Un
  informe sin sesión y sin título reconocible se queda en «operaciones», que es la clase
  que no afirma nada: llamarlo «sesión» sería decir de él algo que nadie ha dicho.

- **D-439 — La búsqueda mira DENTRO del informe, y sin indexar nada.** Es la razón de
  ser del cuadro de búsqueda: «busca ReadCSV» tiene que dar con el informe que lo
  menciona sin saber de qué sesión salió. Son markdowns de decenas de kB en disco local,
  así que un `contains` normalizado sobre el contenido —cacheado bajo demanda, no al
  listar— es exactamente la herramienta del tamaño del problema. Montar un índice sería
  añadir un estado derivado que mantener sincronizado para ahorrar milisegundos que
  nadie está esperando (anti-objetivo del encargo).

- **D-440 — Y normaliza tildes y mayúsculas (`TextSearch`).** Lo que hay dentro de un
  informe lo escribió un modelo en español: «duplicación», «sesión», «análisis». Un
  `Contains` a secas obliga a teclear la tilde exacta de lo que uno tiene delante en la
  pantalla, que es una forma tonta de no encontrar nada.

### §2 — El visor

- **D-441 — Markdig ANALIZA; el dibujo es nuestro.** El encargo daba libertad y pedía
  decidir por calidad de tablas y coste de dependencia. Las tres opciones y por qué
  esta:
  - **Markdig.Wpf** (0.5.0.1, sin tocar desde hace años) trae su propio diccionario de
    estilos fijos. No sigue el tema de la aplicación —habría que pelearse con él en cada
    clave— y las tablas son justo su parte floja. Un tema mal en la mitad de los
    arranques no es un detalle: es la mitad de los arranques.
  - **WebView2** significa meter un navegador fuera de proceso, con su runtime que hay
    que tener instalado, para leer un fichero de texto de 20 kB — y volver a escribir el
    tema entero en CSS, es decir, mantener dos paletas que tienen que coincidir.
  - **Markdig + recorrido propio a `FlowDocument`** es lo que se hizo. Analizar markdown
    a mano sería un error (es un formato con esquinas y Markdig es el analizador de
    referencia), pero dibujar es trivial y es donde están nuestras reglas: la
    `System.Windows.Documents.Table` nativa hace reparto REAL de columnas —que es lo que
    estos informes necesitan— y los pinceles salen del tema por `DynamicResource`, así
    que claro y oscuro salen bien sin una segunda paleta. De regalo, el texto es
    seleccionable y el `Ctrl+F` del sistema funciona sobre él.

- **D-442 — Ni un color escrito a mano, y hay un test que lo vigila.** El renderizador
  pide `TextFillColorPrimaryBrush`, `CardStrokeColorDefaultBrush` y compañía con
  `SetResourceReference`, así que siguen al tema aunque cambie con la página abierta. El
  test barre el fuente buscando `#RRGGBB` y no puede haber ninguno — es la misma
  disciplina de F5.9 §2 y de `ButtonForegroundTests`, aplicada al documento.

- **D-443 — Lista y visor viven en el MISMO view-model.** «Volver» tiene que devolver la
  lista con sus filtros Y su scroll, y eso solo es gratis si la lista nunca se destruyó:
  el visor se enseña ENCIMA (la lista se oculta, no se descarga). Dos páginas separadas
  habrían obligado a serializar el estado del filtro para restaurarlo, que es la clase
  de código que acaba perdiendo un campo en la sexta iteración.

- **D-444 — Los enlaces del informe salen FUERA.** El renderizador no navega: entrega la
  URL a quien lo llamó, y el view-model la abre en el navegador del sistema tras
  comprobar que es `http`/`https`. Atalaya no es un navegador y un informe no es una
  página web dentro de la ventana. Un esquema raro se dice y no se ejecuta.

- **D-445 — La descarga copia el fichero, no lo vuelve a generar.** `File.Copy` del
  `.md` tal cual. Reconstruir el markdown para guardarlo abriría la puerta a que lo
  descargado y lo publicado dejaran de ser el mismo documento — y estos son inmutables
  por diseño. El nombre por defecto es `atalaya-{app}-{tipo}-{fecha}.md`: dice qué es
  sin abrirlo y ordena solo en la carpeta de descargas.

### §3 — Un solo camino para ver un informe

- **D-446 — Se retiran los DOS atajos que había.** «Ver informe de sesión» en V5 hacía
  `Process.Start` sobre el `.md`; el registro de operaciones de Métricas usaba
  `IFileOpener` para lo mismo. Eran dos formas distintas de leer lo mismo, las dos
  fuera de la aplicación y las dos en crudo. Ahora los dos navegan a Informes con
  `ShowReport(slug, id)`. `MetricsViewModel` pierde su dependencia de `IFileOpener`
  —quedarse con ella sin usarla es cómo se acumulan los atajos— y conserva
  `ReportPathFor`, que sigue haciendo falta para saber si la fila tiene informe.

- **D-447 — Un enlace a un informe que no está se DICE.** Puede pasar: una sesión que no
  llegó a escribirlo, o un pull que se llevó el fichero. La vista se queda en la lista y
  avisa. Quedarse callado es indistinguible de que el botón no funcione, que es
  exactamente el bug que abrió F5.15.

- **D-448 — «Ver hallazgos de esta sesión» solo en informes de SESIÓN.** Un consolidado
  de cierre o un reset no hablan de una tanda concreta de hallazgos; el botón lleva a
  Hallazgos filtrado por la aplicación, que es todo lo que el dato soporta afirmar.

### §4 — Cobertura

- **D-449 — Lo que queda probado (31 tests).** De la lista: que solo salen las sesiones
  con informe y que las que no lo tienen siguen en el hub; que los metadatos salen de la
  sesión; el orden; que cierre y reset llevan su insignia y que verify también es
  sesión; que un informe sin sesión se describe por su cabecera —autor sin la máquina,
  fecha parseada, tipo por el título— y deja en «—» lo que no declara; y que uno sin
  título reconocible no se hace pasar por sesión. De los filtros: que se combinan; que
  el rango recorta por los dos extremos; que la búsqueda encuentra por CONTENIDO, por
  aplicación y por autor, y que le dan igual las tildes. Del view-model: que arranca sin
  filtros, que «Limpiar» los devuelve, que el rango a mano incluye el día de su extremo,
  que el estado vacío distingue un hub vacío de un filtro estrecho, y que un informe
  publicado por otro aparece al recargar. Del visor: que abre, renderiza, y que
  **volver conserva el filtro**; que un consolidado no ofrece el enlace a hallazgos; que
  ese enlace abre V3 filtrado por su app. Del render: que una tabla sale como tabla —con
  sus tres columnas, su cabecera en negrita y la alineación de los guiones respetada—,
  que reconoce encabezados, listas y código, que un enlace se entrega a quien lo abre
  fuera, y que un informe vacío no revienta nada. De la descarga: el nombre por defecto,
  que copia el markdown byte a byte y lo anuncia, y que cancelar no escribe ni avisa. Y
  del tema: que no hay un solo color a mano ni en el renderizador ni en la vista.

- **D-450 — Y un test que caza lo que el compilador no.** Un `StaticResource` con una
  clave inexistente compila y revienta al abrir la página, delante del usuario.
  `Todas_las_claves_que_pide_la_vista_existen` cruza las claves que usa `ReportsView`
  contra las declaradas en ella y en `App.xaml`. Es barato y cubre el único fallo de
  esta vista que los demás tests no verían.

- **D-451 — MANUAL.md, creado.** En F6.1 se anotó que no existía (D-433) y la DoD lo
  volvió a pedir, ahora con dos secciones. Se escribe entero: qué hace cada vista, en
  qué orden se usan y qué significa lo que enseñan — con Informes y con el enlace desde
  Métricas en su sitio. Es documentación de USUARIO y por eso vive aparte del README,
  que es de instalación y arquitectura.

## F6.4 — Identidad visual: el icono de la aplicación y la marca corporativa

Tanda de identidad. Casi todo lo que sigue es una decisión sobre **dónde NO** poner algo.

### §1 — El icono

- **D-452 — Dos SVG, no uno escalado.** Reducir el icono de 256 a 16 px no da un icono
  pequeño: da una mancha. El halo de la luz —un `radialGradient`— se convierte en suciedad
  alrededor de la torre, el degradado del fondo se lee como un gris plano y la tronera, que
  mide 12 unidades de ancho, desaparece. `atalaya-icon-small.svg` es el MISMO icono dicho
  más alto: silueta, sin halo, sin degradado, sin tronera, con la luz más grande porque es
  lo que hace que esto sea una atalaya y no una torre cualquiera. Los tamaños 16 y 24 salen
  de él; 32, 48, 64 y 256 del grande.

- **D-453 — La regla de las unidades, que es lo que hizo falta rehacer.** El primer intento
  del pequeño «simplificaba» a ojo y salió peor que el grande a 24 px: los merlones se
  fundían en un bloque con mordiscos. Con el viewBox de 256, a 16 px **un píxel son 16
  unidades**, así que todo rasgo que deba verse mide 16 como mínimo y 32 cuando es el que da
  la lectura. De ahí los números del fichero: merlones de 32 (2 px) separados por muescas de
  16 (1 px), muescas de 28 de profundidad, base de 28 de alto y 26 unidades de aire entre la
  luz y las almenas — con menos, la luz y la torre se funden en una sola mancha, que es
  exactamente lo que pasaba.

- **D-454 — El .ico se escribe a mano, y por eso puede ser multi-tamaño.**
  `System.Drawing.Icon` sabe leer un .ico pero no componer uno de seis imágenes: guardarlo
  con él habría dado un fichero de UN tamaño, que es justo lo que no se quiere. El
  contenedor son 6 bytes de cabecera y 16 por entrada, está publicado, y escribirlo permite
  además elegir la codificación por tamaño: **DIB de 32 bits hasta 64 px** —lo que espera
  todo el shell, incluidos los diálogos viejos— y **PNG en el de 256**, como se hace desde
  Vista, que evita que el fichero pese 256 kB de más. La máscara AND va aunque el mapa
  lleve alfa: hay rutas del shell que la leen y sin ella el icono sale con un rectángulo
  negro detrás.

- **D-455 — La herramienta de assets vive FUERA de la solución.** `scripts/IconGen/` no
  está en `Atalaya.sln` y no se despliega: se ejecuta a mano con `scripts/build-assets.ps1`
  cuando cambia un SVG. Compilar Atalaya no puede depender de tener un renderizador de SVG
  instalado, y por eso el `.ico` **se versiona** aunque sea un artefacto derivado. Es la
  excepción razonada a «en el repo solo fuentes»: la alternativa era una dependencia de
  build para todo el equipo a cambio de 48 kB.

- **D-456 — SVG.NET y no Svg.Skia.** Los dos rasterizan; SVG.NET es puro gestionado y
  Svg.Skia arrastra binarios nativos por plataforma. Para una herramienta que cualquiera del
  equipo tiene que poder ejecutar con solo el SDK, «no hay nada que instalar» gana a
  «renderiza un 2 % mejor». Y el SVG que hay que dibujar son cuatro paths y dos degradados.

- **D-457 — El icono va COMPILADO como recurso, no como fichero suelto.** La ventana, el
  Alt-Tab y la barra de tareas lo piden siempre; que dependieran de un `.ico` que alguien
  puede borrar de la carpeta sería regalar un fallo. `ApplicationIcon` cubre el ejecutable
  —Explorador y accesos directos— y el `<Resource>` cubre la ventana.

- **D-458 — Y la ruta del recurso va en MINÚSCULAS, porque no da igual.** Con
  `Link="Assets\atalaya.ico"` el recurso se guarda como `Assets/atalaya.ico`, y la
  resolución de un `pack://` pasa la ruta a minúsculas antes de buscar: `assets/...` no
  casa con `Assets/...` y la ventana revienta al abrirse. **Lo cazó un test**, que era
  exactamente para lo que se escribió — un `pack://` roto compila igual de bien que uno
  correcto.

- **D-459 — El aviso lo firma la aplicación.** Los toasts de Atalaya son NUESTROS (no los
  del sistema), así que llevan el mismo `.ico` a 16 px: el tamaño para el que existe la
  variante de silueta. No es marca corporativa — es el icono de quien habla.

### §2 — La marca corporativa, y sobre todo dónde no

- **D-460 — El logotipo no se toca, y hay un test que lo comprueba byte a byte.** Lo único
  permitido era preparación técnica: dejar el fondo en transparencia. La fuente que entregó
  comunicación **ya venía con canal alfa**, así que el paso correcto era **no hacer nada**:
  `maxam-logo.png` es una copia literal de `maxam-logo-source.png`, sin volver a codificar.
  Volver a guardar un PNG «igual» ya es tocarlo. La herramienta sabe quitar un fondo blanco
  si algún día llega una fuente que lo tenga, y no sabe hacer nada más.

- **D-461 — El problema del tema oscuro se resuelve por DEBAJO.** Las letras del logotipo
  son `#51555A` y sobre el fondo oscuro de la aplicación no se leen. Recolorearlas no es
  decisión de este equipo —los manuales de marca no lo permiten—, así que lo que cambia es
  el papel: una placa clara (`#F4F5F7`) redondeada y con aire. Se comprobó en render: sin
  placa, el logotipo sobre oscuro queda a un paso de ser ilegible; con ella, nítido. En
  tema claro la placa es **transparente** y desaparece.

- **D-462 — La placa lleva un color LITERAL, y es a propósito.** Los tokens del tema se
  oscurecen en oscuro, que es exactamente lo contrario de lo que esta superficie tiene que
  hacer. No es «tocar la paleta de la aplicación» (anti-objetivo): es el papel bajo una
  firma, y su trabajo es no seguir al tema.

- **D-463 — La versión en negativo se resuelve en DISCO, no compilada.**
  `maxam-logo-dark.png` todavía no existe: compilarla como recurso obligaría a tenerla para
  poder construir. Buscándola junto al ejecutable, el día que comunicación la entregue basta
  con dejarla caer en `assets/` y el tema oscuro la usa sola, sin placa, sin recompilar y
  sin tocar una línea. El comodín del csproj acepta que no haya ninguna.

- **D-464 — Sin asset, el hueco DESAPARECE.** Ni marco vacío, ni interrogante, ni traza de
  error: un despliegue sin marca es una situación normal. `BrandAssets.Resolve` devuelve
  null y `BrandMark` se colapsa. Es la diferencia entre un hueco preparado y un hueco roto,
  y está probada en las cuatro combinaciones de assets presentes.

- **D-465 — Tres emplazamientos, y un test que los cuenta.** La bienvenida (debajo del
  bloque de conexión: quien abre la aplicación por primera vez viene a conectarse), la
  página Cuenta (junto a la organización, que es de lo que ahí se habla) y el «Acerca de».
  El test comprueba las apariciones exactas en esos dos XAML **y que no haya ninguna** en el
  rail, la sesión en vivo, Hallazgos, Métricas, Informes, Inventario, Portafolio, Ajustes ni
  el alta. Es el test que impide que dentro de seis meses haya un logo en la barra lateral:
  la contención no se sostiene sola.

- **D-466 — El pie del informe es una LÍNEA, no un membrete.** `Atalaya · {organización}`
  tras un separador. En texto, porque el markdown tiene que seguir siendo legible en
  cualquier visor, incluido un `cat` en una terminal — y porque el visor de F6.3 renderiza a
  `FlowDocument`, no a HTML, así que no hay dónde meter una imagen sin inventarse un formato.
  Sin organización se firma solo «Atalaya»: escribir «Atalaya ·» y nada detrás sería enseñar
  el hueco de un dato que el hub todavía no da. **Los informes ya publicados no se tocan**:
  son inmutables, y la firma aparece en los que se generen a partir de ahora.

- **D-467 — El nombre de la organización se lee de UN sitio.** `HubContext.OrganizationName`.
  Lo necesitan la firma del informe y el «Acerca de», y un dato leído de dos sitios distintos
  acaba diciendo dos cosas distintas. Nunca lanza: sin clon no hay organización, y eso no es
  un error.

### §3 — «Acerca de»

- **D-468 — La versión sale del ENSAMBLADO, no de una constante.** `AboutInfo.CurrentVersion`
  prefiere la versión informativa —la que un despliegue puede sellar con un sufijo `+sha`— y
  se cae a la del fichero y a la del ensamblado. Un número escrito en un XAML es un número
  que se queda viejo, y el test lo compara contra el ensamblado vivo.

- **D-469 — El diálogo va antes de la zona peligrosa.** Lo último de una página no puede ser
  algo que no da miedo: la zona peligrosa se queda al final, donde estaba, y «Acerca de» se
  cuela justo encima.

### Cobertura

- **D-470 — Lo que queda probado (18 tests).** Del icono: que el `.ico` trae los seis tamaños,
  todos con imagen dentro, a 32 bits y con entradas que apuntan dentro del fichero; que la
  variante pequeña soltó de verdad halo, degradado y tronera y que la grande los conserva; que
  las dos son el mismo icono (mismo lienzo, mismos colores de marca); que está declarado en el
  ejecutable, en la ventana y en el aviso; y que **el recurso existe en el ensamblado con la
  ruta exacta que se pide**. Del logotipo: las cuatro combinaciones de assets —ninguno, solo
  el normal, los dos, solo el negativo— con su placa o sin ella; que lo desplegado es byte a
  byte lo de comunicación; y que viaja junto al ejecutable mientras la fuente se queda fuera.
  De la contención: los tres emplazamientos contados y los nueve prohibidos comprobados. De la
  firma: que el informe de sesión y el consolidado la llevan, y que sin organización no queda
  un separador colgando. Del «Acerca de»: que la versión es la real, que sin organización no
  se pinta un bloque vacío y que Ajustes lo abre con los datos del hub. Y del pipeline: que el
  script existe, conoce las dos fuentes y que la herramienta NO está en la solución.

- **D-471 — Y un test ajeno que hubo que ajustar.** `The_brand_is_written_once` contaba las
  apariciones de la palabra «Atalaya» en `MainWindow.xaml` para vigilar que la marca se
  escribe una sola vez. Las rutas `pack://…/assets/atalaya.ico` la hacían fallar sin que la
  regla se hubiera roto: una ruta de recurso no es marca escrita, nadie la lee en pantalla.
  Se descuentan antes de contar. La regla sigue siendo la misma; lo que se afinó es la forma
  de medirla.

- **D-472 — Lo que se verifica a mano.** Abrir la aplicación y mirar cuatro sitios: la barra
  de título, el Alt-Tab, la barra de tareas y el Explorador sobre `dist/Atalaya.exe`. Y con
  el tema oscuro puesto, que el logotipo de Cuenta se lee sobre su placa. El render de
  contraste está hecho y adjuntado; lo que no se puede automatizar es que a alguien le
  parezca bien.

### §4 — Enmiendas de la misma tanda (la negativa llegó, y el logo sube a la barra de título)

- **D-473 — La versión en negativo llegó y no hizo falta tocar ni una línea de lógica.**
  Era la prueba de D-463: `maxam-logo-dark.png` se deja en `assets/` y el tema oscuro la usa
  sola. Lo único que se escribió fue el **paso de preparación de su fuente**, idéntico al de
  la variante normal —`maxam-logo-dark-source.png` → copia byte a byte, porque también viene
  con canal alfa—, porque que una variante sea opcional no la convierte en un caso aparte.

- **D-474 — La placa clara deja de verse, y su rama se queda.** Con la negativa presente,
  `Resolve(dark: true)` devuelve `NeedsPlate: false` y la placa no se pinta nunca en este
  despliegue — que es lo que se pedía. La RAMA sigue en el código porque sigue siendo la
  respuesta correcta a un despliegue sin ese fichero, y está probada. Borrarla habría
  cambiado «no se ve» por «un logotipo ilegible el día que alguien despliegue sin el asset».

- **D-475 — A la izquierda de «Atalaya» va el icono de la APLICACIÓN, no el logotipo.**
  Se pidió «el logo al lado del texto Atalaya» y se entendió mal: se puso ahí el logotipo de
  Maxam, y lo que se quería era el icono de casa. La lista de emplazamientos de la marca
  vuelve a ser de TRES —bienvenida, Cuenta, «Acerca de»— y D-465 queda intacta. El icono en
  la barra de título no es un emplazamiento de marca corporativa: es lo que Windows pone en
  esa esquina en cualquier ventana.

- **D-476 — Y al final, NADA al lado del texto.** Se probó el icono de la aplicación en el
  hueco nativo de la barra (`ui:TitleBar.Icon`) y no convenció: en esa esquina no aporta nada
  que no diga ya la palabra, y la barra se lee mejor limpia. Windows lo sigue enseñando donde
  hace falta —barra de tareas, Alt-Tab y Explorador— desde `Window.Icon` y `ApplicationIcon`,
  que son los que de verdad lo colocan. Queda anotado porque el hueco es tentador y alguien
  volverá a proponerlo: se intentó, se miró y se quitó.

- **D-476b — Y `DecodePixelWidth` NO es decorativo.** Un `.ico` pedido con
  `Source="pack://…"` a secas se decodifica por su fotograma **más grande** y se encoge: los
  16 px de la barra de título y del aviso salían del dibujo de 256, que es exactamente el
  borrón que la variante de silueta existe para evitar (D-452). Sin esto, los dos SVG del
  pipeline no servían de nada en pantalla — el fotograma de 16 estaba en el fichero y no lo
  veía nadie. Los tres usos pasan a `<BitmapImage DecodePixelWidth="…">` con el tamaño que
  van a ocupar, y un test comprueba que ninguno usa la vía corta y que los tamaños pedidos
  son tamaños que el `.ico` trae. El hallazgo sobrevive a D-476: el aviso sigue pidiendo 16 px
  y el «Acerca de» 64, y los dos habrían salido del dibujo de 256.

- **D-477 — La regla «la marca se escribe una vez» se mide por lo VISIBLE.**
  `The_brand_is_written_once` contaba las apariciones de la palabra en el fichero entero, y
  falló dos veces seguidas sin que la regla se hubiera roto: primero por la ruta del icono
  (`/assets/atalaya.ico`) y luego por el `xmlns` del espacio de nombres de los controles. Ni
  una ruta de recurso ni una declaración de espacio de nombres las lee nadie en pantalla.
  Ahora se miran solo los atributos que llevan texto a la pantalla —`Text`, `Content`,
  `Title`— y se exige que la palabra aparezca exactamente dos veces, las dos como la marca
  entera. La regla es la misma; lo que se afinó es cómo se mide, y ahora se rompe cuando se
  rompe la regla y no cuando alguien nombra un fichero.

- **D-478 — La suscripción al cambio de tema se ata al ciclo de vida del control.** Estaba en
  el constructor y se soltaba en `Unloaded`: un control que se descargue y se vuelva a cargar
  se quedaba sordo. Con el logo en la barra de título —que vive tanto como la ventana— el
  fallo habría sido invisible hasta el día en que alguien cambiara el tema con la aplicación
  abierta. Se suscribe en `Loaded`, con un `-=` previo para no duplicar.

## F6.5 — Severidad por aplicación: la segunda fila de roscos

Tras la de cobertura, la pregunta que faltaba. Una dice cuánto se ha mirado de cada
aplicación; la otra, qué se encontró y de qué gravedad.

- **D-479 — Se llama SEVERIDAD, no «criticidad».** Se propuso lo segundo. La aplicación lleva
  desde el §2 diciendo «severidad» —en los chips de V3, en las insignias de V5, en los
  informes, en el JSON del hub y en el enum del dominio—, y un panel que la llamara de otra
  forma obligaría a traducir mentalmente entre dos vistas de la misma ventana. La consistencia
  de vocabulario no es una preferencia de estilo: es lo que permite buscar una palabra y
  encontrarla en todas partes.

- **D-480 — Cuenta solo los ACTIVOS, y por eso el rango temporal no manda.** Es la foto de la
  deuda VIVA: lo resuelto se arregló y de lo silenciado se decidió que no se arregla, así que
  sumarlos convertiría «lo que queda por hacer» en un histórico de todo lo que hubo. Y como es
  un estado de hoy —igual que el tile de activos (D-318)—, recortarlo por el periodo daría una
  deuda más pequeña que la real cada vez que alguien eligiera «4 semanas». El selector de
  aplicación sí manda, porque ese sí es un filtro de alcance y no de tiempo.

- **D-481 — Y el subtítulo lo DICE.** «Hallazgos activos a día de hoy, sin recortar por el
  periodo». Sin esa frase, cambiar el rango y ver los mismos números se lee como un fallo del
  programa en vez de como lo que es. Es la misma disciplina que la etiqueta de periodo de la
  cabecera: un dato que no obedece a un filtro visible tiene que explicar por qué.

- **D-482 — Aquí SÍ van los colores de severidad, y es la única gráfica del panel que los usa.**
  D-316 los reservó: significan crítica/alta/media/baja en toda la aplicación, así que ninguna
  serie puede pintarse de rojo por decoración. Esta fila no los toma prestados — los usa por lo
  que significan. La paleta semántica no ilustra el dato: **es** el dato. `SeverityPalette`
  sigue siendo el único sitio donde viven, así que el rojo de este rosco y el del chip de un
  hallazgo crítico no pueden divergir.

- **D-483 — Orden fijo desde las 12 en punto, de más grave a menos.** No se reordena por
  tamaño. Un rosco que pusiera primero el tramo más gordo obligaría a leer la leyenda en cada
  aplicación para saber de qué color es cada cosa; con el orden fijo, la posición ya lo dice y
  la leyenda es un recordatorio, no un requisito. Las severidades sin hallazgos se saltan, y el
  orden se conserva entre las que quedan.

- **D-484 — Una aplicación limpia se dibuja VACÍA, no se omite.** Que una app no tenga deuda
  viva es un dato tan bueno como tenerla. Si desapareciera de la fila sería indistinguible de
  una que nadie ha auditado nunca, que es exactamente la confusión contraria. Se pinta el aro
  apagado, un «0» en el centro y «0 activos» debajo — lo apagado dice «aquí no hay nada» sin
  fingir un tramo.

- **D-485 — El rosco se hizo genérico en vez de duplicarlo, con tres costuras.** `DonutRing`
  nació para la cobertura y valía casi tal cual; lo que le faltaba era dejar de decidir cosas
  que no le tocan:
  - **`DonutSegment.Tooltip`**: el texto exacto lo pone quien llama. La cobertura dice
    «Auditadas: 4 de 10 (40 %)» y la severidad «Alta — 4 hallazgos (33 %)». Forzar una frase
    única habría dejado a una de las dos diciendo una rareza.
  - **`DonutSegment.Payload`**: lo que se entrega al pulsar un tramo. El rosco no sabe qué es
    —una severidad, un estado, lo que sea—: solo lo devuelve. Sin esto habría que resolver
    «clic en el tramo rojo» por el NOMBRE del tramo, que es texto de presentación.
  - **`SegmentGap` y `EmptyBrush`**, los dos con valor por defecto que deja la fila de
    cobertura exactamente como estaba. El anti-objetivo era no tocar las demás gráficas, y la
    forma de cumplirlo no es no tocar el control: es que lo nuevo sea opt-in.

- **D-486 — El aire se DESCUENTA del tramo, no se añade.** Si se sumara, la vuelta pasaría de
  360° y el último tramo saldría desplazado: el anillo no cerraría. Y un tramo minúsculo tiene
  un mínimo de un grado, que es lo que hace que «1 crítica de 400» siga viéndose en vez de
  desaparecer bajo su propio hueco.

- **D-487 — Dos gestos sobre la misma figura, y el de dentro gana.** El tramo lleva a esa app
  con esa severidad; el centro y el nombre, a esa app entera — que es lo que cuenta el número
  del centro. Conviven porque el manejador del tramo marca el clic como atendido: sin eso, el
  clic seguiría subiendo hasta el botón que envuelve el rosco y se dispararían LOS DOS,
  navegando dos veces y ganando la segunda.

- **D-488 — Y V3 aprende a recibir una severidad.** `FindingsViewModel.SetSeverity`, con el
  mismo mecanismo diferido que `SetApp` (los combos no existen todavía cuando se llama). El
  método que los aplicaba pasa a llamarse `ApplyPendingFilters` y hace los dos de una vez, bajo
  la misma suspensión de recarga: aplicarlos por separado habría re-agregado la lista dos veces
  por navegación.

- **D-489 — Lo que queda probado (11 tests).** Del agregador: que solo cuenta activos —con un
  resuelto y un silenciado de la misma severidad fuera—; que una app sin activos sale igual
  pero vacía; que **ningún rango temporal la recorta**, comprobado sobre los cuatro; que el
  filtro de aplicación sí manda; y que la fila se ordena de más deuda a menos. Del panel: que
  los tramos van en orden de gravedad con los colores reservados y saltándose las severidades
  sin hallazgos; que el tooltip dice «Alta — 3 hallazgos (75 %)» y singulariza el uno; que la
  leyenda se pinta UNA vez para la fila; que la app limpia se explica; y las dos navegaciones
  —tramo y centro— comprobando además que la lista de destino trae los hallazgos correctos.
  De la vista: seis gráficas, dos `DonutRing` y la de severidad **entre** cobertura y flujo.

- **D-490 — Un test ajeno que hubo que afinar.** `La_grafica_trae_cursor_y_tooltip_por_columna`
  exigía `ToolTip = Tip(` en `DonutRing`. Sigue vigilando lo mismo —que cada tramo diga lo que
  vale— pero ahora contra `segment.Tooltip ?? Tip(`, que es la forma que admite las dos filas.
## F6.6 — «Verificar ahora» no reconocía un arreglo real (parte del 2026-08-27)

BUG-0003 (`HexStringToByteArray`, `errores.calculo.negocio`, XBLAST) se arregló siguiendo la
recomendación del propio hallazgo — `ArgumentNullException.ThrowIfNull`, longitud par,
`Convert.FromHexString` —, con commit y push hechos. Al pulsar «Verificar ahora»: aviso «no se
pudo verificar» sin causa, «Reabierto — no localizado tras cambios del código» tres veces en el
historial, y el hallazgo intacto en Activo + Por revisar. El banner de la ficha, mientras tanto,
enseñaba el método ya arreglado: el re-anclaje por símbolo funcionaba y el verify no lo usaba.

### §1 — El diagnóstico

- **D-491 — El verify se rendía en la fase de ANCLAJE, sin llegar a la de juicio.** Buscaba la
  línea y el hash originales; no los encontraba —porque el código malo se había borrado, que es
  literalmente lo que significa arreglar algo— y terminaba ahí con «no localizado». El aspecto
  normal de un arreglo se estaba leyendo como el fracaso de una búsqueda. El hallazgo no llegaba
  a pasar por delante del auditor ni una sola vez: no es que el veredicto fuera malo, es que no
  hubo veredicto.

- **D-492 — Y la ficha ya sabía hacerlo bien.** `SnippetReader` tiene desde F5.6 (D-222) la
  cadena entera —hash en su línea, hash en otra línea, símbolo vía Roslyn, y solo entonces «no
  localizado»— y por eso el banner enseñaba el método nuevo. El arreglo no era inventar nada:
  era que el camino de verificar usara el anclaje que el camino de pintar ya usaba.

### §2 — Dos fases, y el anclaje deja de decidir si se pregunta

- **D-493 — Anclar decide QUÉ código se enseña, nunca SI se pregunta.** Esa es la inversión.
  `VerifyCoordinator.Aim` devuelve un objetivo con su `VerifyBasis` — el fragmento exacto
  (`Anclado`), el miembro entero cuando el hash ya no casa pero el símbolo sigue (`Simbolo`), o la
  unidad completa cuando no queda ni símbolo pero la unidad SÍ cambió (`Unidad`)— y solo cuando
  las tres se agotan se abandona. Antes, un fallo de anclaje era un veredicto; ahora es una
  elección de encuadre.

- **D-494 — «No localizado» queda reservado para cuando no hay NADA que juzgar.** Ni ancla, ni
  símbolo, ni una unidad que haya cambiado desde el último avistamiento. Con esas tres puertas
  cerradas no hay pregunta honrada que hacerle al auditor, y marcar `needsReview` es lo correcto:
  hace falta una persona. Lo que ya no puede pasar es que se llegue ahí porque el código se
  arregló.

- **D-495 — El prompt lo dice con todas las letras.** «Si el fragmento que se auditó ya no
  aparece, eso NO es motivo de no-verificable: es lo que pasa cuando algo se arregla.» Y cada
  fragmento va rotulado con lo que es —«el código anclado YA NO ESTÁ; este es el código ACTUAL de
  Hex.HexStringToByteArray»—, porque enseñar código nuevo sin decir que es nuevo invita al modelo
  a contestar sobre el viejo. Con el fragmento viaja además la **recomendación** del hallazgo: es
  el criterio contra el que se juzga si lo que hay ahora cuenta como arreglo, y sin ella el
  verificador tiene que adivinarlo.

- **D-496 — Y el vocabulario se amplía a `no-es-defecto`.** El verify solo ofrecía {confirmado,
  resuelto, no-verificable}, así que un auditor que quisiera discrepar tenía que colarlo por
  «resuelto» — exactamente el agujero que F5.1b cerró en la reconciliación. La disputa ya existía
  en el dominio; lo que faltaba era la casilla. El traductor acepta también el vocabulario de la
  reconciliación (presente/arreglado): decir lo mismo con la otra palabra no es incumplir.

- **D-497 — La guarda de evidencia de cambio NO se relaja: se REUTILIZA.**
  `ReconciliationService.UnchangedSinceLastSighting` se llama tal cual desde el verify, con las
  dos capas de siempre —mismo commit, mismo `contentHash` de la unidad—. Lo que cambia en F6.6 es
  que ahora se LLEGA hasta ella. Un «arreglado» sobre una unidad que se puede probar que no ha
  cambiado se sigue degradando a «presente», y la degradación se sigue escribiendo. El sello del
  verify pasa a llevar el `unitContentHash` del fichero: sin él, la segunda capa de la guarda
  estaba en el código pero nunca tenía con qué comparar en este camino.

- **D-498 — Y un hallazgo que se resuelve deja de estar «por revisar», sin evento propio.** La
  duda que esa marca representaba acaba de contestarse. Se retira en silencio porque el evento
  del resultado es «Resuelto» y una segunda línea diciendo lo mismo con otras palabras es el eco
  que este parte vino a quitar.

### §3 — El evento se llamaba mal

- **D-499 — Un hallazgo ACTIVO no puede reabrirse.** «Reabierto» significa que algo cerrado
  vuelve a abrirse (`Finding.Reopen`, que además mueve el estado). Emitirlo sobre un hallazgo que
  nunca dejó de estar activo le decía al usuario «ha vuelto el defecto» cuando lo que había
  pasado era que se había perdido un rastro. Dos eventos nuevos: **`NotLocated` («No localizado»,
  ◌)** y **`Reanchored` («Re-anclado», ⌖)**. Grises los dos en la línea de tiempo: son
  contabilidad del anclaje, no alarmas.

- **D-500 — `Reanchored` solo se anota cuando el re-anclaje es lo ÚNICO que pasó.** Si detrás
  viene un veredicto, el veredicto es el evento; anotar los dos convertiría el caso de aceptación
  —un evento con la evidencia— en dos líneas donde la primera no informa de nada. Se emite
  cuando el auditor no llegó a pronunciarse sobre un objetivo re-anclado, que es la única
  situación en la que el historial se quedaría mudo sobre un trabajo que sí se hizo.

- **D-501 — El historial no necesita eco: `Finding.Record`.** Pulsar «Verificar ahora» tres veces
  seguidas escribía tres líneas idénticas. Repetir una pregunta no le pasa nada nuevo al
  hallazgo. `Record` colapsa contra el ÚLTIMO evento —mismo evento, mismo autor, mismo detalle— y
  lo cuenta: «no localizado (×3)», con la hora de la última vez. Solo contra el último: en cuanto
  pasa cualquier otra cosa entremedias, la repetición vuelve a ser noticia y se anota aparte.

- **D-502 — Y `Finding.Confirm` se queda como estaba, a propósito.** Tres confirmaciones seguidas
  también son tres líneas iguales, pero ahí cada una incrementa `timesConfirmed` y alimenta la
  máquina de confianza: son tres observaciones, no un eco. El colapso se aplica solo a las
  entradas que el camino de verificar escribe a mano. Lo mismo con el `Reopened` que la
  re-medición emite al retirar «por revisar» (F5.16): es el camino de los hallazgos medidos, y el
  anti-objetivo de esta tanda era no mezclarlo con este.

### §4 — El aviso sin causa

- **D-503 — «El verify no pudo emitir veredicto» era cierto e inútil.** No decía qué había
  pasado, ni por qué, ni qué hacer — y el usuario lo leyó tres veces sin enterarse de nada.
  `VerifyOutcome.Notes` lleva ahora una frase por hallazgo con lo que REALMENTE pasó: el
  veredicto con su evidencia, la degradación con su razón, el re-anclaje, o la causa concreta de
  no haber podido verificar. `Toast` las junta y es lo que la ficha enseña. La regla: nunca «no
  se pudo» a secas.

### §5 — Cobertura

- **D-504 — Lo que queda probado (7 tests nuevos, 1 reescrito).** El caso real completo: ancla
  perdida + símbolo presente + unidad cambiada → se PREGUNTA (con `Basis = Simbolo`, el miembro
  nombrado y el fragmento nuevo, sin rastro del viejo) y se resuelve vía Verify con evidencia,
  `needsReview` limpio y **un solo** evento en el historial. Que el prompt le dice al auditor que
  juzgue el código de ahora y le pasa la recomendación. La guarda: símbolo presente y unidad sin
  cambios → «arreglado» degradado a presente. Símbolo desaparecido con unidad cambiada → juicio
  sobre la unidad entera. Símbolo desaparecido con unidad sin cambios → «No localizado», con su
  etiqueta y sin «Reabierto». Y el eco: tres verificaciones idénticas dejan una línea «(×3)», y
  una cuarta tras otro evento vuelve a anotarse aparte.

- **D-505 — Y el test que había que reescribir.** `Verify_lost_anchor_sets_needsReview_not_resolved`
  cambiaba de fichero entero y esperaba «no localizado»: bajo la regla nueva eso es una unidad
  cambiada, y una unidad cambiada SÍ se juzga. Ahora sella el `unitContentHash` del contenido
  nuevo en `lastConfirmed` —el hallazgo se vio por última vez contra ESE fichero— y comprueba lo
  que siempre quiso comprobar, que es lo único que no ha cambiado: «no localizado» nunca es
  «resuelto». El nombre lo dice ahora entero:
  `Verify_lost_anchor_with_nothing_to_judge_sets_needsReview_not_resolved`.

- **D-506 — Lo que se verifica a mano.** El caso de aceptación con el hallazgo real: abrir
  BUG-0003 en XBLAST, pulsar «Verificar ahora» y ver el aviso con el veredicto, el estado en
  Resuelto, «Por revisar» retirado y una sola línea nueva en el historial con la evidencia.
## F6.7 — El aviso del snippet se lee según el ESTADO del hallazgo, no solo según el hash

Fleco del parte anterior. Con BUG-0003 ya resuelto, la ficha seguía sacando la franja ámbar «el
código de la línea X ya no es el que se auditó… Verifica para confirmarlo». Sobre un hallazgo
resuelto esa frase dice justo lo contrario de lo que ha pasado: el código de la línea ya no es el
que se auditó **porque se arregló**.

- **D-507 — El anclaje dice QUÉ relación hay; el estado dice si eso es un problema.** Son dos
  preguntas distintas y el lector del snippet solo contestaba la primera. «El hash ya no casa y el
  símbolo sigue» es el mismo hecho en los tres estados: sobre un activo es deriva sin verificar
  —hay que atenderla—, sobre un resuelto es el arreglo, y sobre un silenciado no es nada, porque
  se decidió no tocarlo. `SnippetReader.ForFinding` es la capa que aplica esa lectura; las
  sobrecargas de `Read` se quedan siendo el anclaje a secas y siguen sin saber nada del hallazgo.

- **D-508 — El aviso se parte en HECHO y ACCIÓN, y solo el hecho sobrevive fuera de lo activo.**
  Tres de los nueve avisos llevaban pegada una llamada a la acción («Verifica para re-anclarlo o
  cerrarlo»), y era ella la que no tenía sentido en un hallazgo que ya no pide nada. Partirlos en
  origen —`Notice` es lo que se pinta, `Fact` es el hecho sin la petición— evita la alternativa,
  que era recortar la frase a posteriori buscando dónde empieza el «Verifica». Un texto que se
  compone bien no hay que descoserlo después.

- **D-509 — Resuelto: la nota del arreglo, y con código delante.** «Resuelto — el código actual
  incluye el arreglo (verificado en {commit}, {fecha})», con el commit y la fecha de la resolución,
  no los del último avistamiento: lo que se está sellando es el arreglo. El snippet sigue
  enseñando el código actual exactamente como hasta ahora — cambia lo que se dice encima, no lo
  que se ve debajo.

- **D-510 — Sin código que enseñar, la nota positiva sería una afirmación sin respaldo.** Un
  resuelto cuyo fichero ya no está en el clon no permite decir «el código actual incluye el
  arreglo»: no hay código actual que mirar. Ahí se escribe el sello —«Resuelto en {commit} el
  {fecha}»— seguido del hecho, y se acaba. Sin la petición de verificar, que es lo único que
  sobraba.

- **D-511 — Silenciado: ni aviso ni nota, con una excepción declarada.** Se decidió no arreglarlo,
  así que la deriva del código no le pide nada a nadie y la franja desaparece. La excepción es el
  panel VACÍO: cuando no hay código que enseñar —sin clon, sin ubicación, fichero borrado— el
  hecho se conserva, porque sin él la ficha enseñaría un hueco sin decir por qué. Se retira el
  aviso, no la explicación.

- **D-512 — Y el tono es parte del mensaje.** La franja llevaba el ámbar de precaución cableado en
  el XAML, así que aunque el texto cambiara el color seguía diciendo «cuidado». `SnippetTone`
  —`Aviso` | `Nota`— viaja con el panel y el estilo lo conmuta con un `DataTrigger`: el ámbar se
  queda intacto para lo que hay que atender, y lo demás se pinta neutro con los tokens del tema.
  El botón «Verificar ahora» de la franja se retira con el mismo criterio: `CanVerify` nace de la
  regla del anclaje (`OffersVerify`) y el estado puede retirarlo, nunca añadirlo. El botón de la
  ficha no se toca — verificar un resuelto sigue siendo legítimo, lo que no es legítimo es que lo
  pida un aviso.

- **D-513 — Lo que queda probado (4 tests).** La MISMA deriva —hash perdido, símbolo presente—
  leída desde los tres estados: activo → ámbar, con «ya no es el que se auditó» y su acción, y el
  botón; resuelto → la nota del arreglo palabra por palabra, tono neutro, sin acción y con el
  snippet enseñando el código nuevo; silenciado → sin franja, con el código igualmente delante. Y
  el cuarto, el resuelto sin código: el sello, el hecho, y ni un «Verifica».
