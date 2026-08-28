# Atalaya

Aplicación de escritorio Windows (WPF, .NET 8) para gestionar de forma colaborativa
el sistema de auditoría de código v4 con agentes de IA (GitHub Copilot). El "backend"
es un repositorio git compartido (**audit-hub**); no hay servidor propio ni base de
datos externa.

## Arquitectura

Solución en capas; las flechas indican dependencias
(`Domain ← Storage|Inventory|Copilot|ImportV4 ← App`; nada referencia a `App`):

| Proyecto | Rol | TFM |
|---|---|---|
| `src/Atalaya.Domain` | Entidades, ULID, fingerprint semántico, máquina de confianza, dedupe. Sin dependencias. | `net8.0` |
| `src/Atalaya.Storage` | Serialización del esquema (§2), `HubSyncService` (LibGit2Sharp: pull/rebase/push + resolución de conflictos). | `net8.0` |
| `src/Atalaya.Inventory` | Escaneo de clones: stack, módulos, unidades, LOC, hashes, re-escaneo con renombres. | `net8.0` |
| `src/Atalaya.Copilot` | Integración `GitHub.Copilot.SDK` (adaptador real, autenticado con el token de cuenta y usando siempre el CLI embebido del paquete) + `ICopilotAgent` con fake inyectable; tools, permisos, coste, prompts/brief. | `net8.0` |
| `src/Atalaya.ImportV4` | Importador tolerante del formato markdown v4. | `net8.0` |
| `src/Atalaya.App` | WPF + MVVM (CommunityToolkit.Mvvm), Generic Host (DI), tema Fluent (WPF-UI), vistas V1–V7 + Cuenta; device flow de GitHub y el `GitHubAccountService` que sirve el token a git, a Copilot y a la identidad de commits. | `net8.0-windows` |
| `tests/*` | xUnit + FluentAssertions, un proyecto por `src`. | |

**Principios**: el agente de IA nunca escribe estado — entrega hallazgos por una tool
tipada y la app valida y persiste (mejora 1). Los dashboards se calculan siempre; en
disco solo viven datos primarios (mejora 8). Identidad de hallazgo = ULID + fingerprint;
los `BUG-0042` son alias de presentación (mejora 2).

## Empezar

1. **Instala Atalaya.**
2. **Conectar con GitHub.** Al abrirla por primera vez aterrizas en **Cuenta**: pulsa
   **Conectar con GitHub**, escribe en `github.com/login/device` el código que te muestra
   (el botón lo copia solo) y autoriza. Atalaya verifica en cadena que estás autenticado,
   que tienes acceso al hub —lo clona ahí mismo— y que tu Copilot responde.
3. **Audita.** Inventario (V2) → selecciona unidades → **Auditar selección** → sigue el
   progreso en **V5 (sesión en vivo)**.

No hay nada más que configurar: **ni la URL del hub, ni un PAT, ni la identidad git, ni una
consola**. Ese mismo login de GitHub sirve para las tres cosas — el acceso git al hub, la
autenticación de Copilot y el autor de los commits.

**Requisitos:** Windows 10/11 con el runtime de .NET 8 (el SDK 8 si vas a compilar) y una
cuenta de GitHub **con asiento de Copilot**. No necesitas Node.js ni el CLI de Copilot:
Atalaya usa el binario que trae el propio paquete del SDK. (Para montar un despliegue
nuevo, ver el [anexo de la OAuth App](#anexo-registrar-la-oauth-app-administrador).)

### Después de conectar

- **Nueva aplicación**: indica ruta del clon local + URL del repo; Atalaya detecta el stack,
  construye el inventario del primer ciclo y lo publica. La ruta del clon local es **por
  máquina** (`%LOCALAPPDATA%/Atalaya/machines.json`), nunca va al hub.
- **Eliminar una aplicación**: la papelera de su tarjeta en el Portafolio. Ver
  [Dar de baja una aplicación](#dar-de-baja-una-aplicación) — es un borrado completo y pide
  escribir el nombre para confirmarlo.
- **Cuenta** (rail izquierdo, o clic en tu avatar de la barra de estado) muestra tu perfil,
  el estado de las cuatro comprobaciones —re-ejecutables con **Comprobar conexión**—, la
  fecha del último sync y el botón **Desconectar** (borra tus credenciales; **no** toca el
  clon del hub, así que puedes reconectar con otra cuenta sin perder nada).
- El indicador **verde/ámbar/rojo** de la barra es la sincronización con git. Junto a él, tu
  avatar; si sale un **⚠ ámbar**, GitHub ha rechazado tus credenciales y hay que reconectar.
- La **barra inferior es solo para lo estable**: sincronización, cuenta y «Auditando…» mientras
  haya una sesión viva. Los avisos (sesión terminada, fallo de sync, un claim que perdiste) son
  **efímeros**: aparecen abajo a la derecha, se van solos a los 8 s y se descartan con un clic.
  Nunca se apilan ahí: del cierre de sesión solo hay uno, y el resumen que sí se puede consultar
  vive en el item **«Última sesión»** del rail.

### Diagnóstico rápido

| Síntoma | Causa | Solución |
|---|---|---|
| El botón **Authorize** de github.com sale deshabilitado | La organización **restringe las OAuth Apps** y no ha aprobado «Atalaya» | En la sección *Organization access*, pulsa **Request**; o pide a un *owner* que apruebe la app. [Doc de GitHub](https://docs.github.com/es/organizations/managing-oauth-access-to-your-organizations-data/about-oauth-app-access-restrictions) |
| «Entra primero en github.com y completa el SSO…» | La organización usa **SAML SSO** y tu sesión no está activa | Abre `github.com`, completa el SSO de la organización y pulsa **Comprobar conexión** |
| «Tu cuenta X no pertenece a la organización Y» | La cuenta con la que te has autenticado no es miembro | Pide acceso a un *owner*, o conéctate con la cuenta correcta |
| «puede leer … pero NO escribir en él» | Tienes acceso de lectura al hub, pero Atalaya necesita publicar. GitHub devuelve **404** a un push sin permiso de escritura, igual que si el repo no existiera | Pide permiso **Write** sobre el repositorio del hub |
| «no ve el repositorio …: o no existe, o es privado» | La URL del despliegue apunta a un repo que tu cuenta no ve | Revisa `hubUrl` en `appsettings.deploy.json` y que te hayan añadido al repositorio |
| «Tu cuenta no tiene asiento de Copilot asignado» | Autenticación correcta, **falta el asiento** (no es un fallo de login) | Pídelo al administrador; compruébalo en [github.com/settings/copilot](https://github.com/settings/copilot) |
| «El código ha caducado» | Han pasado ~15 min sin autorizar | Vuelve a pulsar **Conectar con GitHub** |
| «Sin conexión con GitHub» | Red o proxy | Corrige la red y pulsa **Reintentar** / **Comprobar conexión** |
| «certificate revocation status could not be verified» | Tu red bloquea los endpoints **CRL/OCSP** (proxy corporativo que inspecciona TLS). No es un fallo de credenciales | Atalaya continúa por defecto si el certificado es de confianza, está en vigor y corresponde al servidor; te lo avisa en **Cuenta**. El arreglo correcto es que IT desbloquee esos endpoints. Si lo ves como error, tienes activado **Exigir comprobación de revocación TLS** en Opciones avanzadas |
| «El certificado que presenta el servidor no es de confianza» | Tu empresa inspecciona TLS y falta su **CA corporativa** en el almacén de Windows | Instala la CA de tu organización. Atalaya nunca acepta certificados no confiables |
| «Este despliegue no tiene configurado el client id…» | Falta el prerrequisito del administrador | Ver el [anexo](#anexo-registrar-la-oauth-app-administrador) |
| GitHub rechaza las credenciales tras funcionar | Token revocado o expirado por la organización | **Cuenta → Conectar con GitHub** otra vez |

### Si tu organización bloquea las OAuth Apps y no hay un *owner* disponible

**Ajustes → Opciones avanzadas** conserva el camino antiguo: un **PAT** (cifrado con DPAPI)
que se usa **solo** si no hay cuenta conectada. Es una salida de emergencia, no el camino
normal. Ahí mismo hay un override de la URL del hub, **solo para desarrollo**.

### Ajustes relacionados (en `%LOCALAPPDATA%/Atalaya/settings.json`)

- `copilotTimeoutMinutes` (por defecto **15**): tiempo máximo por unidad. El SDK trae 1
  minuto por defecto, insuficiente para una auditoría real; súbelo si tienes unidades muy
  grandes.
- `copilotBaseDirectory` (por defecto **vacío**): déjalo vacío. Solo tiene efecto en el
  camino heredado (sin cuenta conectada), donde marca dónde busca el SDK el login del CLI.
- `maxPassesPerUnit` (por defecto **5**): tope de pasadas del **barrido** sobre una unidad.
  Editable en **Ajustes → Umbrales**. La app repite la revisión hasta que una pasada queda
  *seca*; con tope **1** cada auditoría es una pasada única. Si se agota sin secarse, la unidad
  se cierra como *«cobertura posiblemente incompleta»* — visible en el veredicto y en el informe.
- `copilotModel` (por defecto **`gpt-5`**): modelo con el que corren las sesiones nuevas.
  El desplegable de **Ajustes** se puebla con lo que el SDK lista para **tu** cuenta (con su
  multiplicador de coste cuando lo publica); si no se puede consultar, se muestra el configurado
  con un aviso. El modelo en uso queda registrado en la sesión y en su informe.

Los secretos **no** están aquí: la cuenta vive cifrada con DPAPI en
`%LOCALAPPDATA%/Atalaya/auth.dat`, y nunca sale de tu máquina.

## Veredictos del auditor y disputas

Cuando el auditor revisa una unidad se pronuncia sobre cada hallazgo que ya existe en ella:

| Veredicto | Qué hace la app |
|---|---|
| `presente` | Reconfirma el hallazgo (máquina de confianza). |
| `arreglado` | Lo resuelve — **solo si la unidad cambió** desde la última vez que se vio (mismo commit o mismo contenido ⇒ se degrada a `presente` y queda registrado). |
| `no-es-defecto` | Discrepa de quien lo reportó: **no resuelve ni desactiva**, marca el hallazgo como *disputado* con el razonamiento y el modelo. |
| `no-verificable` | Lo marca `needsReview`. |

Un hallazgo **disputado** sigue activo y espera decisión humana. En **V3 → «Solo disputados»** se
cierra la disputa en una de las dos direcciones, siempre con autor:

- **«Es falso positivo»** — se silencia con motivo `falso-positivo`. No es una resolución: nunca
  hubo nada que arreglar.
- **«Sigue siendo defecto»** — se retira la marca y el hallazgo continúa igual.

Varios modelos discrepando del mismo hallazgo se acumulan (`⚖ disputado ×N modelos`): es la señal
de que merece una mirada humana.

Detener una sesión es un final ordenado: se guarda lo auditado hasta la parada, con su informe, se
liberan los claims y se publica. Una sesión detenida no cierra ciclo.

## La sesión en vivo

Lanzar una auditoría es un acto explícito desde **Inventario**; a partir de ahí corre en segundo
plano y **puedes navegar libremente**:

- El item **«Sesión en vivo»** de la barra lateral late mientras corre y pasa a **«Última sesión»**
  al terminar. Al volver, V5 se reconstruye con el estado real — no depende de haber estado abierta.
- La barra inferior dice **«Auditando {app} · unidad n/N · pasada p»** desde cualquier página, y
  lleva a V5 de un clic.
- Si la sesión termina sin la vista abierta, un aviso efímero resume el resultado y el desglose
  completo te espera en **«Última sesión»**.
- Cerrar la aplicación con una sesión viva pregunta antes y, si aceptas, la **detiene
  ordenadamente**: guarda lo auditado con su informe y libera las unidades.

V5 tiene tres columnas — cola de unidades con su estado y coste, actividad (el texto real del
agente intercalado con los eventos de herramienta) y hallazgos por severidad — y un pie con
progreso, tiempo, **llamadas y coste** (la métrica que manda), tokens y media por unidad. Al
terminar aparece una pantalla de cierre donde **cada contador se despliega** para ver qué hallazgos
lo componen.

En la cola, cada unidad se lee por **el nombre de su fichero** (`CommonStatics.cs`): la ruta
completa está en el tooltip y en la cabecera de su sección de actividad. Si dos unidades del mismo
lote se llaman igual, solo esas dos ganan el tramo de ruta que las distingue
(`Class/EnumContextMenuType.cs` frente a `Enums/EnumContextMenuType.cs`).

**Si la aplicación muere de golpe** (cierre forzado, cuelgue), la siguiente vez que arranque
detecta la sesión que quedó abierta, escribe su registro marcado como interrumpida, libera las
unidades que tuviera reclamadas y te lo dice. Los hallazgos ya estaban guardados: la ingesta
escribe en vivo.

## Dar de baja una aplicación

La papelera de la esquina de cada tarjeta del **Portafolio** hace un *hard-reset*: elimina del hub
`apps/{slug}/` entera —hallazgos, sesiones, informes, inventario, silencios, claims y `app.json`—
en un commit explicativo (`app: hard-reset de {slug} por {usuario}`) que se publica en el acto.
También limpia el rastro local de esa app en esta máquina (su ruta de clon en `machines.json`).

- Antes de borrar pide **escribir el nombre de la aplicación**, y la pantalla dice exactamente qué
  se pierde: cuántos hallazgos, sesiones, informes y silencios.
- **No se toca el repositorio auditado** ni tu clon local del código: se borra lo que Atalaya sabe
  de esa app, no la app.
- El **historial git del hub conserva una copia** que un administrador puede rescatar; desde
  Atalaya no hay «restaurar».
- Los demás usuarios la ven desaparecer con su siguiente sincronización.
- Con una sesión activa sobre esa app el icono está **deshabilitado**: detén la sesión primero.
- Volver a auditarla más adelante es un alta normal desde **Nueva aplicación**, empezando de cero.

## Configuración de despliegue

Junto al ejecutable viaja **`appsettings.deploy.json`** (también embebido en el binario como
valor de fábrica; el fichero de disco gana):

```json
{
  "hubUrl": "https://github.com/alloci88/atalaya-hub",
  "gitHubClientId": "<CLIENT_ID>",
  "organizationLogin": ""
}
```

- **`hubUrl`** — el repositorio audit-hub. El usuario nunca lo ve ni lo escribe. **Migrar el
  hub al repo de la organización = cambiar esta línea en el despliegue**, cero acciones de
  usuario: los clones existentes se re-apuntan solos al nuevo remoto conservando su historial, que
  se publica en el siguiente push, y la página de Cuenta avisa del cambio.
  Antes de migrar, comprueba que cada usuario tenga permiso **Write** sobre el repo destino (ser
  miembro de la organización no basta) y, si la organización restringe las OAuth Apps, que
  «Atalaya» esté aprobada para ella.
- **`gitHubClientId`** — el client id de la OAuth App (ver anexo). **No es un secreto**: el
  device flow no usa client secret, por eso puede ir embebido.
- **`organizationLogin`** — si se rellena, tras el login Atalaya comprueba la pertenencia a
  esa organización y lo dice claro si falta, en vez de fallar después al clonar. Vacío =
  no se comprueba (correcto mientras el hub sea un repo personal: el clon es la puerta real).

## Anexo: registrar la OAuth App (administrador)

> **Ya está hecho** para el despliegue actual: la app «Atalaya» está registrada y su client id
> (`Ov23liC0Kt139QXJauYV`) viaja en `appsettings.deploy.json`. Estos pasos son la receta para
> re-registrarla al migrar el hub a la organización, o para montar un despliegue nuevo.

Prerrequisito **humano**, una sola vez para todo el equipo:

1. GitHub → **Settings** → **Developer settings** → **OAuth Apps** → **New OAuth App**.
2. *Application name*: `Atalaya`. *Homepage URL*: cualquiera (p. ej. la del repo).
   *Authorization callback URL*: irrelevante en device flow — pon la misma homepage.
3. Marca **Enable Device Flow**. ← imprescindible.
4. Copia el **Client ID** y pégalo en `gitHubClientId` de `appsettings.deploy.json` en el
   despliegue.

Cuando el hub migre a la organización: se re-registra la app allí (o un *owner* aprueba la
app existente para la organización) y solo cambian `gitHubClientId` / `hubUrl` en el fichero
de despliegue.

**Scopes que pide Atalaya:** `repo` (git contra el hub privado), `read:org` (comprobar
pertenencia) y `read:user` (login, nombre, avatar, email). Nada más.

## Flujos

- **Lotes** (V2 → seleccionar unidades → *Auditar selección*): reclama unidades, audita
  con Copilot, ingiere hallazgos en vivo (V5), aplica resolución implícita, marca
  auditadas, escribe sesión + informe y hace push. Al quedar 0 pendientes cierra el ciclo.
- **Integral / Superficial**: ámbito completo / presupuesto parcial. Superficial nunca
  marca resueltos (regla anti-degradación).
- **Verify** (V3): re-verifica hallazgos; re-ancla por snippet; `no-verificable` o
  ubicación perdida ⇒ `needsReview`, nunca "resuelto".
- **Gobernanza** (V3/V4): silenciar (motivo + caducidad), asignar, comentar, cambiar
  severidad, resolver manual, y **generar prompt de arreglo** (§5.7) al portapapeles.
- **Importar v4**: migra una carpeta `CodeAudit/` (tolerante; registra lo no importable).
- **Métricas** (V6): panel filtrable por aplicación y periodo, con **Coste en el tiempo** y,
  justo debajo, **Resoluciones en el tiempo** (una línea por aplicación, mismo color en las
  dos, con toggle *Acumulado*), cobertura del ciclo, flujo de hallazgos y registro de sesiones.
- **Informes** (V7): la lista de todo lo que las auditorías dejaron escrito, con filtros y
  búsqueda por contenido, visor markdown renderizado dentro de la app (tablas incluidas) y
  descarga del `.md`. Es el único sitio desde el que se lee un informe.

El manual de uso, pantalla a pantalla, está en [`MANUAL.md`](MANUAL.md). Lo que queda por hacer
y lo que se decidió aplazar, en [`BACKLOG.md`](BACKLOG.md).

## Compilar y probar

```bash
dotnet build Atalaya.sln
dotnet test Atalaya.sln
```

`Domain` y `Storage` compilan con *warnings-as-errors*.

## Empaquetado

`scripts/publish.ps1` produce una carpeta auto-contenida (framework-dependent o
self-contained) lista para distribuir:

```powershell
pwsh scripts/publish.ps1            # framework-dependent
pwsh scripts/publish.ps1 -SelfContained
```

## Assets de identidad

Todo lo visual vive en `assets/`. Lo versionado incluye tanto las **fuentes** como lo
**generado**, para que compilar Atalaya no dependa de tener un renderizador de SVG:

| Fichero | Qué es |
|---|---|
| `atalaya-icon.svg` | El icono. Fuente de los tamaños 32, 48, 64 y 256. |
| `atalaya-icon-small.svg` | El mismo icono como **silueta**, fuente de 16 y 24. A 16 px el halo, el degradado y la tronera son ruido: la variante pequeña los suelta y engorda los rasgos. |
| `atalaya.ico` | **Generado.** Multi-tamaño (16, 24, 32, 48, 64, 256). Es el icono del ejecutable, de la ventana, del Alt-Tab, de la barra de tareas y del aviso propio de la app. |
| `maxam-logo-source.png` | El logotipo corporativo tal y como lo entregó comunicación. No se toca. |
| `maxam-logo.png` | **Generado.** Lo que la aplicación pinta en tema claro. Copia byte a byte de su fuente, que ya viene con transparencia. |
| `maxam-logo-dark-source.png` | La versión en negativo (letras claras), tal y como la entregó comunicación. |
| `maxam-logo-dark.png` | **Generado.** Lo que la aplicación pinta en tema oscuro. Es *opcional*: si falta, el logotipo normal se pinta sobre una placa clara de soporte. |

Para regenerar lo generado tras tocar un SVG:

```powershell
pwsh scripts/build-assets.ps1            # regenera atalaya.ico y las dos variantes del logo
pwsh scripts/build-assets.ps1 -Verify    # además falla si lo versionado no coincide con sus fuentes
```

Detrás hay una herramienta .NET (`scripts/IconGen/`) **fuera de `Atalaya.sln`**: rasteriza
los SVG con SVG.NET y escribe el contenedor ICO. Es determinista — mismas fuentes, mismos
bytes—, así que regenerar sin cambios no ensucia el árbol.

> **El logotipo corporativo no se altera.** La única preparación permitida es técnica
> (dejar el fondo en transparencia). Recolorearlo o redibujarlo no es decisión del equipo
> de producto: la versión en negativo la entrega comunicación, y lo que hace la aplicación
> es elegir cuál de las dos toca según el tema.

## Limitaciones conocidas

- El **adaptador real de Copilot** (`RealCopilotAgent`) está compilado y verificado
  contra la superficie real del paquete `GitHub.Copilot.SDK` 1.0.11, pero su ruta de
  ejecución requiere un asiento Copilot (no disponible en CI); los tests end-to-end
  usan el `FakeCopilotAgent`, que ejercita todo el pipeline.
- Del **device flow** está verificada contra GitHub la petición de código (devuelve un grant
  válido con el `client_id` del despliegue, así que la OAuth App y su *Enable Device Flow*
  están bien). La segunda pata —autorizar en el navegador y recibir el token— exige una
  persona delante y no se ha ejercitado; su máquina de estados (pending → slow_down →
  éxito, caducidad, denegación) sí está cubierta por tests con el endpoint OAuth simulado.
- La **verificación de asiento de Copilot** usa `ListModelsAsync` y clasifica el fallo por
  el texto del error; si GitHub cambia esos mensajes, el caso "sin asiento" podría caer en
  el diagnóstico genérico.
- `read_signatures` usa una heurística de líneas; la extracción con Roslyn para C#
  queda pendiente.
- Las gráficas de V6 se dibujan con WPF puro (barras) para evitar dependencias nativas
  no verificables en el entorno de build; `MetricsQuery` (el cálculo) es agnóstico a la
  librería de gráficas.
- El re-anclaje por snippet asume snippets de una línea (los multilínea pueden marcar
  `needsReview`).

Ver `DECISIONS.md` para todas las decisiones tomadas en zonas de libertad.
