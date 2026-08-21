using Atalaya.Domain.Ids;

namespace Atalaya.Domain.Model;

/// <summary>Per-unit verdict recorded in a session (§2).</summary>
public sealed record UnitVerdictRecord(string Unit, string Module, string Verdict, string? Summary);

/// <summary>Session tallies (§2).</summary>
public sealed class SessionCounters
{
    public int New { get; set; }
    public int Confirmed { get; set; }
    public int Resolved { get; set; }
    public int SilencedRespected { get; set; }
    public int Recurrences { get; set; }
}

/// <summary>Token/cost totals for a session (§6.3).</summary>
public sealed class UsageTotals
{
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal? Cost { get; set; }
    public string? Currency { get; set; }

    public void Add(long input, long output, decimal? cost)
    {
        InputTokens += input;
        OutputTokens += output;
        if (cost is not null)
        {
            Cost = (Cost ?? 0m) + cost.Value;
        }
    }
}

/// <summary>
/// An immutable, append-only audit-session event (§2), stored as <c>sessions/{ulid}.json</c>.
/// </summary>
public sealed class AuditSession
{
    public int SchemaVersion { get; set; } = 1;

    public Ulid Id { get; init; }

    public required string AppSlug { get; set; }

    public AuditMode Mode { get; set; }

    public required string By { get; set; }

    public required string Machine { get; set; }

    public DateTimeOffset StartedUtc { get; set; }

    public DateTimeOffset? EndedUtc { get; set; }

    /// <summary>Commit of the audited clone.</summary>
    public string? Commit { get; set; }

    public string? Model { get; set; }

    public int CycleN { get; set; }

    public List<UnitVerdictRecord> Units { get; set; } = new();

    public SessionCounters Counters { get; set; } = new();

    public UsageTotals Usage { get; set; } = new();

    /// <summary>Free-text notes, e.g. "no signature extractor available for stack Go".</summary>
    public List<string> Notes { get; set; } = new();
}
