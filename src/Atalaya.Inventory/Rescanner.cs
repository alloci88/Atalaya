using Atalaya.Domain;
using Atalaya.Domain.Model;

namespace Atalaya.Inventory;

/// <summary>The delta of a re-scan against a previous cycle inventory (§4).</summary>
/// <param name="Merged">The updated inventory, preserving states, with renames carried over.</param>
/// <param name="Added">Units that appeared.</param>
/// <param name="Removed">Units that disappeared (and were not renames).</param>
/// <param name="Renamed">Old→new pairs detected by identical content hash at a new path.</param>
public sealed record RescanResult(
    InventoryCycle Merged,
    IReadOnlyList<InventoryUnit> Added,
    IReadOnlyList<InventoryUnit> Removed,
    IReadOnlyList<(InventoryUnit From, InventoryUnit To)> Renamed);

/// <summary>
/// Reconciles a fresh scan with the previous cycle inventory: carries audited state forward,
/// detects renames by content hash so a moved-but-unchanged unit keeps its state, and reports
/// additions/removals — without losing states (§4).
/// </summary>
public static class Rescanner
{
    public static RescanResult Reconcile(InventoryCycle previous, InventoryCycle current)
    {
        var prevByPath = previous.Units.ToDictionary(u => u.Path, StringComparer.Ordinal);
        var matchedCurrent = new HashSet<string>(StringComparer.Ordinal);
        var consumedPrev = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<InventoryUnit>();
        var renamed = new List<(InventoryUnit, InventoryUnit)>();
        var added = new List<InventoryUnit>();

        // Pass 1: same path → carry state.
        foreach (InventoryUnit cur in current.Units)
        {
            if (prevByPath.TryGetValue(cur.Path, out InventoryUnit? prev))
            {
                CarryState(cur, prev);
                consumedPrev.Add(prev.Path);
                matchedCurrent.Add(cur.Path);
                merged.Add(cur);
            }
        }

        // Removed pool = previous units not matched by path.
        var removedPool = previous.Units.Where(u => !consumedPrev.Contains(u.Path)).ToList();

        // Pass 2: current-only units → rename (same hash) or genuinely new.
        foreach (InventoryUnit cur in current.Units)
        {
            if (matchedCurrent.Contains(cur.Path))
            {
                continue;
            }

            InventoryUnit? source = removedPool.FirstOrDefault(
                p => p.ContentHash is not null && p.ContentHash == cur.ContentHash);

            if (source is not null)
            {
                CarryState(cur, source);
                removedPool.Remove(source);
                renamed.Add((source, cur));
            }
            else
            {
                added.Add(cur);
            }

            merged.Add(cur);
        }

        merged.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        var mergedCycle = new InventoryCycle { CycleN = current.CycleN, Units = merged };

        return new RescanResult(mergedCycle, added, removedPool, renamed);
    }

    private static void CarryState(InventoryUnit target, InventoryUnit source)
    {
        // "grande" (freshly computed) always wins; otherwise a previously audited unit stays audited.
        if (target.State != UnitState.Grande && source.State == UnitState.Auditada)
        {
            target.State = UnitState.Auditada;
            target.AuditedInSession = source.AuditedInSession;
        }
    }
}
