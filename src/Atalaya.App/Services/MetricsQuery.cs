using Atalaya.Domain;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>New vs resolved in a week (burndown).</summary>
public sealed record WeekBucket(string Label, int New, int Resolved);

/// <summary>Per-app active-findings tally.</summary>
public sealed record AppActive(string Slug, int Active);

/// <summary>All V6 metrics (§8), computed from primary data (mejora 8).</summary>
public sealed record MetricsSummary(
    int ActiveTotal, int Critica, int Alta, int Media, int Baja,
    int Resolved, int Silenced,
    double AvgDaysToResolution, double CriterioPct,
    long InputTokens, long OutputTokens, decimal? Cost,
    IReadOnlyList<WeekBucket> Burndown, IReadOnlyList<AppActive> PerApp);

/// <summary>Computes the V6 metrics across apps and sessions (§8).</summary>
public sealed class MetricsQuery
{
    private readonly HubContext _hub;
    private readonly TimeProvider _time;

    public MetricsQuery(HubContext hub, TimeProvider? time = null)
    {
        _hub = hub;
        _time = time ?? TimeProvider.System;
    }

    public MetricsSummary Build(string? slugFilter = null)
    {
        var slugs = slugFilter is { Length: > 0 } ? new[] { slugFilter } : _hub.Store.ListAppSlugs().ToArray();
        var findings = new List<Finding>();
        var sessions = new List<AuditSession>();
        var perApp = new List<AppActive>();

        foreach (string slug in slugs)
        {
            var appFindings = _hub.Store.ListFindings(slug);
            findings.AddRange(appFindings);
            sessions.AddRange(_hub.Store.ListSessions(slug));
            perApp.Add(new AppActive(slug, appFindings.Count(f => f.Status == FindingStatus.Activo)));
        }

        var active = findings.Where(f => f.Status == FindingStatus.Activo).ToList();
        int Count(Severity s) => active.Count(f => f.Severity == s);

        var resolved = findings.Where(f => f.Status == FindingStatus.Resuelto && f.Resolved is not null).ToList();
        double avgDays = resolved.Count == 0
            ? 0
            : resolved.Average(f => Math.Max(0, (f.Resolved!.Utc - f.FirstDetected.Utc).TotalDays));

        double criterioPct = findings.Count == 0
            ? 0
            : 100.0 * findings.Count(f => f.Tag == FindingTag.Criterio) / findings.Count;

        long inTok = sessions.Sum(s => s.Usage.InputTokens);
        long outTok = sessions.Sum(s => s.Usage.OutputTokens);
        decimal? cost = sessions.Any(s => s.Usage.Cost is not null)
            ? sessions.Sum(s => s.Usage.Cost ?? 0m)
            : null;

        return new MetricsSummary(
            active.Count, Count(Severity.Critica), Count(Severity.Alta), Count(Severity.Media), Count(Severity.Baja),
            resolved.Count, findings.Count(f => f.Status == FindingStatus.Silenciado),
            avgDays, criterioPct, inTok, outTok, cost,
            BuildBurndown(findings, resolved), perApp.OrderByDescending(a => a.Active).ToList());
    }

    private IReadOnlyList<WeekBucket> BuildBurndown(IReadOnlyList<Finding> findings, IReadOnlyList<Finding> resolved)
    {
        const int weeks = 8;
        DateTimeOffset now = _time.GetUtcNow();
        DateTimeOffset start = now.Date.AddDays(-7 * (weeks - 1));
        var buckets = new WeekBucket[weeks];

        for (int w = 0; w < weeks; w++)
        {
            DateTimeOffset from = start.AddDays(7 * w);
            DateTimeOffset to = from.AddDays(7);
            int created = findings.Count(f => f.FirstDetected.Utc >= from && f.FirstDetected.Utc < to);
            int closed = resolved.Count(f => f.Resolved!.Utc >= from && f.Resolved.Utc < to);
            buckets[w] = new WeekBucket(from.ToString("MM-dd"), created, closed);
        }

        return buckets;
    }
}
