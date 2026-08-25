using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// Finding governance (§5.6, §5.7): silence, un-silence, assign, comment, change severity, resolve
/// manually, reopen. Every action mutates a finding through its domain methods (audit trail),
/// persists, and pushes immediately. Silencing is always a human action with author and reason.
/// </summary>
public sealed class GovernanceService
{
    private readonly HubContext _hub;
    private readonly IUlidFactory _ulids;

    public GovernanceService(HubContext hub, IUlidFactory ulids)
    {
        _hub = hub;
        _ulids = ulids;
    }

    private string Me => _hub.ResolveIdentity().Name;

    public void Silence(string slug, Ulid findingId, SilenceReason reason, string? notes, DateTimeOffset? expiresUtc)
    {
        Finding f = Require(slug, findingId);
        _hub.Store.WriteSilence(slug, new Silence
        {
            FindingUlid = f.Id,
            Reason = reason,
            Notes = notes,
            By = Me,
            Utc = DateTimeOffset.UtcNow,
            ExpiresUtc = expiresUtc,
        });

        f.MarkSilenced(DateTimeOffset.UtcNow, Me, notes);
        _hub.Store.WriteFinding(slug, f);
        Push(slug, $"silence: {f.DisplayId ?? f.Id.ToString()}");
    }

    public void Unsilence(string slug, Ulid findingId)
    {
        Finding f = Require(slug, findingId);
        _hub.Store.DeleteSilence(slug, f.Id);
        f.Unsilence(DateTimeOffset.UtcNow, Me, "des-silenciado");
        _hub.Store.WriteFinding(slug, f);
        Push(slug, $"unsilence: {f.DisplayId ?? f.Id.ToString()}");
    }

    public void Assign(string slug, Ulid findingId, string? assignee)
    {
        Finding f = Require(slug, findingId);
        f.Assign(assignee, DateTimeOffset.UtcNow, Me);
        _hub.Store.WriteFinding(slug, f);
        Push(slug, $"assign: {f.DisplayId ?? f.Id.ToString()}");
    }

    public void ChangeSeverity(string slug, Ulid findingId, Severity severity)
    {
        Finding f = Require(slug, findingId);
        f.ChangeSeverity(severity, DateTimeOffset.UtcNow, Me);
        _hub.Store.WriteFinding(slug, f);
        Push(slug, $"severity: {f.DisplayId ?? f.Id.ToString()} → {severity}");
    }

    public void ResolveManually(string slug, Ulid findingId, string justification, string commit)
    {
        Finding f = Require(slug, findingId);
        f.Resolve(new ResolutionStamp(DateTimeOffset.UtcNow, ResolutionVia.Manual, AuditMode.Verify, commit, Me, justification));
        _hub.Store.WriteFinding(slug, f);
        Push(slug, $"resolve: {f.DisplayId ?? f.Id.ToString()}");
    }

    /// <summary>
    /// Cierra una disputa dando la razón al auditor que discrepó (F5.1b): el hallazgo NO se
    /// resuelve —nunca hubo nada que arreglar— sino que se silencia con motivo
    /// <see cref="SilenceReason.FalsoPositivo"/>, que es el cajón que §2 ya tenía para esto, con
    /// autor y fecha. La marca de disputa se retira porque la decisión ya está tomada; el
    /// historial la conserva.
    /// </summary>
    public void ResolveDisputeAsFalsePositive(string slug, Ulid findingId, string? notes)
    {
        Finding f = Require(slug, findingId);
        string justification = notes ?? DescribeDisputes(f);

        _hub.Store.WriteSilence(slug, new Silence
        {
            FindingUlid = f.Id,
            Reason = SilenceReason.FalsoPositivo,
            Notes = justification,
            By = Me,
            Utc = DateTimeOffset.UtcNow,
            ExpiresUtc = null,
        });

        f.ClearDisputes(DateTimeOffset.UtcNow, Me, $"disputa aceptada como falso positivo: {justification}");
        f.MarkSilenced(DateTimeOffset.UtcNow, Me, justification);
        _hub.Store.WriteFinding(slug, f);
        Push(slug, $"dispute: falso-positivo {f.DisplayId ?? f.Id.ToString()}");
    }

    /// <summary>
    /// Cierra una disputa dando la razón a quien lo reportó (F5.1b): sigue siendo un defecto. Se
    /// retira la marca y el hallazgo continúa exactamente como estaba.
    /// </summary>
    public void DismissDispute(string slug, Ulid findingId, string? notes)
    {
        Finding f = Require(slug, findingId);
        f.ClearDisputes(DateTimeOffset.UtcNow, Me,
            notes is null ? null : $"sigue siendo un defecto: {notes}");
        _hub.Store.WriteFinding(slug, f);
        Push(slug, $"dispute: mantenido {f.DisplayId ?? f.Id.ToString()}");
    }

    /// <summary>Resume quién discrepó y por qué, para dejarlo escrito en el silencio.</summary>
    private static string DescribeDisputes(Finding f)
        => f.Disputes.Count == 0
            ? "falso positivo (sin disputa registrada)"
            : string.Join(" · ", f.Disputes.Select(d => $"{d.Model ?? "auditor"}: {d.Justification}"));

    public void Reopen(string slug, Ulid findingId, string? detail)
    {
        Finding f = Require(slug, findingId);
        f.Reopen(DateTimeOffset.UtcNow, Me, detail);
        _hub.Store.WriteFinding(slug, f);
        Push(slug, $"reopen: {f.DisplayId ?? f.Id.ToString()}");
    }

    public Comment AddComment(string slug, Ulid findingId, string body, string? kind = null)
    {
        var comment = new Comment
        {
            Id = _ulids.NewUlid(),
            FindingUlid = findingId,
            By = Me,
            Utc = DateTimeOffset.UtcNow,
            Body = body,
            Kind = kind,
        };
        _hub.Store.WriteComment(slug, comment);
        Push(slug, $"comment: {findingId}");
        return comment;
    }

    private Finding Require(string slug, Ulid findingId)
        => _hub.Store.TryReadFinding(slug, findingId.ToString())
           ?? throw new InvalidOperationException($"Hallazgo {findingId} no encontrado.");

    private void Push(string slug, string message) => _hub.Sync?.CommitAndPush(message);
}
