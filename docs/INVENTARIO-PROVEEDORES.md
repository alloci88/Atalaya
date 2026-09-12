# INVENTARIO-PROVEEDORES.md — lo que hoy da por hecho que el proveedor es Copilot

**Entrega PROV-1 · 2026-09-12 · sobre `main` en `4ef4a87`.** Medida (N-2): **no se ha cambiado ni
una línea de producto**. La única edición de código de la entrega es un comentario en `DocsTests`
(comportamiento 6). Lo que se vio por el camino y no se arregla está en el apartado «Lo que se ve y
no se toca» de cada sección.

Atalaya va a tener más proveedores de IA: por API (OpenAI-compatible, Anthropic, Gemini), por agente
local (Codex CLI, Gemini CLI, además de Claude Code) y, si su SDK lo permite, otros de asiento. Este
documento **no diseña ninguno**: dice qué hay y cuánto mide. El diseño es de la entrega siguiente.

**Cómo se leen las cinco secciones juntas.** La **sección 1 es el censo de SITIOS**: cada fila es un
lugar del código o del manual que da por hecho Copilot, y es ahí donde un sitio se cuenta —una vez y
solo una—. Las secciones 2 a 5 miran el mismo código por otros ejes —el contrato, las herramientas,
el dinero, los dobles— y **citan los mismos ficheros y líneas como evidencia, sin volver a
contarlos**. Los cruces son pocos y están medidos: **12 referencias compartidas** entre la sección 1
y las demás (9 con la 2, 1 con la 3, 3 con la 4 —una de ellas, `CreditCalculator.cs:185`, en las
tres—). Cuando aparezca un total de sitios, el total es el de la sección 1.

**Lo que NO se ha ejecutado.** Ningún banco: ni PromptBench, ni el de capturas, ni el de carga, ni
M1/M2 (N-8, y los de crédito por encargo explícito). Se cuentan y se clasifican **por su código**.
Las cifras de coste del banco salen de lo ya escrito en `DECISIONS.md`, no de una corrida nueva.

**Dos cosas que esta medida encontró y que contradicen a `docs/ESTADO.md`.** Se dicen aquí y **no se
arreglan**, porque ESTADO está en «lo que no se toca» de esta spec:

- ESTADO dice que «el subcomando `claude` de PromptBench es lo único de los bancos que gasta cuota
  de IA» (A1). **Es falso**: `scripts/PromptBench/SweepBench.cs:138` construye un `ClaudeCodeProvider`
  real, así que **`barrido` gasta igual** — y `barrido` es precisamente con el que se corrieron M1 y
  M2. La línea de ESTADO es una afirmación de A1 que esta medida desmiente.
- ESTADO dice que «el arreglo asistido va en `IAssistedFixProvider`, separada de la interfaz del
  auditor» (F16, D-804). En el código `IAssistedFixProvider` **hereda** de `IAuditorProvider`
  (`src/Atalaya.Agents/FixContracts.cs:188`): la separación es de método, no de tipo.

---

## 1 · Lo que asume Copilot

| # | Fichero | Línea | Qué asume | Qué tendría que leer del proveedor activo | Tamaño |
|---|---|---|---|---|---|
| 1 | `src/Atalaya.Domain/Model/CreditCalculator.cs` | 156 | `UsdPerCredit = 0,01` — la unidad de coste del producto es el AI credit de GitHub, y todo importe nace, se guarda y se agrega en credits. | La unidad de coste que el proveedor declara (credits, dólares, tokens, o ninguna) y a cuánto está. | L |
| 2 | `src/Atalaya.Domain/Model/CreditCalculator.cs` | 185 | `IsBilled` devuelve true para todo id que no sea literalmente `claude-code`: cualquier casa nueva se supone facturando a la organización como Copilot. | Si el proveedor factura a la organización, declarado por él. | M |
| 3 | `src/Atalaya.Domain/Model/CreditCalculator.cs` | 191 | Proveedor vacío o `"copilot"` cuenta la caché DENTRO de la entrada. | La semántica de tokens del proveedor (entrada con o sin caché). | M |
| 4 | `src/Atalaya.Domain/Model/CreditCalculator.cs` | 196 | Un id de proveedor desconocido cae a la forma de conteo de Copilot. | Lo mismo, pedido al proveedor en vez de supuesto. | M |
| 5 | `src/Atalaya.Domain/Model/CreditCalculator.cs` | 245 | La entrada facturable se calcula restando cachés bajo el supuesto de Copilot. | La fórmula de entrada facturable del proveedor. | S |
| 6 | `src/Atalaya.Domain/Model/CreditCalculator.cs` | 265 | El resultado se emite dividiendo dólares entre el valor del credit: la salida siempre es credits. | La unidad de salida declarada por el proveedor. | S |
| 7 | `src/Atalaya.Domain/Model/ModelRateSeed.cs` | 43 | `Create()` siembra la tabla de tarifas del hub sólo con la tabla pública de precios de Copilot. | El catálogo de tarifas del proveedor activo, o que declare que no tiene. | L |
| 8 | `src/Atalaya.Domain/Model/ModelRateSeed.cs` | 38 | `SourceNote` escribe en el hub que la procedencia es `docs.github.com (Copilot · models-and-pricing)`. | La fuente de tarifas que el proveedor declare. | S |
| 9 | `src/Atalaya.Domain/Model/CostReconciliation.cs` | 23 | `ModelIds.Auto = "auto"`: el único pseudomodelo que conoce el producto es el enrutador de Copilot. | La lista de pseudomodelos del proveedor activo. | S |
| 10 | `src/Atalaya.Domain/Model/AuditSession.cs` | 386 | `Provider` nulo en una sesión guardada se lee como Copilot. | El id por defecto con el que se leen las sesiones sin casa escrita. | S |
| 11 | `src/Atalaya.Domain/Model/Stamps.cs` | 19 | El sello de detección sin `Provider` se lee como Copilot. | Lo mismo, en el sello. | S |
| 12 | `src/Atalaya.Domain/Model/PromptBudget.cs` | 137 | El prompt real de una sesión se deriva de `AccountingOf`, que ante un id nuevo devuelve la forma de Copilot. | La semántica de tokens del proveedor que escribió la sesión. | M |
| 13 | `src/Atalaya.Domain/Enums.cs` | 154 | Las temáticas del dominio declaran que sus criterios viven en `ThemeCatalog`, dentro de `Atalaya.Copilot`. | Nada del proveedor: el catálogo es de dominio y está alojado en la casa equivocada. | M |
| 14 | `src/Atalaya.Agents/AuditorReadiness.cs` | 25 | `NoSeat` es un valor de la taxonomía común: se presupone que toda casa tiene concepto de asiento. | Si el proveedor tiene concepto de asiento; una API por clave no lo tiene. | M |
| 15 | `src/Atalaya.Agents/AuditorPayloads.cs` | 102 | `CostUnit` es opcional y su ausencia no obliga a nada: quien lee la muestra la interpreta como credits. | La unidad declarada, obligatoria, con el proveedor respondiendo por ella. | M |
| 16 | `src/Atalaya.Agents/FixToolText.cs` | 64 | La quinta herramienta se llama `ask_user` porque así la nombra el runtime de Copilot, y todas las casas la declaran con ese nombre. | El nombre que el proveedor activo use, o uno propio de Atalaya. | S |
| 17 | `src/Atalaya.Storage/RuleExclusionMigration.cs` | 37 | La migración necesita un `delegate` para describir reglas porque el catálogo vive en `Atalaya.Copilot`, que depende de Storage y no al revés. | Nada del proveedor: es la deuda de alojar el catálogo en el proyecto de una casa. | M |
| 18 | `src/Atalaya.Copilot/Atalaya.Copilot.csproj` | 13 | `GitHub.Copilot.SDK 1.0.11`, y como `Atalaya.App` referencia este proyecto, el SDK entra en la aplicación entera. | Nada: el SDK debería quedarse detrás del proveedor y no llegar a la app. | M |
| 19 | `src/Atalaya.Copilot/Prompts.cs` | 135 | `PromptComposer` —el compositor del prompt de TODOS los proveedores, que usan `SessionCoordinator` y `VerifyCoordinator`— vive en el proyecto de Copilot. | Nada del proveedor: el prompt es común y está alojado en la casa de uno. | M |
| 20 | `src/Atalaya.Copilot/Prompts.cs` | 173 | El turno de continuación ordena `unit_done` la ÚLTIMA. | Si la herramienta de cierre del proveedor es terminal; el orden es de Copilot. | S |
| 21 | `src/Atalaya.Copilot/Prompts.cs` | 253 | El prompt compartido exige las cuatro llamadas en un turno con `unit_done` la última, porque en Copilot es terminal: el prompt de todos se gobierna por la casa más exigente. | Si el proveedor cierra el turno en su herramienta terminal, y componer el orden con ese dato. | M |
| 22 | `src/Atalaya.Copilot/RuleCatalog.cs` | 13 | El catálogo de reglas —dominio puro, común a todas las casas— vive en el proyecto de Copilot. | Nada del proveedor: hay que moverlo. | M |
| 23 | `src/Atalaya.Copilot/ThemeCatalog.cs` | 28 | El catálogo de temáticas, igual. | Nada del proveedor: hay que moverlo. | M |
| 24 | `src/Atalaya.Copilot/SeverityRubric.cs` | 32 | La rúbrica de severidad, igual. | Nada del proveedor: hay que moverlo. | M |
| 25 | `src/Atalaya.Copilot/Directives.cs` | 98 | El presupuesto de directivas y el estimador de tokens del prompt, igual. | Nada del proveedor: hay que moverlo. | M |
| 26 | `src/Atalaya.Copilot/Contracts.cs` | 16 | `NoAccount` dice que el remedio es «Conectar con GitHub» y que el mismo login habilita el hub y el asiento de Copilot. | El texto de «sin credencial» del proveedor activo, y cuál es su remedio. | S |
| 27 | `src/Atalaya.Copilot/Contracts.cs` | 21 | `NoSeat` manda a `github.com/settings/copilot` a pedir asiento. | El remedio de «sin licencia» del proveedor, si lo tiene. | S |
| 28 | `src/Atalaya.Copilot/Contracts.cs` | 41 | `QuotaExhausted` nombra «AI credits de Copilot» al usuario. | La unidad de cuota del proveedor. | S |
| 29 | `src/Atalaya.Copilot/Contracts.cs` | 68 | El login por máquina fuera de la app se explica como `npm install -g @github/copilot` + `copilot` + `/login`. | El procedimiento de login fuera de la app del proveedor activo (clave de API, `codex login`, `gemini`…). | M |
| 30 | `src/Atalaya.Copilot/CopilotFailure.cs` | 132 | `Raw(ex)` es el formateador de error crudo, y la aplicación lo llama para errores que no son del proveedor (filas 53 y 55). | El formateador de crudo del proveedor activo, o uno neutro en `Atalaya.Agents`. | M |
| 31 | `src/Atalaya.App/Atalaya.App.csproj` | 58 | La aplicación referencia `Atalaya.Copilot` directamente, no sólo por DI. | Nada: la app debería hablar sólo con `Atalaya.Agents`. | M |
| 32 | `src/Atalaya.App/App.xaml.cs` | 288 | `IAssistedFixProvider` se registra en el contenedor como un `RealCopilotAgent` concreto: el arreglo asistido tiene una casa fija en el arranque. | El proveedor de arreglo del registro, sin tipo concreto en el contenedor. | M |
| 33 | `src/Atalaya.App/App.xaml.cs` | 331 | El primer elemento del registro —y por tanto el de fábrica y el que la pantalla Cuenta enseña arriba— es el `IAssistedFixProvider`, o sea Copilot. | El orden y el de fábrica declarados como configuración, no por posición. | S |
| 34 | `src/Atalaya.App/App.xaml.cs` | 411 | `BuildRunner` toma su timeout de `CopilotTimeoutMinutes`, aunque compilar no es cosa de ninguna casa. | Un timeout de compilación propio, sin nombre de proveedor. | S |
| 35 | `src/Atalaya.App/Services/AuditorProviderRegistry.cs` | 56 | `Fallback` busca `RealCopilotAgent.Id`: el proveedor de refugio se nombra por el tipo concreto de Copilot. | Cuál es el de fábrica, declarado por el registro o por configuración. | M |
| 36 | `src/Atalaya.App/Services/AuditorProviderRegistry.cs` | 105 | `ById` con un id desconocido devuelve el `Fallback` en silencio: un ajuste que nombre un proveedor retirado audita con Copilot sin decirlo. | El id guardado, y decir que no se reconoce antes de sustituirlo. | S |
| 37 | `src/Atalaya.App/Services/AuditorProviderRegistry.cs` | 130 | `NameOf` de un id no registrado cae a `ProviderNames.Display`, que para vacío devuelve «GitHub Copilot». | El nombre de presentación guardado con la sesión. | S |
| 38 | `src/Atalaya.App/Services/ProviderNames.cs` | 34 | Proveedor vacío se presenta como «GitHub Copilot» en informes, métricas, fichas y ciclos. | El nombre de presentación del proveedor que escribió la sesión. | S |
| 39 | `src/Atalaya.App/Services/ProviderNames.cs` | 40 | El mapa de id → nombre está escrito a mano con dos entradas. | La lista de proveedores conocidos, alimentada por los drivers. | M |
| 40 | `src/Atalaya.App/Services/AppPaths.cs` | 22 | Hay una carpeta de datos llamada `copilot` en la raíz de la aplicación. | La carpeta de trabajo que pida el proveedor activo. | S |
| 41 | `src/Atalaya.App/Services/AgentBusyGate.cs` | 27 | Un cerrojo único para toda la aplicación: auditar y arreglar comparten runtime, asiento y presupuesto, que es verdad con Copilot. | Si el proveedor admite concurrencia y con qué límite. | M |
| 42 | `src/Atalaya.App/Services/SettingsService.cs` | 52 | `MinCopilotTimeoutMinutes` es el mínimo de todos los timeouts de la aplicación. | Un mínimo por proveedor, o uno neutro. | S |
| 43 | `src/Atalaya.App/Services/SettingsService.cs` | 223 | `CopilotBaseDirectory` es un ajuste de primera clase de la máquina. | Un saco de ajustes por proveedor, no un campo con nombre de casa. | S |
| 44 | `src/Atalaya.App/Services/SettingsService.cs` | 229 | `CopilotTimeoutMinutes` es el timeout de envío de cualquier sesión. | El timeout del proveedor activo. | S |
| 45 | `src/Atalaya.App/Services/SettingsService.cs` | 298 | `CopilotModel` es un campo fijo del fichero de ajustes. | El modelo por proveedor, en un mapa indexado por id. | S |
| 46 | `src/Atalaya.App/Services/SettingsService.cs` | 596 | `ModelFor` es un binario: si no es Claude Code, es el modelo de Copilot. Un tercer proveedor recibiría el modelo de Copilot. | El modelo de ESE proveedor, por su id. | M |
| 47 | `src/Atalaya.App/Services/SettingsService.cs` | 608 | `SetModelFor` escribe en `CopilotModel` todo lo que no sea Claude Code. | Lo mismo, en la entrada del proveedor. | S |
| 48 | `src/Atalaya.App/Services/ConnectionHelp.cs` | 16 | `DocsCopilotSeat` es la única URL de ayuda de asiento que conoce el producto. | La URL de ayuda del proveedor activo, si tiene asiento. | S |
| 49 | `src/Atalaya.App/Services/ConnectionChecker.cs` | 365 | Cualquier proveedor que devuelva `NoSeat` se enlaza a la página de asientos de Copilot. | El enlace de ayuda que el proveedor adjunte a su diagnóstico. | S |
| 50 | `src/Atalaya.App/Services/ModelResolver.cs` | 116 | Una lista de modelos vacía se explica con «el asiento, una política de la organización o su cuota»: las tres causas son de Copilot. | Las causas que el proveedor enumere para una lista vacía. | S |
| 51 | `src/Atalaya.App/Services/LiveSessionService.cs` | 613 | El pie de la sesión en vivo arranca con `CostFormat.Unit`, que es credits (o dólares), antes de saber quién audita. | La unidad del proveedor con el que se va a lanzar. | S |
| 52 | `src/Atalaya.App/Services/LiveSessionService.cs` | 665 | El fallo de resolución de modelo se cuenta con `CopilotHelp.ModelUnavailable` aunque la sesión sea de otra casa. | El texto de «modelo no disponible» del proveedor activo. | S |
| 53 | `src/Atalaya.App/Services/LiveSessionService.cs` | 728 | El crudo de un error que ni siquiera es del proveedor se formatea con `CopilotFailure.Raw`. | Un formateador neutro, o el del proveedor activo. | S |
| 54 | `src/Atalaya.App/Services/LiveFixService.cs` | 406 | Lo mismo en la sesión de arreglo: `CopilotHelp.ModelUnavailable`. | El texto del proveedor activo. | S |
| 55 | `src/Atalaya.App/Services/LiveFixService.cs` | 512 | Lo mismo con el crudo: `CopilotFailure.Raw`. | Un formateador neutro. | S |
| 56 | `src/Atalaya.App/Services/CostEstimator.cs` | 165 | `DefaultCostUnit` es la unidad de la máquina (credits/dólares) y no depende de con quién se va a auditar. | La unidad declarada por el proveedor que va a auditar. | M |
| 57 | `src/Atalaya.App/Services/CostEstimator.cs` | 178 | En la estimación previa, una sesión sin proveedor escrito se cuenta como de Copilot. | El id por defecto del histórico, declarado en un solo sitio. | S |
| 58 | `src/Atalaya.App/Services/CostEstimator.cs` | 63 | El diálogo de lanzamiento sólo tiene dos respuestas: un número en credits, o «sin coste para la organización». | Si el proveedor factura y en qué, con una tercera respuesta posible («no se sabe»). | S |
| 59 | `src/Atalaya.App/Services/CostEstimator.cs` | 90 | La rama de «no factura» explica la causa nombrando a Claude Code: el único no-Copilot del producto. | El motivo que declare el proveedor que no factura. | S |
| 60 | `src/Atalaya.App/Services/CostFormat.cs` | 51 | `Unit` es «credits» o «$»: no existe ninguna otra unidad en la que el producto sepa escribir un coste. | La unidad del proveedor que produjo esa cifra. | M |
| 61 | `src/Atalaya.App/Services/CostFormat.cs` | 82 | `SubscriptionCost` es «incluido en tu suscripción de Claude»: la única alternativa a los credits está escrita con el nombre de la otra casa. | La frase de «no facturado» del proveedor activo. | S |
| 62 | `src/Atalaya.App/Services/CostFormat.cs` | 94 | `BillingUnit` escribe «AI credits», la unidad de GitHub, en el panel y en las líneas por proveedor. | La unidad larga del proveedor. | S |
| 63 | `src/Atalaya.App/Services/CostFormat.cs` | 189 | `Both` escribe en TODO informe «N AI credits (M $)», sea cual sea la casa. | Las dos cifras en la unidad del proveedor de esa sesión. | S |
| 64 | `src/Atalaya.App/Services/CostFormat.cs` | 443 | `WithUnit` recibe el `providerId` y lo IGNORA: siempre escribe la unidad de credits. | La unidad del proveedor pasado. | M |
| 65 | `src/Atalaya.App/Services/CostFormat.cs` | 511 | El tooltip de la unidad dice «lo que GitHub factura por estos tokens». | Quién factura, según el proveedor de esa cifra. | S |
| 66 | `src/Atalaya.App/Services/MetricsQuery.cs` | 256 | La etiqueta de temáticas del panel llama a `Copilot.ThemeCatalog`. | Nada del proveedor: el catálogo debe salir del proyecto de Copilot. | S |
| 67 | `src/Atalaya.App/Services/MetricsQuery.cs` | 347 | La línea de coste por proveedor del panel se compone con la unidad global («GitHub Copilot · 68,2 AI credits»). | La unidad de cada proveedor en su propia línea. | S |
| 68 | `src/Atalaya.App/Services/MetricsQuery.cs` | 1136 | El «Top 5 reglas» resuelve títulos con `Copilot.RuleCatalog`. | Nada del proveedor: el catálogo está alojado en la casa equivocada. | S |
| 69 | `src/Atalaya.App/Services/MetricsQuery.cs` | 1470 | El agrupado de coste por proveedor mete en el cubo `copilot` toda sesión sin casa escrita. | El id por defecto del histórico. | S |
| 70 | `src/Atalaya.App/Services/MetricsQuery.cs` | 1527 | `LegacyProviderId = "copilot"` está escrito a mano en Métricas. | El id por defecto, en un solo sitio compartido. | S |
| 71 | `src/Atalaya.App/Services/MetricsQuery.cs` | 1792 | La columna «Proveedor» del registro de operaciones pasa por `ProviderNames.Display`: las sesiones viejas salen como «GitHub Copilot». | El nombre de presentación de la casa escrita en la sesión. | S |
| 72 | `src/Atalaya.App/Services/ReportBuilder.cs` | 43 | `ProviderLine` escribe «Proveedor: GitHub Copilot» cuando la sesión no trae casa. | El nombre de la casa registrada, o decir que no consta. | S |
| 73 | `src/Atalaya.App/Services/ReportBuilder.cs` | 212 | La cabecera del informe de sesión usa esa línea. | Lo mismo. | S |
| 74 | `src/Atalaya.App/Services/ReportBuilder.cs` | 912 | La cabecera del informe de verificación, igual. | Lo mismo. | S |
| 75 | `src/Atalaya.App/Services/ReportBuilder.cs` | 1064 | La cabecera del informe de arreglo, igual. | Lo mismo. | S |
| 76 | `src/Atalaya.App/Services/ReportBuilder.cs` | 1293 | La preferencia de casa del ciclo se nombra con el mismo mapa. | Lo mismo. | S |
| 77 | `src/Atalaya.App/Services/ReportPage.cs` | 461 | La cabecera de la página de informe compone «Proveedor · modelo» con el mismo mapa. | Lo mismo. | S |
| 78 | `src/Atalaya.App/Services/ReportPage.cs` | 1143 | Ídem en la segunda cabecera de informe. | Lo mismo. | S |
| 79 | `src/Atalaya.App/Services/ReportPage.cs` | 1373 | Ídem en la tercera. | Lo mismo. | S |
| 80 | `src/Atalaya.App/Services/CycleConfigService.cs` | 150 | La etiqueta del juez preferido del ciclo usa el mismo mapa. | Lo mismo. | S |
| 81 | `src/Atalaya.App/Services/HubContext.cs` | 369 | El hub resuelve reglas con `Copilot.RuleCatalog`. | Nada del proveedor: el catálogo está alojado en la casa equivocada. | S |
| 82 | `src/Atalaya.App/Services/PortfolioQuery.cs` | 135 | El Portafolio etiqueta temáticas con `Copilot.ThemeCatalog`. | Ídem. | S |
| 83 | `src/Atalaya.App/Services/PortfolioQuery.cs` | 139 | La frase del ciclo temático, ídem. | Ídem. | S |
| 84 | `src/Atalaya.App/Services/ModelRatesService.cs` | 162 | `auto` se trata aparte porque es el enrutador de Copilot: el único pseudomodelo reconocido. | Los pseudomodelos que declare cada proveedor. | S |
| 85 | `src/Atalaya.App/Services/SessionCoordinator.cs` | 533 | `_agent as ClaudeCodeProvider` — el corte de pasada se pregunta por el tipo concreto porque «Copilot no tiene esta llamada». | Una capacidad declarada en la interfaz, como `IThreadedAuditor`. | M |
| 86 | `src/Atalaya.App/Converters.cs` | 376 | El conversor de temáticas de la interfaz llama a `Copilot.ThemeCatalog`. | Ídem: el catálogo, fuera del proyecto de Copilot. | S |
| 87 | `src/Atalaya.App/ViewModels/SettingsViewModel.cs` | 227 | Sin proveedor seleccionado, Ajustes enseña `CopilotModel`. | El modelo del proveedor activo. | S |
| 88 | `src/Atalaya.App/ViewModels/SettingsViewModel.cs` | 275 | `ProviderNotice` dice al usuario «El arreglo asistido sigue siendo de Copilot». | Quién arregla según el registro, que desde F16 es el proveedor elegido. | S |
| 89 | `src/Atalaya.App/ViewModels/SettingsViewModel.cs` | 653 | Guardar escribe en `CopilotModel` todo lo que no sea Claude Code. | La entrada del proveedor elegido. | S |
| 90 | `src/Atalaya.App/ViewModels/SettingsViewModel.cs` | 665 | El saneado del timeout se rotula «el timeout de Copilot». | El nombre del ajuste del proveedor activo. | S |
| 91 | `src/Atalaya.App/ViewModels/FindingDetailViewModel.cs` | 645 | La ficha del hallazgo lee `Atalaya.Copilot.RuleCatalog`. | Ídem: el catálogo, fuera del proyecto de Copilot. | S |
| 92 | `src/Atalaya.App/ViewModels/FindingDetailViewModel.cs` | 726 | La ficha etiqueta la temática con `Copilot.ThemeCatalog`. | Ídem. | S |
| 93 | `src/Atalaya.App/ViewModels/FindingDetailViewModel.cs` | 819 | La procedencia del hallazgo nombra la casa con `ProviderNames.Display`: en blanco sale «GitHub Copilot». | La casa registrada en el sello. | S |
| 94 | `src/Atalaya.App/ViewModels/InventoryViewModel.cs` | 706 | La etiqueta del juez preferido del ciclo, en V2, con el mismo mapa. | Lo mismo. | S |
| 95 | `src/Atalaya.App/Views/SettingsView.xaml` | 160 | La ayuda de «Modelo del auditor» explica el mundo con dos casas: «Copilot los publica… y Claude Code ofrece sus alias». | Una frase que salga de lo que cada proveedor declare sobre su lista de modelos. | S |
| 96 | `src/Atalaya.App/Views/SettingsView.xaml` | 285 | La sección se titula «Tarifas de GitHub Copilot». | El nombre del proveedor cuyas tarifas se editan. | S |
| 97 | `src/Atalaya.App/Views/SettingsView.xaml` | 318 | La ayuda dice «Precios de la tabla pública de GitHub Copilot, por millón de tokens». | La fuente de tarifas del proveedor. | S |
| 98 | `src/Atalaya.App/Views/SettingsView.xaml` | 322 | La salvedad plegada nombra a Claude Code como la única casa que no entra en la tabla. | Qué proveedores no se tarifan, leído del registro. | S |
| 99 | `src/Atalaya.App/Views/SettingsView.xaml` | 558 | La fila se rotula «Timeout de Copilot (minutos)». | El nombre del ajuste del proveedor activo. | S |
| 100 | `src/Atalaya.App/Views/AccountView.xaml` | 139 | La ayuda de conexión dice «Con ella entra además tu asiento de Copilot». | Qué habilita esa cuenta según los proveedores registrados. | S |
| 101 | `MANUAL.md` | 39 | El login de GitHub sirve «para las tres cosas», una de ellas la autenticación de Copilot. | Qué autentica esa cuenta según los proveedores instalados. | S |
| 102 | `MANUAL.md` | 159 | `auto` se explica como «el enrutador de Copilot». | Los pseudomodelos de cada proveedor. | S |
| 103 | `MANUAL.md` | 471 | El arreglo asistido se explica como «sea Copilot o Claude Code». | La lista de proveedores que saben arreglar. | S |
| 104 | `MANUAL.md` | 491 | «Con Copilot el coste son AI credits» — el pie se documenta por casas enumeradas. | La unidad que declare cada proveedor. | S |
| 105 | `MANUAL.md` | 589 | El coste del panel se documenta en AI credits, «la misma unidad que el panel de Copilot». | Ídem. | S |
| 106 | `MANUAL.md` | 591 | El azulejo de coste se describe como «todas las sesiones de Copilot que gastaron». | Las sesiones de todas las casas que facturen. | S |
| 107 | `MANUAL.md` | 1250 | La tabla de Ajustes documenta la fila «Timeout de Copilot (min)». | El nombre del ajuste del proveedor. | S |
| 108 | `MANUAL.md` | 1344 | La siembra de tarifas se documenta como «la lista de precios publicada de Copilot». | La fuente de tarifas de cada proveedor. | S |
| 109 | `MANUAL.md` | 1531 | «Copilot es el proveedor por defecto y el único requisito del equipo». | Cuál es el de fábrica, declarado por configuración. | S |
| 110 | `MANUAL.md` | 1538 | La comparativa de proveedores es una tabla de DOS columnas: GitHub Copilot y Claude Code. | Una tabla generada de los proveedores registrados. | M |
| 111 | `MANUAL.md` | 1540 | «Qué necesitas: tu cuenta de GitHub conectada, con asiento de Copilot». | El requisito que declare cada proveedor. | S |
| 112 | `MANUAL.md` | 1548 | El login de Claude Code se explica «exactamente igual que con Copilot usa tu login de GitHub». | El procedimiento de credencial de cada casa, por sí mismo. | S |
| 113 | `MANUAL.md` | 1603 | «La regla, en una frase: Copilot gasta la bolsa de la organización y se mide en AI credits». | Si el proveedor factura y en qué unidad. | M |
| 114 | `MANUAL.md` | 1607 | La sección de coste arranca del cambio de facturación de GitHub del 1 de junio de 2026. | El modelo de facturación del proveedor activo. | S |
| 115 | `MANUAL.md` | 1647 | «Ajustes → Tarifas. Es la tabla de GitHub Copilot, y así se titula». | El proveedor cuyas tarifas se editan. | S |
| 116 | `MANUAL.md` | 1662 | «Es la tabla de lo que FACTURA, o sea de los modelos de Copilot». | Los modelos de las casas que facturen. | S |
| 117 | `MANUAL.md` | 1679 | La regla de las dos bolsas se repite como dicotomía Copilot / Claude Code. | El régimen de facturación de cada proveedor. | M |
| 118 | `MANUAL.md` | 1691 | «Copilot: coste en AI credits, que es lo que tu organización paga. Entra en el azulejo…». | La unidad de cada casa y si entra en las métricas. | S |
| 119 | `MANUAL.md` | 1713 | La tabla de mensajes de Cuenta dice «Sigues auditando con Copilot» ante un opcional ausente. | El proveedor de refugio del registro. | S |
| 120 | `MANUAL.md` | 1715 | Ante la cuota agotada de Claude Code, el remedio documentado es «audita mientras tanto con Copilot». | Los proveedores disponibles en esa máquina. | S |
| 121 | `MANUAL.md` | 1917 | La quinta herramienta se explica diciendo que «con Copilot esto no cambia nada, porque allí nunca existió». | Quién transporta `ask_user` en cada casa. | S |
| 122 | `MANUAL.md` | 1978 | La sección de fallos abre con «Cuando Copilot rechaza una sesión…». | El proveedor que rechazó la sesión. | S |
| 123 | `MANUAL.md` | 1986 | La tabla de fallos documenta «La organización ha agotado sus AI credits de Copilot». | El texto de cuota del proveedor activo. | S |
| 124 | `MANUAL.md` | 1987 | Y «Tu cuenta no tiene asiento de Copilot asignado», con la URL de GitHub. | El texto de asiento del proveedor, si tiene asiento. | S |
| 125 | `MANUAL.md` | 2177 | La autoactualización se explica diciendo que interrumpir «tira trabajo ya pagado a Copilot». | Quién paga el trabajo de la sesión en curso. | S |

**Reparto.** 125 filas. Por zona: **17 en dominio** (`Atalaya.Domain`, `Atalaya.Agents`, `Atalaya.Storage`), **13 en proveedores** (`Atalaya.Copilot`), **70 en app/vistas** (`Atalaya.App`, servicios, view-models y XAML) y **25 en `MANUAL.md`**. Por tamaño: **2 L** (la unidad de coste del producto y la siembra de tarifas), **30 M** y **93 S**.

Los tres focos que concentran casi todo: la **unidad de coste** —el credit de GitHub es la única unidad que el producto sabe escribir, y de ahí cuelgan 20 filas entre `CreditCalculator`, `CostFormat`, `CostEstimator`, Métricas e informes—; el **nombre por defecto** —`ProviderNames.Display` devuelve «GitHub Copilot» para un proveedor vacío y eso se propaga a 15 sitios de presentación por una sola línea—; y el **alojamiento del vocabulario** —el prompt, el catálogo de reglas, las temáticas, la rúbrica y las directivas viven en `Atalaya.Copilot`, y por eso la aplicación entera referencia el proyecto que arrastra el SDK.

### Lo que se ve y no se toca

- `src/Atalaya.App/ViewModels/SettingsViewModel.cs:275` dice «El arreglo asistido sigue siendo de Copilot», pero desde F16 el arreglo lo hace el proveedor elegido (`AuditorProviderRegistry.CurrentFixer`, `App.xaml.cs:424`). Es un texto que ya no describe el producto. **Apuntado, no arreglado.**
- `src/Atalaya.App/Services/CostFormat.cs:443` — `WithUnit(decimal?, string? providerId)` recibe el proveedor y no lo usa. Es un parámetro muerto que promete una lectura que no ocurre. **Apuntado, no arreglado.**
- `src/Atalaya.Inventory/DirectiveCatalog.cs:68` y `:70` proponen `.github/copilot-instructions.md` y `.github/instructions/*.md` como candidatos a directiva. Eso NO es una asunción sobre el proveedor de Atalaya: es una convención del repositorio auditado, y seguiría siendo correcta con cualquier auditor. No entra en la tabla.
- `src/Atalaya.App/Services/AppPaths.cs:22` expone una carpeta `copilot` que sólo usa `CopilotBaseDirectory`, hoy vacío por defecto. Es un directorio que probablemente nunca se crea. **Apuntado, no arreglado.**
- Hay decenas de comentarios y `<summary>` que explican el mundo como «Copilot y Claude Code» (por ejemplo `src/Atalaya.Agents/IAuditorProvider.cs:50`, `:88`). No cambian comportamiento y no se han inventariado uno a uno, pero envejecerán con el tercer proveedor.
- `src/Atalaya.App/Services/ConnectionChecker.cs` ya está bien: recorre `_providers.All` y compone la clave de cada fila (`ProviderStepKey`, línea 168). No hay nada que tocar ahí salvo la fila 49.

### Lo que ESTADO no me dio

No abrí `DECISIONS.md` ni una vez: cada `D-` que necesité entender estaba resumido en `ESTADO.md` con el detalle suficiente para ir directo al código. Lo que sí tuve que ir a buscar a `src/`:

- **Dónde vive el prompt y los catálogos.** ESTADO dice que el vocabulario común de agentes vive en `Atalaya.Agents` (D-775) y que `PromptComposer` compone en un solo sitio (D-852), pero no dice cuál es ese sitio. Lo encontré en `src/Atalaya.Copilot/Prompts.cs:135`, y con él los catálogos de `src/Atalaya.Copilot/RuleCatalog.cs`, `ThemeCatalog.cs`, `SeverityRubric.cs` y `Directives.cs`.
- **Que `Atalaya.App` referencia el proyecto de Copilot.** ESTADO no habla de dependencias entre proyectos. `src/Atalaya.App/Atalaya.App.csproj:58`.
- **Que el arreglo asistido se registra en el contenedor como `RealCopilotAgent`.** ESTADO dice que el arreglo va en `IAssistedFixProvider` (F16) y que quien arregla es el proveedor activo (D-776/F16), no que el tipo concreto esté clavado en el arranque. `src/Atalaya.App/App.xaml.cs:288`.
- **Que `CostFormat.Unit` no depende del proveedor.** ESTADO dice que `CostFormat` es el único sitio donde un coste se hace texto (D-1004) y que cada muestra viaja con su `CostUnit` (D-780); no dice que el formateador escriba siempre credits o dólares sin mirar la casa. `src/Atalaya.App/Services/CostFormat.cs:51`, `:94`, `:443`.
- **Que `IsBilled` es una negación de `claude-code`.** ESTADO dice que el consumo de Claude Code no se tarifa (F16-RETOQUE-2); no dice que la regla esté escrita como «todo lo que no sea claude-code factura», que es lo que hace a un proveedor nuevo facturable por omisión. `src/Atalaya.Domain/Model/CreditCalculator.cs:185`.
- **Que `AccountingOf` cae a la forma de Copilot ante un id desconocido.** ESTADO da la regla de las dos semánticas (D-785) pero no el caso por defecto. `src/Atalaya.Domain/Model/CreditCalculator.cs:196`.
- **Que `ModelFor`/`SetModelFor` son un binario.** ESTADO dice que el modelo es un campo por proveedor, `copilotModel` y `claudeCodeModel` (D-779); no que la selección entre los dos sea un `if` que manda todo lo desconocido a Copilot. `src/Atalaya.App/Services/SettingsService.cs:596` y `:608`.
- **Que el corte de pasada se decide con un `as ClaudeCodeProvider`.** ESTADO describe el corte (D-880) y dice que las capacidades opcionales viven en la interfaz (D-915), pero el corte no está entre ellas. `src/Atalaya.App/Services/SessionCoordinator.cs:533`.
- **Que el cerrojo de agente es uno solo para toda la aplicación.** ESTADO no lo recoge en ninguna de las cinco secciones que me indicaron. `src/Atalaya.App/Services/AgentBusyGate.cs:27`.
- **Que `BuildRunner` reutiliza el timeout de Copilot.** ESTADO menciona «el timeout de Copilot y el de compilación» como dos filas distintas de Ajustes (D-766), lo que sugiere que son ajustes distintos; en el código son el mismo número. `src/Atalaya.App/App.xaml.cs:411`.
- **La URL de ayuda de asiento.** ESTADO dice que los textos de conexión salen de `ConnectionHelp` (D-040); la URL concreta y su uso incondicional ante `NoSeat` están en `src/Atalaya.App/Services/ConnectionHelp.cs:16` y `src/Atalaya.App/Services/ConnectionChecker.cs:365`.
- **El texto del login por máquina.** ESTADO dice que el login de Copilot es `copilot` + `/login` fuera de Atalaya (D-029); el texto que se le enseña al usuario, con el `npm install`, está en `src/Atalaya.Copilot/Contracts.cs:68`.
- **Que `CopilotFailure.Raw` y `CopilotHelp.ModelUnavailable` se llaman desde la aplicación.** ESTADO dice que `CopilotFailure` es el único clasificador (D-707), lo que se lee como «dentro de su casa»; que la sesión en vivo y el arreglo los invoquen directamente lo vi en `src/Atalaya.App/Services/LiveSessionService.cs:665`, `:728` y `src/Atalaya.App/Services/LiveFixService.cs:406`, `:512`.
- **El desfase de `ProviderNotice`.** ESTADO no recoge los textos de ayuda de Ajustes uno a uno; el «el arreglo asistido sigue siendo de Copilot» sólo se ve en `src/Atalaya.App/ViewModels/SettingsViewModel.cs:275`.

---

## 2 · El contrato del proveedor

Qué se le pide a una casa que audite, y qué se le resuelve por fuera.

El contrato vive en `src/Atalaya.Agents/IAuditorProvider.cs` y lo implementan dos clases:
`RealCopilotAgent` (`src/Atalaya.Copilot/RealCopilotAgent.cs:25`) y `ClaudeCodeProvider`
(`src/Atalaya.ClaudeCode/ClaudeCodeProvider.cs:31`). Ninguna de las dos declara
`IAuditorProvider` directamente: las dos declaran `IAssistedFixProvider`, que **hereda** de
`IAuditorProvider` (`src/Atalaya.Agents/FixContracts.cs:188`). D-804 dice que el arreglo está
separado del auditor, y en el código esa separación es de **método** —`FixAsync`, con una
implementación por defecto que lanza `NotSupportedException`
(`src/Atalaya.Agents/FixContracts.cs:200`)—, no de tipo.

El vocabulario común que acompaña a la interfaz, todo en `src/Atalaya.Agents/`:

- **Lo que el proveedor recibe**: `AuditUnitRequest` (`AuditorPayloads.cs:147`) y `VerifyRequest`
  (`AuditorPayloads.cs:244`). Traen el prompt ya compuesto; el proveedor no lo redacta.
- **Por dónde contesta**: `IAuditToolbox` con seis herramientas (`IAuditorProvider.cs:7-41`) e
  `IVerifyToolbox` con una (`IAuditorProvider.cs:44-47`). El proveedor propone, la aplicación
  juzga y persiste.
- **Lo que reporta de gasto**: `UsageSample` (`AuditorPayloads.cs:97-110`), con `CostUnit` para
  que dos magnitudes distintas no se sumen.
- **Cómo se queja**: `AgentReadiness` y `AgentProblem` (`AuditorReadiness.cs:14-68`), más las
  excepciones `Auditor*` (`AuditorExceptions.cs`).
- **Qué modelos ofrece**: `AgentModel` (`AuditorReadiness.cs:81`).

### Lo que el contrato cubre

Trece miembros. Cinco de ellos —`ProviderId`, `ProviderName`, `IsOptional`, `IsPresent` y
`Diagnose`— traen implementación por defecto, para que los dobles de prueba no tengan que
declararlos.

| Miembro | Qué promete | Qué hace Copilot | Qué hace Claude Code |
|---|---|---|---|
| `ProviderId` · `IAuditorProvider.cs:80` (def. `GetType().Name`) | El identificador que se ESCRIBE en la sesión, el hallazgo y el informe; no puede cambiar nunca | Constante `Id = "copilot"` · `RealCopilotAgent.cs:86`, devuelta en `:92` | Constante `Id = "claude-code"` · `ClaudeCodeProvider.cs:35`, devuelta en `:145` |
| `ProviderName` · `:83` (def. = `ProviderId`) | Cómo se llama para una persona | `"GitHub Copilot"` · `RealCopilotAgent.cs:95` | `"Claude Code"` · `ClaudeCodeProvider.cs:148` |
| `IsOptional` · `:99` (def. `false`) | Este proveedor es un extra y no se le exige a nadie | **No lo declara**: se queda en `false`. Es el requisito del equipo | `true` · `ClaudeCodeProvider.cs:155` |
| `IsPresent` · `:109` (def. `true`) | ¿Está en esta máquina? Comprobación barata: sin red, sin credencial, sin cuota | **No lo declara**: se queda en `true`. El runtime viaja dentro del paquete del SDK (`CopilotCliLocator.cs`) | `ResolveCli() is not null` · `ClaudeCodeProvider.cs:158`, que mira el PATH o el localizador inyectado (`ClaudeCliLocator.cs:78`) |
| `ModelName` · `:112` | El modelo en uso, para el registro de la sesión | `_modelProvider()` → `settings.CopilotModel`, leído en cada lectura · `RealCopilotAgent.cs:117`, cableado en `App.xaml.cs:296` | `_modelProvider()` → `settings.ClaudeCodeModel` · `ClaudeCodeProvider.cs:161`, cableado en `App.xaml.cs:318` |
| `event TextStreamed` · `:115` | El texto del asistente según llega, para la vista en vivo | Evento del SDK traducido en `OnSessionEvent` · declarado en `RealCopilotAgent.cs:119`, disparado en `:742` y `:758` | Callback que el lector del CLI invoca · declarado en `ClaudeCodeProvider.cs:170`, pasado al runner en `:365`, `:510` y `:588` |
| `event UsageReported` · `:118` | El consumo por llamada, que acumula la sesión | `UsageAdapter.From(usage.Data)` · `RealCopilotAgent.cs:680`; el adaptador lee los campos de caché por reflexión y no inventa ceros (`UsageAdapter.cs:23`) | Lo emite `ClaudeStreamReader` (`:291`, `:400`, `:417`): anticipo, definitivo por llamada y cuadre final; el driver le pone el modelo · `ClaudeCodeProvider.cs:366` |
| `EnsureReadyAsync` · `:121` | ¿Puede correr? Falso significa que hace falta autenticarse | `(await CheckAsync(ct)).Ready` · `RealCopilotAgent.cs:139` | Idéntico · `ClaudeCodeProvider.cs:177` |
| `CheckAsync` · `:124` | El diagnóstico detallado —estado y motivo— para la pantalla Cuenta | `GetAuthStatusAsync` del SDK y, si autentica, un `ListModelsAsync` que ejercita el asiento; la cuota se clasifica antes que el asiento · `RealCopilotAgent.cs:147-193` | Dos preguntas separadas: ¿está el CLI? y `claude auth status --json`; una respuesta que no es el JSON esperado sale como desconocida con el crudo · `ClaudeCodeProvider.cs:193-232` |
| `ListModelsAsync` · `:131` | Los modelos que la cuenta puede usar; lanza si no se puede preguntar | Del runtime, con el multiplicador de facturación cuando lo publica · `RealCopilotAgent.cs:201-212` | **Tres alias escritos a mano** —`opus`, `sonnet`, `haiku`— porque el CLI no publica lista; sin multiplicador, que sería inventado · `ClaudeCodeProvider.cs:251-267` |
| `AuditUnitAsync` · `:134` | Audita UNA unidad reportando por el toolbox y terminando en `unit_done` | `EnsureStartedAsync` y una sesión del SDK con el catálogo de `BuildAuditSessionConfig` · `RealCopilotAgent.cs:214-218` | Lanza el CLI con el servidor MCP y, si el CLI admite `--include-partial-messages`, con el corte en `unit_done` armado · `ClaudeCodeProvider.cs:286-308` |
| `VerifyAsync` · `:137` | Re-verifica hallazgos reportando veredictos por el toolbox | Una sesión con una sola tool, `submit_verdict` · `RealCopilotAgent.cs:334-346` | `RunSessionAsync` con `AuditorTools.ForVerify`; ni corte ni hilo, es de una llamada · `ClaudeCodeProvider.cs:310` |
| `Diagnose` · `:154` (def.: respeta lo ya clasificado, si no «desconocido» con el crudo) | Traducir el dialecto de ESTA casa a la taxonomía común | `CopilotFailure.Diagnose` sobre los errores del SDK · `RealCopilotAgent.cs:101`; el clasificador en `CopilotFailure.cs` | `ClaudeFailure.Classify` sobre el evento `result` —texto, `terminal_reason` y código HTTP— más el texto de ayuda · `ClaudeCodeProvider.cs:448-458`, clasificador en `ClaudeFailure.cs:29` |

El valor por defecto de `Diagnose` no corre nunca en producción: las dos casas lo sobrescriben.
Existe para los dobles de prueba.

### Lo que se resuelve fuera del contrato

| Capacidad | ¿En el contrato? | Cómo lo resuelve Copilot | Cómo lo resuelve Claude Code |
|---|---|---|---|
| **Autenticación: el veredicto** | **Sí** — `CheckAsync` / `EnsureReadyAsync`, y el motivo en `AgentProblem` | `RealCopilotAgent.cs:147` | `ClaudeCodeProvider.cs:193` |
| **Autenticación: dónde vive la credencial** | **No** | El token de la cuenta de GitHub, inyectado como función y releído en cada arranque de sesión (`RealCopilotAgent.cs:73`, cableado en `App.xaml.cs:303`); sin token, el SDK cae a `UseLoggedInUser` y usa lo que dejó `copilot /login`. El blob vive cifrado con DPAPI del usuario en `auth.dat` (`AccountStore.cs:48-64`) | **Ninguna.** Atalaya no guarda nada de Anthropic: usa la sesión que el CLI ya tenga (`ClaudeCodeProvider.cs:24-28`, `:213`) |
| **Tarifas: ¿esta casa factura?** | **No** — el proveedor no tiene nada que decir | Factura: es el caso que `CreditCalculator.Calculate` valora (`CreditCalculator.cs:203`) | No factura, y lo decide un `if` por cadena: `IsBilled` compara con `"claude-code"` · `CreditCalculator.cs:185-186` |
| **Tarifas: cómo cuentan los tokens de entrada** | **No** | `TokenAccounting.InputIncludesCache`, por un `switch` sobre `"copilot"` y sobre el vacío del histórico · `CreditCalculator.cs:188-196` | `InputExcludesCache`, por el mismo `switch` · `CreditCalculator.cs:192` |
| **Reporte de consumo** | **Sí** — `UsageReported` y `UsageSample`, con `CostUnit`, `Calls` y `Reconciliation` | Una muestra por llamada, vía `UsageAdapter` · `RealCopilotAgent.cs:680` | Tres clases de muestra por turno —anticipo, definitiva y cuadre—, con `Calls = 0` en las que no son llamadas nuevas · `ClaudeStreamReader.cs:291`, `:400`, `:417`; la unidad declarada es `ClaudeUsage.ListPriceUnit` (`ClaudeUsage.cs:33`) |
| **Herramientas: qué se ejecuta** | **Sí** — `IAuditToolbox` e `IVerifyToolbox` son el contrato | `RealCopilotAgent.cs:264-333` construye el `SessionConfig` sobre el toolbox | `AuditorTools.ForAudit` / `ForVerify` construyen tools MCP sobre el mismo toolbox · `AuditorTools.cs:48`, `:174` |
| **Herramientas: el catálogo que ve el modelo** | **No** — nombres, descripciones y esquemas los escribe cada driver | Seis `AddTool` con el texto literal · `RealCopilotAgent.cs:303-330` (auditoría) y `:342` (verificación) | Seis `McpTool` con **el mismo texto, copiado** · `AuditorTools.cs:80-138` y `:177` (verificación). El servidor se sirve por una tubería con nombre (`McpPipeHost.cs`) y el CLI se lanza con `--tools ""`, `--allowedTools` exacta, `--strict-mcp-config` y `--setting-sources ""` · `ClaudeCliRunner.cs:170-192` |
| **Streaming del texto** | **Sí** — `TextStreamed` | `RealCopilotAgent.cs:742` | `ClaudeCodeProvider.cs:365` |
| **Streaming de la herramienta que se escribe (hilo de actividad)** | **No** en `IAuditorProvider`; **sí** como capacidad opcional declarada: `INarratingAuditor.ToolStreamed` (`ToolStream.cs:63-66`) | La implementa; lee `ToolExecutionStartEvent` y `AssistantToolCallDeltaEvent.InputDelta` · `RealCopilotAgent.cs:129`, `:694`, `:717` | La implementa; lee `input_json_delta` y los bloques `thinking` · `ClaudeCodeProvider.cs:114`, `:370` |
| **Cancelación: la petición** | **Sí** — todos los métodos toman `CancellationToken` | `SendAndWaitAsync(prompt, timeout, ct)`; el turno se corta dejando la sesión utilizable · `RealCopilotAgent.cs:385`, `CopilotUnitThread.cs:80-90` | Al cancelarse el token se **mata el proceso** del CLI · `ClaudeCliRunner.cs:289`, `:527` |
| **Cancelación: el corte en `unit_done`** | **No** | No existe: el SDK no cobra la llamada de cortesía | `ToolRetention` + `ClaudeCut`: se retiene la respuesta y se manda un `interrupt` por stdin, nunca un kill · `ClaudeCliRunner.cs:326-410`. Lo que no se pudo cortar se avisa por un evento **propio del tipo**, `CutSkipped` (`ClaudeCodeProvider.cs:106`), que el coordinador escucha con `_agent as ClaudeCodeProvider` · `SessionCoordinator.cs:534-538` |
| **Tope de tiempo** | **No** | `SendTimeout`, propiedad del tipo concreto, releída en cada envío · `RealCopilotAgent.cs:108-114`; sale del ajuste `copilotTimeoutMinutes`, 15 min por defecto, mínimo 1 (`SettingsService.cs:229`, `App.xaml.cs:300`) | **No hay tope por turno.** El único reloj es `ClaudeCliRunner.IdleTimeout`, 10 s, y es del corte, no de la pasada · `ClaudeCliRunner.cs:83` |
| **El modo: conversación por unidad o petición por pasada** | **No** en `IAuditorProvider`; **sí** como capacidad opcional declarada: `IThreadedAuditor` / `IUnitThread` (`UnitThread.cs:26-55`, D-915) | La implementa: una sesión del SDK viva por unidad, un `SendAndWaitAsync` por pasada · `RealCopilotAgent.cs:238-255`, `CopilotUnitThread.cs` | La implementa: una invocación del CLI viva por unidad, con `--input-format stream-json` · `ClaudeCodeProvider.cs:329-371`, `ClaudeUnitThread.cs` |
| **El modo: quién decide cuál se usa** | **No** — lo decide el coordinador | `SessionCoordinator.OpenThreadAsync` pregunta `_agent is IThreadedAuditor` (`:1432`); si no sabe hilar, avisa una vez por sesión y deja nota por unidad (`:1437-1448`). El modo exhaustivo no es un tercer camino: pone a `true` ese mismo `threadless` · `SessionCoordinator.cs:675-692`, leído de `settings.ExhaustiveSweep` al crear la sesión (`:356`) | Igual: no hay `if` por nombre en este camino |
| **Los ULIDs creados en el barrido** | **No** en el proveedor; capacidad opcional declarada **del toolbox**: `ISweepCreations` (`UnitThread.cs:94-98`) | `toolbox as ISweepCreations`, y el casado por orden en `SweepReceipts` · `RealCopilotAgent.cs:267-287` | `toolbox is ISweepCreations`, y el **mismo** `SweepReceipts` · `AuditorTools.cs:144`, `:152-171` |
| **Modelo desconocido: antes de lanzar** | **Parcial** — `ListModelsAsync` está en el contrato; qué hacer con lo que devuelve, no | La lista sale del runtime, así que un id retirado se detecta | La lista son tres alias fijos, así que cualquier id completo escrito a mano queda «fuera de lista» |
| **Modelo desconocido: la decisión** | **No** — vive en `ModelResolver` | No se puede preguntar → se sigue con el configurado y el motivo lo clasifica el proveedor (`ModelResolver.cs:85-105`). Lista vacía → se enumeran asiento, política y cuota sin afirmar ninguna (`:110-122`). Configurado fuera de lista → **se reescribe al primero de la lista y se guarda** (`:130-136`) | El mismo código, sin ninguna rama por proveedor |
| **Modelo desconocido: en marcha** | **Sí** — `AgentProblem.ModelUnavailable` es del vocabulario común (`AuditorReadiness.cs:38`) | `CopilotFailure` lo clasifica y el driver lanza `AuditorModelUnavailableException` · `RealCopilotAgent.cs:586` | `ClaudeFailure` lo clasifica por `api_error_status == 404` o por `unrecognized_model` · `ClaudeFailure.cs:43`, `:73-76`; el driver lanza la misma excepción · `ClaudeCodeProvider.cs:385` |
| **Qué modelo se guarda para cada casa** | **No** | `settings.CopilotModel`, por la rama `else` de `SettingsService.IsClaudeCode` · `SettingsService.cs:596`, `:602`, `:614` | `settings.ClaudeCodeModel`, por la rama `if`. Y el `switch` está **repetido** en la vista · `SettingsViewModel.cs:651-663` |
| **Cómo se nombra una casa en el histórico** | **No** — los informes y las métricas leen sesiones sin registro delante | `switch` sobre `RealCopilotAgent.Id`, con el vacío tratado como Copilot · `ProviderNames.cs:38-43`; lo mismo en `CostEstimator.cs:178` y `MetricsQuery.cs:1527` | `switch` sobre `ClaudeCodeProvider.Id` · `ProviderNames.cs:41` |
| **Quién es el de fábrica** | **No** | `AuditorProviderRegistry.Fallback` busca por `p.ProviderId == RealCopilotAgent.Id` · `AuditorProviderRegistry.cs:56` | Nada: es a quien se cae, no a quien se vuelve |
| **Que el proveedor sea opcional y esté o no** | **Sí** — `IsOptional` / `IsPresent`, y `Current` los consulta sin nombrar a nadie · `AuditorProviderRegistry.cs:67-73`, `:98-99` | Por defecto: obligatorio y presente | `true` y el localizador · `ClaudeCodeProvider.cs:155`, `:158` |
| **Arreglo asistido** | **No** — `IAssistedFixProvider`, capacidad aparte (D-804), resuelta con `Current as IAssistedFixProvider` · `AuditorProviderRegistry.cs:91` | `FixAsync` sobre una sesión del SDK · `RealCopilotAgent.cs:364` | `FixAsync` por `--input-format stream-json` · `ClaudeCodeProvider.cs:470` en adelante |

#### La cuenta

Veintitrés capacidades miradas, una por fila. **Siete están dentro del contrato**: el veredicto de
autenticación (`CheckAsync`/`EnsureReadyAsync`), el reporte de consumo (`UsageReported`), la
ejecución de las herramientas (`IAuditToolbox`/`IVerifyToolbox`), el streaming del texto
(`TextStreamed`), la cancelación de la petición (el `CancellationToken` de cada método), el
diagnóstico de un modelo que falla en marcha (`AgentProblem.ModelUnavailable`) y la opcionalidad
(`IsOptional`/`IsPresent`). **Una está a medias**: los modelos antes de lanzar, donde
`ListModelsAsync` sí es del contrato pero qué se hace con lo que devuelve no. **Quince quedan
fuera.**

De esas quince, **cinco filas se resuelven por una capacidad opcional bien declarada**, y son
cuatro interfaces:

- `IThreadedAuditor` / `IUnitThread` — el modo de barrido, y también quién lo elige (D-915).
- `INarratingAuditor` — el hilo de actividad (D-1016).
- `ISweepCreations` — los ULIDs creados, declarada sobre el **toolbox** y no sobre el proveedor.
- `IAssistedFixProvider` — el arreglo (D-804).

Las cuatro se consultan con un `is` o un `as` sobre un **tipo del vocabulario común**, en
`Atalaya.Agents`: quien no sabe hacer algo no se entera de que existe, y quien pregunta no nombra
a nadie.

**Seis filas están resueltas por un `if` que nombra al proveedor**, repartidas en nueve sitios:
`CreditCalculator.IsBilled` (`:185`), `CreditCalculator.AccountingOf` (`:188`),
`SettingsService.IsClaudeCode` (`:614`) y su copia en `SettingsViewModel` (`:655`),
`ProviderNames.Display` (`:38`), `CostEstimator` (`:178`), `MetricsQuery.LegacyProviderId`
(`:1527`), `AuditorProviderRegistry.Fallback` (`:56`) y el `_agent as ClaudeCodeProvider` de
`SessionCoordinator` (`:534`).

**Las cuatro que quedan no son ni una cosa ni la otra**: cada driver las resuelve por dentro y la
aplicación no se ramifica por nadie. Son dónde vive la credencial, el catálogo de herramientas que
ve el modelo, la decisión sobre un modelo fuera de lista (`ModelResolver`, sin una sola rama por
proveedor) y el tope de tiempo. Esta última solo lo parece: `SendTimeout` es una propiedad de
`RealCopilotAgent` y el ajuste se llama `copilotTimeoutMinutes`, así que el nombre del proveedor
está igual de metido, solo que en el tipo y en el fichero de ajustes en vez de en un `if`.

**Así que la respuesta a la pregunta de ESTADO es que la capacidad bien declarada es la
excepción, y tiene una frontera nítida.** Dentro del motor del barrido —`Atalaya.Agents` y
`SessionCoordinator`— la norma es la capacidad declarada, y se cumple. Fuera de él —el dinero en
`Atalaya.Domain`, los ajustes y las vistas en `Atalaya.App`— la norma es el `if` por cadena. El
único `if` por nombre **dentro** del coordinador es el de `CutSkipped`, y el propio comentario del
código lo justifica diciendo que el corte es de esa casa (`SessionCoordinator.cs:519-520`).

La frontera tiene una explicación que el código declara y que no es un descuido: el dinero y los
nombres se aplican sobre sesiones **ya guardadas**, que Métricas relee meses después, y el
proveedor que las escribió puede no estar registrado hoy o no existir ya en esta versión
(`CreditCalculator.cs:156-159`, `ProviderNames.cs:9-25`). Un `IsBilled` que preguntara al
proveedor vivo no sabría qué contestar sobre una sesión de una casa retirada. Eso explica el mapa
de lectura del histórico; no explica que el ajuste de modelo y el desplegable de Ajustes, que
hablan de proveedores vivos, también vayan por cadena.

### Lo que se ve y no se toca

1. **Los textos de las seis herramientas de auditoría están duplicados a mano.** D-777 dice que
   son las mismas «palabra por palabra», y hoy lo son: `RealCopilotAgent.cs:303-330` y
   `AuditorTools.cs:80-138` llevan el mismo párrafo escrito dos veces. El arreglo asistido sí
   tiene fuente única —`FixToolText` en `Atalaya.Agents`, que leen los dos drivers
   (`RealCopilotAgent.cs:456-459`, `FixTools.cs:72-120`)—, y su test lo comprueba
   (`tests/Atalaya.ClaudeCode.Tests/FixSurfaceTests.cs:60`). El del auditor no: el único test que
   toca el catálogo de Copilot compara los **nombres** contra una lista escrita a mano
   (`tests/Atalaya.Copilot.Tests/UnitThreadSurfaceTests.cs:29-37`) y ningún test compara los dos
   textos entre sí. La igualdad la sostiene la disciplina.

2. **El tope de tiempo solo existe en una casa.** Copilot tiene `SendTimeout` releído en cada
   envío; Claude Code no tiene ningún tope por turno —`IdleTimeout` (`ClaudeCliRunner.cs:83`) es
   del corte, 10 s—. Una pasada de Claude Code que se quede colgada solo la para el usuario. Y el
   ajuste que la gobierna en la otra casa se llama `copilotTimeoutMinutes` y se enseña como «el
   timeout de Copilot» (`SettingsViewModel.cs:665-666`).

3. **`ModelResolver` puede reescribir un modelo válido de Claude Code.** Si el configurado no
   está en `ListModelsAsync`, se cambia al primero de la lista y se **guarda**
   (`ModelResolver.cs:130-136`), y esto corre antes de cada sesión en vivo
   (`LiveSessionService.cs:662`). Para Claude Code la lista son tres alias fijos, mientras la
   documentación del propio método dice que «un identificador completo se puede seguir escribiendo
   a mano en Ajustes» (`ClaudeCodeProvider.cs:261-263`). Ese id se reescribiría a `opus` en el
   siguiente lanzamiento. No lo he ejecutado; es lo que dice el código.

4. **`CreditCalculator.IsBilled` decide por cadena y nadie más puede opinar.** Un tercer proveedor
   que tampoco facturara a la organización nacería facturable —es la suposición conservadora, y
   está declarada (`CreditCalculator.cs:180-183`)—, y para arreglarlo habría que tocar `Domain`.
   `IAuditorProvider` no tiene ningún miembro sobre esto.

5. **`RealCopilotAgent` implementa `IAsyncDisposable` y `ClaudeCodeProvider` no.** Los dos se
   registran como singletons (`App.xaml.cs:288`, `:311`). La asimetría no es visible desde el
   contrato.

6. **`AuditUnitAsync` sigue en el contrato aunque en producción el barrido va por
   `IThreadedAuditor`.** Es el camino de respaldo y el del modo exhaustivo, así que no sobra; pero
   el contrato tiene dos formas de auditar una unidad y solo una de ellas es obligatoria.

7. **`IAssistedFixProvider` hereda de `IAuditorProvider`.** ESTADO dice «separada de la interfaz
   del auditor» (D-804); en el código es un método aparte sobre la misma herencia, así que quien
   quiera implementar solo el arreglo tiene que implementar antes el contrato entero del auditor.

No propongo nada sobre ninguno de los siete. Esta entrega dice qué hay.

### Lo que ESTADO no me dio

- **Los miembros del contrato.** ESTADO nombra `IAuditorProvider` y dice dónde vive el vocabulario
  (D-775, línea 611), pero no lista ni un miembro. Fui a `src/Atalaya.Agents/IAuditorProvider.cs`.
- **Que cinco miembros tienen implementación por defecto** —y por qué: los dobles de prueba—.
  Mismo fichero, `:67-99` y `:139-157`.
- **Dónde vive el token de GitHub.** ESTADO da el CÓMO en otra sección: «Copilot autentica con
  `GitHubToken` y `UseLoggedInUser` a false» (D-041, línea 776, sección «Setup, conexión,
  despliegue y marca»), no en la de proveedores. El DÓNDE —`auth.dat` cifrado con DPAPI del
  usuario— salió de `src/Atalaya.App/Services/AccountStore.cs:48-64`.
- **Que `IsBilled` es una comparación literal con `"claude-code"`.** ESTADO dice el hecho de
  producto («el consumo de Claude Code no se tarifa», F16-RETOQUE-2 y D-821) pero no la forma.
  `src/Atalaya.Domain/Model/CreditCalculator.cs:185`.
- **Que el tope de tiempo es solo de Copilot.** ESTADO lo insinúa en «Gobernanza» —«Personales:
  tope de pasadas, modelo y timeout de Copilot» (D-773, línea 46)— y no en la sección de
  proveedores, que era donde yo miraba. Lo confirmé en `RealCopilotAgent.cs:108` y por ausencia en
  `ClaudeCliRunner.cs`.
- **Que el catálogo de tools del auditor está duplicado.** D-777 dice «palabra por palabra» y
  D-805 dice que el del arreglo sale de `FixToolText`; el contraste se deduce, pero hay que abrir
  los dos ficheros para saber que el del auditor no tiene fuente única y que ningún test lo
  compara.
- **`ISweepCreations`.** D-925 (línea 156) cuenta el comportamiento —«los dos submit devuelven el
  ULID»— pero no que sea una capacidad opcional declarada sobre el toolbox.
  `src/Atalaya.Agents/UnitThread.cs:94`.
- **Que `CutSkipped` se escucha con un `as ClaudeCodeProvider`.** ESTADO cuenta el corte entero
  (D-880, D-878) sin decir que el aviso viaja por un evento que no está en ninguna interfaz.
  `SessionCoordinator.cs:534`.
- **Qué hace `ModelResolver` con un modelo fuera de lista.** ESTADO cubre la lista vacía (D-711) y
  la tarifa que falta (D-787), no el reescrito silencioso. `ModelResolver.cs:130`.
- **Que `IAssistedFixProvider` EXTIENDE `IAuditorProvider`.** Es el único punto donde ESTADO me
  hizo esperar otra cosa: D-804 dice «separada de la interfaz del auditor» y
  `FixContracts.cs:188` dice `IAssistedFixProvider : IAuditorProvider`. Las dos frases son
  compatibles —el método está separado— pero la palabra «separada» me llevó a buscar dos jerarquías
  y no las hay.

**No abrí `DECISIONS.md` ni una vez.** Las frases de ESTADO con su `D-` delante bastaron para
saber qué buscar; lo que faltó en todos los casos fue el CÓMO está escrito, y eso solo lo tiene el
código. Para las dos secciones que el encargo señalaba —«Barrido y pasadas» y «Arreglo con
agente»— ESTADO fue suficiente para orientarme: sabía que el hilo era la producción, que el modo
exhaustivo reusaba `threadless` y que el arreglo iba por otra interfaz antes de abrir un `.cs`.

---

## 3 · El catálogo de herramientas

Medida de PROV-1, comportamiento 3. Todo lo de aquí está leído en el código del árbol de trabajo
limpio en `4ef4a87`. No se ha tocado nada.

Dos afirmaciones de ESTADO se comprueban antes que nada:

- **«`Atalaya.Mcp` es un relé entre stdio y una tubería con nombre, aleatoria y de un solo uso»
  (D-777): CIERTO.** El proyecto entero son 84 líneas (`src/Atalaya.Mcp/Program.cs`), no parsea JSON
  y no conoce ninguna tool: copia bytes de su stdin a la tubería y de la tubería a su stdout
  (`Program.cs:41-47`), con descarga inmediata (`Program.cs:66-85`). El nombre lo genera Atalaya con
  16 bytes de `RandomNumberGenerator` (`src/Atalaya.ClaudeCode/McpPipeHost.cs:43`) y la tubería se
  abre con `maxNumberOfServerInstances: 1` (`McpPipeHost.cs:66-71`), que es el «un solo uso».
- **«Las tools que ve Claude Code son las mismas que ve Copilot, palabra por palabra» (D-777):
  CIERTO SALVO EN UNA.** `unit_done` NO dice lo mismo en las dos casas. En Copilot la terminalidad
  la declara el SDK (`RealCopilotAgent.cs:327`, `terminal: true`); en MCP no existe ese concepto, así
  que el texto de la descripción lleva una frase de más —«Cuando la llames, HAS TERMINADO: no digas
  nada más.»— que Copilot no ve (`AuditorTools.cs:124`). Las otras seis coinciden carácter a
  carácter. En el arreglo la paridad sí está garantizada por construcción: el texto sale de
  `FixToolText` (D-805) y hay dos tests que lo afirman.

### 3.1 · La lista real de herramientas

Ficheros implicados:

- Copilot, auditoría y verificación: `src/Atalaya.Copilot/RealCopilotAgent.cs`
- Copilot, arreglo: `src/Atalaya.Copilot/RealCopilotAgent.cs` (`BuildFixSessionConfig`)
- Claude Code, auditoría y verificación: `src/Atalaya.ClaudeCode/AuditorTools.cs`
- Claude Code, arreglo: `src/Atalaya.ClaudeCode/FixTools.cs`
- Fuente única del texto del arreglo: `src/Atalaya.Agents/FixToolText.cs`

**Auditoría (sesión de barrido).** El texto vive DOS veces, copiado a mano: no hay constante
compartida como en el arreglo.

| Herramienta | Qué hace | Cómo la ve Copilot | Cómo la ve Claude Code | ¿Mismo texto? |
| --- | --- | --- | --- | --- |
| `submit_findings` | Reporta todos los hallazgos nuevos de la unidad en una llamada; devuelve `{accepted, duplicateOf, error, id}` por cada uno | `RealCopilotAgent.cs:303-306` (`AddTool`, delegado tipado del SDK) | `AuditorTools.cs:80-86` (`McpTool` + JSON Schema a mano) | Sí |
| `submit_finding` | Fallback singular del anterior | `RealCopilotAgent.cs:307-309` | `AuditorTools.cs:88-93` | Sí |
| `report_verdicts` | Un veredicto por cada hallazgo existente listado en el prompt (D-077) | `RealCopilotAgent.cs:310-316` | `AuditorTools.cs:95-104` | Sí |
| `add_locations` | Extiende un hallazgo existente con más ubicaciones de la misma unidad (D-091) | `RealCopilotAgent.cs:317-321` | `AuditorTools.cs:106-117` | Sí |
| `unit_done` | Cierra la unidad con resumen y lo silenciado por patrón | `RealCopilotAgent.cs:322-327`, con `terminal: true` | `AuditorTools.cs:119-133`, terminalidad METIDA EN EL TEXTO | **No** (una frase de más en Claude Code) |
| `read_signatures` | Firmas —no cuerpos— de las dependencias directas de la unidad | `RealCopilotAgent.cs:328-329` | `AuditorTools.cs:135-139` | Sí |
| `submit_verdict` | Única tool de una sesión de VERIFICACIÓN: confirmado / resuelto / no-verificable | `RealCopilotAgent.cs:342-343` | `AuditorTools.cs:177-189` | Sí |

Los dos `submit` devuelven además el ULID de lo que crean, y el criterio de casado es uno solo para
las dos casas: `SweepReceipts` (`AuditorTools.cs:152-171` y `RealCopilotAgent.cs:270-289`).

**Arreglo asistido.** Aquí `FixToolText` (D-805) es fuente única del NOMBRE y la DESCRIPCIÓN de las
cinco; los dos drivers la leen y no copian nada.

| Herramienta | Qué hace | Cómo la ve Copilot | Cómo la ve Claude Code | ¿Mismo texto? |
| --- | --- | --- | --- | --- |
| `read_file(path, startLine, endLine)` | Lee un fichero del clon, por rango, con presupuesto | `RealCopilotAgent.cs:456` ← `FixToolText.cs:25-34` | `FixTools.cs:71-79` ← `FixToolText.cs:25-34` | Sí, por constante |
| `apply_edit(path, reason, edits[])` | La única forma de modificar código | `RealCopilotAgent.cs:457` ← `FixToolText.cs:36-43` | `FixTools.cs:81-89` ← `FixToolText.cs:36-43` | Sí, por constante |
| `run_build_and_tests()` | Pide a Atalaya que compile y pase los tests. Sin argumentos (D-548) | `RealCopilotAgent.cs:458` ← `FixToolText.cs:45-50` | `FixTools.cs:91-97` ← `FixToolText.cs:45-50` | Sí, por constante |
| `fix_done(...)` | Cierra el arreglo con resumen, título y descripción de commit y riesgos | `RealCopilotAgent.cs:459` ← `FixToolText.cs:54-58`, terminal por el SDK | `FixTools.cs:99-116` ← `FixToolText.cs:54-58`, terminal por el driver (`FixToolSet.Closed`, `FixTools.cs:17-24`) | Sí el texto; la terminalidad la declara cada transporte |
| `ask_user(question, choices, allowFreeform)` | Pregunta al usuario y bloquea hasta que conteste | **No se declara**: la pone el runtime del SDK y desemboca en `config.OnUserInputRequest` (`RealCopilotAgent.cs:438-450`) | `FixTools.cs:118-143` ← `FixToolText.cs:64-71` | **No comparable**: Copilot usa la descripción del runtime, no la de Atalaya |

Fuente única `FixToolText`: las cinco por nombre (`FixToolText.All`, `FixToolText.cs:74-77`), y las
descripciones de **cuatro** en la práctica —`read_file`, `apply_edit`, `run_build_and_tests` y
`fix_done`—. `AskUserDescription` (`FixToolText.cs:66-71`) existe y está declarada como compartida,
pero solo la lee Claude Code: en Copilot esa quinta la describe el SDK. Ninguna tool de AUDITORÍA
pasa por `FixToolText`.

**Por qué vía cada una.** Copilot: `GitHub.Copilot.SDK` in-process, `CopilotTool.DefineTool` sobre un
delegado C# tipado, el esquema lo deriva el SDK de la firma (`RealCopilotAgent.cs:531-538`). Claude
Code: catálogo MCP servido por Atalaya —`AtalayaMcpServer` dentro de la aplicación
(`src/Atalaya.ClaudeCode/AtalayaMcpServer.cs`)— sobre una tubería con nombre (`McpPipeHost`), con
`Atalaya.Mcp` de relé; el CLI ve los nombres cualificados `mcp__atalaya__<tool>`
(`AuditorTools.cs:32-35`) y la lista de `--allowedTools` se DERIVA del catálogo, no se escribe a
mano (`ClaudeCodeProvider.cs:358` en auditoría, `:506` en arreglo). El esquema JSON, en cambio, se
escribe a mano tool por tool (`McpTool.cs:33-82`).

### 3.2 · Las guardas y dónde viven

Criterio de la columna «dónde vive»: **dominio compartido** = `Atalaya.Agents`, `Atalaya.Domain` o
`Atalaya.App/Services` —código que los dos proveedores atraviesan sin saberlo—; **transporte de un
proveedor** = `Atalaya.Copilot`, `Atalaya.ClaudeCode` o `Atalaya.Mcp`.

| Guarda | Qué protege | Fichero:línea | Dónde vive | ¿La heredaría un proveedor nuevo? |
| --- | --- | --- | --- | --- |
| Lectura por rango con `totalLines`/`firstLine`/`lastLine` y `notice` aparte (D-1032) | Que un fichero grande se pueda leer entero a trozos en vez de perderse | `src/Atalaya.App/Services/FixToolbox.cs:203-275` | Dominio compartido | Sí |
| Troceo por líneas ENTERAS, nunca a mitad (D-1032) | Que la concatenación de rangos sea el fichero byte a byte | `FixToolbox.cs:205-218, 241-263` | Dominio compartido | Sí |
| Tope de lectura de 120.000 caracteres (D-1032) | Que una lectura no se coma el turno; una línea más larga que el tope se dice con esas palabras | `FixToolbox.cs:70` y `:241-259` | Dominio compartido | Sí |
| Presupuesto de 30 lecturas (D-526, D-1032) | Que explorar no sustituya a arreglar | `FixToolbox.cs:62` y `:159-170` | Dominio compartido | Sí |
| `FixAskGuard`: rechaza pedirle al usuario que pegue código (D-1032) | Que el usuario no se convierta en la herramienta de lectura del agente | `src/Atalaya.Agents/FixAskGuard.cs:25-86`, aplicado en `src/Atalaya.App/Services/LiveFixService.cs:915-921` | Dominio compartido (la regla en `Agents`, la puerta en `App`) | Sí: cuelga de `IUserQuestions`, no del driver |
| `apply_edit` conserva el BOM (D-1035) | Que una edición no mueva los tres bytes de cabecera y ensucie el diff | `FixToolbox.cs:401` + `EncodingOf` en `:547-617` | Dominio compartido | Sí |
| `apply_edit` conserva fin de línea y final de fichero (D-1035) | Que lo que está fuera del fragmento salga idéntico | `FixToolbox.cs:342-401` (sustitución literal sobre la cadena leída; nunca se recomponen separadores) | Dominio compartido | Sí |
| Fichero nuevo sin BOM (D-1035) | No añadir marca donde no la había | `FixToolbox.cs:557-560` y `:616-617` (`NoPreamble`) | Dominio compartido | Sí |
| Fragmento ausente → error; ambiguo sin `replaceAll` → error (D-545) | Que no se edite a ciegas | `FixToolbox.cs:364-377` | Dominio compartido | Sí |
| `oldText` vacío solo sirve para CREAR (D-545) | Que no se pise un fichero entero «sin querer» | `FixToolbox.cs:348-362` | Dominio compartido | Sí |
| Una edición que no cambia nada se rechaza | Que un no-cambio no cuente como arreglo | `FixToolbox.cs:385-389` | Dominio compartido | Sí |
| Rutas comprobadas en CANÓNICO, no por texto (D-549) | `..\..\otra-cosa` y los enlaces simbólicos | `FixToolbox.cs:470-511` | Dominio compartido | Sí |
| Permiso fichero a fichero fuera del hallazgo, y no se vuelve a preguntar por el mismo (D-546) | Que el ámbito lo decida el usuario | `FixToolbox.cs:300-330` | Dominio compartido | Sí |
| El test del hallazgo entra sin preguntar, por nombre (D-547) | Que la prueba del defecto no sea un trámite | `FixToolbox.cs:513-545` | Dominio compartido | Sí |
| Copia de seguridad ANTES de la primera edición (D-539) | La reversibilidad | `FixToolbox.cs:332-336` | Dominio compartido | Sí |
| `findingId` acotado a los ULID listados en la unidad (D-077) | Que un ULID inventado no toque nada | `src/Atalaya.App/Services/SessionToolbox.cs:357` y `:466` | Dominio compartido | Sí |
| `add_locations` rechaza ubicaciones fuera de la unidad (D-091) | Que un defecto sistémico no se extienda a lo que no se está auditando | `SessionToolbox.cs:378` | Dominio compartido | Sí |
| `SweepReceipts`: un solo criterio para casar ULID con lo creado (D-916) | Que dos casas no cuenten distinto | `src/Atalaya.Agents/SweepReceipts.cs`, usado en `AuditorTools.cs:164-170` y `RealCopilotAgent.cs:276-288` | Dominio compartido | Sí |
| `FixToolText` como fuente única de nombre y descripción (D-805) | Que una diferencia entre casas siga siendo atribuible al modelo | `src/Atalaya.Agents/FixToolText.cs` | Dominio compartido | Sí, si el proveedor nuevo la lee |
| Tope de 3 minutos del commit, que mata el proceso (D-1033) | Que «Me quedo los cambios» termine siempre | `src/Atalaya.App/Services/FixCommitter.cs:75`, `:183`, `:195` | Dominio compartido | Sí |
| `commit --only` + preparar ruta a ruta + `git reset` ruta a ruta si falla (D-1033, D-1059) | Que el commit no se lleve el resto del árbol del usuario | `FixCommitter.cs:154-176`, `:239-295` | Dominio compartido | Sí |
| `run_build_and_tests` SIN argumentos (D-548) | Que una tool no se convierta en una shell | Esquema vacío en `FixTools.cs:94-97`; delegado sin parámetros en `RealCopilotAgent.cs:425` | **Transporte de cada proveedor** (la ejecución sí es dominio: `FixToolbox.RunBuildAndTests`) | No: cada transporte declara su propia firma |
| `OnPermissionRequest` que rechaza TODO lo demás (shell, ficheros, red) | La superficie cerrada en Copilot | `RealCopilotAgent.cs:523-524` | Transporte Copilot | No |
| `--tools ""`, `--allowedTools` exacta, `--strict-mcp-config`, `--setting-sources ""`, `--permission-mode dontAsk`, `--no-session-persistence` (D-777, D-807) | Lo mismo, en Claude Code: sin herramientas propias del CLI, sin MCP ajenos, sin hooks ni ajustes de la máquina | `src/Atalaya.ClaudeCode/ClaudeCliRunner.cs:171-193` | Transporte Claude Code | No |
| `--allowedTools` DERIVADA del catálogo, nunca escrita a mano | Que lo ofrecido y lo permitido no puedan discrepar | `ClaudeCodeProvider.cs:358`, `:506` + `AuditorTools.cs:32-35` | Transporte Claude Code | No |
| El directorio de trabajo del CLI NUNCA es el clon (D-807) | Que el `CLAUDE.md` del proyecto no sea un segundo canal de instrucciones | `ClaudeCliRunner.cs:146-160`, `ClaudeCodeProvider.cs:139` | Transporte Claude Code | No |
| Tubería aleatoria de 16 bytes y una sola instancia de servidor (D-777) | Que nadie más se cuelgue del toolbox vivo | `McpPipeHost.cs:43`, `:66-71` | Transporte Claude Code | No |
| El relé muere a los 10 s si Atalaya no escucha | Que el CLI marque el servidor «failed» en vez de esperar para siempre | `src/Atalaya.Mcp/Program.cs:38` | Transporte Claude Code | No |
| El relé NUNCA escribe por su stdout; lo que va mal sale por stderr con código ≠ 0 | Que una línea suelta no rompa el JSON-RPC | `src/Atalaya.Mcp/Program.cs:17-19`, `:22-26`, `:53-62` | Transporte Claude Code | No |
| Tope de 10 s del corte, con las tres condiciones esperadas y no preguntadas (D-880) | Que no se corte encima de una hermana de `unit_done` que está persistiendo hallazgos | `ClaudeCliRunner.cs:83` y `:357-380` | Transporte Claude Code | No |
| Un corte pasa por bueno solo con el sello `aborted_tools` (D-880) | Que un final por otro motivo no se declare éxito | `ClaudeCliRunner.cs:140`, `:670-681` | Transporte Claude Code | No |
| Terminalidad de `fix_done` y `unit_done` | Que no se le dé otro turno al agente después de cerrar | Copilot: `RealCopilotAgent.cs:327`, `:459`, `:535` (`IsTerminal` del SDK). Claude Code: `FixTools.cs:17-24`, `:114` y la frase de `AuditorTools.cs:124` | **Transporte de cada proveedor** | No |
| Una excepción del handler vuelve como `isError` de la tool, nunca como tubería rota | Que un fallo lo lea el modelo y se corrija | `AtalayaMcpServer.cs:303-322` | Transporte Claude Code | No |
| Las llamadas se atienden en PARALELO (`ask_user` bloquea sin callar al servidor) | Que esperar a una persona no congele el resto del turno | `AtalayaMcpServer.cs:110`, `:137-152` | Transporte Claude Code | No |
| Lectura tolerante de argumentos: un número donde se pidió texto no tumba la unidad | Que la forma no mate la sesión, y que el fondo lo rechace el toolbox | `AuditorTools.cs:192-262`, `FixTools.cs:150-219` | Transporte Claude Code (en Copilot lo hace el SDK por firma tipada) | No |
| `Schema.Object` clona cada sub-esquema (`DeepClone`) | Que reutilizar el esquema de una ubicación no reviente al ABRIR la sesión | `McpTool.cs:35-68` | Transporte Claude Code | No |

Topes de tiempo que NO tocan al proveedor, y que por eso no entran en la cuenta: los 10 s de abrir
el editor (`src/Atalaya.App/Services/EditorLauncher.cs:84`, D-208) y los 30 s del push al hub
(`src/Atalaya.Storage/Sync/HubSyncService.cs`, D-1022). El tope de 3 minutos del commit (D-1033) sí
entra: es el final del flujo de arreglo que el agente abre.

### Recuento

**36 guardas: 21 en dominio compartido y 15 en el transporte de un proveedor** (12 de ellas en
`Atalaya.ClaudeCode`/`Atalaya.Mcp`, 1 en `Atalaya.Copilot` y 2 —`run_build_and_tests` sin argumentos
y la terminalidad— duplicadas en los dos transportes porque cada uno las declara a su manera).

Un tercer proveedor **hereda gratis** todo lo que decide sobre el CLON y sobre los HALLAZGOS: el
presupuesto de 30 lecturas, el tope de 120.000 caracteres, el troceo por líneas con su `notice`, la
conservación de BOM y fin de línea, el fragmento literal ausente o ambiguo, `oldText` vacío solo para
crear, las rutas en canónico, el permiso fichero a fichero, la copia previa, `FixAskGuard`, el
acotado de `findingId`, `SweepReceipts` y el commit con `--only` y su tope de 3 minutos. Son guardas
del toolbox de la aplicación, y un proveedor nuevo las atraviesa por el mero hecho de implementar
`IAssistedFixProvider` / `IAuditorProvider`.

Lo que **tendría que repetir**, una por una, en su propio transporte:

1. Cerrar la superficie: la lista de lo permitido y el rechazo de todo lo demás (el
   `OnPermissionRequest` de Copilot, los seis argumentos del CLI de Claude Code).
2. Derivar esa lista del catálogo en vez de escribirla a mano.
3. Aislar el directorio de trabajo / el contexto ambiental del proveedor, para que no entre un
   segundo canal de instrucciones.
4. Declarar que `run_build_and_tests` no acepta argumentos.
5. Declarar la terminalidad de `fix_done` y de `unit_done` —y si su transporte no tiene el concepto,
   copiar la frase de `AuditorTools.cs:124`, que es exactamente la divergencia que hoy existe—.
6. Convertir una excepción del handler en un error DE LA TOOL y no en un transporte roto.
7. Atender las llamadas en paralelo, o `ask_user` bloqueará la sesión entera.
8. Ser tolerante en la forma de los argumentos, si su transporte no tipa las firmas.
9. Su propio tope de conexión y su propio criterio de corte, si los tiene.
10. Y, para las tools de AUDITORÍA, **volver a copiar a mano las siete descripciones**: ahí no hay
    `FixToolText` que leer.

El punto 10 es el resultado principal de esta medida: la paridad del arreglo está garantizada por
construcción y por dos tests (`tests/Atalaya.ClaudeCode.Tests/FixSurfaceTests.cs:96-99`,
`tests/Atalaya.Copilot.Tests/FixSessionSurfaceTests.cs:50-53`); la de la auditoría es una convención
escrita en comentarios (`AuditorTools.cs:10-15`, `McpTool.cs:10-15`), sin constante ni test, y ya ha
divergido en `unit_done`.

### Lo que se ve y no se toca

Apuntado, no arreglado. Ninguna guarda se ha movido de sitio.

1. **`unit_done` ya no dice lo mismo en las dos casas.** `AuditorTools.cs:124` lleva «Cuando la
   llames, HAS TERMINADO: no digas nada más.» y `RealCopilotAgent.cs:322-327` no. Puede ser
   deliberado —MCP no tiene `IsTerminal` y hay que decirlo por texto— pero contradice a D-777 tal y
   como está escrito, y nada impide que la próxima divergencia sea accidental.
2. **El catálogo de auditoría no tiene su `FixToolText`.** Siete descripciones copiadas a mano en dos
   ficheros, sin constante compartida y sin test de paridad, mientras el catálogo de arreglo —cinco
   tools— sí tiene ambas cosas. La tentación evidente es un `AuditToolText`; no se ha hecho.
3. **`FixToolText.AskUserDescription` se anuncia como compartida y solo la lee un driver.** El XML de
   `FixToolText.cs:14-21` explica bien por qué, pero `All` (`:74-77`) enumera cinco y Copilot declara
   cuatro: quien lea la constante puede creer que Copilot ve ese texto.
4. **El arreglo cualifica sus tools con `AuditorTools.Qualified`** (`ClaudeCodeProvider.cs:506`). Es
   correcto —el prefijo es del servidor, no del catálogo— pero el nombre de la clase dice otra cosa.
5. **Comentario desfasado en `McpTool.cs:31`**: «los esquemas de las seis tools». Auditoría son seis
   más `submit_verdict`, y el arreglo cinco.
6. **`read_file` descuenta el presupuesto ANTES de comprobar que el fichero existe**
   (`FixToolbox.cs:169` frente a `:172`): una ruta equivocada cuesta una lectura. Puede ser
   deliberado —una llamada es una llamada— pero no está dicho en el comentario.
7. **`FixToolbox.cs:329`, `inScope = false;` justo después de `_inScope.Add(relative)`.** El
   comentario lo justifica (se registra como «fuera de ámbito, autorizado»), pero las dos líneas
   juntas se leen como una contradicción.

### Lo que ESTADO no me dio

- **Dónde vive el texto de las tools de auditoría.** ESTADO afirma la paridad (D-777) pero no dice
  que sean dos copias a mano. Al código: `src/Atalaya.ClaudeCode/AuditorTools.cs:78-140` y
  `src/Atalaya.Copilot/RealCopilotAgent.cs:302-329`. Ahí apareció la divergencia de `unit_done`.
- **`read_signatures` y `submit_verdict` no aparecen en ESTADO** (0 menciones de cada uno). Las
  encontré enumerando los catálogos en el código.
- **`ask_user` en Copilot.** ESTADO dice que «con Claude Code `ask_user` la sirve Atalaya con el
  mismo nombre y contrato» (D-805), pero no que en Copilot la descripción sea la del runtime y no la
  de `FixToolText`. Al código: `RealCopilotAgent.cs:436-450` frente a `FixTools.cs:118-143`.
- **Dónde se aplica `FixAskGuard`.** ESTADO lo nombra y dice que «ve esa petición antes de pintar la
  tarjeta» (D-1032) pero no dónde. A `LiveFixService.cs:915-921`, que es lo que decide que sea
  herencia y no repetición.
- **El fichero de los topes de lectura.** ESTADO da los números —120.000 y 30— pero no el sitio.
  `FixToolbox.cs:62` y `:70`; el nombre `FixToolbox` no aparece en ESTADO.
- **Por qué se conservan los fines de línea.** D-1035 los nombra junto al BOM como si fueran la misma
  guarda; el código aclara que los fines de línea se conservaban ya solos y que lo único que se
  perdía era el preámbulo (`FixToolbox.cs:547-556`). Sin eso habría contado dos guardas donde hay
  una y media.
- **La terminalidad como guarda con dos implementaciones.** ESTADO dice que «`fix_done` lo cierra el
  driver» (D-805) sin decir que en Copilot lo declara el SDK. A `RealCopilotAgent.cs:535`
  (`IsTerminal`) y `FixTools.cs:11-24`.
- **Los tests de paridad.** ESTADO no menciona que haya afirmación automática sobre el texto
  compartido. Los encontré por búsqueda: `FixSurfaceTests.cs:96-99` y `FixSessionSurfaceTests.cs:50-53`.

Para lo demás ESTADO bastó: la sección «Arreglo con agente» me dio las guardas de D-545, D-546,
D-547, D-548, D-549, D-1032, D-1033, D-1035 y D-1059 con su número y su frase, y la sección
«Proveedores de IA y tarifas» me dio D-777, D-778 y D-807 enteros. **No abrí `DECISIONS.md` ni una
vez**: cada laguna se cerró leyendo el código, que era más barato que buscar la sección por título.

---

## 4 · Tarifas y secretos

Medida de la spec PROV-1. Solo se lee: nada de lo que hay aquí toca producto.

### (a) Tarifas

#### Cómo está indexado `model-rates.json`

**Por modelo, con el proveedor como calificador opcional.** No es un diccionario: es una **lista plana
de tarifas**, y cada tarifa lleva `model` (la clave real, comparada sin distinguir mayúsculas) más un
`provider` que puede ser `null` — «esta tarifa vale para cualquiera».

El esquema en código es `ModelRate` / `ModelRateTable`, en
`src/Atalaya.Domain/Model/ModelRates.cs`:

| parte | dónde |
| --- | --- |
| el registro de una tarifa (`Model`, `InputPerMillion`, `OutputPerMillion`, `CachedInputPerMillion`, `CacheWritePerMillion?`, `Provider?`, `EffectiveFrom?`, `Note?`) | `src/Atalaya.Domain/Model/ModelRates.cs:40-48` |
| `Matches(model, provider)` — casa por modelo, y por proveedor **solo si la tarifa lo nombra** | `src/Atalaya.Domain/Model/ModelRates.cs:51-55` |
| `IsProviderSpecific` | `src/Atalaya.Domain/Model/ModelRates.cs:58` |
| la tabla (`SchemaVersion`, `Source`, `ReviewedOn`, `Rates`) | `src/Atalaya.Domain/Model/ModelRates.cs:69-79` |
| `Find(model, provider)` — **la atada a un proveedor gana a la genérica** | `src/Atalaya.Domain/Model/ModelRates.cs:85-105` |
| la ruta: raíz del hub, no por aplicación | `src/Atalaya.Storage/HubPaths.cs:21` |

El fichero de **esta máquina** existe: `%LOCALAPPDATA%\Atalaya\hub\model-rates.json`. Su forma real,
con una entrada de ejemplo (son precios públicos de GitHub, no hay nada secreto que ocultar):

```json
{
  "schemaVersion": 1,
  "source": "Sembrado el 2026-09-01 de docs.github.com (Copilot · models-and-pricing). …",
  "reviewedOn": "2026-09-01",
  "rates": [
    {
      "model": "gpt-5-mini",
      "inputPerMillion": 0.25,
      "outputPerMillion": 2.0,
      "cachedInputPerMillion": 0.025,
      "cacheWritePerMillion": null,
      "provider": null,
      "effectiveFrom": "2026-09-01",
      "note": null,
      "isProviderSpecific": false
    }
  ]
}
```

31 tarifas. **Ninguna de las 31 lleva `provider`**: todas son genéricas.

Dos detalles de la forma real que el esquema no anuncia:

- `isProviderSpecific` **se serializa al disco** aunque sea una propiedad calculada del record
  (`ModelRates.cs:58`). Es redundante con `provider` y no se lee al cargar; es ruido escrito.
- `cacheWritePerMillion: null` no es cero: significa «este modelo no cobra aparte la escritura de
  caché», y esos tokens vuelven al montón de entrada (`ModelRates.cs:27-32`, aplicado en
  `CreditCalculator.cs:243-247`).

#### Qué pasa con un modelo que no está en la tabla

Confirmado en código, las tres afirmaciones de ESTADO:

| pregunta | respuesta | fichero:línea |
| --- | --- | --- |
| ¿Se aproxima con otra tarifa? | No. `Find` devuelve `null` y el cálculo para ahí con motivo `RateMissing` | `src/Atalaya.Domain/Model/CreditCalculator.cs:235-239` |
| ¿Qué texto sale? | «tarifa no configurada» (y «modelo no registrado» si la sesión no guardó modelo) | `src/Atalaya.App/Services/CostFormat.cs:478-479` |
| ¿El agregado se marca parcial? (D-787) | Sí: cuenta las sesiones **facturables, con tokens** cuyo motivo es `ModelUnknown` o `RateMissing` | `src/Atalaya.App/Services/MetricsQuery.cs:846-848` |
| ¿Y se dice con su número? | Sí, `CostIsPartial` + `PartialCostNotice` | `src/Atalaya.App/Services/MetricsQuery.cs:616-621` |
| ¿Una sesión sin tokens dispara el parcial? | No: `HasTokens(x)` es condición del recuento | `src/Atalaya.App/Services/MetricsQuery.cs:848` |
| ¿Una casa que no factura dispara el parcial? | No: se cuenta aparte, en `untariffed` | `src/Atalaya.App/Services/MetricsQuery.cs:852` |
| ¿«auto» es un modelo? (D-1004) | No. `ModelIds.IsPlaceholder` lo reconoce como hueco de modelo, no como modelo sin tarifa | `src/Atalaya.Domain/Model/CostReconciliation.cs:23,26-27` |
| ¿Y dónde se aplica eso? | El hueco de «auto» sale como `CostGapReason.Desconocido`, no `SinTarifa`; y ningún modelo de llamada llamado «auto» entra en la lista de candidatos | `src/Atalaya.Domain/Model/CostReconciler.cs:80-82,87,116` |

#### Dónde vive la regla de que Claude Code no se tarifa

`CreditCalculator.IsBilled`, en **`src/Atalaya.Domain/Model/CreditCalculator.cs:185-186`**.

Decide **por el id del proveedor, comparado literalmente contra la cadena `"claude-code"`**:

```csharp
public static bool IsBilled(string? providerId)
    => !string.Equals(providerId?.Trim(), "claude-code", StringComparison.OrdinalIgnoreCase);
```

No es una propiedad del proveedor ni una consulta a `IAuditorProvider`. Es un `if` por nombre, y está
en el dominio a propósito: los agregados releen meses de historia escrita por proveedores que hoy
pueden no estar registrados (el mismo argumento que `AccountingOf`, `CreditCalculator.cs:158-161`).

Conviene decirlo porque **contrasta con D-784**, que puso la opcionalidad del proveedor en `IsOptional`
/ `IsPresent` de la interfaz (`src/Atalaya.Agents/IAuditorProvider.cs:99,109`) precisamente «no en un
`if` por nombre». «Si factura» sigue siendo un `if` por nombre.

Dónde muerde, todo por el mismo embudo:

| efecto | fichero:línea |
| --- | --- |
| Es lo **primero** que se pregunta, antes que tokens y tarifas → `CostUnavailable.NotBilled` | `src/Atalaya.Domain/Model/CreditCalculator.cs:220-223` |
| El texto que sale no es «tarifa no configurada» sino la frase de suscripción | `src/Atalaya.App/Services/CostFormat.cs:481` |
| No cuenta como hueco de coste ni pide reconciliación | `src/Atalaya.Domain/Model/CostReconciler.cs:64-66` |
| No cuenta como parcial en Métricas; se cuenta aparte | `src/Atalaya.App/Services/MetricsQuery.cs:852` |
| Ajustes **rechaza guardar** una tarifa cuyo `provider` no factura | `src/Atalaya.App/ViewModels/ModelRatesViewModel.cs:272-278` |

#### Qué haría falta para tarifar por proveedor

**D-786 no está solo declarado: el mecanismo está implementado y probado.** El campo existe, se
persiste, la resolución prefiere la específica y hay test con un proveedor inventado
(`tests/Atalaya.Domain.Tests/CreditCalculatorTests.cs:267`, `Provider: "otra-reventa"`). Una tarifa por
proveedor escrita **a mano en `hub/model-rates.json`** funciona hoy, y sobrevive a un guardado desde
Ajustes: `RateRow` la lee (`ModelRatesViewModel.cs:19`) y la vuelve a escribir (`:62`).

Lo que falta es **el camino para crearla desde el producto**, y una puerta cerrada:

| qué falta | fichero | tamaño |
| --- | --- | --- |
| **Columna «Proveedor» en la tabla de Tarifas.** La rejilla tiene seis columnas —Modelo, Entrada, Salida, Caché leída, Caché escrita, Nota— y ninguna es el proveedor; `AddRow` crea la fila con `Provider` vacío. El dato existe en el view-model pero no tiene celda. | `src/Atalaya.App/Views/SettingsView.xaml:365-411` (cabeceras) y `:428-441` (celdas) | **M** (dos rejillas paralelas de 6→7 columnas, `MinWidth` y el `Fingerprint` ya lo incluye) |
| **La siembra no pone ninguno**: `Add(...)` fija `Provider: null` para las 31 tarifas, sin parámetro para cambiarlo. | `src/Atalaya.Domain/Model/ModelRateSeed.cs:172-182` | **S** |
| **El título de la sección dice «Tarifas de GitHub Copilot».** Con proveedor por fila deja de ser cierto. | `src/Atalaya.App/Views/SettingsView.xaml:285` | **S** |
| **`IsBilled` cierra la puerta al único otro proveedor que existe.** Cualquier tarifa con `provider: "claude-code"` se rechaza al guardar, y `Calculate` ni la mira. Tarifar por proveedor «de verdad» exige que «si factura» deje de ser una constante del dominio: propiedad del proveedor, o ajuste de la organización. | `src/Atalaya.Domain/Model/CreditCalculator.cs:185-186` + `src/Atalaya.App/ViewModels/ModelRatesViewModel.cs:272-278` | **L** (es una decisión de producto, no una refactorización: toca dominio, Ajustes, Métricas y los textos de `CostFormat`) |
| **`isProviderSpecific` se escribe al JSON** y confundirá a quien edite el fichero a mano creyendo que es el campo que hay que tocar. | `src/Atalaya.Domain/Model/ModelRates.cs:58` (falta `[JsonIgnore]`) | **S** |

Resumen: para tarifar por proveedor **a otro proveedor facturable** basta S+M (columna, siembra,
título). Para tarifar a Claude Code hace falta además la L, que es revertir una decisión de producto.

### (b) Secretos y credenciales

#### El mapa

| credencial | fichero | dónde vive | protección | ¿va al hub (y a git)? |
| --- | --- | --- | --- | --- |
| **Token de la cuenta de GitHub** (device flow) — el blob entero de la cuenta: `token`, `id`, `login`, `name`, `avatarUrl`, `email` | `auth.dat` | `%LOCALAPPDATA%\Atalaya\auth.dat` (`AppPaths.cs:31`) | **DPAPI, `DataProtectionScope.CurrentUser`.** Confirmado en disco: el fichero de esta máquina empieza por la firma DPAPI `01 00 00 00 d0 8c 9d df 01 15 d1 11 8c 7a 00 c0 4f c2 97 eb`. Escritura en `AccountStore.cs:95-97`, lectura en `:80-81`. Otro usuario de Windows no lo descifra: se comporta como «no conectado» (`:86-92`) | **No.** Está fuera del clon |
| **PAT de respaldo** (para organizaciones que bloquean OAuth Apps) | campo `protectedPat` dentro de `settings.json` | `%LOCALAPPDATA%\Atalaya\settings.json` (`AppPaths.cs:28`) | **DPAPI `CurrentUser`, y además base64.** Cifra en `SettingsService.cs:626-628`, descifra en `:644-645`. El campo es `AppSettings.ProtectedPat` (`:111`) | **No.** `settings.json` es de la máquina; el comentario del propio código lo dice: «never secrets, never the hub» (`AppPaths.cs:27`) |
| **Login del CLI `copilot`** (`copilot` + `/login`, por máquina, D-029) | lo que escriba el propio CLI de GitHub | fuera de Atalaya, en la ubicación estándar del SDK (`CopilotBaseDirectory` vacío por defecto, `SettingsService.cs:219-223`) | **La de GitHub, no la de Atalaya.** Atalaya no la lee ni la copia; solo la usa vía `UseLoggedInUser = true` cuando no hay token de cuenta (`RealCopilotAgent.cs:789-790`) | **No** |
| **Sesión de Claude Code** (suscripción personal) | lo que escriba el CLI de Anthropic | fuera de Atalaya | La del CLI. Atalaya lanza el CLI con `--strict-mcp-config` para no heredar servidores ajenos (`ClaudeCliRunner.cs:176-179`) | **No** |
| **`gitHubClientId` de la OAuth App** | `appsettings.deploy.json` | **junto al exe** y embebido; disco gana a embebido (D-034). El versionado en `src/Atalaya.App/appsettings.deploy.json` está **vacío** | **En claro — y está bien.** Un `client_id` de device flow es público por diseño; **no hay `client_secret` en ningún sitio**: el flujo es `POST /login/device/code` → `POST /login/oauth/access_token` sin secreto (`GitHubDeviceFlow.cs:71-72,141`) | **No**, pero el de la organización real aparece en carpetas `bin/` y `dist/` locales, ambas **ignoradas por git** (`.gitignore:2` y `:4`) |
| **Credencial de git para empujar el hub** | ninguno | no se persiste | Se arma en memoria por llamada: `x-access-token` + el token resuelto (`HubContext.cs:571-585`). La precedencia D-036 está en `CredentialSource`: cuenta > PAT > gestor del SO (`HubContext.cs:588-591`) | **No.** El `remote.origin.url` del clon de esta máquina es `https://github.com/alloci88/atalaya-hub.git` — **sin token embebido** |
| **Declaración del servidor MCP** (`mcp-{guid}.json`) | fichero temporal por sesión | directorio de trabajo de la sesión | En claro, pero **no contiene ninguna credencial**: solo `command`, `args` y el nombre de la tubería, que es aleatoria y de un solo uso (`ClaudeCliRunner.cs:838-866`) | **No** |

El reset de fábrica borra las dos credenciales locales y sus vecinas: `auth.dat` vía
`_account.Disconnect()`, la marca de sesión, el clon y `machines.json`
(`src/Atalaya.App/Services/FactoryResetService.cs:190-193`).

**Una corrección a D-276.** ESTADO dice que «el PAT … se edita en el fichero: se fue la interfaz, no
la capacidad». El campo del fichero es `protectedPat`, un **blob DPAPI en base64**: nadie puede
escribirlo a mano. `SettingsService.SetPat` (`:618`) no tiene **ni un solo llamador en `src/`** — solo
tests (`ConnectionSetupTests.cs:103,175`, `SettingsServiceTests.cs:23`,
`SettingsViewTests.cs:106`, que comprueba por reflexión que el método sigue existiendo). Se lee lo que
ya hubiera; **poner un PAT nuevo no se puede hacer desde ninguna parte**. La capacidad que sobrevive
es la de *usar* un PAT, no la de *ponerlo*.

#### La comprobación: ¿hay hoy una credencial donde no debería?

Miré primero qué escribe el código en cada fichero del hub (`src/Atalaya.Storage/HubPaths.cs`: `hub.json`,
`model-rates.json`, y bajo `apps/{slug}/` el `app.json`, `inventory/`, `findings/`, `silences/`,
`pattern-silences/`, `sessions/`, `fixes/`, `cost-reconciliations/`). Ningún escritor pone un token:
la sesión guarda `by`, `machine`, `model`, `provider`, `usage` y notas
(`src/Atalaya.Domain/Model/AuditSession.cs:372,383,394`), y la ficha de arreglo guarda
`commitAuthor` (`src/Atalaya.Domain/Model/FixRecord.cs:83`, escrito en
`src/Atalaya.App/Services/LiveFixService.cs:1329`).

Después, los ficheros reales de esta máquina (47 ficheros fuera de `.git`):

| qué busqué | resultado |
| --- | --- |
| `ghp_` / `gho_` / `ghu_` / `ghs_` / `ghr_` / `github_pat_` / `sk-` en el clon del hub | **nada** |
| `"token"`, `Authorization`, `Bearer`, `password`, `secret` con valor en los JSON del hub | **nada**; el único acierto es la palabra «secretos» dentro del texto en prosa de un resumen de sesión |
| lo mismo en `%LOCALAPPDATA%\Atalaya\logs` | **nada** |
| claves de `settings.json` en esta máquina | 22 claves, **ninguna es `protectedPat`**: `connectionMigrated`, `requireTlsRevocationCheck`, `editor`, `theme`, `costCurrency`, `window`, `pollingSeconds`, `defaultThresholds`, `enableAssistedFix`, `assistedFixDefaultApplied`, `sweepCapDefaultApplied`, `copilotTimeoutMinutes`, `maxPassesPerUnit`, `exhaustiveSweep`, `copilotModel`, `auditorProvider`, `claudeCodeModel`, `lastUpdateCheckUtc`, `lastSeenReleaseTag`, `lastSeenReleaseUrl`, `dismissedUpdateVersion` |
| token embebido en `remote.origin.url` del clon | **no**, la URL va limpia |

**No hay ninguna credencial en el clon del hub ni en `settings.json` de esta máquina.**

Sí aparece **un dato personal**, que no es una credencial pero conviene que conste porque **sí viaja a
git**:

- **`apps/atalaya/fixes/01M237R7340Z9Z495Y8AJJT6YF.json`**, campo **`commitAuthor`**: contiene el
  nombre y la **dirección de correo** con que se firmó ese commit — un correo de retransmisión privada
  de Apple, heredado del `user.email` del git local («Su Nombre»), no de la identidad de GitHub que
  D-037 manda usar. El mismo correo está además en el reflog del clon (`.git/logs/HEAD` y
  `.git/logs/refs/**`), y ahí se publica en cuanto se empuja. Nombro fichero y campo; **no pego el
  valor y no toco nada**.

### Veredicto

Para tarifar por proveedor no falta el mecanismo —está escrito, resuelto con preferencia y cubierto
por test—: falta **la columna «Proveedor» en la tabla de Ajustes** (M) y que la siembra y el título de
la sección dejen de dar por hecho que solo hay una casa (S); y si lo que se quiere es tarifar a Claude
Code, falta además convertir `CreditCalculator.IsBilled` de constante del dominio en propiedad del
proveedor (L), que es revertir una decisión de producto, no refactorizar. Credenciales donde no
deberían, **ninguna**: `auth.dat` y el PAT están los dos bajo DPAPI de usuario y fuera del hub, el
clon y `settings.json` de esta máquina están limpios y la URL del remoto no lleva token — lo único que
viaja a git sin necesitarlo es una **dirección de correo personal** en `commitAuthor` de
`apps/atalaya/fixes/01M237R7340Z9Z495Y8AJJT6YF.json` y en el reflog del clon.

### Lo que se ve y no se toca

- **`isProviderSpecific` se serializa a `model-rates.json`.** Es una propiedad calculada que no se
  lee al cargar; le falta `[JsonIgnore]` (`src/Atalaya.Domain/Model/ModelRates.cs:58`). Ruido en un
  fichero que se edita a mano.
- **`SettingsService.SetPat` no tiene llamador en producción** (`SettingsService.cs:618`). Código vivo
  que solo ejercitan los tests, y que hace que D-276 diga más de lo que el producto puede hacer.
- **«Si factura» es un `if` por nombre** (`CreditCalculator.cs:185-186`) mientras «si es opcional» es
  una propiedad de la interfaz (`IAuditorProvider.cs:99,109`). Dos reglas del mismo tipo resueltas de
  dos maneras.
- **El `commitAuthor` de los arreglos se toma del git local, no de la identidad del hub.** D-037 dice
  que la identidad sale del perfil (login + email público o `noreply`); en esta máquina lo escrito es
  el `user.email` del repo, que resultó ser un correo personal de retransmisión.
- **El título «Tarifas de GitHub Copilot»** (`SettingsView.xaml:285`) contradice a `ModelRate.Provider`:
  la tabla es de la organización, no de una casa.

### Lo que ESTADO no me dio

- **Cómo está indexado el fichero de tarifas.** ESTADO dice «una tarifa puede atarse a un proveedor»
  (D-786) pero no si eso es una clave compuesta, un diccionario anidado o un campo opcional. Lo
  encontré en `src/Atalaya.Domain/Model/ModelRates.cs:40-105`: lista plana con `provider` opcional y
  desempate a favor del específico.
- **Si D-786 está implementado o solo declarado.** ESTADO enuncia la capacidad; no dice que la
  siembra no la use ni que Ajustes no tenga columna para ella. Lo vi en
  `ModelRateSeed.cs:172-182` y `SettingsView.xaml:365-441`.
- **Cómo decide `IsBilled`.** ESTADO dice que «el consumo de Claude Code no se tarifa»
  (F16-RETOQUE-2) y dónde vive, pero no el criterio. Lo leí en `CreditCalculator.cs:185-186`: cadena
  literal, no propiedad del proveedor.
- **Con qué protección se guarda el PAT.** ESTADO dice que se edita en el fichero (D-276) y que el
  reset lo borra, pero no que esté bajo DPAPI ni que el campo sea base64. Lo encontré en
  `SettingsService.cs:111,626-628,644-645` — y de paso descubrí que no hay forma de ponerlo.
- **Qué contiene `auth.dat`.** ESTADO lo nombra (D-285) como algo que el reset borra; no dice que
  guarde el blob entero de la cuenta ni con qué mecanismo. Lo leí en `AccountStore.cs:48-97`.
- **Si hay un `client_secret` en algún sitio.** ESTADO dice que el `client_id` va en
  `appsettings.deploy.json` (D-033) y calla sobre el secreto. Comprobado en
  `GitHubDeviceFlow.cs:71-72,141` y `DeployConfig.cs:35`: no existe, y con device flow no hace falta.

No hizo falta abrir `DECISIONS.md` en ningún momento: las seis lagunas se cerraron en el código, que
está anotado con los mismos identificadores de decisión que ESTADO cita. ESTADO sirvió para saber
**qué preguntar y dónde mirar**; no para contestar ninguna de las seis.

---

## 5 · Los dobles y el banco

Medida de PROV-1, comportamiento 5. Todo se cuenta **por el código**: `grep -rl` para ficheros y
`grep -c` para ocurrencias. **No se corrió ningún banco** — ni PromptBench, ni `tour.ps1`, ni
`Atalaya.Shots`, ni el de carga, ni M1/M2.

La unidad de conteo de tests es el **método `[Fact]`/`[Theory]` declarado**, no el caso expandido.
La suite entera son **2.166 métodos** (los 2.772 de ESTADO son casos de `[Theory]` ya expandidos;
D-919 cuenta 2.167 métodos, así que la unidad cuadra). Donde se dice «tests», es *tests declarados
en los ficheros que tocan ese doble* — una cota superior honesta: no todos los `[Fact]` de un
fichero instancian el doble, pero el fichero no compila sin él.

### Los dobles de prueba, por proveedor

| doble | fichero | a qué proveedor sustituye | qué simula | ficheros de test | tests |
| --- | --- | --- | --- | ---: | ---: |
| `FakeCopilotAgent` | `src/Atalaya.Copilot/FakeCopilotAgent.cs` (276 líneas) | **a ninguno en concreto**: implementa `IAssistedFixProvider`, la interfaz, no el SDK de Copilot | un auditor entero guionizado: hallazgos, veredictos, reconciliación, extensión de ubicaciones, lista de modelos, supresión por patrón, pasos de arreglo asistido y evidencia del veredicto | **54** (49 lo nombran + 5 más por `TestFactory.Inventory/Portfolio/Shell`) | **776** (157 instanciaciones directas) |
| `Atalaya.FakeCli` | `tests/Atalaya.FakeCli/Program.cs` | **Claude Code** — el binario `claude` | un CLI que **sí habla MCP**: lee `--mcp-config`, lanza `Atalaya.Mcp` como hijo, hace `initialize` / `tools/list` / `tools/call` por JSON-RPC y contesta `stream-json` con `usage`, `modelUsage` y `total_cost_usd` acumulado. Lo único fingido es **quién decide qué llamar** | **1** (`tests/Atalaya.App.Tests/AssistedFixClaudeTests.cs`) | **13** |
| `FakeCli` (clase privada) | `tests/Atalaya.ClaudeCode.Tests/FakeCliTests.cs:193` | **Claude Code** — el binario `claude` | un `.cmd` que escupe un guion de eventos y sale con el código que se le diga. Prueba cómo se **lee** la salida, no cómo se llama a las tools | **1** | **9** |
| `Grabaciones/turno-real.jsonl` | `tests/Atalaya.ClaudeCode.Tests/Grabaciones/turno-real.jsonl` (12 líneas) | **Claude Code** — no es un doble, es una **grabación** del CLI 2.1.263 con los flags de producción | un turno real reproducido: `system/init`, `rate_limit_event`, `message_start`, los `text_delta`, `message_delta` con el consumo definitivo y `result` | **2** (`RealStreamReplayTests`, `LectorCosteTests`) | **5** |
| `RecordingTurns` | `tests/Atalaya.Copilot.Tests/UnitThreadSurfaceTests.cs:133` | **Copilot** — la costura `ICopilotTurns` de `CopilotUnitThread` | los turnos de una `CopilotSession` para poder probar el hilo **sin asiento** | **1** | **6** |
| 22 dobles ad-hoc de `IAuditorProvider` | inline en 17 ficheros de `Atalaya.App.Tests` (`LoopingAgent`, `QuotaAtUnitAgent`, `ScriptedVerifier`, `PromptSpyAgent`, `RecordingAgent`, `ThrowingAgent`…) | **a ninguno**: la interfaz | un comportamiento raro cada uno — bucle infinito, cuota agotada a media unidad, lote vacío, presupuesto reventado, pausa tras la primera unidad | **17** | **199** |
| 9 dobles de `IAuditToolbox` | inline en `Atalaya.ClaudeCode.Tests` y `Atalaya.Copilot.Tests` | ninguno: son el doble de **la aplicación**, con el proveedor real delante | que las tools acepten, cuenten o exploten | 8 | — |

Los dos primeros son los que sostienen la suite. El resto son piezas de un solo uso.

**Qué habría que escribir para un proveedor nuevo, doble a doble:**

- **`FakeCopilotAgent`: no hace falta otro, y el nombre miente.** Está escrito contra
  `IAssistedFixProvider` y no toca nada de `GitHub.Copilot.SDK`; a un proveedor nuevo le sirve tal
  cual, porque los 776 tests que dependen de él no prueban una casa, prueban el contrato. Lo que
  habría que tocar es el nombre y su sitio: vive en `src/Atalaya.Copilot/`, que es el proyecto de
  una casa concreta, y eso ata todo el `Atalaya.App.Tests` a ese ensamblado por un doble que ya no
  es suyo.
- **`Atalaya.FakeCli`: hay que escribir uno nuevo, y no es barato.** Está a la altura del
  **protocolo** de Claude Code —`--mcp-config`, JSON-RPC sobre el puente, `stream-json` con tres
  contadores distintos que hay que saber restar—, no a la altura de la interfaz. Un proveedor que
  no sea un CLI con MCP necesita su propio doble de proceso, o ninguno si su SDK se puede fingir en
  memoria (que es lo que hace `RecordingTurns` con Copilot).
- **`FakeCli` (el `.cmd`): hay que escribir uno nuevo si el proveedor nuevo es un ejecutable.** Es
  un guion de líneas del formato del CLI; el guion no se reaprovecha, pero el patrón —un `.cmd` que
  hace `type` de un fichero— cuesta veinte líneas.
- **`turno-real.jsonl`: hay que grabar uno nuevo, y grabarlo cuesta cuota.** Es la única pieza de la
  suite que no se puede escribir a mano por definición: su valor es justamente que nadie la inventó.
- **`RecordingTurns`: es el modelo a imitar.** Costura interna (`ICopilotTurns`) más doble en
  memoria: es lo que permite probar el hilo de Copilot sin asiento. Un proveedor nuevo que declare
  su propia costura fina se prueba así, sin proceso y sin cuota.
- **Los ad-hoc de `IAuditorProvider`: ninguno hace falta.** Son de la interfaz; un proveedor nuevo
  no los invalida.

### El banco M1/M2 contra un proveedor nuevo

**Qué ficheros hay que tocar.** El banco es `scripts/PromptBench/` (1.088 líneas en 5 ficheros) y
no cuelga nada más de él; no está en `Atalaya.sln`, así que cuesta cero del ciclo build + test.

| fichero | qué hay dentro | qué pasa con un proveedor nuevo |
| --- | --- | --- |
| `scripts/PromptBench/Program.cs` (400) | el despacho de subcomandos y el modo `claude` | **`Program.cs:205` cablea `new ClaudeCodeProvider(bridge, () => model, () => work)` a pelo.** No hay `AuditorProviderRegistry`, ni bandera `--proveedor`, ni interfaz: el subcomando **se llama `claude` porque es el proveedor**. Un segundo proveedor obliga a inventar el selector que no existe |
| `scripts/PromptBench/SweepBench.cs` (356) | el modo `barrido`: `SessionCoordinator` de producción sobre un clon | **`SweepBench.cs:138` repite el mismo cableado**, y además pone `CutOnUnitDone`, `CutInThread` y `UsePartialMessages`, que son propiedades **de `ClaudeCodeProvider`**, no de `IAuditorProvider`. Un proveedor nuevo sin esas palancas no compila ahí |
| `scripts/PromptBench/BenchCredits.cs` (46) | la valoración en credits a tarifa Opus | está atado a la semántica de entrada de Claude Code (`TokenAccounting.InputExcludesCache`): «con Copilot habría que restar, y por eso esta clase no es de uso general». Un proveedor nuevo necesita su rama o sus tarifas |
| `scripts/PromptBench/CallTrace.cs` (214) | el mapa de llamadas: fresca / leída / **ESCRITA** / salida | se alimenta de `UsageSample`, que sí es del vocabulario común. **No hay que tocarlo** |
| `scripts/PromptBench/BenchToolbox.cs` (72) | el toolbox que acepta todo y cuenta | del lado de la aplicación. **No hay que tocarlo** |
| `scripts/PromptBench/PromptBench.csproj` | referencias | ya referencia `Atalaya.Agents`, `Atalaya.App`, `Atalaya.ClaudeCode`, `Atalaya.Copilot`, `Atalaya.Domain` y `Atalaya.Mcp`. Habría que añadir el proyecto nuevo |
| `scripts/PromptBench/README.md` | cómo se usa y cómo se lee | documenta `& $bench claude …` por su nombre; cambia si el subcomando deja de ser el proveedor |
| `src/Atalaya.Agents/UnitThread.cs` | `IThreadedAuditor` / `IUnitThread` | M2 solo corre contra un proveedor que hile. El coordinador avisa por `ThreadUnavailable`, no simula |

**Qué costó la última corrida.** Está en DECISIONS § **M2 — Medida: las pasadas como turnos de una
misma conversación**, entrada **D-917**. El escenario: `CalculadoraCarga.cs` y `ClienteRemoto.cs`,
modo `barrido`, tope 6, sin el corte de F21 en los dos brazos, **Claude Code con `opus`**, tres
tandas por brazo y brazos alternados. Es decir, **12 unidades auditadas** (2 unidades × 3 tandas × 2
brazos).

| brazo | Fresca | Leída | Escrita | Salida | **Credits/unidad** |
| --- | ---: | ---: | ---: | ---: | ---: |
| producción | 37.306 | 181.099 | 96.539 | 46.682 | **204,7** |
| hilo | 5.228 | 172.501 | 25.776 | 14.741 | **64,2** |

**La cifra que DECISIONS da es la media por unidad: 204,7 credits/unidad en el brazo de producción
y 64,2 en el del hilo** (−69 %). El total de la corrida **no está escrito**; sale de multiplicar por
las seis unidades de cada brazo: **≈ 1.613 credits ≈ 16 $** a 1 credit = 0,01 $.

Y hay que leerlo como lo que es: **no es una factura**. `BenchCredits` valora a la **tarifa de Opus
publicada** (5 / 25 / 0,50 / 6,25 $ por millón para fresca, salida, lectura y escritura de caché),
la misma con la que F20 reprodujo al credit la sesión de referencia (D-871). Claude Code **no
factura a la organización** —`CreditCalculator.IsBilled("claude-code")` es `false`—, así que lo que
esa corrida gastó de verdad fue **cuota del plan del usuario**, no credits. El número sirve para
decir «este brazo cuesta un tercio del otro», no para poner un importe.

De **M1** (D-908, mismo escenario, `opus`, tres tandas por brazo, brazos alternados) **no sale
ninguna cifra de credits**. Lo único de coste que anota es relativo: bajo la lupa de Seguridad el
brazo libre cierra en «3 pasadas, 6 llamadas y 4 hallazgos» y el estructurado sigue hasta «6
pasadas, 12 llamadas y 9 hallazgos» — el doble.

**Si se puede correr sin gastar.** No hay respuestas grabadas, ni modo offline, ni doble en todo
`scripts/PromptBench/`: `FakeCopilotAgent` está en `src/` y el banco no lo nombra; el
`Atalaya.FakeCli` vive en `tests/` y el banco tampoco. Lo único gratis es el camino que no llega a
instanciar proveedor.

| subcomando | qué mide | ¿gasta cuota? | qué tocar para un proveedor nuevo | S/M/L |
| --- | --- | --- | --- | :-: |
| `composicion` (el de por defecto) | de qué está hecho cada prompt bloque a bloque y qué fracción es el código auditado; si el prefijo estable lo es | **No.** Solo llama a `PromptComposer.Compose`: en ese camino no se construye ningún proveedor | **Nada.** Es agnóstico por construcción — mide nuestros bytes, no la respuesta de nadie | **S** (cero) |
| `claude --whole` / `--split` | una pasada real por unidad: caché del proveedor, mapa de llamadas, fila de la primera llamada de cada unidad | **Sí.** `Program.cs:205` lanza el CLI y el servidor MCP de verdad | el selector de proveedor que no existe, más el nombre del subcomando (es el del proveedor) y `BenchCredits` si se quiere valorar | **M** |
| `barrido` | el barrido entero con el `SessionCoordinator` de producción sobre un clon —regla de parada, tope, reconciliación, informe—; cobertura por tope y credits/unidad. **Es el que corrió M1 y M2** | **Sí.** `SweepBench.cs:138` construye `ClaudeCodeProvider` y hace `CheckAsync` antes de empezar. Solo el **hub** es fingido | lo de `claude` más las tres propiedades específicas del driver (`CutOnUnitDone`, `CutInThread`, `UsePartialMessages`) y, para `--hilo`, un `IThreadedAuditor` del proveedor nuevo | **L** |

Las palancas de medida viven en `barrido` y ninguna cambia quién paga: `--estructurado` (M1, el
recorrido miembro × familia que no se hizo fase, D-908), `--sin-corte`, `--corte-en-hilo`, `--tope`,
`--tandas`, `--sin-eventos-crudos`.

### Lo que se ve y no se toca

- **No se corrió ningún banco.** Ni `PromptBench composicion` —que es gratis— ni nada. Todas las
  cifras salen de leer el código y DECISIONS.
- **`scripts/Banco/tour.ps1`** recorre las 18 vistas en cuatro combinaciones contra el `dist` y
  **conduce la aplicación con el ratón** (N-8). No se tocó.
- **`scripts/Banco/Atalaya.Shots`** fotografía 4 vistas y 9 diálogos; monta un `FakeCopilotAgent`
  con `auditScript`/`fixScript` y `modelName: "claude-opus-4.7"` (`Fixture.cs:115`). No gasta cuota,
  pero no se corrió.
- **`scripts/BancoCarga/Atalaya.Carga`** pone N personas a auditar a la vez; también sobre
  `FakeCopilotAgent` (`Sesionista.cs:93`, `modelName: "banco-de-carga"`). No gasta cuota, no se
  corrió.
- **No se tocó `src/`, `tests/`, `scripts/`, `.github/`, `MANUAL.md`, `BACKLOG.md`, `DECISIONS.md`,
  `docs/ESTADO.md` ni `docs/NORMAS.md`.** Este fichero es el único escrito. Sin commits y sin build.
- **De DECISIONS solo se abrieron dos secciones, por título**: `## M1` y `## M2`, localizadas con
  `grep -n "^## " DECISIONS.md`. No se leyó entera ni se hojeó nada más.
- **No se renombró `FakeCopilotAgent` ni se movió de proyecto**, aunque el apartado de arriba dice
  que el nombre miente. Esto es una medida; la propuesta se queda como propuesta.

### El saldo

**Cuántos tests quedarían huérfanos al añadir un proveedor: muy pocos, y no los que parece.** Los
776 tests que pasan por `FakeCopilotAgent` **no se quedan huérfanos**, porque el doble está a la
altura de `IAssistedFixProvider` y no de Copilot: sirve igual para la casa nueva, con la única
deuda de que se llama «Copilot» y vive en el proyecto de Copilot. Lo mismo los 199 de los dobles
ad-hoc. Los que **sí** quedarían sin doble son los **27 del lado del proceso**: los 13 de
`AssistedFixClaudeTests` (`Atalaya.FakeCli`), los 9 de `FakeCliTests` (el `.cmd`) y los 5 de la
grabación — todos atados al protocolo concreto de Claude Code, y ninguno reutilizable. Un proveedor
nuevo llega con **cero cobertura de su capa de transporte** hasta que alguien le escriba su propio
`FakeCli`, y esa es la única pieza cara: 533 líneas de programa que hablan JSON-RPC con el puente de
verdad. De los 108 tests de `Atalaya.ClaudeCode.Tests`, un proveedor nuevo no hereda ninguno.

**Y el banco no se puede ejercitar gratis contra un proveedor nuevo.** El único subcomando gratis es
`composicion`, y precisamente por eso no prueba nada del proveedor: mide nuestros bytes antes de que
salgan. Los dos que sí lo ejercitan —`claude` y `barrido`— llaman al proveedor real, sin modo
offline, sin grabación reproducible y sin doble; `barrido` ni siquiera arranca si `CheckAsync` no
contesta que hay sesión. Repetir M1 o M2 contra una casa nueva cuesta lo que costó la última vez,
y la última vez fueron **≈ 1.613 credits de valoración (unos 16 $ a tarifa Opus) por 12 unidades
auditadas**, pagados en cuota del plan.

### Lo que ESTADO no me dio

- **La cifra de credits de M2.** ESTADO no trae ningún coste de ninguna corrida del banco: la
  sección «Tests, bancos y CI» dice quién gasta, nunca cuánto. Los **204,7 / 64,2 credits por
  unidad** están en `DECISIONS.md` § M2, **D-917**. *(Esta es la laguna que A2 venía a cerrar y
  sigue abierta.)*
- **Qué dobles existen.** ESTADO **no nombra `FakeCopilotAgent`** —el encargo decía que sí y no es
  cierto: `grep -n "FakeCopilotAgent" docs/ESTADO.md` no da ninguna línea—, ni `Atalaya.FakeCli`, ni
  la grabación `turno-real.jsonl`. Los encontré todos en el código (`src/Atalaya.Copilot/`,
  `tests/Atalaya.FakeCli/`, `tests/Atalaya.ClaudeCode.Tests/Grabaciones/`).
- **Cuántos tests dependen de cada doble.** ESTADO da agregados (2.772 tests, 80,7 % en
  `Atalaya.App.Tests`) pero nada por doble. Contado a mano con `grep -rl` / `grep -c`.
- **Qué subcomandos tiene PromptBench.** ESTADO nombra tres —`composicion`, `claude`, `barrido`—
  pero no dice que son **todos** los que hay, ni cuál es el de por defecto (`composicion`, cuando no
  se pasa argumento). Eso está en `scripts/PromptBench/Program.cs:28` y en el `switch` de la 131.
- **Quién gasta cuota — y aquí ESTADO dice algo que el código desmiente.** La línea de ESTADO «El
  subcomando `claude` de PromptBench es lo único de los bancos que gasta cuota de IA» (A1) **no es
  exacta**: `SweepBench.cs:138` construye un `ClaudeCodeProvider` real, le hace `CheckAsync` y le
  pasa el `SessionCoordinator` de producción, así que **`barrido` gasta igual** — y de hecho es el
  subcomando con el que se corrieron M1 y M2 (D-917 lo escribe: «Reproduce `PromptBench barrido
  --clon <banco> --tope 6 --sin-corte --model opus [--hilo]»). La línea vecina de ESTADO dice que en
  `barrido` «solo el hub es fingido», que es correcta y contradice a la anterior.
- **Qué hay que tocar del banco para otro proveedor.** ESTADO no dice en ningún sitio que el
  proveedor esté **cableado a pelo** en dos líneas del banco, sin registro ni bandera. Leído en
  `Program.cs:205` y `SweepBench.cs:138`.
- **Si el banco se puede correr sin gastar.** ESTADO afirma que `composicion` es gratis, pero no
  dice si existe modo offline, grabación o doble para los otros dos. Comprobado leyendo los cinco
  ficheros de `scripts/PromptBench/`: no existe ninguno.
- **La unidad de la cuenta de tests.** ESTADO dice «2.772 tests en verde», que son casos
  expandidos; los métodos declarados son 2.166. La equivalencia la fija **D-919** («2.167 en
  total»), no ESTADO.
- **Cómo se localiza el doble del CLI en tiempo de test.** ESTADO no lo menciona; está en
  `tests/Atalaya.App.Tests/AssistedFixClaudeTests.cs:689` (`locator: () =>
  Path.Combine(AppContext.BaseDirectory, "Atalaya.FakeCli.exe")`) y en las `ProjectReference` con
  `ReferenceOutputAssembly="false"` de los dos `.csproj` de test.
- **Que existe una costura para fingir el SDK de Copilot.** `ICopilotTurns` /
  `LiveCopilotTurns` en `src/Atalaya.Copilot/CopilotUnitThread.cs` no aparece en ESTADO; lo
  encontré por el doble `RecordingTurns` de `UnitThreadSurfaceTests.cs`. ESTADO sí trae D-915 («un
  proveedor que no sabe hilar lo dice por evento»), pero no que Copilot tenga ya su hilo con costura
  propia.

---

## Lo que el inventario dice

1. **125 sitios dan por hecho Copilot**: 17 en dominio, 13 en el proyecto del proveedor, 70 en app y
   vistas, 25 en `MANUAL.md`. Por tamaño: **2 L, 30 M, 93 S**.
2. **Tres focos concentran casi todo**: la unidad de coste (~20 filas; el credit de GitHub es la
   única unidad en la que el producto sabe escribir un importe), el nombre por defecto (una línea,
   `ProviderNames.Display`, que se propaga a 15 sitios de presentación) y el alojamiento del
   vocabulario común —prompt, reglas, temáticas, rúbrica y directivas— dentro de `Atalaya.Copilot`.
3. **La abstracción existe y funciona donde audita**: 13 miembros en `IAuditorProvider` y cuatro
   capacidades opcionales bien declaradas. Pero de 23 capacidades miradas, **15 quedan fuera del
   contrato**, y seis se resuelven con un `if` que nombra al proveedor repartido en nueve sitios.
4. **La frontera es nítida**: dentro del motor del barrido manda la capacidad declarada; fuera —el
   dinero, los ajustes y las vistas— manda la comparación por cadena.
5. **Las guardas del arreglo se heredan casi todas**: 21 de 36 están en dominio compartido. Las 15
   del transporte hay que repetirlas, y la décima es volver a copiar a mano las siete descripciones
   de las tools de auditoría, que no tienen `FixToolText` y **ya divergen** en `unit_done`.
6. **Las tarifas por proveedor ya están implementadas y probadas, pero no se pueden usar**: no hay
   columna «Proveedor» en Ajustes y `IsBilled` compara contra la cadena `"claude-code"`, así que un
   proveedor nuevo **factura por omisión** como si fuera Copilot.
7. **Credenciales donde no deberían: ninguna.** `auth.dat` y el PAT, bajo DPAPI de usuario y fuera
   del hub. Sí viaja a git un correo personal en `commitAuthor` de un `fixes/*.json`.
8. **Los dobles aguantan: solo 27 tests quedan sin cobertura**, los del transporte de Claude Code.
   Lo caro es que un proveedor nuevo llega con cero cobertura de su capa de transporte.
9. **El banco no se puede ejercitar gratis contra un proveedor nuevo**: los dos subcomandos que lo
   prueban llaman al proveedor real. La última corrida de M2 valió ≈ 1.613 credits (~16 $).
10. **El tamaño total**: 93 S + 30 M + 2 L de la sección 1, más la columna de tarifas (M), el texto
    único de las tools de auditoría (M), un `FakeCli` nuevo (L) y llevar el vocabulario fuera de
    `Atalaya.Copilot` (L). **Cuatro L, treinta y dos M, noventa y tres S.**
