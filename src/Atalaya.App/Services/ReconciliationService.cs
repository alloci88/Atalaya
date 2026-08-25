using Atalaya.Domain;
using Atalaya.Domain.Anchoring;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>Qué hizo la app con un veredicto del auditor.</summary>
public enum ReconcileOutcome
{
    /// <summary>Reconfirmado: sigue activo, máquina de confianza aplicada.</summary>
    Reconfirmed,

    /// <summary>Resuelto con la evidencia del auditor.</summary>
    Resolved,

    /// <summary>Marcado <c>needsReview</c>: el auditor no pudo determinarlo.</summary>
    NeedsReview,

    /// <summary>Silenciado y detectado presente: se registra la detección, NO se reactiva.</summary>
    SilenceRespected,
}

/// <summary>
/// Aplica los veredictos de reconciliación del auditor (F4) a los hallazgos, por ULID.
/// <para>
/// Es el único camino por el que un hallazgo previo cambia de estado durante una auditoría. No
/// existe la resolución implícita: si el auditor no se pronuncia, el hallazgo no se toca y la
/// unidad queda marcada como incompleta. Esto es lo que mata el ciclo duplicar→resolver que
/// arrastraba la saga del fingerprint: resolver exige una afirmación explícita.
/// </para>
/// </summary>
public sealed class ReconciliationService
{
    private readonly HubContext _hub;

    public ReconciliationService(HubContext hub) => _hub = hub;

    /// <summary>
    /// Los hallazgos que se le muestran al auditor para una unidad: los activos y los silenciados
    /// cuya ubicación primaria cae en esa unidad. Los resueltos NO se listan — si un problema
    /// resuelto reaparece, el auditor no lo ve en la lista, lo reporta como nuevo, y eso ES la
    /// reincidencia (§F4: simplicidad gana).
    /// </summary>
    public IReadOnlyList<Finding> ExistingForUnit(string slug, string unitPath)
    {
        string unit = CodeAnchor.NormalizePath(unitPath);
        return _hub.Store.ListFindings(slug)
            .Where(f => f.Status is FindingStatus.Activo or FindingStatus.Silenciado)
            .Where(f => f.Locations.Any(l => CodeAnchor.NormalizePath(l.Path) == unit))
            .OrderBy(f => f.Severity)
            .ThenBy(f => f.Id.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Aplica un veredicto a un hallazgo ya cargado y lo persiste. El llamante es responsable de
    /// haber validado que el ULID estaba en la lista mostrada al auditor.
    /// </summary>
    public ReconcileOutcome Apply(
        string slug, Finding finding, ReconcileVerdict verdict, string evidence,
        AuditMode mode, DetectionStamp stamp)
    {
        ReconcileOutcome outcome = verdict switch
        {
            ReconcileVerdict.Arreglado => Resolve(finding, evidence, mode, stamp),
            ReconcileVerdict.NoVerificable => NeedsReview(finding, evidence, stamp),
            _ => Present(slug, finding, evidence, mode, stamp),
        };

        _hub.Store.WriteFinding(slug, finding);
        return outcome;
    }

    private static ReconcileOutcome Resolve(Finding finding, string evidence, AuditMode mode, DetectionStamp stamp)
    {
        finding.Resolve(new ResolutionStamp(
            stamp.Utc, ResolutionVia.Auditor, mode, stamp.Commit, stamp.By,
            string.IsNullOrWhiteSpace(evidence) ? "arreglado (sin evidencia aportada)" : evidence));
        return ReconcileOutcome.Resolved;
    }

    private static ReconcileOutcome NeedsReview(Finding finding, string evidence, DetectionStamp stamp)
    {
        finding.NeedsReview = true;
        finding.History.Add(new HistoryEntry(stamp.Utc, FindingEvent.Confirmed, stamp.By,
            $"auditor: no verificable desde la unidad — {Detail(evidence)}"));
        return ReconcileOutcome.NeedsReview;
    }

    /// <summary>
    /// "Presente". Sobre un activo es una reconfirmación normal. Sobre un SILENCIADO con silencio
    /// vivo se registra la detección y se deja silenciado — el silencio es una decisión humana y
    /// el auditor no la revoca. Si el silencio ha caducado, la detección lo levanta (semántica de
    /// §2 conservada: un silencio caducado no suprime).
    /// </summary>
    private ReconcileOutcome Present(string slug, Finding finding, string evidence, AuditMode mode, DetectionStamp stamp)
    {
        if (finding.Status != FindingStatus.Silenciado)
        {
            finding.Confirm(mode, stamp);
            return ReconcileOutcome.Reconfirmed;
        }

        Silence? silence = _hub.Store.TryReadSilence(slug, finding.Id);
        if (silence is not null && !silence.IsLiveAt(stamp.Utc))
        {
            finding.Unsilence(stamp.Utc, stamp.By, "silencio caducado; re-detectado por el auditor");
            finding.Confirm(mode, stamp);
            return ReconcileOutcome.Reconfirmed;
        }

        // Detección registrada sin reactivar: ni cambia el estado ni la confianza.
        finding.LastConfirmed = stamp;
        finding.History.Add(new HistoryEntry(stamp.Utc, FindingEvent.Confirmed, stamp.By,
            $"detectado presente durante el silencio (no reactivado) — {Detail(evidence)}"));
        return ReconcileOutcome.SilenceRespected;
    }

    private static string Detail(string evidence)
        => string.IsNullOrWhiteSpace(evidence) ? "sin evidencia aportada" : evidence.Trim();

    /// <summary>Parsea el veredicto del agente. Tolerante a acentos y caja, estricto en el resto.</summary>
    public static bool TryParseVerdict(string? raw, out ReconcileVerdict verdict)
    {
        switch ((raw ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "presente": verdict = ReconcileVerdict.Presente; return true;
            case "arreglado": verdict = ReconcileVerdict.Arreglado; return true;
            case "no-verificable":
            case "no verificable": verdict = ReconcileVerdict.NoVerificable; return true;
            default: verdict = default; return false;
        }
    }
}
