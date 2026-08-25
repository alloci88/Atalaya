using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Anchoring;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;

namespace Atalaya.App.Services;

/// <summary>
/// Runs a verify session (§5.4): re-anchors each finding's location by snippet hash, asks the agent
/// for a verdict, and applies it — confirmado refreshes, resuelto resolves (via verify), no-verificable
/// (or a lost anchor) sets needsReview. "No localizado" is NEVER confused with "resuelto".
/// </summary>
public sealed class VerifyCoordinator
{
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly IUlidFactory _ulids;
    private readonly ICopilotAgent _agent;

    public VerifyCoordinator(HubContext hub, MachineConfigStore machines, IUlidFactory ulids, ICopilotAgent agent)
    {
        _hub = hub;
        _machines = machines;
        _ulids = ulids;
        _agent = agent;
    }

    public async Task<int> RunAsync(string slug, IReadOnlyList<Ulid> findingIds, CancellationToken ct)
    {
        string? clone = _machines.Load().ClonePathFor(slug);
        string commit = GitInfo.HeadSha(clone);
        string by = _hub.ResolveIdentity().Name;
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Verify, commit, by);

        var targets = new List<VerifyTarget>();
        foreach (Ulid id in findingIds)
        {
            Finding? f = _hub.Store.TryReadFinding(slug, id.ToString());
            if (f is null || f.Locations.Count == 0)
            {
                continue;
            }

            Location loc = f.Locations[0];
            (bool anchored, int line, string? snippet) = SnippetAnchor.TryAnchor(clone, loc.Path, loc.Line, loc.SnippetHash);
            if (!anchored)
            {
                // Location could not be re-anchored → needsReview; never "resuelto".
                f.NeedsReview = true;
                f.History.Add(new HistoryEntry(stamp.Utc, FindingEvent.Reopened, by, "no localizado tras cambios del código"));
                _hub.Store.WriteFinding(slug, f);
                continue;
            }

            targets.Add(new VerifyTarget(f.Id.ToString(), loc.Path, line, snippet, f.Title, f.Description));
        }

        if (targets.Count == 0)
        {
            return 0;
        }

        var toolbox = new VerifyToolbox(_hub, slug, stamp);
        string prompt = PromptComposer.ComposeVerifyPrompt(targets);
        await _agent.VerifyAsync(new VerifyRequest(prompt, targets), toolbox, ct);

        _hub.Store.WriteSession(new AuditSession
        {
            Id = _ulids.NewUlid(),
            AppSlug = slug,
            Mode = AuditMode.Verify,
            By = by,
            Machine = Environment.MachineName,
            StartedUtc = stamp.Utc,
            EndedUtc = DateTimeOffset.UtcNow,
            Commit = commit,
            CycleN = _hub.Store.TryReadApp(slug)?.CurrentCycle ?? 1,
            Model = _agent.ModelName,
        });
        _hub.Sync?.CommitAndPush($"verify: {slug} {targets.Count} hallazgos");
        return toolbox.Applied;
    }

    /// <summary>Applies verify verdicts to findings (§5.4). Uses the ULID, never the alias.</summary>
    private sealed class VerifyToolbox : IVerifyToolbox
    {
        private readonly HubContext _hub;
        private readonly string _slug;
        private readonly DetectionStamp _stamp;

        public VerifyToolbox(HubContext hub, string slug, DetectionStamp stamp)
        {
            _hub = hub;
            _slug = slug;
            _stamp = stamp;
        }

        public int Applied { get; private set; }

        public void SubmitVerdict(string findingUlid, string verdict, string evidence)
        {
            if (!Ulid.TryParse(findingUlid, out Ulid id))
            {
                return;
            }

            Finding? f = _hub.Store.TryReadFinding(_slug, id.ToString());
            if (f is null)
            {
                return;
            }

            switch (verdict.Trim().ToLowerInvariant())
            {
                case "confirmado":
                    f.Confirm(AuditMode.Verify, _stamp); // refreshes lastConfirmed, no confidence change
                    break;
                case "resuelto":
                    f.Resolve(new ResolutionStamp(_stamp.Utc, ResolutionVia.Verify, AuditMode.Verify, _stamp.Commit, _stamp.By, evidence));
                    break;
                default: // no-verificable
                    f.NeedsReview = true;
                    f.History.Add(new HistoryEntry(_stamp.Utc, FindingEvent.Reopened, _stamp.By, "verify: no verificable"));
                    break;
            }

            _hub.Store.WriteFinding(_slug, f);
            Applied++;
        }
    }
}

/// <summary>Re-anchors a stored location by its snippet hash (§5.4).</summary>
public static class SnippetAnchor
{
    public static (bool Anchored, int Line, string? Snippet) TryAnchor(string? clonePath, string path, int line, string? snippetHash)
    {
        if (string.IsNullOrWhiteSpace(clonePath))
        {
            return (true, line, null); // no clone to check against — trust the stored line
        }

        string abs = Path.Combine(clonePath, path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(abs))
        {
            return (false, line, null);
        }

        string[] lines = File.ReadAllLines(abs);
        if (snippetHash is null)
        {
            return (line >= 1 && line <= lines.Length, line, line >= 1 && line <= lines.Length ? lines[line - 1] : null);
        }

        // Still at the recorded line?
        if (line >= 1 && line <= lines.Length && CodeAnchor.ComputeSnippetHash(lines[line - 1]) == snippetHash)
        {
            return (true, line, lines[line - 1]);
        }

        // Moved — search the file for the matching snippet.
        for (int i = 0; i < lines.Length; i++)
        {
            if (CodeAnchor.ComputeSnippetHash(lines[i]) == snippetHash)
            {
                return (true, i + 1, lines[i]);
            }
        }

        return (false, line, null);
    }
}
