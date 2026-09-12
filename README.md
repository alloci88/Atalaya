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
| `src/Atalaya.App` | WPF + MVVM (CommunityToolkit.Mvvm), Generic Host (DI), tema Fluent (WPF-UI), vistas V1–V8 + Cuenta; device flow de GitHub y el `GitHubAccountService` que sirve el token a git, a Copilot y a la identidad de commits. | `net8.0-windows` |
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
- `providerModels` (por defecto **vacío**): el modelo con el que corren las sesiones nuevas de
  **cada proveedor**, indexado por su identificador (`{"copilot": "gpt-5", "claude-code": "opus"}`).
  El desplegable de **Ajustes** se puebla con lo que el proveedor lista para **tu** cuenta (con su
  multiplicador de coste cuando lo publica); si no se puede consultar, se muestra el configurado
  con un aviso. El modelo en uso queda registrado en la sesión y en su informe. Las máquinas que
  vengan de una versión anterior traen su modelo en `copilotModel` y `claudeCodeModel`: la
  aplicación los adopta al arrancar, una sola vez, sin perder ninguna elección.

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
  "hubUrl": "",
  "appRepoUrl": "https://github.com/alloci88/Atalaya",
  "gitHubClientId": "",
  "organizationLogin": ""
}
```

> El ejemplo son los valores REALES del despliegue actual, copiados de
> `src/Atalaya.App/appsettings.deploy.json`. Se escriben aquí una vez y se leen de ahí: los enlaces
> del «Acerca de» y el chequeo de versión salen de `appRepoUrl`, nunca de una URL escrita en el
> código (BUGFIX-VERSION).

> **Y de fábrica solo viene el `appRepoUrl`.** El hub, el client id y la organización van
> **vacíos** a propósito: son de CADA despliegue, no del código. Quien instala Atalaya conecta su
> cuenta en **Cuenta** y elige su hub al configurarla; escribir aquí los de una organización
> concreta haría que cualquier copia del repositorio saliera apuntando a un hub que no es el suyo.
> El `appRepoUrl` sí viene puesto porque es el repositorio de la propia Atalaya —el sitio del que
> salen SUS Releases— y ése es el mismo para todo el mundo.

- **`hubUrl`** — el repositorio audit-hub. El usuario nunca lo ve ni lo escribe. **Migrar el
  hub al repo de la organización = cambiar esta línea en el despliegue**, cero acciones de
  usuario: los clones existentes se re-apuntan solos al nuevo remoto conservando su historial, que
  se publica en el siguiente push, y la página de Cuenta avisa del cambio.
  Antes de migrar, comprueba que cada usuario tenga permiso **Write** sobre el repo destino (ser
  miembro de la organización no basta) y, si la organización restringe las OAuth Apps, que
  «Atalaya» esté aprobada para ella.
- **`appRepoUrl`** — el repositorio de **la propia Atalaya**, de donde salen sus Releases. Es lo
  que consulta el aviso de versión nueva al arrancar, con el token de la cuenta ya conectada (cero
  credenciales nuevas). Va aparte del hub a propósito: son dos repositorios con dos vidas
  distintas, y acoplarlos haría que migrar el hub apagara el aviso sin que nadie se enterara.
  Vacío = no se comprueba nada y no se avisa de nada, en silencio.
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
- **Auditar lo que ha cambiado** (F9): la operación de cada sprint. Compara el commit de la
  auditoría de cada unidad con HEAD del clon local —commit contra commit, con LibGit2Sharp— y dice
  qué clases han cambiado desde entonces, con cuántos commits y cuándo. La deriva es **derivada**
  (se calcula del historial cada vez, no se persiste) y **ortogonal** al estado de auditoría: una
  clase puede estar «auditada» y «cambiada» a la vez. Guardarraíl anti-bucle: los arreglos hechos
  desde Atalaya se reconocen por la huella del contenido que dejaron y salen como «arreglada —
  pendiente de verificar», no como deuda nueva. Cuando el historial no coopera —commit ausente,
  reescrito, clon por detrás— lo dice, en vez de un cero. «Seleccionar cambiadas» en el Inventario
  e indicador clicable en el Portafolio.

El manual de uso, pantalla a pantalla, está en [`MANUAL.md`](MANUAL.md). Lo que queda por hacer
y lo que se decidió aplazar, en [`BACKLOG.md`](BACKLOG.md).

## Compilar y probar

```bash
dotnet build Atalaya.sln
dotnet test Atalaya.sln
```

`Domain` y `Storage` compilan con *warnings-as-errors*.

**Y que arranque**, que es lo que ningún test unitario mira:

```bash
dist/Atalaya.exe --selfcheck          # 0 = arranca · 1 = no arranca
```

Hace el arranque completo sin abrir ventana y enumera lo que comprueba. Es el mismo chequeo que
corre el workflow de release sobre el paquete antes de publicarlo, y el mismo que corren los tests
`StartupSelfCheckTests` sobre una carpeta de estado vacía —un primer arranque en limpio—.

### Formato de números y fechas

Atalaya escribe **siempre en es-ES**, no en la cultura de la máquina: la aplicación es monolingüe
en español y sus informes se comparten entre personas (ver D-627 en `DECISIONS.md`). La frontera es:

| | Cultura |
|---|---|
| Interfaz, informes markdown, `ESTADO.md`, evidencias de un hallazgo | **es-ES** (`AppCulture.Display`) |
| JSON del hub, ULID, hashes, alias `BUG-0042`, rutas | **invariante** |

`AppCulture.Apply()` lo fija al arrancar para todos los hilos; los **artefactos compartidos** dicen
su cultura a mano, porque tienen que salir igual aunque los genere un test o un script.

En los tests, `Atalaya.App.Tests` fija la cultura con un `[ModuleInitializer]` — un test que asuma
la de la máquina solo falla en el runner, que es tarde. Los demás proyectos de test **no** la
fijan a propósito: prueban código invariante por diseño, y pinarles es-ES ocultaría un fallo real.

## Empaquetado

`scripts/publish.ps1` produce una carpeta auto-contenida (framework-dependent o
self-contained) lista para distribuir:

```powershell
pwsh scripts/publish.ps1            # framework-dependent
pwsh scripts/publish.ps1 -SelfContained
```

Esto es el camino de **desarrollo**. Lo que se reparte al equipo sale de una Release: ver
«Publicar una versión», más abajo.

## Publicar una versión

La versión vive en **un solo sitio**: `<Version>` en `Directory.Build.props`. De ahí sale la de
todos los ensamblados y la que enseña «Acerca de».

El ritual son **dos comandos** (los ejecuta una persona, como todo push):

```bash
git tag v1.2.3
git push origin v1.2.3
```

### El tag: `v` minúscula y un número, y se empuja aparte

Tres reglas cortas, y las tres se aprendieron pagando (BUGFIX-RELEASE en `DECISIONS.md`):

- **`v` MINÚSCULA.** El workflow escucha `v*`, así que un `V1.2.3` **no publica nada** — y lo peor
  no es eso: se queda en el repositorio, `git describe` lo ve, y contamina la versión de los builds
  siguientes. Eso tumbó la publicación de la 1.4.1 cinco veces. Hoy ya no puede: un tag que no es
  un número se ignora al estampar, y el propio workflow avisa en su log de los tags mal escritos
  que encuentre. Pero sigue sin publicar, así que **si te equivocas, bórralo**:
  `git tag -d V1.2.3 && git push origin :refs/tags/V1.2.3`.
- **Un número SemVer detrás**: `v1.2.3`, o `v2.0.0-rc.1`. Nada de `v.1.2.3` ni `v1.2`. El workflow
  lo comprueba **en el primer paso, en segundos**, antes de los tres minutos de tests.
- **El tag se empuja EXPLÍCITAMENTE.** `git push` a secas no lleva tags, y **GitHub Desktop no
  siempre empuja un tag creado sobre un commit que ya estaba subido**: si lo creas desde Desktop,
  comprueba que ha llegado (`git push origin v1.2.3` no hace daño si ya está).

El workflow `.github/workflows/release.yml` hace el resto en `windows-latest`:

1. **Pasa los tests**, y **guarda el `.trx` como artefacto del run pase o falle**. Cuando un test
   cae en el runner y no en tu máquina, el nombre y la pila están ahí — no hay que adivinar ni
   relanzar a ciegas. Un paquete no se publica con tests rojos.
2. **Publica self-contained win-x64** con la versión **del tag** (`-p:Version=1.2.3`), así que el
   binario distribuido no puede mentir sobre el tag que lo produjo — el propio workflow comprueba
   el estampado y falla si no coinciden.
3. **Añade el relevo de actualización** (`AtalayaUpdater.exe`), publicado aparte como un único
   fichero self-contained. Es lo que sustituye la carpeta cuando alguien pulsa «Actualizar»: se
   copia a `%LOCALAPPDATA%` y corre desde fuera de lo que va a reemplazar, así que tiene que
   bastarse solo. El workflow **falla** si no acaba en `dist/`.
4. **Comprime** `dist/` como `Atalaya-v1.2.3-win-x64.zip`, con su `appsettings.deploy.json`, y
   calcula su **`Atalaya-v1.2.3-win-x64.zip.sha256`**. Los dos se adjuntan a la Release: la app
   verifica el checksum antes de tocar la instalación, y **sin ese fichero se niega a instalar** y
   manda al camino manual.
5. **Comprueba que el paquete arranca.** Descomprime el zip aparte y ejecuta
   `Atalaya.exe --selfcheck` sobre lo que se va a descargar: el arranque entero sin abrir ventana
   —cultura, despliegue, contenedor, ajustes y migraciones, **todos** los servicios registrados,
   los ficheros que deben viajar y la carcasa—, con 0 o 1 por respuesta. Si sale 1, **no se
   publica nada**. Existe porque la 1.1.3 salió con 1.661 tests en verde y no arrancaba: ningún
   test montaba el contenedor, así que el grafo de dependencias no lo miraba nadie (ver
   BUGFIX-ARRANQUE en `DECISIONS.md`).
6. **Crea la Release** del tag con los dos adjuntos y las notas que GitHub genera a partir de los
   commits desde el tag anterior (`--generate-notes`). Se pueden pulir a mano en la web después.

No hacen falta secretos: el `GITHUB_TOKEN` del propio workflow basta, y sus permisos son los
mínimos (`contents: write`).

**Sin consola a mano.** En la pestaña **Actions → Release → Run workflow** se puede lanzar dando
la versión (`1.2.3` o `v1.2.3`): el workflow crea el tag él mismo.

**Reintentar es seguro.** El paso de publicación es idempotente: si la Release del tag ya existe
—creada a mano desde la web, o por un intento anterior que falló más tarde— se le adjuntan el zip
y su checksum en vez de fallar. El primer fallo nunca deja un tag quemado.

**El formato del paquete no cambia**: sigue siendo «descarga el zip y descomprime donde quieras».
La actualización desde la app (F11) se construyó sobre ese formato a propósito — se evaluó
Velopack y se descartó; el veredicto y lo que se probó están en `DECISIONS.md`.

`scripts/publish.ps1` sigue siendo el camino de desarrollo y no lo toca nada de esto.

### Numeración

- **Patch** (`1.2.3` → `1.2.4`): arreglos.
- **Minor** (`1.2` → `1.3`): funcionalidad nueva compatible.
- **Major**: cambios que obligan a hacer algo al equipo (migrar el hub, reconectar cuentas).

**El tag es el único ritual**: no hay que subir ningún número a mano. Desde BUGFIX-VERSION, un
build local se estampa a partir de `git describe` (`1.2.3-dev.4+abc1234`) y el `<Version>` de
`Directory.Build.props` es solo el suelo para cuando no hay git con el que preguntar. La versión
que se distribuye la pone el workflow desde el tag.

### Firma de código

Los ejecutables **no están firmados**, así que Windows SmartScreen avisa la primera vez que
alguien ejecuta un zip recién descargado («Más información» → «Ejecutar de todas formas»). Se
quita de raíz con un certificado de firma; está apuntado en [`BACKLOG.md`](BACKLOG.md).

## Assets de identidad

Todo lo visual vive en `assets/`. Lo versionado incluye tanto las **fuentes** como lo
**generado**, para que compilar Atalaya no dependa de tener un renderizador de SVG:

| Fichero | Qué es |
|---|---|
| `atalaya-icon.svg` | El icono. Fuente de los tamaños 32, 48, 64 y 256. |
| `atalaya-icon-small.svg` | El mismo icono como **silueta**, fuente de 16 y 24. A 16 px el halo, el degradado y la tronera son ruido: la variante pequeña los suelta y engorda los rasgos. |
| `atalaya.ico` | **Generado.** Multi-tamaño (16, 24, 32, 48, 64, 256). Es el icono del ejecutable, de la ventana, del Alt-Tab, de la barra de tareas y del aviso propio de la app. |

Y eso es todo lo que hay: **`assets/` no guarda ningún logotipo de organización**. La marca que
Atalaya enseña en pantalla es el **nombre de la organización configurada** —el `organizationLogin`
del despliegue, o el que declare el hub—, que es un dato y no una imagen. Sin organización no se
enseña marca alguna: ni logo, ni nombre, ni hueco reservado.

Para regenerar lo generado tras tocar un SVG:

```powershell
pwsh scripts/build-assets.ps1            # regenera atalaya.ico
pwsh scripts/build-assets.ps1 -Verify    # además falla si lo versionado no coincide con sus fuentes
```

Detrás hay una herramienta .NET (`scripts/IconGen/`) **fuera de `Atalaya.sln`**: rasteriza
los SVG con SVG.NET y escribe el contenedor ICO. Es determinista — mismas fuentes, mismos
bytes—, así que regenerar sin cambios no ensucia el árbol.

> **El icono es de casa.** La torre de `atalaya-icon.svg` es original y se dibuja aquí; no
> se toma prestada de ningún juego de iconos de terceros. Lo que la aplicación pinta como
> marca de organización es texto, así que no hay ninguna imagen ajena que mantener.

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
