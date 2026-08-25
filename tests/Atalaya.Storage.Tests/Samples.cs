using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;

namespace Atalaya.Storage.Tests;

/// <summary>Sample entity builders for storage tests.</summary>
internal static class Samples
{
    private static readonly UlidFactory Ulids = new(SystemClock.Instance);

    public static readonly DateTimeOffset T0 = new(2026, 8, 21, 10, 0, 0, TimeSpan.Zero);

    public static HubInfo Hub() => new() { OrganizationName = "Contoso" };

    public static AppConfig App(string slug = "webapp") => new()
    {
        Slug = slug,
        Name = "Web App",
        RepoUrl = "https://example/webapp.git",
        Stack = TechStack.DotNet,
    };

    public static Finding Finding(string ruleId = "errores.recursos.no-liberado", string path = "src/Db/Pool.cs")
    {
        var stamp = new DetectionStamp(T0, AuditMode.Lotes, "abc1234", "alvaro");
        return new Finding
        {
            Id = Ulids.NewUlid(),
            RuleId = ruleId,
            Pillar = Pillar.Errores,
            Tag = FindingTag.Criterio,
            Severity = Severity.Critica,
            Confidence = Confidence.Media,
            Title = "Conn leaked",
            Description = "desc",
            Impact = "impact",
            Recommendation = "reco",
            Locations = { new Location(path, 120, "sha256:snip") },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
            History = { new HistoryEntry(T0, FindingEvent.Detected, "alvaro", "detected via lotes") },
        };
    }

    public static Claim Claim(string unit = "src/Db/Pool.cs", string by = "alvaro") => new()
    {
        Unit = unit,
        Module = "Db",
        By = by,
        Machine = "PC-" + by,
        Utc = T0,
        TtlMinutes = 30,
    };

    public static AuditSession Session(string slug = "webapp", string by = "alvaro") => new()
    {
        Id = Ulids.NewUlid(),
        AppSlug = slug,
        Mode = AuditMode.Lotes,
        By = by,
        Machine = "PC-" + by,
        StartedUtc = T0,
        EndedUtc = T0.AddMinutes(5),
        Commit = "abc1234",
        CycleN = 1,
    };

    public static InventoryCycle Inventory(int cycle = 1, params (string Path, UnitState State)[] units)
    {
        var inv = new InventoryCycle { CycleN = cycle };
        foreach ((string path, UnitState state) in units)
        {
            inv.Units.Add(new InventoryUnit { Path = path, Module = "M", Loc = 100, State = state });
        }

        return inv;
    }

    public static Ulid NewUlid() => Ulids.NewUlid();
}
