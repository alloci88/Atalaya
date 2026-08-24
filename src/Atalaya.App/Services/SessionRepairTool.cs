using Atalaya.Domain;
using Atalaya.Domain.Fingerprinting;
using Atalaya.Domain.Ingestion;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>Resultado de una reparación puntual de sesión (F3.1 Bloque 1).</summary>
public sealed record SessionRepairReport(
    string SessionId,
    IReadOnlyList<SessionRepairPair> Pairs,
    IReadOnlyList<string> Skipped);

/// <summary>Par reparado por <see cref="SessionRepairTool"/> (F3.1 Bloque 1).</summary>
public sealed record SessionRepairPair(
    string ReopenedFindingUlid,
    string DeletedDuplicateUlid,
    string OldFingerprint,
    string NewFingerprint,
    double MatchScore);

/// <summary>
/// Absorción propuesta por <see cref="SessionRepairTool.PlanConsolidation"/>: un canónico
/// que se conserva y una lista de miembros a fusionar/borrar (F3.1 Bloque 1b, D-074).
/// </summary>
public sealed record ConsolidationCluster(
    string CanonicalUlid,
    string CanonicalTitle,
    string CanonicalFingerprint,
    IReadOnlyList<string> MergeUlids,
    IReadOnlyList<string> MergeFingerprints);

/// <summary>
/// Purga propuesta por el planner: fichero de finding a eliminar sin fusionar (residuo fixture
/// "test"/"test5" identificado por título). Se lista aparte de <see cref="ConsolidationCluster"/>
/// para que el operador pueda vetarlas independientemente.
/// </summary>
public sealed record ConsolidationPurge(string Ulid, string Title, string Reason);

/// <summary>
/// Plan generado por <see cref="SessionRepairTool.PlanConsolidation"/>. Es puro (no toca disco),
/// pensado para pintarse por consola y aprobarse antes de <see cref="SessionRepairTool.Apply"/>.
/// </summary>
public sealed record ConsolidationPlan(
    string Slug,
    DateTimeOffset PlannedUtc,
    IReadOnlyList<ConsolidationCluster> Clusters,
    IReadOnlyList<ConsolidationPurge> Purges);

/// <summary>Resultado de <see cref="SessionRepairTool.Apply"/>.</summary>
public sealed record ConsolidationResult(
    int ClustersConsolidated,
    int FindingsAbsorbed,
    int FindingsPurged);

/// <summary>
/// F3.1 Bloque 1 · Repair de sesión. F3.1 Bloque 1b (D-073, D-074) · consolidación
/// multi-generación con dry-run obligatorio.
/// <para>
/// Repair de UNA sesión: para cada activo creado por la sesión objetivo, corre
/// <see cref="SecondPassMatcher"/> contra el catálogo y —si encuentra un resuelto legítimo del
/// mismo problema— lo reabre y borra el duplicado. A diferencia de la versión F3.1 original NO
/// sobrescribe <see cref="Finding.Fingerprint"/> del reabierto (D-071): ese hash era volátil
/// (dependía del <c>ruleId</c>/<c>symbol</c> que el LLM emitió en la sesión mala) y las sesiones
/// posteriores volvían a divergir. Ahora el reabierto conserva su fingerprint canónico y AMBOS
/// hashes históricos (el original y el del duplicado) se acumulan en
/// <see cref="Finding.PreviousFingerprints"/>. Así <c>HubStore.FindByFingerprint</c> encuentra
/// al reabierto sea cual sea el hash que la próxima sesión calcule.
/// </para>
/// <para>
/// Consolidación: agrupa hallazgos por ruta y cluster por Jaccard≥0,5 del título; escoge
/// canónico (activo si lo hay, si no el más antiguo) y absorbe el resto en su
/// <see cref="Finding.PreviousFingerprints"/> + <see cref="Finding.History"/>. Es destructiva
/// (mueve historia, borra ficheros), por eso <see cref="PlanConsolidation"/> es puro y
/// <see cref="Apply"/> exige el plan como parámetro. Idempotente por construcción.
/// </para>
/// </summary>
public sealed class SessionRepairTool
{
    private readonly HubContext _hub;

    public SessionRepairTool(HubContext hub) => _hub = hub;

    // ---------------------------------------------------------------------------------------
    // Repair de UNA sesión — se mantiene la API pública para no romper `--repair-session`.
    // ---------------------------------------------------------------------------------------

    public SessionRepairReport Repair(string slug, string sessionUlid, DateTimeOffset now, string by)
    {
        AuditSession session = _hub.Store.ListSessions(slug).FirstOrDefault(s => s.Id.ToString() == sessionUlid)
            ?? throw new InvalidOperationException($"Sesión '{sessionUlid}' no encontrada en '{slug}'.");

        DateTimeOffset from = session.StartedUtc;
        DateTimeOffset to = (session.EndedUtc ?? session.StartedUtc).AddMinutes(1);
        HashSet<string> auditedPaths = session.Units
            .Where(u => u.Verdict == "auditada")
            .Select(u => Fingerprint.NormalizePath(u.Unit))
            .ToHashSet(StringComparer.Ordinal);

        var allFindings = _hub.Store.ListFindings(slug);
        var candidateNews = allFindings
            .Where(f => f.Status == FindingStatus.Activo
                        && f.FirstDetected.Utc >= from && f.FirstDetected.Utc <= to
                        && f.Locations.Count > 0
                        && auditedPaths.Contains(Fingerprint.NormalizePath(f.Locations[0].Path)))
            .ToList();

        var pairs = new List<SessionRepairPair>();
        var skipped = new List<string>();

        foreach (Finding neu in candidateNews)
        {
            var pool = allFindings.Where(f => f.Id != neu.Id).ToList();

            var submitted = new SubmittedFinding(
                neu.RuleId, neu.Pillar, neu.Tag, neu.Severity,
                neu.Title, neu.Description, neu.Impact, neu.Recommendation,
                neu.Locations, Symbol: null);

            SecondPassMatch? match = SecondPassMatcher.TryMatch(submitted, pool);
            if (match is null || match.Finding.Status != FindingStatus.Resuelto)
            {
                skipped.Add(neu.Id.ToString());
                continue;
            }

            Finding old = match.Finding;
            string canonicalFp = old.Fingerprint;   // se conserva (D-071)
            string duplicateFp = neu.Fingerprint;

            // El fp del duplicado entra en el linaje: cualquier futura ingestión que vuelva a
            // producir ese hash encuentra al reabierto vía HubStore.FindByFingerprint.
            if (!old.PreviousFingerprints.Contains(duplicateFp) && duplicateFp != canonicalFp)
            {
                old.PreviousFingerprints.Add(duplicateFp);
            }

            old.Reopen(now, by,
                $"repair-session {sessionUlid}: duplicado {neu.Id} re-vinculado (score={match.Score:0.00})");
            old.History.Add(new HistoryEntry(now, FindingEvent.Confirmed, by,
                $"repair-session {sessionUlid}: absorbe fp del duplicado {Short(duplicateFp)} en PreviousFingerprints"));

            // Adopta las locations del duplicado (línea drifted).
            old.Locations.Clear();
            foreach (Location loc in neu.Locations)
            {
                old.Locations.Add(loc);
            }

            _hub.Store.WriteFinding(slug, old);

            string dupPath = _hub.HubPaths.FindingFile(slug, neu.Id.ToString());
            if (File.Exists(dupPath))
            {
                File.Delete(dupPath);
            }

            pairs.Add(new SessionRepairPair(old.Id.ToString(), neu.Id.ToString(), canonicalFp, canonicalFp, match.Score));
        }

        return new SessionRepairReport(sessionUlid, pairs, skipped);
    }

    // ---------------------------------------------------------------------------------------
    // Consolidación multi-generación (D-074).
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Genera un plan de consolidación para la app. No escribe nada. Agrupa por ruta normalizada
    /// y clusteriza por Jaccard≥<see cref="SecondPassMatcher.Threshold"/> del título; escoge
    /// canónico (activo si lo hay; si no el más antiguo por <c>FirstDetected</c>). Además lista
    /// como purgas los residuos fixture cuyos títulos son "test", "test5" o similares (D-067
    /// documentada pero no ejecutada — D-072).
    /// </summary>
    public ConsolidationPlan PlanConsolidation(string slug, DateTimeOffset? nowUtc = null)
    {
        IReadOnlyList<Finding> all = _hub.Store.ListFindings(slug);

        // Purgas: títulos residuo. No consumen del baseline de clustering.
        var purges = new List<ConsolidationPurge>();
        var remaining = new List<Finding>(all.Count);
        foreach (Finding f in all)
        {
            if (IsFixtureResidue(f.Title))
            {
                purges.Add(new ConsolidationPurge(f.Id.ToString(), f.Title, "fixture residue"));
            }
            else
            {
                remaining.Add(f);
            }
        }

        var clusters = new List<ConsolidationCluster>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        // Determinismo: recorremos por FirstDetected ascendente, así el "semilla" es el más viejo.
        var ordered = remaining
            .Where(f => f.Locations.Count > 0)
            .OrderBy(f => f.FirstDetected.Utc)
            .ThenBy(f => f.Id.ToString(), StringComparer.Ordinal)
            .ToList();

        foreach (Finding seed in ordered)
        {
            if (!visited.Add(seed.Id.ToString()))
            {
                continue;
            }

            string seedPath = Fingerprint.NormalizePath(seed.Locations[0].Path);
            HashSet<string> seedTokens = SecondPassMatcher.Tokenize(seed.Title);
            if (seedTokens.Count == 0)
            {
                continue;
            }

            var members = new List<Finding> { seed };
            foreach (Finding other in ordered)
            {
                if (visited.Contains(other.Id.ToString()))
                {
                    continue;
                }

                if (other.Locations.Count == 0
                    || Fingerprint.NormalizePath(other.Locations[0].Path) != seedPath)
                {
                    continue;
                }

                HashSet<string> otherTokens = SecondPassMatcher.Tokenize(other.Title);
                if (Jaccard(seedTokens, otherTokens) < SecondPassMatcher.Threshold)
                {
                    continue;
                }

                members.Add(other);
                visited.Add(other.Id.ToString());
            }

            if (members.Count < 2)
            {
                continue;   // singleton: nada que consolidar (idempotencia)
            }

            Finding canonical = ChooseCanonical(members);
            var merges = members.Where(m => m.Id != canonical.Id).ToList();
            clusters.Add(new ConsolidationCluster(
                canonical.Id.ToString(),
                canonical.Title,
                canonical.Fingerprint,
                merges.Select(m => m.Id.ToString()).ToArray(),
                merges.Select(m => m.Fingerprint).ToArray()));
        }

        return new ConsolidationPlan(slug, nowUtc ?? DateTimeOffset.UtcNow, clusters, purges);
    }

    /// <summary>
    /// Ejecuta un plan de consolidación previamente generado. Absorbe cada miembro no-canónico
    /// en el canónico (acumula <see cref="Finding.Fingerprint"/> + <see cref="Finding.PreviousFingerprints"/>
    /// y prepende su <see cref="Finding.History"/>) y borra su fichero. Aplica las purgas listadas.
    /// Verifica en disco tras cada operación y falla si algo no cuadra (D-072).
    /// </summary>
    public ConsolidationResult Apply(ConsolidationPlan plan, DateTimeOffset now, string by)
    {
        int absorbed = 0;
        foreach (ConsolidationCluster cluster in plan.Clusters)
        {
            Finding? canonical = _hub.Store.TryReadFinding(plan.Slug, cluster.CanonicalUlid);
            if (canonical is null)
            {
                throw new InvalidOperationException(
                    $"consolidation: canónico {cluster.CanonicalUlid} no está en disco.");
            }

            foreach (string mergeUlid in cluster.MergeUlids)
            {
                Finding? member = _hub.Store.TryReadFinding(plan.Slug, mergeUlid);
                if (member is null)
                {
                    continue;   // idempotencia: ya absorbido en una ejecución previa
                }

                AbsorbInto(canonical, member, now, by);
                absorbed++;
            }

            _hub.Store.WriteFinding(plan.Slug, canonical);

            // D-072: verifica en disco tras escribir.
            foreach (string mergeUlid in cluster.MergeUlids)
            {
                string path = _hub.HubPaths.FindingFile(plan.Slug, mergeUlid);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                if (File.Exists(path))
                {
                    throw new InvalidOperationException(
                        $"consolidation: no pude borrar {path} — abortando para no dejar estado inconsistente.");
                }
            }

            Finding? verify = _hub.Store.TryReadFinding(plan.Slug, cluster.CanonicalUlid);
            if (verify is null
                || cluster.MergeFingerprints.Any(fp => fp != canonical.Fingerprint
                    && !verify.PreviousFingerprints.Contains(fp)))
            {
                throw new InvalidOperationException(
                    $"consolidation: canónico {cluster.CanonicalUlid} no absorbió todos los fps esperados.");
            }
        }

        int purged = 0;
        foreach (ConsolidationPurge purge in plan.Purges)
        {
            string path = _hub.HubPaths.FindingFile(plan.Slug, purge.Ulid);
            if (File.Exists(path))
            {
                File.Delete(path);
                purged++;
            }

            if (File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"purge: no pude borrar {path} — abortando.");
            }
        }

        return new ConsolidationResult(plan.Clusters.Count, absorbed, purged);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    /// <summary>Canónico: activo antes que resuelto/silenciado, empatando por FirstDetected asc.</summary>
    private static Finding ChooseCanonical(IEnumerable<Finding> members)
        => members
            .OrderByDescending(m => m.Status == FindingStatus.Activo)
            .ThenBy(m => m.FirstDetected.Utc)
            .ThenBy(m => m.Id.ToString(), StringComparer.Ordinal)
            .First();

    /// <summary>
    /// Absorbe <paramref name="member"/> en <paramref name="canonical"/>: el fp y todos los prev
    /// del miembro entran en <see cref="Finding.PreviousFingerprints"/> del canónico; su history
    /// se anota con un prefijo trazable.
    /// </summary>
    private static void AbsorbInto(Finding canonical, Finding member, DateTimeOffset now, string by)
    {
        if (member.Fingerprint != canonical.Fingerprint
            && !canonical.PreviousFingerprints.Contains(member.Fingerprint))
        {
            canonical.PreviousFingerprints.Add(member.Fingerprint);
        }

        foreach (string prev in member.PreviousFingerprints)
        {
            if (prev != canonical.Fingerprint && !canonical.PreviousFingerprints.Contains(prev))
            {
                canonical.PreviousFingerprints.Add(prev);
            }
        }

        canonical.TimesConfirmed += member.TimesConfirmed;

        canonical.History.Add(new HistoryEntry(now, FindingEvent.Confirmed, by,
            $"consolidation: absorbe {member.Id} (fp {Short(member.Fingerprint)}, status={member.Status})"));

        foreach (HistoryEntry entry in member.History)
        {
            canonical.History.Add(new HistoryEntry(
                entry.Utc, entry.Event, entry.By,
                $"[from {member.Id}] {entry.Detail}"));
        }
    }

    private static bool IsFixtureResidue(string title)
    {
        string t = (title ?? string.Empty).Trim().ToLowerInvariant();
        return t.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(t, @"^test\d*$");
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        int intersection = 0;
        foreach (string s in a)
        {
            if (b.Contains(s))
            {
                intersection++;
            }
        }

        int union = a.Count + b.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static string Short(string fp)
    {
        string body = fp.StartsWith("sha256:", StringComparison.Ordinal) ? fp[7..] : fp;
        return body.Length > 12 ? body[..12] : body;
    }
}
