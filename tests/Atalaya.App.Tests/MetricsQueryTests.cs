using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atalaya.App.Tests;

public sealed class MetricsQueryTests : IDisposable
{
    private readonly string _root;
    private readonly HubContext _hub;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 0, 0, 0, TimeSpan.Zero);

    public MetricsQueryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-metrics", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(_root, "local"));
        var settings = new SettingsService(paths);
        settings.Load();
        _hub = new HubContext(paths, settings, NullLoggerFactory.Instance);
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private Finding Finding(FindingStatus status, DateTimeOffset detected, DateTimeOffset? resolvedAt = null)
    {
        var stamp = new DetectionStamp(detected, AuditMode.Lotes, "abc", "alvaro");
        var f = new Finding
        {
            Id = _ulids.NewUlid(),
            Fingerprint = "sha256:" + Guid.NewGuid().ToString("N") + new string('a', 32),
            RuleId = "criterio.x",
            Pillar = Pillar.Errores,
            Tag = FindingTag.Criterio,
            Severity = Severity.Alta,
            Confidence = Confidence.Media,
            Status = FindingStatus.Activo,
            Title = "t",
            Locations = { new Location("a.cs", 1) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
        if (status == FindingStatus.Resuelto)
        {
            f.Resolve(new ResolutionStamp(resolvedAt ?? detected, ResolutionVia.Manual, AuditMode.Lotes, "c", "alvaro", "fixed"));
        }

        return f;
    }

    [Fact]
    public void Computes_active_resolved_avg_resolution_and_criterio_pct()
    {
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Activo, Now.AddDays(-10)));
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-10), Now.AddDays(-6))); // 4 days

        MetricsSummary m = new MetricsQuery(_hub, new FakeTime(Now)).Build();

        m.ActiveTotal.Should().Be(1);
        m.Resolved.Should().Be(1);
        m.AvgDaysToResolution.Should().BeApproximately(4, 0.1);
        m.CriterioPct.Should().Be(100); // both tagged criterio
        m.Burndown.Should().HaveCount(8);
    }

    [Fact]
    public void Burndown_places_new_and_resolved_in_weeks()
    {
        _hub.Store.WriteFinding("app", Finding(FindingStatus.Resuelto, Now.AddDays(-3), Now.AddDays(-1)));

        MetricsSummary m = new MetricsQuery(_hub, new FakeTime(Now)).Build();

        m.Burndown.Sum(b => b.New).Should().Be(1);
        m.Burndown.Sum(b => b.Resolved).Should().Be(1);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
