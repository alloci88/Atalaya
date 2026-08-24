using Atalaya.Domain;
using Atalaya.Domain.Fingerprinting;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Ingestion;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// Ingests a submitted finding into a hub app: applies §2 dedupe + the confidence machine
/// and persists the result. Shared by inventory scans (large-unit findings) and live audit
/// sessions (H5). The agent never writes state — this does (mejora 1).
/// </summary>
public sealed class FindingIngestionService
{
    private readonly HubContext _hub;
    private readonly IUlidFactory _ulids;
    private readonly IngestionEngine _engine;

    public FindingIngestionService(HubContext hub, IUlidFactory ulids)
    {
        _hub = hub;
        _ulids = ulids;
        _engine = new IngestionEngine(ulids);
    }

    /// <summary>
    /// Ingests one finding. Returns the outcome so callers can update session counters. Persists
    /// the affected finding (new/recurrence/reconfirmation); suppressed detections write nothing.
    /// </summary>
    public IngestionOutcome Ingest(string slug, SubmittedFinding submitted, AuditMode mode, DetectionStamp stamp)
    {
        string fingerprint = Fingerprint.Compute(
            submitted.RuleId, submitted.PrimaryPath, submitted.Symbol, submitted.Title);

        var existing = _hub.Store.FindByFingerprint(slug, fingerprint);
        Silence? silence = _hub.Store.TryReadSilence(slug, fingerprint);
        Silence? live = silence is not null && silence.IsLiveAt(stamp.Utc) ? silence : null;

        // F3.1 Bloque 1: si no hay match por fingerprint (importado v4 por título vs. nuevo por
        // ruleId → hashes distintos para el MISMO problema), probamos matching de 2ª pasada por
        // ruta + solape de título entre TODOS los hallazgos de la app. Si acierta, tratamos el
        // payload como reconfirmación, migramos el fingerprint al esquema nuevo y guardamos el
        // antiguo en PreviousFingerprints — para que silencios/comentarios registrados con el
        // hash viejo sigan aplicando (HubStore.FindByFingerprint mira ambos).
        // F3.1 Bloque 1b (D-069): la 2ª pasada se ejecuta también cuando `existing` contiene
        // SÓLO resueltos. Sin esto, un baseline con gemelos resueltos cuyo hash coincide con el
        // que el LLM calcula en la nueva sesión cortocircuita el matcher (existing.Count > 0)
        // y la ingestión va por la vía de recurrencia — creando un duplicado aunque el reabierto
        // legítimo esté ahí. Si el matcher encuentra un ACTIVO gana sobre esa vía; si sólo
        // encuentra resueltos, se cae al camino actual (recurrencia).
        bool onlyResolved = existing.Count > 0 && existing.All(f => f.Status == FindingStatus.Resuelto);
        if ((existing.Count == 0 || onlyResolved) && live is null)
        {
            IReadOnlyList<Finding> all = _hub.Store.ListFindings(slug);
            // Prioriza activos: cuando hay un reabierto por 2ª pasada anterior conviviendo con
            // gemelos resueltos del mismo linaje, el activo es SIEMPRE el destino correcto.
            SecondPassMatch? activeMatch = SecondPassMatcher.TryMatch(
                submitted, all.Where(f => f.Status == FindingStatus.Activo).ToList());
            SecondPassMatch? match = activeMatch
                ?? (existing.Count == 0 ? SecondPassMatcher.TryMatch(submitted, all) : null);

            if (match is not null && match.Finding.Fingerprint != fingerprint)
            {
                // Silencio activo bajo el hash VIEJO: sigue suprimiendo la detección.
                Silence? oldSilence = _hub.Store.TryReadSilence(slug, match.Finding.Fingerprint);
                if (oldSilence is not null && oldSilence.IsLiveAt(stamp.Utc))
                {
                    return new IngestionOutcome(IngestionKind.SuppressedBySilence, null, fingerprint, oldSilence);
                }

                Finding migrated = MigrateFingerprint(match.Finding, fingerprint, mode, stamp, match.Score);
                _hub.Store.WriteFinding(slug, migrated);
                return new IngestionOutcome(IngestionKind.Reconfirmed, migrated, fingerprint, null);
            }
        }

        IngestionOutcome outcome = _engine.Ingest(submitted, mode, stamp, existing, live);

        if (outcome.Finding is not null)
        {
            _hub.Store.WriteFinding(slug, outcome.Finding);
        }

        return outcome;
    }

    /// <summary>
    /// F3.1 Bloque 1: migra el fingerprint del hallazgo emparejado por 2ª pasada al esquema nuevo,
    /// preservando el antiguo en <see cref="Finding.PreviousFingerprints"/> y añadiendo una entrada
    /// de historia con el score y el nuevo hash. Si el hallazgo estaba resuelto lo reabre —
    /// el "resuelto" de la sesión anterior era falso (piloto CommonStatics.cs) y debe volver a
    /// activo bajo el nuevo fingerprint. La reconfirmación pasa por <see cref="Finding.Confirm"/>
    /// para que la máquina de confianza actúe igual que en el camino normal.
    /// </summary>
    private static Finding MigrateFingerprint(
        Finding original, string newFingerprint, AuditMode mode, DetectionStamp stamp, double score)
    {
        if (!original.PreviousFingerprints.Contains(original.Fingerprint))
        {
            original.PreviousFingerprints.Add(original.Fingerprint);
        }

        original.Fingerprint = newFingerprint;
        original.History.Add(new HistoryEntry(stamp.Utc, FindingEvent.Confirmed, stamp.By,
            $"fingerprint migrated (2nd-pass match score={score:0.00}) → {newFingerprint[..Math.Min(15, newFingerprint.Length)]}…"));

        if (original.Status == FindingStatus.Resuelto)
        {
            original.Reopen(stamp.Utc, stamp.By, "2nd-pass match: resolución previa era falsa (mismo problema, fingerprint distinto)");
        }

        original.Confirm(mode, stamp);
        return original;
    }
}
