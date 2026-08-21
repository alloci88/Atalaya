using System.Text.Json.Serialization;
using Atalaya.Domain.Hashing;
using Atalaya.Domain.Ids;

namespace Atalaya.Domain.Model;

/// <summary>A unit inside a cycle inventory (§2). Unit state lives here — there is no LOTES.md.</summary>
public sealed class InventoryUnit
{
    public required string Path { get; set; }

    public required string Module { get; set; }

    public int Loc { get; set; }

    public string? ContentHash { get; set; }

    public UnitState State { get; set; } = UnitState.Pendiente;

    /// <summary>The session that last audited this unit, if any.</summary>
    public Ulid? AuditedInSession { get; set; }

    /// <summary>Stable identity across cycles/edits (§2). Not serialized (derivable).</summary>
    [JsonIgnore]
    public string UnitHash => HashUtil.UnitHash(Path);
}

/// <summary>
/// The inventory of a single cycle (§2), stored as <c>inventory/{cycleN}.json</c>.
/// A cycle closes when no <see cref="UnitState.Pendiente"/> units remain (large ones
/// do not block closure).
/// </summary>
public sealed class InventoryCycle
{
    public int SchemaVersion { get; set; } = 1;

    public int CycleN { get; set; }

    public List<InventoryUnit> Units { get; set; } = new();

    /// <summary>The cycle can close when nothing is pending (§5.1). Large units don't block.</summary>
    public bool HasPending => Units.Any(u => u.State == UnitState.Pendiente);
}
