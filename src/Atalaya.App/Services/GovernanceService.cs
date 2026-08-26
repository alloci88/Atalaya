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

    // ------------------------------------------------------------------ F5.10 · exclusión de regla

    /// <summary>
    /// Qué pasó al excluir una regla: si existía ya, y cuántos hallazgos activos se silenciaron
    /// de paso. Se devuelve para poder contarlo, no para decidir nada.
    /// </summary>
    public sealed record RuleExclusionResult(string RuleId, bool Replaced, int SilencedFindings);

    /// <summary>
    /// Excluye una regla de UNA aplicación (F5.10). Desde este momento, ninguna auditoría de esta
    /// app registra hallazgos de <paramref name="ruleId"/>: se retira del brief y se suprime en la
    /// ingestión si el auditor la reporta igualmente.
    /// <para>
    /// <paramref name="silenceExisting"/> es una decisión SEPARADA y explícita de quien excluye.
    /// Excluir previene el futuro; qué hacer con los N hallazgos que ya existen es otra pregunta,
    /// y responderla por defecto en cualquiera de los dos sentidos sería decidir por el usuario:
    /// silenciarlos siempre borra deuda real de un plumazo, no silenciarlos nunca deja una lista
    /// que ya nadie va a mirar. Cada hallazgo silenciado así se lleva su propia entrada de
    /// historial y su propio fichero de silencio, con la regla que lo silenció escrita dentro.
    /// </para>
    /// </summary>
    public RuleExclusionResult ExcludeRule(
        string slug, string ruleId, SilenceReason reason, string? notes,
        DateTimeOffset? expiresUtc, bool silenceExisting)
    {
        string rule = (ruleId ?? string.Empty).Trim();
        Storage.HubPaths.RequireSafeRuleId(rule);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool replaced = _hub.Store.TryReadRuleExclusion(slug, rule) is not null;
        _hub.Store.WriteRuleExclusion(slug, new RuleExclusion
        {
            RuleId = rule,
            Reason = reason,
            Notes = notes,
            By = Me,
            Utc = now,
            ExpiresUtc = expiresUtc,
        });

        int silenced = 0;
        if (silenceExisting)
        {
            string detail = $"silenciado por exclusión de la regla {rule}"
                + (string.IsNullOrWhiteSpace(notes) ? "" : $": {notes!.Trim()}");

            foreach (Finding f in _hub.Store.ListFindings(slug)
                         .Where(f => f.Status == FindingStatus.Activo)
                         .Where(f => string.Equals(f.RuleId, rule, StringComparison.Ordinal))
                         .ToList())
            {
                _hub.Store.WriteSilence(slug, new Silence
                {
                    FindingUlid = f.Id,
                    Reason = reason,
                    Notes = notes,
                    By = Me,
                    Utc = now,
                    ExpiresUtc = expiresUtc,
                    ByRuleExclusion = rule,
                });

                f.MarkSilenced(now, Me, detail);
                _hub.Store.WriteFinding(slug, f);
                silenced++;
            }
        }

        // Un solo push para todo el gesto: excluir y silenciar lo existente son una sola decisión
        // del usuario, y partirla en dos commits contaría dos cosas donde hubo una.
        Push(slug, $"rule-exclusion: {rule} en {slug}"
            + (silenced > 0 ? $" (+{silenced} silenciados)" : ""));
        return new RuleExclusionResult(rule, replaced, silenced);
    }

    /// <summary>
    /// Retira la exclusión: la regla vuelve al brief y sus hallazgos vuelven a poder reportarse.
    /// <para>
    /// NO des-silencia lo que se silenció en masa. Cada uno de esos silencios fue una decisión
    /// registrada con su autor y su motivo, y deshacerla en cascada tiraría también los que se
    /// hubieran revisado uno a uno desde entonces. Se levantan desde su ficha, como cualquier otro.
    /// </para>
    /// </summary>
    public bool UnexcludeRule(string slug, string ruleId)
    {
        bool removed = _hub.Store.DeleteRuleExclusion(slug, ruleId);
        if (removed)
        {
            Push(slug, $"rule-exclusion: retirada {ruleId} en {slug}");
        }

        return removed;
    }

    /// <summary>
    /// Cambia la caducidad de una exclusión viva o caducada, conservando motivo y notas. Quien la
    /// toca pasa a ser su autor: es una decisión nueva sobre cuánto más dura, y firmarla con el
    /// nombre de quien la creó haría que el registro mintiera.
    /// </summary>
    public bool SetRuleExclusionExpiry(string slug, string ruleId, DateTimeOffset? expiresUtc)
    {
        RuleExclusion? exclusion = _hub.Store.TryReadRuleExclusion(slug, ruleId);
        if (exclusion is null)
        {
            return false;
        }

        exclusion.ExpiresUtc = expiresUtc;
        exclusion.By = Me;
        exclusion.Utc = DateTimeOffset.UtcNow;
        _hub.Store.WriteRuleExclusion(slug, exclusion);
        Push(slug, $"rule-exclusion: caducidad de {ruleId} en {slug}");
        return true;
    }

    /// <summary>Cuántos hallazgos ACTIVOS de esa regla hay en la app: la N de la pregunta del diálogo.</summary>
    public int CountActiveWithRule(string slug, string ruleId)
        => _hub.Store.ListFindings(slug)
            .Count(f => f.Status == FindingStatus.Activo
                        && string.Equals(f.RuleId, ruleId, StringComparison.Ordinal));

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
