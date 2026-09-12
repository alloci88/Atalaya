using Atalaya.Storage.Sync;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atalaya.App.Services;

/// <summary>State of one step of the chained verification (D2.3).</summary>
public enum CheckState
{
    Pending,
    Running,
    Ok,
    Failed,

    /// <summary>Not applicable in this deployment (e.g. no organization configured).</summary>
    Skipped,

    /// <summary>
    /// Una capacidad OPCIONAL que no está activada (F14, adenda). No es un fallo y no se pinta como
    /// tal: la fila informa de que existe un extra y de cómo activarlo, y no reclama nada.
    /// <para>
    /// Tiene estado propio y no reutiliza <see cref="Skipped"/> porque <c>Skipped</c> se esconde
    /// —no aplica en este despliegue— y esto sí se enseña: la gracia es que quien quiera el extra
    /// sepa que está ahí. Y no es <see cref="Failed"/> porque nadie ha pedido nunca que estuviera.
    /// </para>
    /// </summary>
    Optional,
}

/// <summary>
/// One live row of the chained verification. Bound directly by the Cuenta page, so it updates
/// with a check mark as each step resolves.
/// </summary>
public sealed partial class ConnectionStep : ObservableObject
{
    public ConnectionStep(string key, string title)
    {
        Key = key;
        _title = title;
    }

    public string Key { get; }

    [ObservableProperty] private string _title;
    [ObservableProperty] private CheckState _state = CheckState.Pending;
    [ObservableProperty] private string _detail = string.Empty;
    [ObservableProperty] private string? _helpUrl;

    /// <summary>
    /// Lo que hay detrás de la línea y no cabe en ella (R6 §2). Hoy solo lo usa el hub, para
    /// llevar la ruta del clon: quien la necesita la encuentra, y quien no, no la ve. Vacío en
    /// las demás, y entonces la fila no lleva tooltip.
    /// </summary>
    [ObservableProperty] private string _tooltip = string.Empty;

    public bool IsVisible => State != CheckState.Skipped;

    partial void OnStateChanged(CheckState value) => OnPropertyChanged(nameof(IsVisible));

    internal void Reset(string title)
    {
        Title = title;
        State = CheckState.Pending;
        Detail = string.Empty;
        HelpUrl = null;
        Tooltip = string.Empty;
    }

    internal void Skip()
    {
        State = CheckState.Skipped;
        Detail = string.Empty;
        HelpUrl = null;
    }

    internal void Succeed(string title, string detail = "")
    {
        Title = title;
        Detail = detail;
        HelpUrl = null;
        State = CheckState.Ok;
    }

    /// <summary>
    /// Cuenta algo que se puede tener y no se tiene. Ni verde ni rojo: gris. Es la diferencia entre
    /// «te falta algo» y «hay esto disponible si lo quieres» (F14, adenda).
    /// </summary>
    internal void Inform(string title, string detail)
    {
        Title = title;
        Detail = detail;
        HelpUrl = null;
        State = CheckState.Optional;
    }

    internal void Fail(string detail, string? helpUrl = null)
    {
        Detail = detail;
        HelpUrl = helpUrl;
        State = CheckState.Failed;
    }
}

/// <summary>Outcome of a full run of the chain.</summary>
public sealed record ConnectionCheckResult(bool AllOk, string? FirstProblem);

/// <summary>
/// Runs the four chained checks of the Cuenta page (D2.3), each reporting live and each failing
/// with an actionable diagnosis instead of a raw error:
/// <list type="number">
/// <item>authenticated as {login},</item>
/// <item>member of {org} — only when the deployment configures one,</item>
/// <item>access to the hub (clone it here on first run, else fetch),</item>
/// <item>Copilot available (SDK client with the account token, no session created).</item>
/// </list>
/// </summary>
public sealed class ConnectionChecker
{
    private readonly GitHubAccountService _account;
    private readonly GitHubApiClient _api;
    private readonly DeployConfig _deploy;
    private readonly HubContext _hub;
    private readonly AuditorProviderRegistry _providers;

    /// <summary>
    /// <b>Un solo constructor, a propósito</b> (BUGFIX-ARRANQUE). Hubo un segundo por comodidad
    /// —el que tomaba un <c>IAuditorProvider</c> suelto— y con él la aplicación dejó de arrancar:
    /// desde que el contenedor sabe resolver también <c>IAuditorProvider</c>, los dos eran
    /// satisfacibles y <c>ActivatorUtilities</c> no elige entre iguales, lanza
    /// «The following constructors are ambiguous». El compilador sí sabía desempatarlos, así que
    /// ningún test lo vio: la ambigüedad solo existe para quien resuelve por reflexión.
    /// Quien quiera uno solo, que use <see cref="AuditorProviderRegistry.Of"/>.
    /// </summary>
    public ConnectionChecker(
        GitHubAccountService account,
        GitHubApiClient api,
        DeployConfig deploy,
        HubContext hub,
        AuditorProviderRegistry providers)
    {
        _account = account;
        _api = api;
        _deploy = deploy;
        _hub = hub;
        _providers = providers;

        // F14 — GitHub NO se sustituye nunca, y el orden de las filas lo dice: identidad, ORG y
        // hub van primero y son de GitHub, se audite con quien se audite. Solo DESPUÉS viene una
        // fila por proveedor de auditoría. Quien mire esta pantalla tiene que poder ver que elegir
        // Claude Code no le quita a Atalaya la necesidad de una cuenta de GitHub: sin ella no hay
        // ni autoría, ni hub, ni sitio donde escribir los hallazgos.
        Steps = new ConnectionStep[]
        {
            new("auth", "Autenticado"),
            new("org", "Miembro de la organización"),
            new("hub", "Acceso al hub"),
        }
        .Concat(providers.All.Select(p => new ConnectionStep(ProviderStepKey(p.ProviderId), $"{p.ProviderName} disponible")))
        .ToArray();

        // Los dos grupos salen de la MISMA lista, rebanada por la clave. Construir dos listas
        // aparte daría dos juegos de filas y solo uno se enteraría de la comprobación.
        GitHubSteps = Steps.Where(s => !s.Key.StartsWith("provider:", StringComparison.Ordinal)).ToArray();
        AuditorSteps = Steps.Where(s => s.Key.StartsWith("provider:", StringComparison.Ordinal)).ToArray();
    }

    /// <summary>La clave de la fila de un proveedor. En un sitio, para que las dos mitades casen.</summary>
    internal static string ProviderStepKey(string providerId) => $"provider:{providerId}";

    /// <summary>The four rows, created once and mutated in place (safe to bind).</summary>
    public IReadOnlyList<ConnectionStep> Steps { get; }

    /// <summary>
    /// <b>Las de GitHub</b> (R8): identidad, organización y hub. Son las tres que no se sustituyen
    /// —sin ellas no hay autoría ni sitio donde escribir— y por eso van en su propio grupo, con su
    /// subtítulo. Son las MISMAS instancias que <see cref="Steps"/>, no copias: la comprobación las
    /// muta en el sitio y los dos grupos se enteran.
    /// </summary>
    public IReadOnlyList<ConnectionStep> GitHubSteps { get; }

    /// <summary>
    /// <b>Los auditores</b> (R8). Basta con uno, y cuál se usa se elige en Ajustes; el subtítulo
    /// del grupo lo dice, que es donde antes había una frase de ayuda suelta que nadie relacionaba
    /// con las filas de abajo.
    /// </summary>
    public IReadOnlyList<ConnectionStep> AuditorSteps { get; }

    private ConnectionStep Step(string key) => Steps.First(s => s.Key == key);

    public async Task<ConnectionCheckResult> RunAsync(CancellationToken ct)
    {
        foreach (ConnectionStep step in Steps)
        {
            step.Reset(step.Key switch
            {
                "auth" => "Autenticado",
                "org" => $"Miembro de {(_deploy.ChecksOrgMembership ? _deploy.OrganizationLogin : "la organización")}",
                "hub" => "Acceso al hub",
                _ => $"{_providers.NameOf(step.Key["provider:".Length..])} disponible",
            });
        }

        // F14 (adenda) — los extras ausentes se declaran informativos ANTES de nada, y no vuelven a
        // tocarse. No dependen de GitHub: Claude Code no necesita una cuenta de GitHub para estar o
        // no estar instalado. Ponerlo aquí evita además que un fallo de autenticación los marque
        // como «no aplicable», que es otra forma de contar algo que no es.
        foreach (IAuditorProvider optional in _providers.All.Where(p => p.IsOptional && !p.IsPresent))
        {
            Step(ProviderStepKey(optional.ProviderId)).Inform(
                $"{optional.ProviderName} · opcional",
                "No está instalado en esta máquina, y no hace falta: auditas con "
                + $"{_providers.Fallback.ProviderName}. Si algún día quieres una segunda bolsa de "
                + "cuota y un auditor de otra casa, instálalo e inicia sesión en tu terminal.");
        }

        string? firstProblem = null;

        // 1 — Authenticated. Re-reads the profile so a renamed account or a new avatar refreshes,
        //     and so a revoked token is caught here rather than deep inside a clone.
        ConnectionStep auth = Step("auth");
        auth.State = CheckState.Running;
        string? token = _account.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            auth.Fail("No hay ninguna cuenta conectada. Pulsa «Conectar con GitHub».");
            SkipRestAfterAuth();
            return new ConnectionCheckResult(false, auth.Detail);
        }

        try
        {
            GitHubUser user = await _api.GetCurrentUserAsync(token, ct);
            _account.RefreshProfile(user);
            auth.Succeed($"Autenticado como {user.Login}", user.DisplayName);
        }
        catch (GitHubApiException ex)
        {
            _account.NoteFailure(ex);
            auth.Fail(ex.Message, HelpFor(ex.Problem));
            SkipRestAfterAuth();
            return new ConnectionCheckResult(false, ex.Message);
        }

        // 2 — Organization membership. Only when the deployment names one: while the hub is still
        //     a personal repo, the clone itself is the real gate.
        ConnectionStep org = Step("org");
        if (!_deploy.ChecksOrgMembership)
        {
            org.Skip();
        }
        else
        {
            org.State = CheckState.Running;
            string login = _account.Current!.Login;
            try
            {
                bool member = await _api.IsOrganizationMemberAsync(token, _deploy.OrganizationLogin, ct);
                if (member)
                {
                    org.Succeed($"Miembro de {_deploy.OrganizationLogin}");
                }
                else
                {
                    org.Fail(ConnectionHelp.NotAnOrgMember(login, _deploy.OrganizationLogin),
                        ConnectionHelp.DocsOAuthPolicy);
                    firstProblem ??= org.Detail;
                }
            }
            catch (GitHubApiException ex)
            {
                _account.NoteFailure(ex);
                org.Fail(ex.Message, HelpFor(ex.Problem));
                firstProblem ??= ex.Message;
            }
        }

        // 3 — Hub access: a real clone/fetch with the token. This is the check that matters.
        ConnectionStep hub = Step("hub");
        hub.State = CheckState.Running;
        if (!_hub.IsConfigured)
        {
            hub.Fail(ConnectionHelp.NoHubUrl);
            firstProblem ??= hub.Detail;
        }
        else
        {
            try
            {
                // A full clone + pull with the account token. EnsureHub swallows a failed pull
                // (offline is a normal state), so the health is what tells us whether the sync
                // actually happened — otherwise this step would go green over a broken hub.
                await Task.Run(_hub.EnsureHub, ct);

                if (_hub.Health == SyncHealth.Green && _hub.LastSync is { } t)
                {
                    // LA RUTA DEL CLON VA EN EL TOOLTIP (R6 §2). No en la línea: ésta contesta
                    // «¿llego al hub?» y la ruta no es parte de esa respuesta. Y ya no vive «dos
                    // tarjetas más abajo», porque aquella tarjeta —«Hub local»— decía lo mismo que
                    // esta línea y se ha retirado: el estado y la fecha estaban dos veces en la
                    // misma pantalla.
                    hub.Succeed("Acceso al hub", $"Sincronizado {t.ToLocalTime():g}");
                    hub.Tooltip = $"Clon del hub en esta máquina: {_hub.HubPaths.Root}";
                }
                else
                {
                    (string detail, string? help) = _hub.LastSyncError is { } error
                        ? DescribeHubFailure(new InvalidOperationException(error), _account.Current?.Login ?? "?")
                        : ("El clon existe pero no se ha podido sincronizar con el hub.", null);

                    // git cannot tell "does not exist" from "no read access" from "no write
                    // access" — they are all 404. The API can, so ask it instead of guessing.
                    (detail, help) = await RefineWithApiAsync(detail, help, token, ct);
                    hub.Fail(detail, help);
                    firstProblem ??= detail;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _account.NoteFailure(ex);
                (string detail, string? help) = DescribeHubFailure(ex, _account.Current?.Login ?? "?");
                (detail, help) = await RefineWithApiAsync(detail, help, token, ct);
                hub.Fail(detail, help);
                firstProblem ??= detail;
            }
        }

        // 4 — Un proveedor de auditoría por fila (F14). Cada uno con SU comprobación barata: el de
        //     Copilot pregunta el estado de autenticación al SDK sin crear sesión (F2.1); el de
        //     Claude Code mira si el CLI está y si tiene login. Ninguna gasta cuota.
        //
        //     Se comprueban TODOS, no solo el elegido: la pantalla Cuenta existe para poder
        //     decidir, y para eso hay que ver el estado de los dos. Que a uno le falte algo NO
        //     invalida la conexión —se puede auditar con el otro—, así que un proveedor caído no
        //     tumba el resultado global mientras quede alguno en pie.
        var providerSteps = new List<(ConnectionStep Step, bool Ready)>();

        foreach (IAuditorProvider provider in _providers.All)
        {
            ConnectionStep step = Step(ProviderStepKey(provider.ProviderId));

            // Ya declarado como extra ausente al empezar: no se le pregunta. Lanzar su proceso
            // para confirmar lo que ya sabemos sería gasto por nada, y en una pantalla que se abre
            // a menudo. Copilot nunca entra por aquí: no es opcional, es el requisito.
            if (step.State == CheckState.Optional)
            {
                providerSteps.Add((step, false));
                continue;
            }

            step.State = CheckState.Running;
            try
            {
                AgentReadiness readiness = await provider.CheckAsync(ct);
                if (readiness.Ready)
                {
                    step.Succeed($"{provider.ProviderName} disponible", readiness.Message);
                }
                else
                {
                    step.Fail(readiness.Message,
                        readiness.Problem == AgentProblem.NoSeat ? ConnectionHelp.DocsCopilotSeat : null);
                }

                providerSteps.Add((step, readiness.Ready));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                step.Fail($"No se pudo comprobar {provider.ProviderName}: {ex.Message}");
                providerSteps.Add((step, false));
            }
        }

        // El problema que se destaca arriba es el del proveedor ELEGIDO: es con el que se va a
        // auditar, y por tanto el único cuyo fallo impide trabajar ahora mismo. Que el otro esté
        // sin instalar es información, no una avería.
        string chosenKey = ProviderStepKey(_providers.Current.ProviderId);
        if (providerSteps.FirstOrDefault(p => p.Step.Key == chosenKey) is { Ready: false, Step: { } chosen })
        {
            firstProblem ??= chosen.Detail;
        }

        bool nonProviderOk = Steps
            .Where(s => !s.Key.StartsWith("provider:", StringComparison.Ordinal))
            .All(s => s.State is CheckState.Ok or CheckState.Skipped);

        return new ConnectionCheckResult(
            nonProviderOk && providerSteps.Any(p => p.Ready), firstProblem);
    }

    /// <summary>
    /// Replaces a guessed git diagnosis with what the API actually says about the hub repository.
    /// Leaves the original text when the API cannot help (offline, non-GitHub URL) so we never
    /// downgrade a specific message into a vaguer one.
    /// </summary>
    private async Task<(string Detail, string? HelpUrl)> RefineWithApiAsync(
        string detail, string? helpUrl, string token, CancellationToken ct)
    {
        if (GitHubApiClient.ParseRepositoryUrl(_hub.HubUrl) is not var (owner, repo))
        {
            return (detail, helpUrl);
        }

        string login = _account.Current?.Login ?? "?";
        RepositoryAccess access = await _api.GetRepositoryAccessAsync(token, owner, repo, ct);

        return access switch
        {
            RepositoryAccess.ReadOnly => (ConnectionHelp.HubReadOnly(login, _hub.HubUrl!), null),
            RepositoryAccess.NotVisible => (ConnectionHelp.HubNotVisible(login, _hub.HubUrl!), null),
            RepositoryAccess.OrgPolicyBlocked => (ConnectionHelp.OrgPolicyBlocked, ConnectionHelp.DocsOAuthPolicy),
            RepositoryAccess.SamlRequired => (ConnectionHelp.SamlRequired, ConnectionHelp.DocsSamlSso),
            RepositoryAccess.TokenRejected => (ConnectionHelp.TokenRejected, null),

            // The API says we have read AND write, so the failure is not about repo permissions;
            // keep whatever git reported.
            _ => (detail, helpUrl),
        };
    }

    /// <summary>
    /// Sin identidad no se puede comprobar nada más: el resto de filas se marcan como no aplicables.
    /// <para>
    /// <b>Se calcula, no se enumera.</b> Antes eran tres claves escritas a mano —una de ellas, el
    /// identificador del proveedor de fábrica—, y al pasar a una fila por proveedor esa clave dejó
    /// de existir: la pantalla
    /// Cuenta reventaba con «Sequence contains no matching element» en el caso más común de todos,
    /// abrirla sin cuenta conectada. Una lista de claves paralela a las filas es una lista que se
    /// queda vieja; se recorre lo que hay.
    /// </para>
    /// </summary>
    private void SkipRestAfterAuth()
    {
        foreach (ConnectionStep step in Steps.Where(s => s.Key != "auth" && s.State != CheckState.Optional))
        {
            step.Skip();
        }
    }

    private static string? HelpFor(GitHubApiProblem problem) => problem switch
    {
        GitHubApiProblem.SamlRequired => ConnectionHelp.DocsSamlSso,
        GitHubApiProblem.OrgPolicyBlocked => ConnectionHelp.DocsOAuthPolicy,
        _ => null,
    };

    /// <summary>
    /// Turns a LibGit2Sharp/network failure into the org-policy vocabulary the user can act on:
    /// git only ever reports "authentication replays" or a 404 for a repo it may not see.
    /// </summary>
    internal static (string Detail, string? HelpUrl) DescribeHubFailure(Exception ex, string login)
    {
        string m = ex.Message?.ToLowerInvariant() ?? string.Empty;

        // TLS first: these arrive worded like transport failures but are neither network outages
        // nor credential problems, and telling the user "comprueba la red" sends them the wrong way.
        if (m.Contains("revocation"))
        {
            return (ConnectionHelp.TlsRevocationUnavailable, null);
        }

        if (m.Contains("user rejected certificate") || m.Contains("certificate is not trusted")
            || m.Contains("certificate root is not trusted") || m.Contains("certificate is not valid")
            || m.Contains("the certificate cannot be verified"))
        {
            return (ConnectionHelp.TlsUntrusted, null);
        }

        if (m.Contains("could not resolve host") || m.Contains("failed to send request")
            || m.Contains("timed out") || m.Contains("failed to connect"))
        {
            return (ConnectionHelp.Offline, null);
        }

        if (m.Contains("authentication replays") || m.Contains("401") || m.Contains("unauthorized")
            || m.Contains("403") || m.Contains("404") || m.Contains("not found")
            || m.Contains("too many redirects"))
        {
            return (ConnectionHelp.HubAccessDenied(login), ConnectionHelp.DocsOAuthPolicy);
        }

        return ($"No se pudo acceder al hub: {ex.Message}", null);
    }
}
