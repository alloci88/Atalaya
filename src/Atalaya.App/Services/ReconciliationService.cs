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

    /// <summary>
    /// El auditor dijo «arreglado» pero NO hay evidencia de que la unidad cambiara desde la última
    /// vez que se vio el hallazgo (F5.1b): se degrada a «presente» y no se resuelve nada.
    /// </summary>
    ResolutionRefused,

    /// <summary>
    /// El auditor sostiene que nunca fue un defecto (F5.1b): el hallazgo queda marcado como
    /// disputado, sin resolverse ni desactivarse, a la espera de una decisión humana.
    /// </summary>
    Disputed,
}

/// <summary>
/// Aplica los veredictos de reconciliación del auditor (F4) a los hallazgos, por ULID.
/// <para>
/// Es el único camino por el que un hallazgo previo cambia de estado durante una auditoría. No
/// existe la resolución implícita: si el auditor no se pronuncia, el hallazgo no se toca y la
/// unidad queda marcada como incompleta. Esto es lo que mata el ciclo duplicar→resolver que
/// arrastraba la saga del fingerprint: resolver exige una afirmación explícita.
/// </para>
/// <para>
/// F5.1b: y además exige EVIDENCIA DE CAMBIO. Una afirmación explícita seguía sin ser prueba —
/// el 2026-08-25 un modelo declaró «arreglado» un hallazgo sobre el mismo commit en el que se
/// había detectado, y la app lo cerró. Ver <see cref="UnchangedSinceLastSighting"/>.
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
        => Apply(slug, finding, verdict, evidence, mode, stamp, out _);

    /// <summary>
    /// Igual que la sobrecarga corta, devolviendo además POR QUÉ se degradó un «arreglado», para
    /// que la sesión y el informe puedan nombrarlo. Null cuando no hubo degradación.
    /// </summary>
    public ReconcileOutcome Apply(
        string slug, Finding finding, ReconcileVerdict verdict, string evidence,
        AuditMode mode, DetectionStamp stamp, out string? refusalReason)
    {
        refusalReason = null;
        ReconcileOutcome outcome;

        switch (verdict)
        {
            case ReconcileVerdict.Arreglado:
                // GUARDA DE EVIDENCIA DE CAMBIO (F5.1b). Resolver exige que la unidad haya cambiado
                // desde la última vez que se vio el hallazgo. Si podemos PROBAR que no cambió, el
                // veredicto es una contradicción — nada puede haberse arreglado — y se degrada a
                // presente. Nunca se resuelve, y la degradación jamás es silenciosa.
                if (UnchangedSinceLastSighting(finding, stamp, out string? why))
                {
                    refusalReason = why;
                    Present(slug, finding, RefusalEvidence(evidence, why!), mode, stamp);
                    finding.History.Add(new HistoryEntry(stamp.Utc, FindingEvent.Confirmed, stamp.By,
                        $"«arreglado» degradado a presente: {why}"));
                    outcome = ReconcileOutcome.ResolutionRefused;
                }
                else
                {
                    outcome = Resolve(finding, evidence, mode, stamp);
                }

                break;

            case ReconcileVerdict.NoVerificable:
                outcome = NeedsReview(finding, evidence, stamp);
                break;

            case ReconcileVerdict.NoEsDefecto:
                outcome = Dispute(finding, evidence, stamp);
                break;

            default:
                outcome = Present(slug, finding, evidence, mode, stamp);
                break;
        }

        _hub.Store.WriteFinding(slug, finding);
        return outcome;
    }

    /// <summary>
    /// ¿Podemos PROBAR que la unidad no ha cambiado desde la última vez que se vio este hallazgo?
    /// <para>
    /// Dos capas, ambas en la dirección segura: solo devuelven <c>true</c> cuando hay prueba
    /// positiva de que nada cambió, así que degradar por ellas nunca acusa en falso.
    /// </para>
    /// <list type="number">
    /// <item><b>Mismo commit</b> — el árbol auditado es literalmente el mismo.</item>
    /// <item><b>Mismo contentHash de la unidad</b> — el commit avanzó, pero ESTE fichero no cambió.
    /// Sin esta segunda capa, cualquier commit en otra parte del repositorio bastaría para colar
    /// una resolución falsa.</item>
    /// </list>
    /// <para>
    /// Lo que NO cubre, declarado: un hallazgo anterior a F5.1b no tiene <c>unitContentHash</c>
    /// registrado y solo cuenta con la capa del commit; y un árbol de trabajo sucio cambia el
    /// código sin cambiar el commit. En ambos casos la duda favorece al auditor y se resuelve.
    /// </para>
    /// </summary>
    public static bool UnchangedSinceLastSighting(Finding finding, DetectionStamp now, out string? reason)
    {
        DetectionStamp seen = finding.LastConfirmed ?? finding.FirstDetected;

        if (SameCommit(seen.Commit, now.Commit))
        {
            reason = $"mismo commit que la última vez que se vio el hallazgo ({Short(now.Commit)}): "
                + "el código no ha cambiado, así que no puede haberse arreglado";
            return true;
        }

        if (Same(seen.UnitContentHash, now.UnitContentHash))
        {
            reason = "la unidad no ha cambiado desde la última vez que se vio el hallazgo "
                + $"(mismo contenido; commit {Short(seen.Commit)} → {Short(now.Commit)})";
            return true;
        }

        reason = null;
        return false;
    }

    private static bool Same(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
           && string.Equals(a!.Trim(), b!.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Igual que <see cref="Same"/> pero descartando el centinela <c>unknown</c> que
    /// <c>GitInfo.HeadSha</c> devuelve cuando el clon no es un repositorio git.
    /// <para>
    /// Dos «unknown» no prueban que el código sea el mismo: prueban que no lo sabemos. Tratarlos
    /// como prueba bloquearía TODA resolución legítima en un clon sin git, que es exactamente el
    /// falso positivo que esta guarda no puede permitirse.
    /// </para>
    /// </summary>
    private static bool SameCommit(string? a, string? b)
        => !IsUnknownCommit(a) && !IsUnknownCommit(b) && Same(a, b);

    private static bool IsUnknownCommit(string? commit)
        => string.IsNullOrWhiteSpace(commit)
           || string.Equals(commit!.Trim(), "unknown", StringComparison.OrdinalIgnoreCase);

    private static string Short(string? commit)
        => string.IsNullOrWhiteSpace(commit) ? "(sin commit)"
            : commit!.Length <= 10 ? commit : commit[..10];

    private static string RefusalEvidence(string evidence, string why)
        => $"el auditor dijo «arreglado» ({Detail(evidence)}) pero {why}";

    private static ReconcileOutcome Resolve(Finding finding, string evidence, AuditMode mode, DetectionStamp stamp)
    {
        finding.Resolve(new ResolutionStamp(
            stamp.Utc, ResolutionVia.Auditor, mode, stamp.Commit, stamp.By,
            string.IsNullOrWhiteSpace(evidence) ? "arreglado (sin evidencia aportada)" : evidence));
        return ReconcileOutcome.Resolved;
    }

    /// <summary>
    /// «No es un defecto»: una discrepancia de criterio, no una resolución (F5.1b). Marca el
    /// hallazgo como disputado con el razonamiento y el modelo que discrepó, y no toca nada más —
    /// ni el estado, ni la confianza, ni <c>lastConfirmed</c>. La salida del estado es humana.
    /// </summary>
    private static ReconcileOutcome Dispute(Finding finding, string evidence, DetectionStamp stamp)
    {
        finding.Dispute(stamp.Utc, stamp.By, stamp.Model, evidence);
        return ReconcileOutcome.Disputed;
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
            case "no-es-defecto":
            case "no es defecto": verdict = ReconcileVerdict.NoEsDefecto; return true;
            default: verdict = default; return false;
        }
    }
}
