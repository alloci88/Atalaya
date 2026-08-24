using Atalaya.Domain;
using Atalaya.Domain.Fingerprinting;
using Atalaya.Domain.Ingestion;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>Resultado de una reparación puntual de sesión (F3.1 Bloque 1).</summary>
/// <param name="SessionId">Sesión analizada.</param>
/// <param name="Pairs">Pares (resuelto ↔ duplicado nuevo) detectados y re-vinculados.</param>
/// <param name="Skipped">Nuevos que NO tuvieron pareja fiable (se quedan como están).</param>
public sealed record SessionRepairReport(
    string SessionId,
    IReadOnlyList<SessionRepairPair> Pairs,
    IReadOnlyList<string> Skipped);

/// <summary>Par reparado por <see cref="SessionRepairTool"/>.</summary>
public sealed record SessionRepairPair(
    string ReopenedFindingUlid,
    string DeletedDuplicateUlid,
    string OldFingerprint,
    string NewFingerprint,
    double MatchScore);

/// <summary>
/// F3.1 Bloque 1 — utilidad de reparación puntual para sesiones donde el matching de 2ª pasada
/// habría re-vinculado pares "resuelto → nuevo" pero el episodio ya ocurrió (piloto 2026-08-24
/// sobre <c>CommonStatics.cs</c>: 4 duplicados nuevos y 4 resoluciones falsas del mismo problema).
/// <para>
/// Ejecuta el algoritmo del <see cref="SecondPassMatcher"/> sobre TODOS los hallazgos de la app,
/// tomando como "nuevos" los creados por la sesión objetivo y como candidatos los resueltos
/// previamente. Reabre el resuelto migrando el fingerprint (guarda el antiguo en
/// <see cref="Finding.PreviousFingerprints"/>) y BORRA el duplicado nuevo del disco — la norma
/// "nunca borrar hallazgos" protege datos legítimos, no duplicados creados por un bug de matching
/// del propio pipeline. Traza completa en el <c>History</c> del hallazgo reabierto.
/// </para>
/// <para>
/// La borra es un delete físico del fichero JSON del duplicado (no persistimos "tombstones"; el
/// duplicado apenas vivía). Los sync commits posteriores lo propagan al resto de máquinas.
/// </para>
/// </summary>
public sealed class SessionRepairTool
{
    private readonly HubContext _hub;

    public SessionRepairTool(HubContext hub) => _hub = hub;

    public SessionRepairReport Repair(string slug, string sessionUlid, DateTimeOffset now, string by)
    {
        AuditSession session = _hub.Store.ListSessions(slug).FirstOrDefault(s => s.Id.ToString() == sessionUlid)
            ?? throw new InvalidOperationException($"Sesión '{sessionUlid}' no encontrada en '{slug}'.");

        // "Nuevos" de la sesión = hallazgos cuyo Detected coincide temporalmente con la sesión
        // y viven en unidades que la sesión auditó. Al no persistir el sessionId en el Finding,
        // acotamos por rango temporal [startedUtc, endedUtc + 1min] y por ruta de las unidades
        // auditadas — es lo bastante estrecho para el caso del piloto.
        DateTimeOffset from = session.StartedUtc;
        DateTimeOffset to = session.EndedUtc.AddMinutes(1);
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
            // Buscamos entre los resueltos de la misma unidad, excluyendo el propio "neu".
            var pool = allFindings.Where(f => f.Id != neu.Id).ToList();

            var submitted = new SubmittedFinding(
                neu.RuleId, neu.Pillar, neu.Tag, neu.Severity,
                neu.Title, neu.Description, neu.Impact, neu.Recommendation,
                neu.Locations, symbol: null);

            SecondPassMatch? match = SecondPassMatcher.TryMatch(submitted, pool);
            if (match is null || match.Finding.Status != FindingStatus.Resuelto)
            {
                skipped.Add(neu.Id.ToString());
                continue;
            }

            Finding old = match.Finding;
            string oldFp = old.Fingerprint;
            string newFp = neu.Fingerprint;

            if (!old.PreviousFingerprints.Contains(oldFp))
            {
                old.PreviousFingerprints.Add(oldFp);
            }

            old.Fingerprint = newFp;
            old.Reopen(now, by,
                $"repair-session {sessionUlid}: duplicado {neu.Id} re-vinculado (score={match.Score:0.00})");
            old.History.Add(new HistoryEntry(now, FindingEvent.Confirmed, by,
                $"fingerprint migrated by repair-session {sessionUlid}: {oldFp[..15]}… → {newFp[..15]}…"));
            old.Locations.Clear();
            foreach (Location loc in neu.Locations)
            {
                old.Locations.Add(loc);
            }

            _hub.Store.WriteFinding(slug, old);

            // Borrar el duplicado del disco.
            string dupPath = _hub.HubPaths.FindingFile(slug, neu.Id.ToString());
            if (File.Exists(dupPath))
            {
                File.Delete(dupPath);
            }

            pairs.Add(new SessionRepairPair(old.Id.ToString(), neu.Id.ToString(), oldFp, newFp, match.Score));
        }

        return new SessionRepairReport(sessionUlid, pairs, skipped);
    }
}
