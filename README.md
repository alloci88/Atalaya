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
| `src/Atalaya.Copilot` | Integración `GitHub.Copilot.SDK` (adaptador real) + `ICopilotAgent` con fake inyectable; tools, permisos, coste, prompts/brief. | `net8.0` |
| `src/Atalaya.ImportV4` | Importador tolerante del formato markdown v4. | `net8.0` |
| `src/Atalaya.App` | WPF + MVVM (CommunityToolkit.Mvvm), Generic Host (DI), tema Fluent (WPF-UI), vistas V1–V6. | `net8.0-windows` |
| `tests/*` | xUnit + FluentAssertions, un proyecto por `src`. | |

**Principios**: el agente de IA nunca escribe estado — entrega hallazgos por una tool
tipada y la app valida y persiste (mejora 1). Los dashboards se calculan siempre; en
disco solo viven datos primarios (mejora 8). Identidad de hallazgo = ULID + fingerprint;
los `BUG-0042` son alias de presentación (mejora 2).

## Requisitos

- **Windows 10/11** y el **.NET 8 SDK** (para compilar) / runtime (para ejecutar).
- Una **cuenta con asiento de GitHub Copilot**. El SDK no ofrece login programático:
  la primera vez ejecuta `copilot` en una terminal y **autentícate una vez**; Atalaya
  reutiliza esa sesión (`UseLoggedInUser = true`). Si falla la autenticación, la app
  muestra una pantalla de ayuda con botón de reintento.
- Git instalado (Atalaya usa LibGit2Sharp; no hace shell-out a `git.exe`).

## Montar el repositorio audit-hub

1. Crea un **repositorio git vacío** (local `--bare`, o en GitHub/GitLab), p.ej.
   `git init --bare //servidor/atalaya-hub.git`.
2. Abre Atalaya → **Ajustes** → pega la **URL del hub**, tu **identidad git**
   (nombre/email; si faltan se toman de la config global) y, si el remoto lo requiere,
   un **PAT** (se guarda cifrado con DPAPI; Atalaya no tiene almacén propio de secretos).
3. Pulsa **Conectar / crear hub**: clona el hub y, si está vacío, escribe `hub.json`
   y hace el primer push.
4. **Nueva aplicación**: indica ruta del clon local + URL del repo; Atalaya detecta el
   stack, construye el inventario del primer ciclo y lo publica.

La ruta del clon local es **por máquina** (`%LOCALAPPDATA%/Atalaya/machines.json`),
nunca va al hub.

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
- `read_signatures` usa una heurística de líneas; la extracción con Roslyn para C#
  queda pendiente.
- Las gráficas de V6 se dibujan con WPF puro (barras) para evitar dependencias nativas
  no verificables en el entorno de build; `MetricsQuery` (el cálculo) es agnóstico a la
  librería de gráficas.
- El re-anclaje por snippet asume snippets de una línea (los multilínea pueden marcar
  `needsReview`).

Ver `DECISIONS.md` para todas las decisiones tomadas en zonas de libertad.
