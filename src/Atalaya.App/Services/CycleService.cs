using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;

namespace Atalaya.App.Services;

/// <summary>El resultado de intentar cerrar un ciclo: si se cerró y qué foto dejó (F9.2 §2).</summary>
/// <param name="Closed">Solo true para QUIEN lo cerró. Si otro se adelantó, este desiste.</param>
/// <param name="Aging">Lo que quedaba envejecido en el momento del cierre.</param>
public sealed record CycleCloseResult(bool Closed, CycleAging Aging)
{
    /// <summary>No se cerró: o ya lo cerró otro, o queda pendiente.</summary>
    public static CycleCloseResult NotClosed { get; } = new(false, CycleAging.None);
}

/// <summary>
/// Cycle close (§5.1): promotes confidence media→alta for findings confirmed in the cycle, opens
/// the next cycle inventory and records a `cierre` session.
/// Concurrency: only whoever still sees 0-pending after a fresh pull closes; if another user already
/// advanced the cycle, this desists.
/// <para>
/// <b>El ciclo nuevo no nace todo pendiente: se SIEMBRA</b> (F9.2 §1, <see cref="CycleSeeding"/>).
/// La deriva acumulada durante el ciclo —que no reabrió nada mientras duró— se cobra justo aquí, en
/// la frontera: lo que cambió desde su auditoría entra pendiente, lo que no cambió conserva su
/// auditoría, y lo que está arreglado a falta de verificar conserva su acción.
/// </para>
/// </summary>
public sealed class CycleService
{
    private readonly HubContext _hub;
    private readonly IUlidFactory _ulids;
    private readonly DriftQuery? _drift;
    private readonly MachineConfigStore? _machines;

    /// <param name="drift">
    /// De dónde sale la siembra. Sin él —o sin clon en esta máquina— no hay historial con el que
    /// demostrar que nada cambió, y el ciclo nuevo nace entero pendiente: es el mismo caso que «sin
    /// historial disponible», y la dirección segura es re-auditar de más, nunca de menos.
    /// </param>
    public CycleService(
        HubContext hub, IUlidFactory ulids, DriftQuery? drift = null, MachineConfigStore? machines = null)
    {
        _hub = hub;
        _ulids = ulids;
        _drift = drift;
        _machines = machines;
    }

    /// <summary>
    /// Attempts to close <paramref name="expectedCycle"/>. <see cref="CycleCloseResult.Closed"/> es
    /// true solo para quien lo cerró de verdad, y viene con la foto de lo que quedó envejecido.
    /// </summary>
    public CycleCloseResult TryCloseCycle(string slug, int expectedCycle)
    {
        // Re-sync so the concurrency check sees the latest state (§5.1).
        _hub.Sync?.Pull();

        AppConfig? app = _hub.Store.TryReadApp(slug);
        if (app is null || app.CurrentCycle != expectedCycle)
        {
            return CycleCloseResult.NotClosed; // someone already advanced the cycle — desist
        }

        InventoryCycle? inv = _hub.Store.TryReadInventory(slug, expectedCycle);
        if (inv is null || inv.HasPending)
        {
            return CycleCloseResult.NotClosed; // still pending (or gone) — nothing to close
        }

        // La deriva del ciclo que TERMINA, medida antes de tocar nada: es a la vez la semilla del
        // inventario nuevo y la foto honesta del cierre. Una sola lectura para las dos cosas — dos
        // lecturas separadas es exactamente cómo un panel y su informe acaban diciendo cifras
        // distintas del mismo instante.
        AppDrift? drift = MeasureDrift(slug);
        CycleAging aging = CycleSeeding.AgingOf(drift);

        string by = _hub.ResolveIdentity().Name;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var stamp = new DetectionStamp(now, AuditMode.Cierre, "cierre", by);

        // Promote media → alta for active findings (confirmed within the cycle).
        int promoted = 0;
        foreach (Finding f in _hub.Store.ListFindings(slug)
                     .Where(f => f.Status == FindingStatus.Activo && f.Confidence == Confidence.Media))
        {
            f.Confirm(AuditMode.Cierre, stamp, cycleClose: true);
            _hub.Store.WriteFinding(slug, f);
            promoted++;
        }

        int next = expectedCycle + 1;
        InventoryCycle fresh = CycleSeeding.Seed(inv, next, app.Thresholds.LargeUnitLoc, drift);

        app.CurrentCycle = next;
        _hub.Store.WriteApp(app);
        _hub.Store.WriteInventory(slug, fresh);

        // El inventario vigente ha cambiado bajo los pies de la caché, y su clave mira el del ciclo
        // en curso: sin esto el panel enseñaría la deriva del ciclo que acaba de cerrarse.
        _drift?.Invalidate();

        Ulid sessionId = _ulids.NewUlid();
        _hub.Store.WriteSession(new AuditSession
        {
            Id = sessionId,
            AppSlug = slug,
            Mode = AuditMode.Cierre,
            By = by,
            Machine = Environment.MachineName,
            StartedUtc = now,
            EndedUtc = now,
            CycleN = next,
            Counters = new SessionCounters { Confirmed = promoted },
        });

        string report = ReportBuilder.BuildCycleCloseReport(
            app, expectedCycle, promoted, _hub.Store.ListFindings(slug), _hub.OrganizationName, aging);
        _hub.Store.WriteReport(slug, sessionId.ToString(), report);

        _hub.Sync?.CommitAndPush($"cierre: {slug} ciclo {expectedCycle}→{next}");
        return new CycleCloseResult(true, aging);
    }

    /// <summary>
    /// La deriva contra el clon de ESTA máquina, o null si no hay con qué medirla. Nunca lanza: un
    /// historial ilegible no puede impedir cerrar un ciclo que ya está auditado entero — se cierra
    /// sembrando pendiente, que es la lectura honesta de «no se ha podido comprobar».
    /// </summary>
    private AppDrift? MeasureDrift(string slug)
    {
        if (_drift is null)
        {
            return null;
        }

        try
        {
            string? clone = _machines?.Load().ClonePathFor(slug);
            return _drift.For(slug, clone);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
