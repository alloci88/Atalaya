namespace Atalaya.Domain.Model;

/// <summary>Detected/declared technology stack of an audited app (§4).</summary>
public enum TechStack
{
    Unknown,
    DotNet,
    JavaScript,
    TypeScript,
    Python,
    Go,
    Java,
    Rust,
    CCpp,
}

/// <summary>Per-app thresholds (§4). All configurable and persisted in app.json.</summary>
public sealed class Thresholds
{
    /// <summary>A unit above this LOC (or <see cref="LargeUnitChars"/>) is "grande" (§4).</summary>
    public int LargeUnitLoc { get; set; } = 1500;

    public int LargeUnitChars { get; set; } = 60_000;

    /// <summary>Days since last confirmation before the freshness semaphore warns (§8, V3).</summary>
    public int FreshnessDays { get; set; } = 60;

    /// <summary>Default claim TTL in minutes (§2).</summary>
    public int ClaimTtlMinutes { get; set; } = 30;

    /// <summary>
    /// Hard ceiling on in+out tokens spent auditing a single unit (F3 Hito 1c). When exceeded
    /// the app aborts THAT unit (verdict <c>presupuesto-superado</c>, sibling of <c>grande</c>)
    /// and continues with the next one, so a run-away agent loop can never spend without a cap.
    /// Default 300 000, chosen against the pilot baseline (37 turns / 1,26 M input on one class):
    /// well above a healthy batched run, well below a pathological one.
    /// </summary>
    public long MaxTokensPerUnit { get; set; } = 300_000;
}

/// <summary>
/// App configuration (§2), stored as <c>apps/{appSlug}/app.json</c>. Note: the local clone
/// path is per-machine and lives in machines.json, NEVER here (§4).
/// </summary>
public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 1;

    public required string Slug { get; set; }

    public required string Name { get; set; }

    public required string RepoUrl { get; set; }

    public TechStack Stack { get; set; } = TechStack.Unknown;

    public Thresholds Thresholds { get; set; } = new();

    /// <summary>Glob-ish exclusion patterns, editable per app (§4).</summary>
    public List<string> Exclusions { get; set; } = new();

    /// <summary>Courtesy ESTADO.md export into the audited repo (§7).</summary>
    public bool ExportStatusMd { get; set; }

    public int CurrentCycle { get; set; } = 1;

    /// <summary>
    /// Per-pillar display-id counters (OPT/MEJ/BUG). Advanced only on successful push (§2),
    /// so a concurrent collision only renumbers an as-yet-unpublished alias.
    /// </summary>
    public Dictionary<string, int> DisplayIdCounters { get; set; } = new();
}
