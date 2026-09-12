# MEDIDA-AGILIDAD — Cuánto cuesta hoy una sesión de agente en Atalaya

**Entrega A1 · 2026-09-12 · sobre `main` en `d01ad55`.** Medida, no limpieza: **no se ha tocado ni
una línea de `src/`, `tests/`, `scripts/`, `.github/`, `MANUAL.md` ni `BACKLOG.md`**. Lo que se ha
visto por el camino y no se arregla está apuntado en «Lo que se ve y no se toca».

Todas las cifras llevan delante el comando que las produjo, para que se puedan repetir. Las que son
estimación lo dicen y dicen con qué método (N-2). Las que **no se han medido** también lo dicen.

- **Máquina y entorno.** Windows 11 (10.0.26200), .NET 8, PowerShell 5.1 / Git Bash. Una sola
  pasada por medida salvo donde se indique; **no hay medias de varias ejecuciones**, así que las
  cifras de reloj llevan el ruido de una máquina de escritorio en uso.
- **El `.trx`** de referencia es `tests/*/TestResults/medida.trx`, producido por el comando de §2.2.
  **No se commitea**: `.gitignore` ya lo excluye por dos vías —`[Tt]est[Rr]esults/` (línea 14) y
  `*.trx` (línea 18)—, comprobado con `git check-ignore -v`.

---

## 1 · El arranque: cuánto hay que leer para empezar

### 1.1 · Tamaño de la documentación de cabecera

```bash
wc -lc DECISIONS.md MANUAL.md BACKLOG.md README.md
```

| Fichero | Líneas | Bytes | Tokens (estimación) |
|---|---:|---:|---:|
| `DECISIONS.md` | 19.693 | 1.416.301 | **~354.000** |
| `MANUAL.md` | 2.284 | 150.859 | ~37.700 |
| `BACKLOG.md` | 1.258 | 109.488 | ~27.400 |
| `README.md` | 466 | 29.942 | ~7.500 |
| **Total** | **23.701** | **1.706.590** | **~427.000** |

**La columna de tokens es una estimación**, y el método es el que pedía el encargo: `bytes / 4`. No
se ha pasado ningún tokenizador real. Para texto español con mucho acento y mucha negrita de
markdown la estimación se queda **corta**, porque los caracteres no ASCII ocupan dos bytes pero
suelen costar más de medio token; el orden de magnitud —cientos de miles— sí es firme.

Para escala: `src/` son **90.949 líneas** de `.cs` + `.xaml` y `tests/` **62.738** de `.cs`
(`find src -name "*.cs" -o -name "*.xaml" | grep -v obj | grep -v bin | xargs cat | wc -l`). **La
documentación de cabecera pesa más líneas que el producto entero menos sus tests.**

### 1.2 · Las «Normas de la casa» solas

```bash
sed -n '7,57p' DECISIONS.md | wc -lc
```

**51 líneas, 3.931 bytes, ~980 tokens estimados.** Es el **0,26 %** de `DECISIONS.md`. Ocho normas
—N-1 a N-8, pese a que el título sigue diciendo «N-1…N-5»— caben en menos de mil tokens.

### 1.3 · Lo que cada fase reciente obligó a leer de `DECISIONS.md`

**Aquí hay una limitación que se declara antes de la tabla (N-2).** El encargo pedía mirar los
`PROMPT-*.md` más recientes del repo o, en su defecto, las secciones que cada entrada F33–F38 cita
**en su primera línea**. Ninguna de las dos fuentes existe:

```bash
find . -name "PROMPT*" -not -path "./.git/*"    # → sin resultados
```

No hay ningún `PROMPT-*.md` en el repositorio ni en la historia, y las entradas de `DECISIONS.md`
**no declaran en su primera línea qué secciones se leyeron**: arrancan directamente con el título de
la fase y su primer `### D-xxxx`. Comprobado sobre las doce entradas más recientes.

**Lo que sí se puede medir, y es el sustituto honesto**: las **referencias cruzadas** que cada
entrada hace a decisiones anteriores (`D-xxxx`). Cada una es una sección de `DECISIONS.md` que el
agente tuvo que localizar y leer para escribir esa frase. Es una **cota inferior** de lo leído —no
recoge lo que se leyó y no se acabó citando— y **no es lo mismo que «lo que el prompt mandó leer»**.

```bash
# El recuento crudo, para ver de qué sale la tabla:
grep -oE "^## .*" DECISIONS.md                      # 134 secciones, con sus líneas de inicio
grep -oE "^(### |- \*\*)D-[0-9]+" DECISIONS.md      # dónde vive cada decisión
# La suma por fase la hace un script de un solo uso; el método está escrito debajo.
```

El método, en una frase: se parte `DECISIONS.md` por sus encabezados `##`, se localiza en qué
sección vive cada `D-xxxx` (tanto los `### D-xxxx` como los `- **D-xxxx`), y para cada fase se suman
las líneas de las secciones ajenas que cita.

| Fase | Líneas de su propia entrada | `D-` citados | Secciones ajenas tocadas | Líneas citadas | % de `DECISIONS.md` |
|---|---:|---:|---:|---:|---:|
| F36-1b | 155 | 6 | 4 | 1.696 | **8,6 %** |
| F36 · Entrega 2 | 72 | 8 | 7 | 952 | 4,8 % |
| F36-2b | 315 | 10 | 9 | 2.181 | **11,1 %** |
| F37 | 100 | 4 | 3 | 557 | 2,8 % |
| F38 | 89 | 7 | 2 | 352 | 1,8 % |

Las secciones que salen citadas una y otra vez son cuatro: **F5.9 (Métricas)**, **F6.3 (Informes)**,
**F26 Partes B y C (las vistas)** y **F32 / BUGFIX-ANCLA**. Entre las cinco fases, lo citado va del
**1,8 % al 11,1 %** del fichero: ninguna fase necesitó ni la novena parte de `DECISIONS.md`, pero
para encontrar ese 2–11 % hay que **saber dónde está**, que es exactamente lo que no se puede hacer
sin recorrerlo o sin un índice.

### 1.4 · Cuánto de `DECISIONS.md` es historia y cuánto es vigente

```bash
grep -cE "^### D-[0-9]+" DECISIONS.md            # 474 encabezados
grep -oE "^### D-[0-9]+" DECISIONS.md | sort -u | wc -l   # 470 ids únicos
grep -cE "^## " DECISIONS.md                     # 134 secciones
```

- **474 bloques `### D-xxxx`, 470 identificadores únicos.** Hay **dos ids repetidos** —`D-710` y
  `D-899`, tres apariciones cada uno—; no se toca, queda apuntado.
- **14.500 de las 19.693 líneas** (73,6 %) viven dentro de un bloque `### D-xxxx`. El resto son
  cabeceras de fase, normas y prosa de enlace.

Sobre qué parte es historia, se han hecho **dos conteos distintos**, y dan respuestas muy
diferentes. Los verbos buscados son los del encargo más los que el fichero usa de hecho:
`revisa|sustituye|se retira|retira|deroga|ya no|revoca|cancela|reemplaza|anula|queda sin efecto|se refunde|obsolet`.

| Medida | Resultado |
|---|---|
| **(a)** Bloques `D-` cuyo **propio texto** contiene alguno de esos verbos | **164 de 474 — 34,6 %** (7.094 líneas, **36,0 % del fichero**) |
| **(b)** `D-`ids **nombrados como objeto** de uno de esos verbos en otro sitio del fichero | **15 de 470 — 3,2 %** |

**La estimación buena es la (b): ~3 % de las decisiones está declarado explícitamente como superado**
(`D-059, D-122, D-197, D-239, D-543, D-556, D-906, D-965, D-977, D-981, D-987, D-1001, D-1022,
D-1033, D-1034`). La (a) **sobrecuenta mucho** y no vale como estimación de derogación: un bloque
«contiene el verbo *revisa*» casi siempre porque *él* revisa a otro, o porque narra que algo *ya no*
pasa —no porque esté retirado. Se deja la (a) en la tabla porque es la que sale del criterio literal
del encargo, y porque su 36 % de líneas mide otra cosa que sí importa: **cuánta prosa del fichero
habla de cambios de rumbo**.

**Lo que no se ha medido.** Cuánto de `DECISIONS.md` es *materialmente* obsoleto —una decisión que
nadie derogó pero que el código ya no cumple— no se ha medido: haría falta contrastar cada `D-`
contra `src/`, que es leer el fichero entero, que es justo el coste que esta medida cuantifica.

---

## 2 · La suite: cuánto tarda y qué es lento

### 2.1 · `build` limpio

```bash
dotnet clean Atalaya.sln -v q && time dotnet build Atalaya.sln -v q
```

| Pasada | Reloj | MSBuild («Tiempo transcurrido») |
|---|---:|---:|
| `clean` + `build`, primera de la sesión | — | **13,03 s** |
| `clean` + `build`, segunda | **7,11 s** | 6,81 s |
| `build` sin cambios (incremental) | **1,81 s** | — |

Las dos pasadas de `clean`+`build` miden lo mismo y difieren en **6 s**; el reparto entre arranque
en frío de los nodos de MSBuild, caché de NuGet y caché del sistema de ficheros **no se ha medido**.
`dotnet clean` **no borra `obj/`**, así que ninguna de las dos es un build desde cero de verdad.

**La cifra que importa para el ciclo de trabajo es la tercera: 1,81 s.** Un agente que cambia treinta
líneas no paga 7 s de build, paga menos de dos.

### 2.2 · La suite entera

```bash
dotnet test Atalaya.sln --no-build --logger "trx;LogFileName=medida.trx"
```

- **Reloj: 80,10 s.**
- **2.772 tests, 0 fallos, 0 omitidos.** (Cuadra con las «2.772 en verde» de F38.)
- **Suma de las duraciones por test del `.trx`: 299,34 s.** Contra 80,10 s de reloj, el paralelismo
  efectivo es de **~3,7×**.

Por proyecto (suma de duraciones del `.trx`, y al lado lo que la consola de `vstest` informa):

| Proyecto | Tests | Suma de duraciones (`.trx`) | Informado por `vstest` |
|---|---:|---:|---:|
| `Atalaya.App.Tests` | **2.236** | **253,64 s** | 1 m 16 s |
| `Atalaya.Storage.Tests` | 88 | 29,55 s | 8 s |
| `Atalaya.ClaudeCode.Tests` | 112 | 13,46 s | 10 s |
| `Atalaya.Inventory.Tests` | 48 | 1,17 s | 509 ms |
| `Atalaya.Domain.Tests` | 118 | 0,66 s | 30 ms |
| `Atalaya.Copilot.Tests` | 164 | 0,63 s | 194 ms |
| `Atalaya.ImportV4.Tests` | 6 | 0,24 s | 15 ms |

Las dos columnas no coinciden y **no se ha investigado por qué**. En `App.Tests` el paralelismo
interno lo explica (253 s de trabajo en 76 s de ventana); en `Domain.Tests` —0,66 s sumados frente a
30 ms informados— no lo explica, así que ahí la discrepancia queda **sin causa medida**. Para todo
lo que sigue se usa **la suma del `.trx`**, que es la que atribuye tiempo a un test concreto.

**`Atalaya.App.Tests` es el 80,7 % de los tests y el 84,7 % del tiempo.** Los seis proyectos
restantes juntos suman 536 tests y 45,7 s.

### 2.3 · Los 30 tests más lentos

Del mismo `medida.trx`, ordenando por `duration`:

| # | Clase | Test | Proyecto | Segundos |
|---:|---|---|---|---:|
| 1 | FactoryResetTests | A_failed_reset_is_reported_by_toast | App | **32,09** |
| 2 | FactoryResetTests | A_failed_push_aborts_the_reset_without_touching_anything | App | **31,87** |
| 3 | McpBridgeTests | Sin_nadie_escuchando_el_puente_se_rinde_en_vez_de_colgarse | ClaudeCode | 10,09 |
| 4 | PublishVerificationTests | Una_publicacion_que_agota_los_intentos_deja_dicho_por_que | Storage | 6,42 |
| 5 | HubPublishTimeoutTests | Un_hub_que_no_contesta_termina_la_sesion_con_motivo_y_no_la_deja_girando | App | 4,46 |
| 6 | VersionStampTests | Un_tag_mal_escrito_no_puede_contaminar_la_version_estampada | App | 3,40 |
| 7 | SettingsViewModelTests | Ningun_id_de_modelo_vive_escrito_en_el_codigo_de_produccion | App | 2,88 |
| 8 | IdentityTests | Ninguna_marca_de_la_antigua_organizacion_sobrevive_en_el_producto | App | 2,84 |
| 9 | HubSyncRegistrationTests | A_failed_pull_is_reported_instead_of_passing_as_healthy | App | 2,72 |
| 10 | DeadRemoteTests | Un_push_que_no_contesta_vuelve_dentro_del_tope_y_con_su_motivo | Storage | 2,51 |
| 11 | DeadRemoteTests | Mientras_la_anterior_sigue_dentro_la_siguiente_no_entra_a_la_vez | Storage | 2,17 |
| 12 | ConcurrentClaimsTests | Dos_sesiones_reclamando_a_la_vez_terminan_las_dos_y_no_se_pisan | Storage | 2,15 |
| 13 | ThresholdPolicySyncTests | La_politica_viaja_por_el_hub_y_el_re_escaneo_del_otro_clasifica_igual | App | 1,85 |
| 14 | ConflictRulesTests | Dos_sesiones_editando_el_MISMO_hallazgo_gana_la_ultima… | Storage | 1,74 |
| 15 | ReleasePipelineTests | Cada_bloque_run_del_workflow_es_PowerShell_valido | App | 1,69 |
| 16 | FactoryResetTests | A_second_user_sees_everything_disappear_after_syncing | App | 1,65 |
| 17 | ConflictRulesTests | Dos_sesiones_a_la_vez_no_pueden_chocar_en_sus_ficheros_de_sesion | Storage | 1,54 |
| 18 | ReleasePipelineTests | El_defecto_que_tumbo_la_publicacion_lo_caza_el_parser | App | 1,45 |
| 19 | SelfUpdateTests | Una_carpeta_de_preparacion_bloqueada_aborta_con_la_receta | App | 1,44 |
| 20 | AssistedFixTests | Los_cinco_pasos_de_quedarse_los_cambios_son_los_que_se_ejecutan | App | 1,43 |
| 21 | TwoCloneSyncTests | Inventory_conflict_merges_per_unit | Storage | 1,39 |
| 22 | SelfUpdateTests | La_limpieza_que_no_pudo_hacerse_se_reintenta_al_siguiente_arranque | App | 1,38 |
| 23 | TwoCloneSyncTests | Claim_conflict_resolves_in_favor_of_the_already_pushed_claim | Storage | 1,35 |
| 24 | AssistedFixClaudeTests | La_huella_del_arreglo_queda_escrita_tambien_con_Claude | App | 1,33 |
| 25 | ConflictRulesTests | La_reclamacion_que_pierde_queda_recuperable_en_el_registro_de_git | Storage | 1,32 |
| 26 | StartupSelfCheckTests | El_arranque_en_limpio_monta_el_contenedor_entero | App | 1,31 |
| 27 | FactoryResetTests | The_impact_counts_what_is_actually_in_the_hub | App | 1,28 |
| 28 | AssistedFixClaudeTests | En_pausa_no_cae_ni_un_cambio_mas_en_el_clon | App | 1,27 |
| 29 | PatternSilenceTests | Pasado_el_tope_la_gestion_pide_consolidar | App | 1,18 |
| 30 | HubSyncNowTests | Sync_now_publishes_the_local_commits_that_were_still_pending | App | 1,18 |

**Dos tests son 64 s de los 299**, el **21,4 %** del tiempo acumulado de la suite entera. Los dos son
de `FactoryResetTests` y los dos ejercitan un camino de fallo con espera. **El tercero de la lista es
seis veces más barato que el segundo**: la cola es muy corta y muy gorda.

Agrupado por clase, el reparto es todavía más concentrado:

| # | Clase | Tests | Segundos | Acumulado |
|---:|---|---:|---:|---:|
| 1 | FactoryResetTests | 14 | **72,36** | 24,2 % |
| 2 | AssistedFixTests | 68 | 14,64 | 29,1 % |
| 3 | McpBridgeTests | 5 | 10,71 | 32,6 % |
| 4 | PublishVerificationTests | 4 | 9,29 | 35,7 % |
| 5 | AppDeletionTests | 13 | 8,28 | 38,5 % |
| 6 | HubSyncRegistrationTests | 10 | 8,27 | 41,3 % |
| 7 | AssistedFixClaudeTests | 13 | 8,26 | 44,0 % |
| 8 | InventoryViewTests | 47 | 7,93 | 46,7 % |
| 9 | HubSyncNowTests | 8 | 6,56 | 48,9 % |
| 10 | FindingsViewTests | 49 | 6,19 | 50,9 % |

**Diez clases de 199 son la mitad del tiempo. Veinte son el 64,5 %.**

### 2.4 · Clasificación por zona

No hay carpetas ni namespaces que separen zonas: los siete proyectos de test tienen **todos sus
`.cs` en la raíz** (`Atalaya.App.Tests`, 150 ficheros sueltos). Así que la zona se deduce del
**contenido del fichero**, con este criterio, aplicado en este orden:

| Zona | El fichero de la clase contiene… |
|---|---|
| **WPF / contenedor** | `Application.Current`, `new Application(`, `System.Windows.Application`, `StaFact`, `STAThread`, `BuildServiceProvider`, `CompositionRoot`, `new ServiceCollection` |
| **Integración con git** | (si no es la anterior) `--bare`, `TestGit.`, `LibGit2Sharp`, `Repository.Init`, `TestFactory.` |
| **Unitario puro** | ninguno de los anteriores |

Las 199 clases se mapearon a fichero sin residuo (**0 clases sin clasificar**).

| Zona | Tests | % tests | Segundos | % tiempo |
|---|---:|---:|---:|---:|
| **Integración con git** | 1.117 | 40,3 % | **208,35** | **69,6 %** |
| **Unitario puro** | 1.449 | 52,3 % | 55,81 | 18,6 % |
| **WPF / contenedor** | 206 | 7,4 % | 35,18 | 11,8 % |

Y por proyecto:

| Proyecto | Zona | Tests | Segundos |
|---|---|---:|---:|
| `App.Tests` | integración-git | 1.103 | 186,64 |
| `App.Tests` | WPF/contenedor | 206 | 35,18 |
| `App.Tests` | unitario puro | 927 | 31,83 |
| `Storage.Tests` | integración-git | 14 | 21,71 |
| `Storage.Tests` | unitario puro | 74 | 7,83 |
| `ClaudeCode.Tests` | unitario puro | 112 | 13,46 |
| `Inventory.Tests` | unitario puro | 48 | 1,17 |
| `Domain.Tests` | unitario puro | 118 | 0,66 |
| `Copilot.Tests` | unitario puro | 164 | 0,63 |
| `ImportV4.Tests` | unitario puro | 6 | 0,24 |

**La lectura, con su límite.** El criterio clasifica **por fichero, no por test**: una clase con un
solo test que monta un repo `--bare` y veinte que no, cuenta sus veintiún tests como integración.
Eso **infla** la zona de git. Lo que la cifra sí dice sin discusión es que **el 40 % de los tests
vive en ficheros que saben montar un repo de git**, y que el tiempo se va donde está ese 40 %.

- **1.449 tests unitarios puros cuestan 55,81 s en total** —38 ms de media—. Esa parte de la suite
  es rápida por naturaleza y no es el problema.
- **Los 206 de WPF/contenedor cuestan 35,18 s** — 171 ms de media, cuatro veces más caro por test
  que un unitario, pero son tan pocos que no mueven el total.
- **Los 1.117 de integración con git cuestan 208,35 s** — 187 ms de media, y con la cola larga de
  §2.3 dentro.

### 2.5 · Los bancos: contados y clasificados, **no ejecutados**

Ninguno se ha corrido (N-8, y los de crédito por encargo explícito). **Ninguno de los cuatro está en
`Atalaya.sln`**, comprobado con `grep -oE '"[^"]*\.csproj"' Atalaya.sln | sort -u`: los 18 proyectos
de la solución son 10 de `src/` y 8 de `tests/`. Por tanto **los bancos no cuestan nada en el ciclo
`build` + `test` medido arriba** — cero segundos de los 80.

| Banco | Tamaño | Qué hace | Por qué no se corre |
|---|---:|---|---|
| `scripts/PromptBench` | 5 `.cs`, 1.088 líneas | Composición del prompt y caché del proveedor | El subcomando `composicion` es offline y gratis; `claude` **gasta cuota** (su propio README lo dice) |
| `scripts/Banco/Atalaya.Shots` | 5 `.cs`, 1.039 líneas | Capturas de 4 vistas densas y 9 diálogos, en proceso propio | N-8: el banco de capturas es herramienta a demanda, no parte del desarrollo |
| `scripts/Banco/tour.ps1` | 304 líneas (+14 de `tour-todo.ps1`) | Las 18 vistas en 4 combinaciones, contra el `dist` | N-8: **conduce la aplicación con el ratón de verdad** |
| `scripts/BancoCarga/Atalaya.Carga` | 4 `.cs`, 625 líneas | N personas auditando a la vez contra un `--bare` temporal | Banco de carga, con agente falso; no es parte del ciclo |
| `scripts/IconGen` | 1 `.cs`, 235 líneas | Genera el `.ico` multi-tamaño | Solo se corre cuando cambia el icono |

**La geometría y la concurrencia sí están dentro de la suite**, y son baratas: `PageHeaderLayoutTests`
41 tests / 2,43 s, `CycleRibbonLayoutTests` 38 / 1,63 s, `AnchoringTests` 28 / 0,15 s,
`ConcurrentClaimsTests` 1 / 2,15 s, `ConflictRulesTests` 3 / 4,60 s. **Los tests de geometría no son
el problema de tiempo** — ni de lejos.

### 2.6 · Tests de forma frente a tests de regla (N-5)

Aproximación **por nombre de clase y de test**, como pedía el encargo; no se ha leído ni uno. Un test
cuenta como **forma** si su **clase** casa
`Layout|Style|Palette|Theme|Surface|Anchor|Header|Footer|Ribbon|Xaml|Icon|Glyph|Geometry|Contrast|Spacing|Typograph|Visual|Dialog|Page|View(?!Model)`
o su **nombre** casa
`existe|no_existe|tiene_|contiene|esta_presente|hay_un|hay_una|se_pinta|lleva_|declara_|usa_el_token|misma_|mismo_`.

| Clasificación | Tests | % | Segundos |
|---|---:|---:|---:|
| **Forma (aprox.)** | **922** | **33,3 %** | 39,02 |
| **Regla (aprox.)** | 1.850 | 66,7 % | 260,32 |

**Esto es una cota superior de la forma, no una acusación.** El criterio mete en «forma» clases que
casi seguro son de regla: `PaletteContrastTests` —124 tests, la clase más grande del repo— comprueba
**contraste**, que es una regla verificable, no la presencia de un control; `FindingsViewTests` (49)
e `InventoryViewTests` (47) caen por llevar `View` en el nombre y ejercitan comportamiento. Lo que la
cifra dice con seguridad es lo otro: **un tercio de la suite vive en clases de presentación, y ese
tercio cuesta el 13 % del tiempo.** La forma es barata de correr; lo que cuesta es escribirla y
mantenerla, y eso **no se ha medido aquí**.

### 2.7 · `dist` y `--selfcheck`

```powershell
& .\scripts\publish.ps1                 # Release, win-x64, no self-contained, + AtalayaUpdater
Start-Process .\dist\Atalaya.exe --selfcheck -Wait
```

| Paso | Reloj |
|---|---:|
| `scripts/publish.ps1` | **9,17 s** |
| `Atalaya.exe --selfcheck` | **1,04 s** (salida 0) |

El `publish` es **en caliente**: los artefactos `Release` de `bin/`+`obj/` del `dist` anterior
seguían ahí y el `clean` de §2.1 no los borra. **Un `publish` desde cero no se ha medido.**

El `--selfcheck` sale en verde con once comprobaciones: cultura, configuración de despliegue,
contenedor, ajustes y migraciones, **97 servicios resueltos**, 3 ficheros del paquete, tema, carcasa,
**primera vista (Portafolio) medida y colocada a 1440×900**, y **10 diálogos medidos**. Por un
segundo de reloj, es la medida más barata del repositorio.

---

## 3 · El cierre: cuánto se escribe por entrega

Las cinco fases numeradas más recientes, con sus commits. **Los `BUGFIX-*` y los commits de release
intercalados no se cuentan**: no son fases y no cierran con el aparato de una.

```bash
git show --numstat --format= <sha>     # por cada commit de la fase, sumando por carpeta
```

| Fase | Commits | Producto (`src/`) | Tests (`tests/`) | DECISIONS | MANUAL | BACKLOG | Doc total | **doc/producto** |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| **F36-1b** | `1b7f281` `3e87e41` `8059dbd` `39e6f78` `66b5926` | +946 −535 | +365 −67 | +159 | +36 | 0 | 195 | **0,21** |
| **F36-2** | `208a7c8` | +1.903 −16 | +806 −0 | +72 | +46 | +12 | 130 | **0,07** |
| **F36-2b** | `18e1f4a` `1f43e43` `666244a` | +1.221 −426 | +600 −129 | +121 | +84 | +18 | 223 | **0,18** |
| **F37** | `de67037` `c26eaec` | +797 −92 | +1.572 −2 | +103 | +26 | +26 | 155 | **0,19** |
| **F38** | `d01ad55` | +70 −235 | +184 −170 | +90 | +3 | +11 | 104 | **1,49** |

Ficheros tocados y tests movidos, misma fuente:

| Fase | Ficheros de `src/` | Ficheros de `tests/` | `[Fact]`/`[Theory]` **+** | `[Fact]`/`[Theory]` **−** | Casos (`Fact`+`InlineData`) **+/−** |
|---|---:|---:|---:|---:|---:|
| F36-1b | 6 | 3 | 8 | 2 | +16 / −5 |
| F36-2 | 7 | 1 | 19 | 0 | +21 / −0 |
| F36-2b | 8 | 2 | 14 | 1 | +25 / −1 |
| F37 | 8 | 6 | 19 | 0 | +20 / −0 |
| F38 | 9 | 9 | 1 | 4 | +4 / −6 |

```bash
git show -U0 --format= <sha> -- tests/ | grep -cE '^\+\s*\[(Fact|Theory)'
git show -U0 --format= <sha> -- tests/ | grep -cE '^\+\s*\[(Fact|InlineData)'
```

**El conteo de atributos no cuadra con el que la entrada escribió a mano, y se dice.** F38 declara
«cinco tests nuevos, uno reescrito, ocho retirados»; el grep ve **+1/−4** atributos `Fact`/`Theory` y
**+4/−6** casos. La diferencia son filas de `[Theory]` dentro de `IdentityTests`, que se reescribió
entera (+139 −106 líneas) en vez de borrarse. **El conteo mecánico es una cota inferior**; la cifra
buena para «cuántos tests hay» sigue siendo el `.trx`.

**La lectura del ratio.** Cuatro de las cinco fases escriben **0,07–0,21 líneas de documentación por
línea de producto**: proporcionado. La quinta, **F38, escribe 1,49** — pero F38 es una fase de
**borrado** (−235 líneas de producto, +70), y ahí el ratio deja de significar nada: lo que se quita
también hay que explicarlo, y explicar por qué algo se va cuesta lo mismo que explicar por qué
llega. **El ratio doc/producto no es la cifra que denuncia el peso del cierre.** La que sí:
**las cinco fases juntas añaden 545 líneas a `DECISIONS.md`**, y ninguna quita ninguna.

---

## Lo que se ve y no se toca

Apuntado, no arreglado (es una medida, no una limpieza):

- **`FactoryResetTests`**: 14 tests, 72,36 s, el 24 % del tiempo acumulado de la suite. Dos de ellos
  —los dos caminos de fallo— son 64 s solos. Huele a espera real dentro del test.
- **`McpBridgeTests.Sin_nadie_escuchando…`**: 10,09 s en un test que, por su nombre, mide un plazo de
  rendición.
- **Dos `D-` con id repetido**: `D-710` y `D-899`, tres bloques cada uno.
- **El título de las normas dice «N-1…N-5»** y las normas son ocho.
- **Los 150 ficheros de `Atalaya.App.Tests` están todos en la raíz del proyecto**, sin carpetas, lo
  que obliga a clasificar por contenido y no por ruta.
- **`Domain.Tests`**: 0,66 s sumados en el `.trx` frente a 30 ms informados por `vstest`, sin causa
  medida.

## Método, para repetirlo

Las cifras del `.trx` (§2.2 a §2.6) y las de git (§3) salen de cinco scripts de un solo uso que
viven en el scratchpad de la sesión y **no se commitean**: parsean `tests/*/TestResults/medida.trx` a
CSV, agregan por proyecto / clase / zona, y recorren `git show --numstat`. Los criterios que aplican
—las tres expresiones de zona de §2.4, las dos de forma/regla de §2.6, la lista de verbos de §1.4 y
los SHA por fase de §3— **están escritos íntegros en este documento**, que es lo que hace falta para
volver a obtener los mismos números.

---

## Lo que la medida dice

1. **El tiempo de máquina no es el problema.** Build incremental 1,81 s + suite entera 80,10 s:
   menos de minuto y medio por cambio, con `--selfcheck` a 1 s y el `dist` a 9 s.
2. **Dentro de esos 80 s, el reparto es brutalmente desigual**: 14 tests de una clase son el 24 % del
   tiempo acumulado, 10 clases de 199 son la mitad, y 2 tests sueltos son 64 de los 299 segundos.
3. **Lo que cuesta es git, no WPF ni la geometría**: 69,6 % del tiempo en ficheros que montan
   repositorios; los 1.449 unitarios puros cuestan 56 s entre todos.
4. **El coste de arranque sí es grande y sí es de lectura**: ~354.000 tokens estimados solo en
   `DECISIONS.md`, 19.693 líneas, 474 bloques `D-`.
5. **Y ese coste está mal repartido**: cada fase reciente necesitó entre el 1,8 % y el 11,1 % del
   fichero, pero para encontrarlo hay que saber dónde está. Solo ~3 % de las decisiones está
   declarado como superado, así que **casi todo sigue formalmente vigente**.
6. **El cierre escribe poco por fase —104 a 223 líneas de documentación— pero nunca borra**: cinco
   fases, +545 líneas a `DECISIONS.md`, −0.
7. **Un tercio de la suite es presentación** y cuesta el 13 % del tiempo: barata de correr, y su
   coste real —escribirla y mantenerla— no se ha medido.
