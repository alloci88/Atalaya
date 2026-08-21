using Atalaya.Domain;
using Atalaya.Domain.Model;
using Atalaya.Storage;

namespace Atalaya.App.Services;

/// <summary>A computed portfolio card for one app (§8 V1). Never stored — always derived.</summary>
public sealed record AppCard(
    string Slug,
    string Name,
    TechStack Stack,
    int CurrentCycle,
    int TotalUnits,
    int AuditedUnits,
    int LargeUnits,
    double Progress,
    int Critica,
    int Alta,
    int Media,
    int Baja,
    int ActiveTotal,
    string? LastSessionBy,
    DateTimeOffset? LastSessionUtc,
    bool AuditingNow,
    IReadOnlyList<int> Trend)
{
    /// <summary>Apps with open critical findings sort first (§8 V1).</summary>
    public int SortKey => Critica > 0 ? 0 : ActiveTotal > 0 ? 1 : 2;
}

/// <summary>
/// Computes the portfolio dashboards from primary hub data (mejora 8: dashboards are always
/// calculated; only primary data lives on disk).
/// </summary>
public sealed class PortfolioQuery
{
    private readonly HubStore _store;
    private readonly TimeProvider _time;

    public PortfolioQuery(HubStore store, TimeProvider? time = null)
    {
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyList<AppCard> BuildAll()
        => _store.ListAppSlugs()
            .Select(Build)
            .Where(c => c is not null)
            .Select(c => c!)
            .OrderBy(c => c.SortKey)
            .ThenByDescending(c => c.Critica + c.Alta)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public AppCard? Build(string slug)
    {
        AppConfig? app = _store.TryReadApp(slug);
        if (app is null)
        {
            return null;
        }

        InventoryCycle? inv = _store.TryReadInventory(slug, app.CurrentCycle);
        int total = inv?.Units.Count ?? 0;
        int audited = inv?.Units.Count(u => u.State == UnitState.Auditada) ?? 0;
        int large = inv?.Units.Count(u => u.State == UnitState.Grande) ?? 0;
        int auditable = Math.Max(1, total - large);
        double progress = total == 0 ? 0 : Math.Clamp((double)audited / auditable, 0, 1);

        var findings = _store.ListFindings(slug);
        var active = findings.Where(f => f.Status == FindingStatus.Activo).ToList();
        int Count(Severity s) => active.Count(f => f.Severity == s);

        AuditSession? last = _store.ListSessions(slug)
            .OrderByDescending(s => s.StartedUtc)
            .FirstOrDefault();

        DateTimeOffset now = _time.GetUtcNow();
        bool auditingNow = _store.ListClaims(slug).Any(c => !c.IsExpiredAt(now));

        return new AppCard(
            slug, app.Name, app.Stack, app.CurrentCycle,
            total, audited, large, progress,
            Count(Severity.Critica), Count(Severity.Alta), Count(Severity.Media), Count(Severity.Baja),
            active.Count,
            last?.By, last?.StartedUtc,
            auditingNow,
            BuildTrend(active, now));
    }

    /// <summary>A 30-day sparkline of active findings first detected per day.</summary>
    private static IReadOnlyList<int> BuildTrend(IReadOnlyList<Finding> active, DateTimeOffset now)
    {
        const int days = 30;
        var buckets = new int[days];
        DateTimeOffset start = now.Date.AddDays(-(days - 1));
        foreach (Finding f in active)
        {
            int idx = (int)(f.FirstDetected.Utc.Date - start).TotalDays;
            if (idx is >= 0 and < days)
            {
                buckets[idx]++;
            }
        }

        return buckets;
    }
}
