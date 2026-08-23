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
| `src/Atalaya.App` | WPF + MVVM (CommunityToolkit.Mvvm), Generic Host (DI), tema Fluent (WPF-UI), vistas V1–V6 + Cuenta; device flow de GitHub y el `GitHubAccountService` que sirve el token a git, a Copilot y a la identidad de commits. | `net8.0-windows` |
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
- **Cuenta** (rail izquierdo, o clic en tu avatar de la barra de estado) muestra tu perfil,
  el estado de las cuatro comprobaciones —re-ejecutables con **Comprobar conexión**—, la
  fecha del último sync y el botón **Desconectar** (borra tus credenciales; **no** toca el
  clon del hub, así que puedes reconectar con otra cuenta sin perder nada).
- El indicador **verde/ámbar/rojo** de la barra es la sincronización con git. Junto a él, tu
  avatar; si sale un **⚠ ámbar**, GitHub ha rechazado tus credenciales y hay que reconectar.

### Diagnóstico rápido

| Síntoma | Causa | Solución |
|---|---|---|
| El botón **Authorize** de github.com sale deshabilitado | La organización **restringe las OAuth Apps** y no ha aprobado «Atalaya» | En la sección *Organization access*, pulsa **Request**; o pide a un *owner* que apruebe la app. [Doc de GitHub](https://docs.github.com/es/organizations/managing-oauth-access-to-your-organizations-data/about-oauth-app-access-restrictions) |
| «Entra primero en github.com y completa el SSO…» | La organización usa **SAML SSO** y tu sesión no está activa | Abre `github.com`, completa el SSO de la organización y pulsa **Comprobar conexión** |
| «Tu cuenta X no pertenece a la organización Y» | La cuenta con la que te has autenticado no es miembro | Pide acceso a un *owner*, o conéctate con la cuenta correcta |
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

Los secretos **no** están aquí: la cuenta vive cifrada con DPAPI en
`%LOCALAPPDATA%/Atalaya/auth.dat`, y nunca sale de tu máquina.

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
  usuario (el clon local existente se conserva).
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
