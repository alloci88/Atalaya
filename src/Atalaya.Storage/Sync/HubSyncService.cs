using Atalaya.Domain.Model;
using Atalaya.Storage.Json;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atalaya.Storage.Sync;

/// <summary>
/// Owns all git operations against the audit-hub clone (§3): clone/open, commit, pull
/// (fetch + fast-forward/rebase) and push (fetch → rebase → push, retry 3× with backoff),
/// with the deterministic conflict policy of <see cref="HubMergePolicy"/>. Uses LibGit2Sharp
/// only — never shells out to git.exe.
/// </summary>
public sealed class HubSyncService : IDisposable
{
    private readonly HubPaths _paths;
    private readonly Identity _identity;
    private readonly CredentialsHandler? _credentials;
    private readonly ILogger<HubSyncService> _log;
    private Repository? _repo;

    public HubSyncService(
        HubPaths paths,
        (string Name, string Email) identity,
        CredentialsHandler? credentials = null,
        ILogger<HubSyncService>? log = null,
        HubCertificatePolicy? certificatePolicy = null)
    {
        _paths = paths;
        _identity = new Identity(identity.Name, identity.Email);
        _credentials = credentials;
        _log = log ?? NullLogger<HubSyncService>.Instance;
        CertificatePolicy = certificatePolicy ?? new HubCertificatePolicy(log: _log);
    }

    /// <summary>The TLS policy applied to every fetch/clone/push. Never null.</summary>
    public HubCertificatePolicy CertificatePolicy { get; }

    /// <summary>Current sync indicator (§3). Starts amber until the first successful pull.</summary>
    public SyncHealth Health { get; private set; } = SyncHealth.Amber;

    /// <summary>
    /// Why the last pull/push failed, or null after a successful one. Pull swallows git errors on
    /// purpose (offline is a normal state, §3 / D-007), so this is the only way a caller can tell
    /// "nothing to pull" from "the pull failed" without reading the log.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>Raised after a pull that changed files, so the UI can react live.</summary>
    public event Action<PullResult>? Pulled;

    /// <summary>
    /// <b>Cuántas veces se intenta publicar antes de rendirse</b> (BUGFIX-PUSH). Eran tres con
    /// esperas de 150, 300 y 450 ms — nueve décimas en total, que es poco para un remoto con otra
    /// persona empujando a la vez. Cinco, con la espera doblándose, dan cuatro segundos y medio de
    /// margen y siguen cabiendo de sobra en el tope de treinta.
    /// </summary>
    public const int PushAttempts = 5;

    /// <summary>
    /// Se va a reintentar la publicación, con el número del intento que empieza (2…5). Existe para
    /// que el hilo de la sesión pueda decirlo mientras pasa: un reintento callado es un silencio
    /// más, y el silencio es lo que se lee como un cuelgue.
    /// </summary>
    public event Action<int>? PushRetrying;

    private Repository Repo => _repo ??= new Repository(_paths.Root);

    private Signature Signature => new(_identity, DateTimeOffset.Now);

    /// <summary>
    /// The hub URL an existing clone was re-pointed at, or null. Non-null means the deployment
    /// moved the hub (e.g. to the organization) and we followed it — worth telling the user.
    /// </summary>
    public string? RemoteRepointedTo { get; private set; }

    /// <summary>
    /// Clones the hub if the working directory is not yet a repo; otherwise opens it — and, when
    /// the deployment now names a DIFFERENT hub, re-points <c>origin</c> at it.
    /// <para>
    /// Without that last step, migrating the hub (changing <c>hubUrl</c> in the deployment) would
    /// silently keep every existing user syncing against the old repository: the directory is a
    /// valid clone, so we would just open it. Re-pointing keeps the local history, which is what
    /// publishes it into an empty destination on the next push.
    /// </para>
    /// </summary>
    public void EnsureCloned(string repoUrl)
    {
        if (Repository.IsValid(_paths.Root))
        {
            _ = Repo;
            RepointOriginIfNeeded(repoUrl);
            return;
        }

        Directory.CreateDirectory(_paths.Root);
        var options = new CloneOptions();
        if (_credentials is not null)
        {
            options.FetchOptions.CredentialsProvider = _credentials;
        }

        options.FetchOptions.CertificateCheck = CertificatePolicy.Check;

        Repository.Clone(repoUrl, _paths.Root, options);
        _repo = new Repository(_paths.Root);
    }

    private void RepointOriginIfNeeded(string repoUrl)
    {
        Remote? origin = Repo.Network.Remotes["origin"];
        if (origin is null || SameRemote(origin.Url, repoUrl))
        {
            return;
        }

        _log.LogWarning(
            "Hub moved: re-pointing origin from {Old} to {New}. Local history is kept and will be "
            + "published to the new remote on the next push.",
            origin.Url, repoUrl);

        Repo.Network.Remotes.Update("origin", r => r.Url = repoUrl);
        RemoteRepointedTo = repoUrl;
    }

    /// <summary>
    /// «¿El mismo repositorio?». La regla vive en <see cref="RemoteUrl"/> desde F5.8: vincular un
    /// clon local hace exactamente esta pregunta, y no puede responderse con otra implementación.
    /// </summary>
    internal static bool SameRemote(string? a, string? b) => RemoteUrl.Same(a, b);

    /// <summary>Stages every change (including deletions) and commits if there is anything to commit.</summary>
    public bool Commit(string message)
    {
        Commands.Stage(Repo, "*");
        var status = Repo.RetrieveStatus();
        if (!status.IsDirty)
        {
            return false;
        }

        Repo.Commit(message, Signature, Signature);
        return true;
    }

    /// <summary>
    /// Fetches and integrates the remote: fast-forward when possible, otherwise rebase local
    /// commits onto the remote applying the conflict policy. Returns what changed.
    /// </summary>
    public PullResult Pull()
    {
        try
        {
            Fetch();
            Branch local = Repo.Head;
            Branch? remote = Repo.Branches[$"origin/{local.FriendlyName}"];
            if (remote?.Tip is null)
            {
                // Remote branch has no commits yet (a brand-new, empty hub): nothing to integrate,
                // but the fetch succeeded, so the sync is healthy.
                Succeeded();
                return PullResult.Empty;
            }

            Commit oldTip = local.Tip;
            var notifications = Integrate(local, remote);
            Commit newTip = Repo.Head.Tip;

            Succeeded();
            if (oldTip == newTip && notifications.Count == 0)
            {
                return PullResult.Empty;
            }

            var changes = DiffPaths(oldTip, newTip);
            var result = new PullResult(changes, notifications);
            if (result.HasChanges || notifications.Count > 0)
            {
                Pulled?.Invoke(result);
            }

            return result;
        }
        catch (LibGit2SharpException ex)
        {
            _log.LogWarning(ex, "Pull failed; staying on last pull (offline?)");
            Failed(ex, SyncHealth.Amber);
            return PullResult.Empty;
        }
    }

    private void Succeeded()
    {
        Health = SyncHealth.Green;
        LastError = null;
    }

    private void Failed(Exception ex, SyncHealth health)
    {
        Health = health;
        LastError = ex.Message;
    }

    /// <summary>
    /// Pushes local commits with the fetch → rebase → push loop (§3), retrying up to 3 times
    /// with backoff when the remote moves under us.
    /// </summary>
    public bool Push()
    {
        for (int attempt = 1; attempt <= PushAttempts; attempt++)
        {
            if (attempt > 1)
            {
                PushRetrying?.Invoke(attempt);
            }

            try
            {
                Pull(); // rebase onto latest remote first
                Branch local = Repo.Head;

                // EL RECHAZO DEL REMOTO HAY QUE PEDIRLO (BUGFIX-PUSH). `Network.Push` NO lanza
                // cuando el otro lado rechaza la referencia: el rechazo llega por
                // `OnPushStatusError`, y sin manejador se descarta en silencio. Sin esto, un push
                // rechazado por non-fast-forward —el caso NORMAL cuando otra persona publica a la
                // vez— volvía como éxito: `Succeeded()`, salud verde y `true`. El commit se quedaba
                // en el clon de quien perdió la carrera, creyendo que estaba publicado, y su
                // reclamación no llegaba al hub. Medido con dos clones empujando a la vez contra el
                // `--bare` de pruebas: las dos publicaciones decían «sí» y en el hub había una.
                var rejected = new List<string>();
                var pushOptions = new PushOptions
                {
                    CertificateCheck = CertificatePolicy.Check,
                    OnPushStatusError = e => rejected.Add($"{e.Reference}: {e.Message}"),
                };
                if (_credentials is not null)
                {
                    pushOptions.CredentialsProvider = _credentials;
                }

                Remote origin = Repo.Network.Remotes["origin"];
                Repo.Network.Push(origin, $"refs/heads/{local.FriendlyName}", pushOptions);

                if (rejected.Count > 0)
                {
                    // Se convierte en lo que es —un rechazo— para que lo recoja el mismo bucle que
                    // ya sabía qué hacer con él: esperar, rehacer el rebase sobre lo que acaba de
                    // llegar, y volver a intentarlo.
                    throw new NonFastForwardException(string.Join("; ", rejected));
                }

                Succeeded();
                return true;
            }
            catch (NonFastForwardException)
            {
                // El remoto se movió debajo: otra persona publicó mientras nosotros preparábamos.
                // Es el caso NORMAL de un hub compartido, no una avería — se rehace el rebase y se
                // vuelve a intentar.
                _log.LogInformation(
                    "Push rejected (remote moved); retry {Attempt}/{Of}", attempt, PushAttempts);
                Backoff(attempt);
            }
            catch (LibGit2SharpException ex)
            {
                _log.LogWarning(ex, "Push failed on attempt {Attempt}/{Of}", attempt, PushAttempts);
                Failed(ex, attempt == PushAttempts ? SyncHealth.Red : SyncHealth.Amber);
                Backoff(attempt);
            }
        }

        Health = SyncHealth.Red;
        return false;
    }

    /// <summary>
    /// Commits que van por delante de la rama remota, es decir, los que aún no se han publicado (F5.1).
    /// <para>
    /// Hasta F5.1 «Sincronizar ahora» solo hacía pull, así que un commit local que no hubiera
    /// logrado publicarse (offline, push rechazado) se quedaba esperando a la siguiente escritura
    /// del usuario para salir. Contarlos es lo que permite empujarlos a propósito y, además,
    /// decirle al usuario cuántos se publicaron en vez de un "sincronizado" sin contenido.
    /// </para>
    /// <para>
    /// Cuenta contra la referencia de seguimiento local (<c>origin/{rama}</c>), es decir, contra
    /// lo último que se trajo; llámalo después de un <see cref="Pull"/> para que el número
    /// signifique "pendientes de publicar" y no "pendientes desde el último fetch".
    /// </para>
    /// </summary>
    public int PendingCommits
    {
        get
        {
            try
            {
                Branch local = Repo.Head;
                if (local.Tip is null)
                {
                    return 0;
                }

                Branch? remote = Repo.Branches[$"origin/{local.FriendlyName}"];
                if (remote?.Tip is null)
                {
                    // Un hub recién creado: la rama remota aún no existe, así que TODO lo local
                    // está sin publicar.
                    return Repo.Commits.Count();
                }

                return Repo.ObjectDatabase.CalculateHistoryDivergence(local.Tip, remote.Tip).AheadBy ?? 0;
            }
            catch (LibGit2SharpException ex)
            {
                _log.LogWarning(ex, "Could not count pending commits");
                return 0;
            }
        }
    }

    /// <summary>
    /// El commit en el que está el clon ahora mismo, o null si el repositorio no tiene historial.
    /// Es el punto al que <see cref="ResetHardTo"/> sabe volver.
    /// </summary>
    public string? HeadCommitSha
    {
        get
        {
            try
            {
                return Repo.Head.Tip?.Sha;
            }
            catch (LibGit2SharpException ex)
            {
                _log.LogWarning(ex, "Could not read HEAD");
                return null;
            }
        }
    }

    /// <summary>
    /// Devuelve el clon —rama Y árbol de trabajo— al commit indicado, tirando lo que hubiera
    /// encima. Es la marcha atrás de una operación que ya escribió y commiteó pero no logró
    /// publicarse (F5.7 §5): sin ella, un push fallido dejaría el borrado hecho en local y a
    /// medio camino de todos los demás.
    /// <para>
    /// Deliberadamente NO es parte de <see cref="Push"/>: solo quien sabe que su escritura es
    /// atómica puede pedir que se deshaga.
    /// </para>
    /// </summary>
    public void ResetHardTo(string commitSha)
    {
        Commit? target = Repo.Lookup<Commit>(commitSha);
        if (target is null)
        {
            throw new InvalidOperationException($"El commit {commitSha} ya no está en el clon.");
        }

        Repo.Reset(ResetMode.Hard, target);
    }

    /// <summary>
    /// <b>Cuánto se espera a una publicación antes de darla por perdida</b> (BUGFIX-PUSH).
    /// <para>
    /// Treinta segundos: una publicación normal del hub tarda uno o dos, y quien está mirando una
    /// sesión arrancar no puede esperar más que eso sin saber a qué. El número es público porque el
    /// motivo que se le enseña al usuario lo lleva dentro y no puede decir uno distinto.
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultPushTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// El tope de ESTE servicio. Es de instancia y no una constante para que se pueda bajar donde
    /// esperar treinta segundos no aporta nada —una prueba que comprueba justamente que el reloj
    /// existe—, sin tocar un estático que comparte todo el proceso (R6 §8).
    /// </summary>
    public TimeSpan PushTimeout { get; set; } = DefaultPushTimeout;

    /// <summary>
    /// <b>Una publicación a la vez</b>. Cuando una vence, el hilo que la ejecuta <b>sigue vivo</b>
    /// dentro de libgit2 —no hay forma de abortarlo— y <c>Repository</c> no es seguro entre hilos:
    /// entrar con una segunda mientras la primera sigue dentro sería corromper el clon. La siguiente
    /// falla en el acto y con su motivo, que es infinitamente mejor.
    /// </summary>
    private readonly SemaphoreSlim _pushGate = new(1, 1);

    /// <summary>Commit then push in one call — the common "after a user write" path.</summary>
    public bool CommitAndPush(string message)
        => CommitAndPush(message, PushTimeout, CancellationToken.None);

    /// <inheritdoc cref="CommitAndPush(string, TimeSpan, CancellationToken)"/>
    public bool CommitAndPush(string message, CancellationToken ct)
        => CommitAndPush(message, PushTimeout, ct);

    /// <summary>
    /// <b>Commit y push con reloj</b> (BUGFIX-PUSH). Nunca se queda esperando para siempre.
    /// <para>
    /// <b>El defecto que cierra, con la pila delante.</b> Una sesión no arrancaba y «Detener» no
    /// respondía. El hilo de interfaz estaba <b>libre</b> —en su bucle de mensajes—, y el de la
    /// sesión, dentro de <c>LibGit2Sharp.Network.Push</c> llamado desde
    /// <c>SessionCoordinator.PublishClaims</c>, más de un minuto. libgit2 no pone reloj a su
    /// transporte: una conexión que se queda a medias espera <b>indefinidamente</b>, sin excepción,
    /// sin traza y sin nada que cancelar. Por eso «Detener» no hacía nada: no había ningún punto de
    /// cancelación al que llegar.
    /// </para>
    /// <para>
    /// <b>Por qué un hilo y no un token.</b> El <see cref="CancellationToken"/> no puede
    /// interrumpir una llamada nativa que ya está dentro de libgit2. Lo único que se puede hacer es
    /// <b>dejar de esperarla</b>: la publicación corre en su propio hilo —de fondo, para que no
    /// pueda mantener vivo el proceso— y aquí se espera con reloj. Si vence, quien llamó se entera
    /// y sigue su camino; el hilo huérfano terminará cuando libgit2 lo suelte, y hasta entonces
    /// tiene la puerta echada para que nadie toque el repositorio a la vez.
    /// </para>
    /// <para>
    /// <b>Y deja traza</b>, que es lo que faltaba: el defecto de hoy no dejó ni una línea en el
    /// registro porque el push nunca volvió. Ahora se apunta cuándo empieza, cuánto tarda y cómo
    /// acaba.
    /// </para>
    /// </summary>
    /// <returns><c>true</c> si se publicó; <c>false</c> si venció el reloj o el push falló.</returns>
    public bool CommitAndPush(string message, TimeSpan timeout, CancellationToken ct)
    {
        if (!_pushGate.Wait(0, CancellationToken.None))
        {
            LastError = "Hay una publicación anterior que el hub todavía no ha soltado.";
            Health = SyncHealth.Red;
            _log.LogWarning("Push omitido: la publicación anterior sigue en vuelo. {Message}", message);
            return false;
        }

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // El commit va DENTRO del hilo: también toca el repositorio, y la puerta que protege al
        // push tiene que protegerlo a él por el mismo motivo.
        // EL HILO HUÉRFANO NO PUEDE TIRAR EL PROCESO. Cuando el reloj vence, quien llamó se va y
        // esto se queda dentro de libgit2; si al salir revienta —el caso normal es encontrarse el
        // `Repository` ya liberado porque alguien cerró el hub mientras tanto— una excepción sin
        // recoger en un hilo suelto mata la aplicación entera. Se traga TODO: nadie está
        // escuchando, y lo que tenía que decirse ya se dijo cuando venció el reloj.
        var worker = new Thread(() =>
        {
            try
            {
                Commit(message);
                done.TrySetResult(Push());
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
            finally
            {
                try
                {
                    _pushGate.Release();
                }
                catch (ObjectDisposedException)
                {
                    // El servicio se cerró mientras esto seguía dentro. No hay puerta que soltar.
                }
            }
        })
        {
            IsBackground = true,
            Name = "atalaya-hub-push",
        };

        _log.LogInformation("Push: empieza «{Message}» (tope {Seconds} s)", message, timeout.TotalSeconds);
        worker.Start();

        try
        {
            if (done.Task.Wait(timeout, ct))
            {
                bool ok = done.Task.GetAwaiter().GetResult();
                _log.LogInformation(
                    "Push: {Outcome} «{Message}» en {Ms} ms", ok ? "publicado" : "rechazado", message,
                    clock.ElapsedMilliseconds);
                return ok;
            }
        }
        catch (AggregateException ex) when (ex.InnerException is not null)
        {
            _log.LogWarning(ex.InnerException, "Push: reventó «{Message}» tras {Ms} ms", message,
                clock.ElapsedMilliseconds);
            Failed(ex.InnerException, SyncHealth.Red);
            return false;
        }

        // La tarea que nadie va a mirar queda OBSERVADA: si el push huérfano acaba reventando, su
        // excepción muere aquí en vez de aparecer como `UnobservedTaskException` mucho después y
        // lejos de su causa.
        _ = done.Task.ContinueWith(
            t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // Venció, o lo cancelaron. En los dos casos se DEJA DE ESPERAR y se dice por qué; el hilo
        // sigue dentro de libgit2 y la puerta queda echada hasta que salga.
        Health = SyncHealth.Red;
        LastError = TimedOut(timeout);
        _log.LogWarning("Push: sin respuesta en {Ms} ms «{Message}»", clock.ElapsedMilliseconds, message);
        return false;
    }

    /// <summary>El motivo, en las palabras con las que se le enseña al usuario.</summary>
    public static string TimedOut(TimeSpan timeout)
        => $"No se pudo publicar en el hub en {timeout.TotalSeconds:0.##} s.";

    private void Fetch()
    {
        Remote origin = Repo.Network.Remotes["origin"];
        var options = new FetchOptions { CertificateCheck = CertificatePolicy.Check };
        if (_credentials is not null)
        {
            options.CredentialsProvider = _credentials;
        }

        var refspecs = origin.FetchRefSpecs.Select(r => r.Specification);
        Commands.Fetch(Repo, origin.Name, refspecs, options, "atalaya fetch");
    }

    private List<string> Integrate(Branch local, Branch remote)
    {
        HistoryDivergence div = Repo.ObjectDatabase.CalculateHistoryDivergence(local.Tip, remote.Tip);

        // Up to date or purely ahead — nothing to pull in.
        if (div.BehindBy is null or 0)
        {
            return new List<string>();
        }

        // Purely behind — fast-forward. The working tree is clean (we commit before pulling).
        if (div.AheadBy is 0)
        {
            Repo.Reset(ResetMode.Hard, remote.Tip);
            return new List<string>();
        }

        // Diverged — rebase our local commits onto the remote tip.
        return Rebase(local, remote);
    }

    private List<string> Rebase(Branch local, Branch remote)
    {
        var notifications = new List<string>();
        var rebaseOptions = new RebaseOptions();
        RebaseResult result = Repo.Rebase.Start(local, remote, null, _identity, rebaseOptions);

        int guard = 0;
        while (result.Status == RebaseStatus.Conflicts)
        {
            if (guard++ > 1000)
            {
                Repo.Rebase.Abort();
                throw new LibGit2SharpException("Rebase did not converge; aborted.");
            }

            ResolveConflicts(notifications);
            result = Repo.Rebase.Continue(_identity, rebaseOptions);
        }

        if (result.Status != RebaseStatus.Complete)
        {
            Repo.Rebase.Abort();
            throw new LibGit2SharpException($"Rebase ended with status {result.Status}.");
        }

        return notifications;
    }

    private void ResolveConflicts(List<string> notifications)
    {
        // Snapshot conflicts first — resolving mutates the index collection.
        var conflicts = Repo.Index.Conflicts.ToList();
        foreach (Conflict conflict in conflicts)
        {
            IndexEntry? oursEntry = conflict.Ours;   // the side rebased onto = remote
            IndexEntry? theirsEntry = conflict.Theirs; // the commit being replayed = local
            string rel = (oursEntry ?? theirsEntry ?? conflict.Ancestor)!.Path;

            string? oursText = ReadBlob(oursEntry);
            string? theirsText = ReadBlob(theirsEntry);

            string? resolved;
            if (rel.Contains("/claims/", StringComparison.Ordinal))
            {
                resolved = ResolveClaim(oursText, theirsText, rel, notifications);
            }
            else if (rel.Contains("/inventory/", StringComparison.Ordinal))
            {
                resolved = ResolveInventory(oursText, theirsText);
            }
            else if (rel.Contains("/findings/", StringComparison.Ordinal))
            {
                resolved = ResolveFinding(oursText, theirsText);
            }
            else
            {
                // Fallback: remote (already published) wins.
                resolved = oursText;
                _log.LogWarning("Unexpected conflict on {Path}; kept remote version", rel);
            }

            WriteResolution(rel, resolved);
        }
    }

    // Claims: the claim already in remote history wins (§3). Remote present → keep remote.
    private static string? ResolveClaim(string? remote, string? local, string rel, List<string> notifications)
    {
        if (remote is not null)
        {
            if (local is not null && local != remote)
            {
                string who = TryClaimOwner(remote) ?? "otro usuario";
                string unit = TryClaimUnit(remote) ?? rel;
                notifications.Add($"{who} reclamó {unit} antes que tú; tu claim se liberó.");
            }

            return remote;
        }

        // Remote released the claim → honor the release (drop it).
        return null;
    }

    private static string ResolveInventory(string? remote, string? local)
    {
        if (remote is null)
        {
            return local!;
        }

        if (local is null)
        {
            return remote;
        }

        InventoryCycle merged = HubMergePolicy.MergeInventory(
            AtalayaJson.Deserialize<InventoryCycle>(local),
            AtalayaJson.Deserialize<InventoryCycle>(remote));
        return AtalayaJson.Serialize(merged);
    }

    private static string ResolveFinding(string? remote, string? local)
    {
        if (remote is null)
        {
            return local!;
        }

        if (local is null)
        {
            return remote;
        }

        Finding merged = HubMergePolicy.MergeFinding(
            AtalayaJson.Deserialize<Finding>(local),
            AtalayaJson.Deserialize<Finding>(remote));
        return AtalayaJson.Serialize(merged);
    }

    private void WriteResolution(string rel, string? content)
    {
        string abs = Path.Combine(_paths.Root, rel.Replace('/', Path.DirectorySeparatorChar));
        if (content is null)
        {
            if (File.Exists(abs))
            {
                File.Delete(abs);
            }
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs, content);
        }

        Commands.Stage(Repo, rel);
    }

    private string? ReadBlob(IndexEntry? entry)
        => entry is null ? null : Repo.Lookup<Blob>(entry.Id)?.GetContentText();

    private static string? TryClaimOwner(string json)
    {
        try
        {
            return AtalayaJson.Deserialize<Claim>(json).By;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryClaimUnit(string json)
    {
        try
        {
            return AtalayaJson.Deserialize<Claim>(json).Unit;
        }
        catch
        {
            return null;
        }
    }

    private List<HubChange> DiffPaths(Commit oldTip, Commit newTip)
    {
        var changes = new List<HubChange>();
        if (oldTip == newTip)
        {
            return changes;
        }

        TreeChanges diff = Repo.Diff.Compare<TreeChanges>(oldTip.Tree, newTip.Tree);
        foreach (TreeEntryChanges c in diff)
        {
            HubChangeKind kind = c.Status switch
            {
                ChangeKind.Added => HubChangeKind.Added,
                ChangeKind.Deleted => HubChangeKind.Deleted,
                _ => HubChangeKind.Modified,
            };
            changes.Add(new HubChange(c.Path, kind));
        }

        return changes;
    }

    /// <summary>
    /// La espera entre intentos, <b>doblándose</b>: 300, 600, 1.200 y 2.400 ms. Con dos personas
    /// publicando contra el mismo hub, esperar lo mismo cada vez es volver a chocar a la misma
    /// velocidad. El último intento no espera a nadie: ya no hay otro detrás.
    /// </summary>
    private static void Backoff(int attempt)
    {
        if (attempt >= PushAttempts)
        {
            return;
        }

        Thread.Sleep(TimeSpan.FromMilliseconds(300 * Math.Pow(2, attempt - 1)));
    }

    /// <summary>
    /// <b>No se cierra el repositorio debajo de un push en vuelo</b> (BUGFIX-PUSH). Tras un reloj
    /// vencido el hilo sigue dentro de libgit2 con el <c>Repository</c> en la mano; liberarlo ahí
    /// es un fallo nativo en un hilo que no lo puede contar. Se espera un momento a que suelte la
    /// puerta y, si no lo hace, <b>se prefiere dejar el handle sin liberar</b>: un handle de más al
    /// cerrar no se nota, y un cierre que revienta se nota siempre.
    /// </summary>
    public void Dispose()
    {
        bool free = _pushGate.Wait(TimeSpan.FromSeconds(2));
        try
        {
            if (free)
            {
                _repo?.Dispose();
                _repo = null;
            }
        }
        finally
        {
            if (free)
            {
                _pushGate.Release();
            }

            _pushGate.Dispose();
        }
    }
}
