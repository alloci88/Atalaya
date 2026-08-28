using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Ingestion;
using Atalaya.Domain.Model;
using Atalaya.Inventory;

namespace Atalaya.App.Services;

/// <summary>
/// Qué hizo una reconciliación de hallazgos medidos (F5.16). Se devuelve para contarlo: una
/// resolución que no se narra es indistinguible de un borrado, y ese fue justo el susto que abrió
/// esta tanda.
/// </summary>
public sealed record MeasuredReconciliation(
    IReadOnlyList<string> Resolved,
    IReadOnlyList<string> Created,
    IReadOnlyList<string> Reopened)
{
    public static MeasuredReconciliation Empty { get; } =
        new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    public int Total => Resolved.Count + Created.Count + Reopened.Count;

    /// <summary>La frase del toast. Vacía cuando no pasó nada — no hay nada que anunciar.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Resolved.Count > 0)
            {
                parts.Add(Resolved.Count == 1
                    ? "1 unidad salió de Grandes; su hallazgo se resolvió"
                    : $"{Resolved.Count} unidades salieron de Grandes; sus hallazgos se resolvieron");
            }

            if (Created.Count > 0)
            {
                parts.Add(Created.Count == 1
                    ? "1 unidad nueva supera el umbral: hallazgo creado"
                    : $"{Created.Count} unidades nuevas superan el umbral: hallazgos creados");
            }

            if (Reopened.Count > 0)
            {
                parts.Add(Reopened.Count == 1
                    ? "1 unidad volvió a superar el umbral: hallazgo reabierto"
                    : $"{Reopened.Count} unidades volvieron a superar el umbral: hallazgos reabiertos");
            }

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// El ciclo de vida de los hallazgos que MIDE la aplicación (F5.16).
/// <para>
/// <b>El agujero que cierra.</b> El re-escaneo actualizaba el inventario —una unidad troceada salía
/// de «Grandes»— y no tocaba los hallazgos. El hallazgo automático de esa unidad se quedaba activo
/// para siempre, describiendo un tamaño que ya no existía. Al usuario le pareció que había
/// desaparecido; lo que había desaparecido era su motivo, no el fichero (ver DECISIONS, F5.16 §0).
/// </para>
/// <para>
/// <b>La regla.</b> Cada hallazgo se verifica con el instrumento que lo detectó. Estos los detecta
/// una medida, así que una medida los resuelve y una medida los reabre — nunca un LLM, nunca por
/// omisión, y siempre con el número escrito en el historial.
/// </para>
/// </summary>
public sealed class MeasuredFindingService
{
    private readonly HubContext _hub;
    private readonly FindingIngestionService _ingestion;
    private readonly MachineConfigStore _machines;

    public MeasuredFindingService(
        HubContext hub, FindingIngestionService ingestion, MachineConfigStore machines)
    {
        _hub = hub;
        _ingestion = ingestion;
        _machines = machines;
    }

    /// <summary>
    /// Pone al día los hallazgos de tamaño contra el inventario recién medido.
    /// <para>
    /// Trabaja sobre <paramref name="inventory"/> y no volviendo a leer el disco: es el MISMO
    /// escaneo que acaba de decidir qué unidades son grandes, así que inventario y hallazgos no
    /// pueden discrepar ni por una carrera ni por un umbral leído dos veces.
    /// </para>
    /// </summary>
    public MeasuredReconciliation Reconcile(string slug, InventoryCycle inventory, string clonePath)
    {
        AppConfig? app = _hub.Store.TryReadApp(slug);
        if (app is null)
        {
            return MeasuredReconciliation.Empty;
        }

        string commit = GitInfo.HeadSha(clonePath);
        string by = _hub.ResolveIdentity().Name;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // F8.1: cultura explícita. Esta fecha se escribe en la EVIDENCIA de un hallazgo, que va al
        // hub y la lee todo el equipo — no puede depender de la máquina que hizo el re-escaneo.
        string when = now.ToLocalTime().ToString("dd/MM/yyyy HH:mm", AppCulture.Display);

        var units = inventory.Units.ToDictionary(u => u.Path, StringComparer.Ordinal);
        var large = new HashSet<string>(
            inventory.Units.Where(u => u.State == UnitState.Grande).Select(u => u.Path),
            StringComparer.Ordinal);

        var findings = _hub.Store.ListFindings(slug)
            .Where(f => UnitMeasure.IsMeasured(f.RuleId))
            .ToList();

        var resolved = new List<string>();
        var created = new List<string>();
        var reopened = new List<string>();

        foreach (Finding f in findings)
        {
            if (f.Locations.Count == 0)
            {
                continue;
            }

            string path = f.Locations[0].Path;

            // Fuera del inventario = no medida en este escaneo (excluida, borrada, renombrada). No
            // se toca: «no la he visto» no es «ya no es grande».
            if (!units.TryGetValue(path, out InventoryUnit? unit))
            {
                continue;
            }

            bool isLarge = large.Contains(path);

            if (f.Status == FindingStatus.Activo && !isLarge)
            {
                string evidence = $"re-escaneo {when}: {unit.Loc} LOC < umbral {app.Thresholds.LargeUnitLoc}"
                    + $", commit del clon {commit}";
                f.Resolve(new ResolutionStamp(now, ResolutionVia.Medida, AuditMode.Verify, commit, by, evidence));
                ClearNeedsReview(f, now, by, "resuelto por medición");
                _hub.Store.WriteFinding(slug, f);
                resolved.Add($"{Alias(f)} {path} ({unit.Loc} LOC)");
                continue;
            }

            if (f.Status == FindingStatus.Resuelto && isLarge)
            {
                string evidence = $"re-escaneo {when}: {unit.Loc} LOC vuelve a superar el umbral "
                    + $"{app.Thresholds.LargeUnitLoc}, commit del clon {commit}";
                f.Reopen(now, by, evidence);
                f.NeedsReview = false;
                _hub.Store.WriteFinding(slug, f);
                reopened.Add($"{Alias(f)} {path} ({unit.Loc} LOC)");
            }
        }

        // Unidades grandes sin ningún hallazgo (ni activo ni resuelto): nacen ahora.
        var covered = new HashSet<string>(
            findings.Where(f => f.Locations.Count > 0).Select(f => f.Locations[0].Path),
            StringComparer.Ordinal);

        // Nacen en modo LOTES, igual que las del escaneo inicial: es el modo con el que la
        // aplicación detecta por medida, y le da a un hallazgo nuevo confianza media. `Verify` no
        // vale — el dominio se niega, y con razón: verificar no crea hallazgos, comprueba los que
        // hay. Que el dominio parara esto en el primer test es exactamente para lo que está.
        var stamp = new DetectionStamp(now, AuditMode.Lotes, commit, by);
        foreach (string path in large.Where(p => !covered.Contains(p)).OrderBy(p => p, StringComparer.Ordinal))
        {
            InventoryUnit unit = units[path];
            SubmittedFinding submitted = InventoryScanner.BuildLargeUnitFinding(path, unit.Loc, app.Thresholds);
            Finding f = _ingestion.Create(submitted, slug, AuditMode.Lotes, stamp);
            created.Add($"{Alias(f)} {path} ({unit.Loc} LOC)");
        }

        return new MeasuredReconciliation(resolved, created, reopened);
    }

    /// <summary>
    /// Vuelve a medir UN hallazgo y aplica el veredicto (F5.16). Es lo que «Verificar ahora» hace
    /// cuando el hallazgo es medido: ni prompt, ni agente, ni tokens — una lectura del fichero.
    /// </summary>
    public MeasuredVerdict Verify(string slug, Finding finding)
    {
        AppConfig? app = _hub.Store.TryReadApp(slug);
        if (app is null || finding.Locations.Count == 0)
        {
            return new MeasuredVerdict(false, "No se pudo medir: el hallazgo no tiene ubicación.");
        }

        string? clone = _machines.Load().ClonePathFor(slug);
        string path = finding.Locations[0].Path;
        UnitMeasurement m = UnitMeasure.Measure(clone, path, app.Thresholds);

        if (!m.Measured)
        {
            // Incertidumbre declarada: NO se toca el hallazgo. Marcarlo «por revisar» aquí sería
            // repetir el error que originó el parte — ensuciar el estado por no poder medir.
            return new MeasuredVerdict(false, m.Problem ?? "no se pudo medir");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string commit = GitInfo.HeadSha(clone);
        string by = _hub.ResolveIdentity().Name;
        string measured = m.Describe(app.Thresholds);

        if (m.Exceeds)
        {
            if (finding.Status == FindingStatus.Resuelto)
            {
                finding.Reopen(now, by, $"re-medición: {measured}, commit del clon {commit}");
            }
            else
            {
                finding.Confirm(AuditMode.Verify, new DetectionStamp(now, AuditMode.Verify, commit, by));
            }

            ClearNeedsReview(finding, now, by, "confirmado por medición");
            _hub.Store.WriteFinding(slug, finding);
            _hub.Sync?.CommitAndPush($"measure: {slug} {Alias(finding)} confirmado");
            return new MeasuredVerdict(true, $"Confirmado: {measured}.");
        }

        finding.Resolve(new ResolutionStamp(
            now, ResolutionVia.Medida, AuditMode.Verify, commit, by,
            $"re-medición: {measured}, commit del clon {commit}"));
        ClearNeedsReview(finding, now, by, "resuelto por medición");
        _hub.Store.WriteFinding(slug, finding);
        _hub.Sync?.CommitAndPush($"measure: {slug} {Alias(finding)} resuelto");
        return new MeasuredVerdict(true, $"Resuelto: {measured}.");
    }

    /// <summary>
    /// Una medida cierra la incertidumbre que dejó el instrumento equivocado (F5.16).
    /// <para>
    /// <c>needsReview</c> significaba «el verificador no supo decidir esto». Cuando la medida
    /// responde —en cualquiera de los dos sentidos— esa duda ya no existe, y dejarla puesta manda
    /// al usuario a revisar a mano algo que la aplicación acaba de contar. El historial conserva
    /// las dos entradas: que se dudó y que se midió.
    /// </para>
    /// </summary>
    private static void ClearNeedsReview(Finding f, DateTimeOffset utc, string by, string reason)
    {
        if (!f.NeedsReview)
        {
            return;
        }

        f.NeedsReview = false;
        f.History.Add(new HistoryEntry(utc, FindingEvent.Reopened, by,
            $"«por revisar» retirado: {reason}"));
    }

    private static string Alias(Finding f) => f.DisplayId ?? f.Id.ToString();
}

/// <summary>El veredicto de una re-medición, con la frase que verá el usuario.</summary>
/// <param name="Applied">Se midió y se aplicó. False = no se tocó nada, y <paramref name="Message"/> dice por qué.</param>
public sealed record MeasuredVerdict(bool Applied, string Message);
