using System.Text.Json.Serialization;
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

    /// <summary>
    /// El miembro afectado tal y como lo declaró el auditor (<c>Clase.Metodo</c>), si lo declaró.
    /// <para>
    /// F5.6 (D-223): <c>submit_finding</c> ya recibía este dato y <c>SubmittedFinding</c> lo
    /// llevaba, pero se tiraba al crear el hallazgo. Es el ancla de reserva cuando el
    /// <see cref="Location.SnippetHash"/> deja de casar porque el código de dentro cambió y el
    /// método sigue ahí. Aditivo y opcional: los hallazgos anteriores lo tienen a <c>null</c> y
    /// se apañan con los identificadores del título.
    /// </para>
    /// </summary>
    public string? Symbol { get; set; }

    public AuditMode Origin { get; set; }

    /// <summary>
    /// La temática del CICLO que lo detectó (F17). No es una clasificación del defecto —un mismo
    /// <c>.Result</c> puede caer en Rendimiento o en Concurrencia según con qué lupa se mirara—,
    /// sino la traza de bajo qué encargo se vio: es lo que decide qué pasada lo reconcilia después.
    /// Aditivo: los hallazgos anteriores a F17 no traen la clave y se leen como General, que era
    /// la única mirada que existía.
    /// </summary>
    [JsonPropertyName("tematica")]
    public AuditTheme Theme { get; set; } = AuditTheme.General;

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
        DetectionStamp stamp,
        AuditTheme theme = AuditTheme.General)
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
            Symbol = submitted.Symbol,
            Origin = origin,
            Theme = theme,
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
    /// <param name="provider">
    /// La casa del auditor que discrepa (F14). Se guarda con la disputa porque dos casas distintas
    /// discrepando del mismo hallazgo no vale lo mismo que dos modelos de la misma: la primera es
    /// una segunda opinión de verdad, la segunda puede ser el mismo punto ciego dos veces.
    /// </param>
    public void Dispute(
        DateTimeOffset utc, string by, string? model, string justification, string? provider = null)
    {
        string reason = string.IsNullOrWhiteSpace(justification)
            ? "sin razonamiento aportado"
            : justification.Trim();
        Disputes.Add(new DisputeEntry(utc, model, by, reason, provider));
        History.Add(new HistoryEntry(utc, FindingEvent.Disputed, by,
            $"no-es-defecto según {Who(model, provider)}: {reason}"));
    }

    /// <summary>
    /// Cómo se nombra a quien juzgó, en una línea de historial. Con las dos cosas cuando se saben
    /// —«gpt-5 (GitHub Copilot)»— porque un id de modelo suelto no dice de quién es, y el historial
    /// lo lee una persona meses después.
    /// </summary>
    private static string Who(string? model, string? provider)
        => (model, provider) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{model} ({provider})",
            ({ Length: > 0 }, _) => model!,
            (_, { Length: > 0 }) => provider!,
            _ => "el auditor",
        };

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

    /// <summary>
    /// Anota un evento en el historial <b>sin eco</b> (F6.6): si el anterior es exactamente el
    /// mismo —mismo evento, mismo autor, mismo detalle— no se añade una línea nueva; se actualiza
    /// la que ya está con la hora de ahora y se cuenta («×3»).
    /// <para>
    /// Nace de pulsar «Verificar ahora» tres veces seguidas sobre el mismo hallazgo: el historial
    /// se llenó de tres líneas idénticas que decían tres veces lo mismo. Un historial es la lista
    /// de lo que le ha PASADO al hallazgo, y repetir la misma pregunta no le pasa nada nuevo.
    /// </para>
    /// <para>
    /// Solo colapsa contra el ÚLTIMO evento: en cuanto pasa cualquier otra cosa entremedias, la
    /// repetición vuelve a ser una línea propia — porque entonces sí es información.
    /// </para>
    /// </summary>
    public void Record(HistoryEntry entry)
    {
        HistoryEntry? last = History.Count > 0 ? History[^1] : null;
        string detail = CoreDetail(entry.Detail);

        if (last is null || last.Event != entry.Event
            || !string.Equals(last.By, entry.By, StringComparison.Ordinal)
            || !string.Equals(CoreDetail(last.Detail), detail, StringComparison.Ordinal))
        {
            History.Add(entry);
            return;
        }

        int times = RepeatCount(last.Detail) + 1;
        History[^1] = last with { Utc = entry.Utc, Detail = $"{detail} (×{times})" };
    }

    /// <summary>El detalle sin la marca de repetición, que es lo que se compara.</summary>
    private static string CoreDetail(string? detail)
    {
        string d = (detail ?? string.Empty).TrimEnd();
        int open = d.LastIndexOf(" (×", StringComparison.Ordinal);
        if (open < 0 || !d.EndsWith(")", StringComparison.Ordinal))
        {
            return d;
        }

        string inside = d[(open + 3)..^1];
        return inside.Length > 0 && inside.All(char.IsDigit) ? d[..open] : d;
    }

    /// <summary>Cuántas veces lleva anotado ese evento. Sin marca, una.</summary>
    private static int RepeatCount(string? detail)
    {
        string d = (detail ?? string.Empty).TrimEnd();
        int open = d.LastIndexOf(" (×", StringComparison.Ordinal);
        return open >= 0 && d.EndsWith(")", StringComparison.Ordinal)
               && int.TryParse(d[(open + 3)..^1], out int n) && n > 0
            ? n
            : 1;
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
