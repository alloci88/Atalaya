using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Storage;
using Atalaya.Storage.Sync;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Microsoft.Extensions.Logging;

namespace Atalaya.App.Services;

/// <summary>Where the hub's git credential comes from (D3).</summary>
public enum HubCredentialSource
{
    /// <summary>The token from the in-app GitHub login. The normal path.</summary>
    Account,

    /// <summary>The DPAPI-protected PAT: hidden fallback for orgs that block OAuth Apps.</summary>
    Pat,

    /// <summary>Neither: LibGit2Sharp falls back to the OS credential manager.</summary>
    OsCredentialManager,
}

/// <summary>
/// Lo que un «Sincronizar ahora» hizo de verdad (F5.1): qué se trajo y qué se publicó. El botón
/// hacía solo pull, así que los commits locales pendientes se quedaban sin salir hasta la
/// siguiente escritura del usuario; contar las dos direcciones es lo que hace que el panel de
/// Hub local pueda decirlo en vez de un «sincronizado» que no distingue ambos casos.
/// </summary>
/// <param name="PulledFiles">Ficheros que el pull trajo del remoto.</param>
/// <param name="Notifications">Avisos del pull (claims que perdiste, etc.).</param>
/// <param name="PendingCommits">Commits locales que estaban sin publicar antes del push.</param>
/// <param name="Pushed">
/// True si lo pendiente llegó al remoto. Cuando no había nada pendiente también es true: no
/// haber tenido que publicar nada no es un fallo.
/// </param>
public sealed record HubSyncReport(
    int PulledFiles,
    IReadOnlyList<string> Notifications,
    int PendingCommits,
    bool Pushed)
{
    public static HubSyncReport None { get; } = new(0, Array.Empty<string>(), 0, true);

    /// <summary>Una línea para el panel de Hub local: siempre dice las dos direcciones.</summary>
    public string Describe() =>
        (PulledFiles == 0 ? "No trajo cambios" : $"Trajo {PulledFiles} fichero(s)")
        + " · "
        + (PendingCommits == 0
            ? "nada pendiente de publicar"
            : Pushed
                ? $"publicó {PendingCommits} commit(s) local(es)"
                : $"⚠ no pudo publicar {PendingCommits} commit(s) local(es)");
}

/// <summary>
/// Owns the live hub: the <see cref="HubStore"/> and the <see cref="HubSyncService"/>.
/// <para>
/// Since F2 the hub URL comes from the deployment configuration (D1) — opaque to the user — and
/// the credentials come from the connected GitHub account (D3). The DPAPI-protected PAT survives
/// as a hidden fallback for teams whose organization blocks OAuth Apps; when an account token
/// exists, the PAT is ignored.
/// </para>
/// </summary>
public sealed class HubContext
{
    private readonly SettingsService _settings;
    private readonly GitHubAccountService _account;
    private readonly DeployConfig _deploy;
    private readonly ILoggerFactory _loggerFactory;
    private string? _builtWithCredential;

    public HubContext(
        AppPaths paths,
        SettingsService settings,
        GitHubAccountService account,
        DeployConfig deploy,
        ILoggerFactory loggerFactory)
    {
        _settings = settings;
        _account = account;
        _deploy = deploy;
        _loggerFactory = loggerFactory;
        HubPaths = new HubPaths(paths.Hub);
        Store = new HubStore(HubPaths, loggerFactory.CreateLogger<HubStore>());
    }

    public HubPaths HubPaths { get; }

    public HubStore Store { get; }

    /// <summary>
    /// El nombre de la organización del hub, o null si el hub todavía no lo dice (F6.4). Vive
    /// aquí y no en cada quien lo necesita —la firma de los informes, el «Acerca de»— porque un
    /// dato leído de dos sitios distintos acaba diciendo dos cosas distintas. Nunca lanza: sin
    /// clon no hay organización, y eso no es un error.
    /// </summary>
    public string? OrganizationName
    {
        get
        {
            try
            {
                string? name = Store.TryReadHub()?.OrganizationName;
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public HubSyncService? Sync { get; private set; }

    /// <summary>
    /// The hub repository actually used: the developer override from advanced settings first
    /// (dev only), then the deployment configuration, then the legacy per-user setting kept for
    /// users configured before F2.
    /// </summary>
    public string? HubUrl
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_settings.Current.HubUrlOverride))
            {
                return _settings.Current.HubUrlOverride!.Trim();
            }

            return _deploy.HasHubUrl ? _deploy.HubUrl.Trim()
                : string.IsNullOrWhiteSpace(_settings.Current.HubRepoUrl) ? null
                : _settings.Current.HubRepoUrl!.Trim();
        }
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(HubUrl);

    /// <summary>True when the local clone already exists (no network needed to read the hub).</summary>
    public bool IsCloned => Repository.IsValid(HubPaths.Root);

    public SyncHealth Health => Sync?.Health ?? SyncHealth.Amber;

    /// <summary>Why the last sync failed, or null when it succeeded. Surfaced in the Cuenta page.</summary>
    public string? LastSyncError => Sync?.LastError;

    /// <summary>
    /// The host whose certificate revocation status could not be checked, or null. Non-null means
    /// the connection worked but this network blocks the CRL/OCSP responders — worth showing.
    /// </summary>
    public string? RevocationUncheckedHost => Sync?.CertificatePolicy.RevocationUncheckedHost;

    /// <summary>Non-null when the deployment moved the hub and we re-pointed the existing clone.</summary>
    public string? RemoteRepointedTo => Sync?.RemoteRepointedTo;

    /// <summary>
    /// Candados huérfanos que se encontraron y quitaron al abrir el clon (F31 §2), y los que
    /// alguien tenía abiertos y por eso no se tocaron. Vacíos es lo normal.
    /// </summary>
    public IReadOnlyList<string> ClearedStaleLocks => Sync?.ClearedStaleLocks ?? Array.Empty<string>();

    /// <inheritdoc cref="ClearedStaleLocks"/>
    public IReadOnlyList<string> LocksInUse => Sync?.LocksInUse ?? Array.Empty<string>();

    /// <summary>When the hub was last successfully pulled, for the Cuenta page.</summary>
    public DateTimeOffset? LastSync { get; private set; }

    /// <summary>Raised (on a background thread) when a pull brought in changes.</summary>
    public event Action<PullResult>? Changed;

    /// <summary>
    /// Raised (possibly on a background thread) whenever <see cref="Health"/>, <see cref="LastSync"/>
    /// or the credential changed, so the shell's indicator updates without waiting for a poll tick.
    /// </summary>
    public event Action? SyncStateChanged;

    /// <summary>
    /// Clones/opens the hub, does a full pull, and initializes an empty hub on first use. Safe to
    /// call repeatedly. Never throws for a merely failed pull (offline is a normal state, §3):
    /// inspect <see cref="Health"/> and <see cref="LastSyncError"/> for that.
    /// </summary>
    public void EnsureHub() => EnsureHubCore();

    /// <summary>
    /// «Sincronizar ahora» de verdad (F5.1): pull con rebase y DESPUÉS push de todo lo local que
    /// siga sin publicarse, devolviendo qué pasó en cada dirección.
    /// <para>
    /// Es también lo que dispara una reconexión de cuenta: <see cref="EnsureSync"/> reconstruye el
    /// servicio con la credencial nueva, el pull vuelve a poner el piloto en verde y el push saca
    /// lo que se hubiera quedado atrás mientras no había cuenta — sin reiniciar la app.
    /// </para>
    /// </summary>
    public HubSyncReport SyncNow()
    {
        if (!IsConfigured)
        {
            return HubSyncReport.None;
        }

        PullResult pulled = EnsureHubCore();
        if (Sync is null)
        {
            return HubSyncReport.None;
        }

        // El empuje de lo pendiente ya lo hizo `EnsureHubCore`, que es por donde pasan también el
        // arranque y el fin de sesión: una sola regla y un solo sitio.
        (int pending, bool pushed) = _lastPublish;
        SyncStateChanged?.Invoke();
        return new HubSyncReport(pulled.Changes.Count, pulled.Notifications, pending, pushed);
    }

    /// <summary>Cuerpo compartido por <see cref="EnsureHub"/> y <see cref="SyncNow"/>.</summary>
    private PullResult EnsureHubCore()
    {
        if (!IsConfigured)
        {
            return PullResult.Empty;
        }

        EnsureSync();
        Sync!.EnsureCloned(HubUrl!);
        PullResult pulled = Pull();
        PublishAfterMigration();
        InitializeIfEmpty();
        SeedModelRates();
        MigrateSilencesToUlidKeys();
        MigrateRuleExclusionsToPatterns();

        // LO QUE NO SE PUDO PUBLICAR NO SE QUEDA ESPERANDO AL USUARIO (F31 §2). Antes solo
        // «Sincronizar ahora» empujaba lo pendiente, así que un commit que no logró salir —el hub
        // sin responder, un push que se pisó con otro— se quedaba en el clon hasta que a alguien
        // se le ocurriera pulsar un botón. El arranque es una sincronización como las demás.
        _lastPublish = PublishPending();
        return pulled;
    }

    private (int Pending, bool Pushed) _lastPublish;

    /// <summary>
    /// Commits que este clon tiene sin publicar ahora mismo. Se cuenta contra lo último que se
    /// trajo, así que significa «pendientes de publicar» después de una sincronización.
    /// </summary>
    public int PendingCommits => Sync?.PendingCommits ?? 0;

    /// <summary>«2 commits pendientes de publicar», o vacío cuando no hay nada atrás.</summary>
    public string PendingLabel
    {
        get
        {
            int pending = PendingCommits;
            return pending switch
            {
                0 => string.Empty,
                1 => "1 commit pendiente de publicar",
                _ => $"{pending} commits pendientes de publicar",
            };
        }
    }

    /// <summary>Saca lo que quedó sin publicar. Devuelve cuántos había y si salieron.</summary>
    private (int Pending, bool Pushed) PublishPending()
    {
        // Se cuenta DESPUÉS del pull (y de las migraciones, que pueden commitear por su cuenta),
        // así que el número significa "pendiente de publicar", no "pendiente desde el fetch".
        int pending = Sync!.PendingCommits;
        return (pending, pending == 0 || Sync.Push());
    }

    /// <summary>Same as <see cref="SyncNow"/>, off the UI thread.</summary>
    public Task<HubSyncReport> SyncNowAsync() => Task.Run(SyncNow);

    /// <summary>
    /// La credencial ha cambiado (desconectar / conectar / cambiar de cuenta): reconstruye el
    /// servicio de sync con ella y avisa a los indicadores (F5.1).
    /// <para>
    /// Sin esto, desconectar dejaba el piloto mostrando el verde del servicio anterior —
    /// contaba lo que pasó con una credencial que ya no existe— hasta que algo volviera a
    /// sincronizar o se reiniciara la app.
    /// </para>
    /// </summary>
    public void RefreshCredentials()
    {
        if (IsConfigured)
        {
            EnsureSync();
        }

        SyncStateChanged?.Invoke();
    }

    /// <summary>
    /// F4: los silencios pasan de estar nombrados por fingerprint a estarlo por el ULID del
    /// hallazgo que silencian. Idempotente y barata (los silencios son pocos), así que corre en
    /// cada apertura del hub; en cuanto no queda ninguno con el nombre viejo no hace nada.
    /// Se commitea solo si movió algo — nunca genera un commit vacío.
    /// </summary>
    private void MigrateSilencesToUlidKeys()
    {
        if (Sync is null || Health != SyncHealth.Green)
        {
            return;
        }

        int moved = 0;
        foreach (string slug in Store.ListAppSlugs())
        {
            SilenceMigration.Result result = SilenceMigration.MigrateApp(HubPaths, slug);
            moved += result.Migrated.Count;
            foreach (string skipped in result.Skipped)
            {
                _loggerFactory.CreateLogger<HubContext>()
                    .LogWarning("Silencio no migrable en {Slug}: {Detail}", slug, skipped);
            }
        }

        if (moved > 0)
        {
            Sync.CommitAndPush($"silences: migración F4 a clave por ULID ({moved})");
            SyncStateChanged?.Invoke();
        }
    }

    /// <summary>
    /// F5.12: las exclusiones por regla pasan a ser patrones silenciados. La exclusión por regla se
    /// retiró entera —convertía el silenciado en mantenimiento de taxonomía— pero lo que alguien
    /// dejara escrito no se tira: se convierte, con la descripción de la regla como ejemplar.
    /// Idempotente y barata (son pocas y el directorio desaparece al migrarlas), así que corre en
    /// cada apertura del hub; en cuanto no queda ninguna no hace nada. Se commitea solo si movió
    /// algo — nunca genera un commit vacío.
    /// </summary>
    private void MigrateRuleExclusionsToPatterns()
    {
        if (Sync is null || Health != SyncHealth.Green)
        {
            return;
        }

        // La fábrica se crea aquí y no se inyecta: es una migración one-shot que solo necesita
        // identidades nuevas, y añadirle una dependencia al constructor del hub por esto sería
        // pagar para siempre por algo que deja de hacer nada en cuanto corre una vez.
        var ulids = new UlidFactory(SystemClock.Instance);
        int moved = 0;
        foreach (string slug in Store.ListAppSlugs())
        {
            RuleExclusionMigration.Result result = RuleExclusionMigration.MigrateApp(
                HubPaths, slug, ulids, DateTimeOffset.UtcNow, DescribeRule);
            moved += result.Migrated.Count;
            foreach (string skipped in result.Skipped)
            {
                _loggerFactory.CreateLogger<HubContext>()
                    .LogWarning("Exclusión de regla no migrable en {Slug}: {Detail}", slug, skipped);
            }
        }

        if (moved > 0)
        {
            Sync.CommitAndPush($"pattern-silences: migración F5.12 de exclusiones por regla ({moved})");
            SyncStateChanged?.Invoke();
        }
    }

    /// <summary>
    /// La frase que describe una regla del catálogo, para usarla de ejemplar al migrar. Es lo único
    /// que el catálogo aporta a la migración; después de ella deja de tener papel de gobernanza.
    /// </summary>
    private static string? DescribeRule(string ruleId)
    {
        RuleDef? rule = RuleCatalog.Find(ruleId);
        return rule is null ? null : $"{rule.Title}: {rule.Look}";
    }

    /// <summary>
    /// After the deployment moved the hub and we re-pointed the clone, push the local history to
    /// the new remote. Without this the destination stays empty: <see cref="InitializeIfEmpty"/>
    /// does nothing (the local <c>hub.json</c> already exists) and no other write is pending, so
    /// the migration would silently publish nothing.
    /// </summary>
    private void PublishAfterMigration()
    {
        if (Sync?.RemoteRepointedTo is null || Health != SyncHealth.Green)
        {
            return;
        }

        Sync.Push();
        SyncStateChanged?.Invoke();
    }

    /// <summary>
    /// Writes <c>hub.json</c> and pushes it the first time we meet a brand-new, empty hub — what
    /// the old "Conectar / crear hub" button in Ajustes used to do before the connection moved to
    /// the Cuenta page. Only runs when the sync is healthy, so we never push over a broken pull.
    /// </summary>
    private void InitializeIfEmpty()
    {
        if (Sync is null || Health != SyncHealth.Green || Store.TryReadHub() is not null)
        {
            return;
        }

        string organization = _deploy.ChecksOrgMembership
            ? _deploy.OrganizationLogin
            : _account.Current?.DisplayName ?? ResolveIdentity().Name;

        Store.WriteHub(new HubInfo { OrganizationName = organization });
        Sync.CommitAndPush("hub: init");
        SyncStateChanged?.Invoke();
    }

    /// <summary>
    /// <b>Las tarifas se aplican solas</b> (R2 §2): al abrir el hub, lo que le falte de la siembra
    /// se escribe y se publica, sin preguntar.
    /// <para>
    /// <b>Por qué aquí.</b> Un precio publicado es un DATO, no una decisión del usuario. Hasta R2 la
    /// siembra colgaba de <c>ModelRatesViewModel</c>, así que el fichero no existía hasta que alguien
    /// abría Métricas → Tarifas · Gestionar: una instalación limpia auditaba y sus sesiones salían
    /// con «tarifa no configurada» hasta que a alguien se le ocurría visitar esa pantalla. Puesto en
    /// la apertura del hub corre en el arranque <b>y</b> en cuanto el hub se clona al conectar la
    /// cuenta, que es el otro momento en que una instalación limpia estrena tabla.
    /// </para>
    /// <para>
    /// <b>Quién hace el trabajo.</b> <see cref="ModelRatesService.SeedMissing"/>, y no este fichero:
    /// la contrapartida de D-790 es que nadie más que el servicio de tarifas puede nombrar
    /// <c>ModelRateSeed</c>, y esa puerta no se abre por comodidad. El servicio se construye aquí
    /// mismo —igual que la fábrica de ULIDs de la migración de F5.12— porque depende de este hub y
    /// pedirlo por el constructor sería un ciclo.
    /// </para>
    /// <para>
    /// El fichero sigue en la RAÍZ DEL HUB y no se muda a la configuración local: un precio es del
    /// contrato de la organización con su proveedor, no de la aplicación ni del puesto. Lo que R2
    /// cambia es dónde se EDITA (Ajustes), no dónde se guarda.
    /// </para>
    /// </summary>
    public void SeedModelRates()
    {
        if (!Directory.Exists(HubPaths.Root))
        {
            // Sin clon no hay dónde sembrar, y crear el árbol por nuestra cuenta sería inventarse un
            // hub. Se vuelve a intentar en cuanto el clon exista.
            return;
        }

        if (new ModelRatesService(this).SeedMissing() > 0)
        {
            SyncStateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Builds the sync service, rebuilding it when the credential changed (connect / disconnect /
    /// switch account) so a new token takes effect without restarting the app.
    /// </summary>
    public void EnsureSync()
    {
        string credentialKey = CredentialKey();
        if (Sync is not null && _builtWithCredential == credentialKey)
        {
            return;
        }

        Sync?.Dispose();
        Sync = new HubSyncService(
            HubPaths, ResolveIdentity(), BuildCredentials(),
            _loggerFactory.CreateLogger<HubSyncService>(),
            new HubCertificatePolicy(
                _settings.Current.RequireTlsRevocationCheck,
                _loggerFactory.CreateLogger<HubCertificatePolicy>()));
        Sync.Pulled += r => Changed?.Invoke(r);
        _builtWithCredential = credentialKey;
    }

    /// <summary>
    /// Suelta el clon: libera los handles de git y olvida el servicio de sync (F5.7 §5).
    /// <para>
    /// Existe por una razón concreta: en Windows no se puede borrar el directorio del clon
    /// mientras LibGit2Sharp lo tiene abierto. El reset de fábrica necesita borrarlo, así que
    /// necesita poder cerrarlo antes. La siguiente llamada a <see cref="EnsureSync"/> lo
    /// reconstruye desde cero, que es exactamente el estado de primer arranque.
    /// </para>
    /// </summary>
    public void CloseSync()
    {
        Sync?.Dispose();
        Sync = null;
        _builtWithCredential = null;
        LastSync = null;
    }

    public Task<PullResult> PullAsync() => Task.Run(Pull);

    private PullResult Pull()
    {
        if (Sync is null)
        {
            return PullResult.Empty;
        }

        try
        {
            PullResult result = Sync.Pull();
            if (Sync.Health == SyncHealth.Green)
            {
                // A successful fetch IS the sync, even when it brought nothing in: this is what
                // makes the first connection show a real timestamp instead of "nunca".
                LastSync = DateTimeOffset.Now;
                _account.ClearNeedsReconnect();
            }
            else if (Sync.LastError is { } error)
            {
                _account.NoteFailure(new InvalidOperationException(error));
            }

            return result;
        }
        catch (Exception ex)
        {
            _account.NoteFailure(ex);
            throw;
        }
        finally
        {
            SyncStateChanged?.Invoke();
        }
    }

    /// <summary>
    /// La cuenta conectada, o <c>null</c>. Se expone para lo que necesita la identidad de D-037
    /// <b>entera</b> —el correo público y el <c>noreply</c>, no solo el nombre—, que es lo que
    /// decide qué autor se puede publicar en el hub (ver <see cref="HubCommitIdentity"/>).
    /// <see cref="ResolveIdentity"/> no sirve para eso: cae al config global de git, que es
    /// justamente de donde sale el correo que no se quiere publicar.
    /// </summary>
    public GitHubAccount? Account => _account.Current;

    /// <summary>
    /// Git identity for hub commits: the connected account's profile (D2.2), else the explicit
    /// setting kept from before F2, else the global git config, else a placeholder.
    /// </summary>
    public (string Name, string Email) ResolveIdentity()
    {
        if (_account.GitIdentity is { } fromAccount)
        {
            return fromAccount;
        }

        string? name = _settings.Current.GitUserName;
        string? email = _settings.Current.GitUserEmail;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
        {
            try
            {
                using Configuration cfg = Configuration.BuildFrom(null);
                name ??= cfg.Get<string>("user.name")?.Value;
                email ??= cfg.Get<string>("user.email")?.Value;
            }
            catch
            {
                // no global config available
            }
        }

        return (name ?? Environment.UserName, email ?? $"{Environment.UserName}@localhost");
    }

    /// <summary>
    /// The credential handed to LibGit2Sharp. GitHub over HTTPS accepts the user token as the
    /// password with the fixed username <c>x-access-token</c> — the same shape the PAT already
    /// used, so both paths share one code path.
    /// <para>
    /// Público desde F5.8: clonar el repo de una app auditada (§2, «Clonarlo ahora») necesita
    /// exactamente esta credencial y no otra. Duplicar la cadena de resolución del token en el
    /// servicio de vinculación habría creado un segundo sitio donde acordarse de que la cuenta
    /// gana al PAT.
    /// </para>
    /// </summary>
    public CredentialsHandler? BuildCredentials()
    {
        string? token = ResolveCredentialToken();
        if (string.IsNullOrEmpty(token))
        {
            return null; // fall back to the OS credential manager
        }

        return (_, _, _) => new UsernamePasswordCredentials
        {
            Username = "x-access-token",
            Password = token,
        };
    }

    /// <summary>Account token wins; the PAT is the hidden fallback (D3).</summary>
    private string? ResolveCredentialToken() => _account.Token ?? _settings.GetPat();

    /// <summary>Which credential the hub is using right now. Shown in Ajustes → avanzadas.</summary>
    public HubCredentialSource CredentialSource =>
        _account.Token is not null ? HubCredentialSource.Account
        : _settings.GetPat() is not null ? HubCredentialSource.Pat
        : HubCredentialSource.OsCredentialManager;

    /// <summary>
    /// Identifies the credential + identity the current <see cref="Sync"/> was built with, so we
    /// know when to rebuild it. Never contains the token itself.
    /// </summary>
    private string CredentialKey()
    {
        (string name, string email) = ResolveIdentity();
        string source = _account.Token is not null ? $"account:{_account.Current?.Login}"
            : _settings.GetPat() is not null ? "pat"
            : "none";
        return $"{source}|{name}|{email}";
    }
}
