using Atalaya.Domain.Ids;
using Atalaya.Domain.Rules;

namespace Atalaya.Domain.Model;

/// <summary>
/// A finding — the central entity (§2). Its identity is the <see cref="Id"/> (ULID), y punto
/// (F4): ya no hay fingerprint ni ninguna otra clave derivada. <see cref="DisplayId"/> es un
/// alias de presentación mutable. Los hallazgos nunca se borran; se mueven entre valores de
/// <see cref="FindingStatus"/>. Las transiciones pasan por los métodos de abajo para que la
/// máquina de confianza y la traza de auditoría se apliquen siempre juntas.
/// </summary>
public sealed class Finding
{
    public int SchemaVersion { get; set; } = 1;

    public Ulid Id { get; init; }

    /// <summary>Human-readable alias (BUG-0042). Assigned only after a successful push (§2).</summary>
    public string? DisplayId { get; set; }

    public required string RuleId { get; set; }

    public Pillar Pillar { get; set; }

    public FindingTag Tag { get; set; }

    public Severity Severity { get; set; }

    public Confidence Confidence { get; set; }

    public FindingStatus Status { get; set; } = FindingStatus.Activo;

    public required string Title { get; set; }

    public string Description { get; set; } = string.Empty;

    public string Impact { get; set; } = string.Empty;

    public string Recommendation { get; set; } = string.Empty;

    public List<Location> Locations { get; set; } = new();

    public AuditMode Origin { get; set; }

    public required DetectionStamp FirstDetected { get; set; }

    public required DetectionStamp LastConfirmed { get; set; }

    public int TimesConfirmed { get; set; } = 1;

    public ResolutionStamp? Resolved { get; set; }

    /// <summary>Set when a location could not be re-anchored or verify said non-verifiable (§5.4).</summary>
    public bool NeedsReview { get; set; }

    /// <summary>
    /// Discrepancias de criterio abiertas sobre este hallazgo (F5.1b): auditores que sostienen que
    /// nunca fue un defecto. Estar disputado NO es un estado del hallazgo — sigue activo, con su
    /// severidad y su confianza intactas — sino una marca que pide una decisión humana.
    /// <para>
    /// Se acumulan: dos o tres modelos distintos discrepando del mismo hallazgo es la señal fuerte.
    /// La marca solo la quita una persona (<see cref="ClearDisputes"/>) o el silencio por
    /// falso-positivo; el historial conserva las entradas aunque la marca se limpie.
    /// </para>
    /// </summary>
    public List<DisputeEntry> Disputes { get; set; } = new();

    public string? Assignee { get; set; }

    /// <summary>Previous <see cref="DisplayId"/> values, if an alias ever changed (§2).</summary>
    public List<string> AliasHistory { get; set; } = new();

    public List<HistoryEntry> History { get; set; } = new();

    /// <summary>Builds a brand-new finding, assigning confidence from the origin mode.</summary>
    public static Finding CreateNew(
        Ulid id,
        Ingestion.SubmittedFinding submitted,
        AuditMode origin,
        DetectionStamp stamp)
    {
        var finding = new Finding
        {
            Id = id,
            RuleId = submitted.RuleId,
            Pillar = submitted.Pillar,
            Tag = submitted.Tag,
            Severity = submitted.Severity,
            Confidence = ConfidenceMachine.ForNew(origin),
            Status = FindingStatus.Activo,
            Title = submitted.Title,
            Description = submitted.Description,
            Impact = submitted.Impact,
            Recommendation = submitted.Recommendation,
            Locations = submitted.Locations.ToList(),
            Origin = origin,
            FirstDetected = stamp,
            LastConfirmed = stamp,
            TimesConfirmed = 1,
        };

        finding.History.Add(new HistoryEntry(stamp.Utc, FindingEvent.Detected, stamp.By, $"detected via {origin}"));
        return finding;
    }

    /// <summary>
    /// Reconfirms this finding: applies the confidence machine, refreshes
    /// <see cref="LastConfirmed"/>, increments <see cref="TimesConfirmed"/> and logs it.
    /// </summary>
    public void Confirm(AuditMode mode, DetectionStamp stamp, bool cycleClose = false)
    {
        Confidence before = Confidence;
        Confidence = ConfidenceMachine.OnReconfirm(Confidence, mode, cycleClose);
        LastConfirmed = stamp;
        TimesConfirmed++;

        string detail = before == Confidence
            ? $"confirmed via {mode}"
            : $"confirmed via {mode}, confidence {before}→{Confidence}";
        History.Add(new HistoryEntry(stamp.Utc, FindingEvent.Confirmed, stamp.By, detail));
    }

    /// <summary>Moves the finding to <see cref="FindingStatus.Resuelto"/> with an anchor (§5.7).</summary>
    public void Resolve(ResolutionStamp stamp)
    {
        Status = FindingStatus.Resuelto;
        Resolved = stamp;
        History.Add(new HistoryEntry(stamp.Utc, FindingEvent.Resolved, stamp.By,
            $"resolved via {stamp.Via}" + (stamp.Justification is null ? "" : $": {stamp.Justification}")));
    }

    /// <summary>
    /// Registra que un auditor sostiene que esto nunca fue un defecto (F5.1b). NO resuelve y NO
    /// desactiva: el hallazgo se queda exactamente donde está, con una disputa colgada.
    /// <para>
    /// Tampoco cuenta como confirmación: quien discrepa no está diciendo «el defecto sigue ahí»,
    /// así que <see cref="TimesConfirmed"/>, <see cref="Confidence"/> y <see cref="LastConfirmed"/>
    /// no se tocan. Mover cualquiera de los tres convertiría un desacuerdo en evidencia.
    /// </para>
    /// </summary>
    public void Dispute(DateTimeOffset utc, string by, string? model, string justification)
    {
        string reason = string.IsNullOrWhiteSpace(justification)
            ? "sin razonamiento aportado"
            : justification.Trim();
        Disputes.Add(new DisputeEntry(utc, model, by, reason));
        History.Add(new HistoryEntry(utc, FindingEvent.Disputed, by,
            $"no-es-defecto según {model ?? "el auditor"}: {reason}"));
    }

    /// <summary>
    /// Cierra la disputa dejando el hallazgo en pie: una persona ha decidido que SÍ es un defecto
    /// (§5.6, gobernanza). El historial conserva las disputas; lo que se retira es la marca.
    /// </summary>
    public void ClearDisputes(DateTimeOffset utc, string by, string? detail)
    {
        int count = Disputes.Count;
        Disputes.Clear();
        History.Add(new HistoryEntry(utc, FindingEvent.DisputeCleared, by,
            detail ?? $"{count} disputa(s) descartada(s): sigue siendo un defecto"));
    }

    /// <summary>Reopens a resolved finding (governance).</summary>
    public void Reopen(DateTimeOffset utc, string by, string? detail)
    {
        Status = FindingStatus.Activo;
        Resolved = null;
        History.Add(new HistoryEntry(utc, FindingEvent.Reopened, by, detail));
    }

    public void ChangeSeverity(Severity newSeverity, DateTimeOffset utc, string by)
    {
        if (newSeverity == Severity)
        {
            return;
        }

        Severity old = Severity;
        Severity = newSeverity;
        History.Add(new HistoryEntry(utc, FindingEvent.SeverityChanged, by, $"{old}→{newSeverity}"));
    }

    public void Assign(string? assignee, DateTimeOffset utc, string by)
    {
        Assignee = assignee;
        History.Add(new HistoryEntry(utc, FindingEvent.Assigned, by,
            assignee is null ? "unassigned" : $"assigned to {assignee}"));
    }

    /// <summary>Marks the finding silenced (a human action; the silence entity is separate).</summary>
    public void MarkSilenced(DateTimeOffset utc, string by, string? detail)
    {
        Status = FindingStatus.Silenciado;
        History.Add(new HistoryEntry(utc, FindingEvent.Silenced, by, detail));
    }

    /// <summary>Lifts silence, returning to active (a human action or silence expiry re-detection).</summary>
    public void Unsilence(DateTimeOffset utc, string by, string? detail)
    {
        if (Status == FindingStatus.Silenciado)
        {
            Status = FindingStatus.Activo;
        }

        History.Add(new HistoryEntry(utc, FindingEvent.Unsilenced, by, detail));
    }

    /// <summary>Assigns the presentation alias (post-push), preserving any prior alias (§2).</summary>
    public void AssignDisplayId(string displayId)
    {
        if (!string.IsNullOrEmpty(DisplayId) && DisplayId != displayId)
        {
            AliasHistory.Add(DisplayId);
        }

        DisplayId = displayId;
    }
}
