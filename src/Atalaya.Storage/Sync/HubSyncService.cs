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
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                Pull(); // rebase onto latest remote first
                Branch local = Repo.Head;
                var pushOptions = new PushOptions { CertificateCheck = CertificatePolicy.Check };
                if (_credentials is not null)
                {
                    pushOptions.CredentialsProvider = _credentials;
                }

                Remote origin = Repo.Network.Remotes["origin"];
                Repo.Network.Push(origin, $"refs/heads/{local.FriendlyName}", pushOptions);
                Succeeded();
                return true;
            }
            catch (NonFastForwardException)
            {
                _log.LogInformation("Push rejected (remote moved); retry {Attempt}/3", attempt);
                Backoff(attempt);
            }
            catch (LibGit2SharpException ex)
            {
                _log.LogWarning(ex, "Push failed on attempt {Attempt}/3", attempt);
                Failed(ex, attempt == 3 ? SyncHealth.Red : SyncHealth.Amber);
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

    /// <summary>Commit then push in one call — the common "after a user write" path.</summary>
    public bool CommitAndPush(string message)
    {
        Commit(message);
        return Push();
    }

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

    private static void Backoff(int attempt) => Thread.Sleep(TimeSpan.FromMilliseconds(150 * attempt));

    public void Dispose() => _repo?.Dispose();
}
