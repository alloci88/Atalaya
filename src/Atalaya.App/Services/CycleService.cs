using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// Cycle close (§5.1): promotes confidence media→alta for findings confirmed in the cycle, opens
/// the next cycle inventory (all pendiente, grandes re-evaluated), and records a `cierre` session.
/// Concurrency: only whoever still sees 0-pending after a fresh pull closes; if another user already
/// advanced the cycle, this desists.
/// </summary>
public sealed class CycleService
{
    private readonly HubContext _hub;
    private readonly IUlidFactory _ulids;

    public CycleService(HubContext hub, IUlidFactory ulids)
    {
        _hub = hub;
        _ulids = ulids;
    }

    /// <summary>Attempts to close <paramref name="expectedCycle"/>. Returns true only if it closed it.</summary>
    public bool TryCloseCycle(string slug, int expectedCycle)
    {
        // Re-sync so the concurrency check sees the latest state (§5.1).
        _hub.Sync?.Pull();

        AppConfig? app = _hub.Store.TryReadApp(slug);
        if (app is null || app.CurrentCycle != expectedCycle)
        {
            return false; // someone already advanced the cycle — desist
        }

        InventoryCycle? inv = _hub.Store.TryReadInventory(slug, expectedCycle);
        if (inv is null || inv.HasPending)
        {
            return false; // still pending (or gone) — nothing to close
        }

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

        // Open the next cycle: everything pendiente; large re-evaluated against the threshold.
        int next = expectedCycle + 1;
        var fresh = new InventoryCycle { CycleN = next };
        foreach (InventoryUnit u in inv.Units)
        {
            bool large = u.Loc > app.Thresholds.LargeUnitLoc;
            fresh.Units.Add(new InventoryUnit
            {
                Path = u.Path,
                Module = u.Module,
                Loc = u.Loc,
                ContentHash = u.ContentHash,
                State = large ? UnitState.Grande : UnitState.Pendiente,
            });
        }

        app.CurrentCycle = next;
        _hub.Store.WriteApp(app);
        _hub.Store.WriteInventory(slug, fresh);

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

        string report = ReportBuilder.BuildCycleCloseReport(app, expectedCycle, promoted, _hub.Store.ListFindings(slug));
        _hub.Store.WriteReport(slug, sessionId.ToString(), report);

        _hub.Sync?.CommitAndPush($"cierre: {slug} ciclo {expectedCycle}→{next}");
        return true;
    }
}
