using Atalaya.Domain;
using Atalaya.Domain.Model;

namespace Atalaya.Storage.Sync;

/// <summary>
/// Deterministic resolution of the three real-conflict kinds (§3). Pure functions so the
/// policy is unit-testable independently of git. "Real conflict" = two users wrote the same
/// file; ULID-named files make this almost impossible, leaving claims, inventory and
/// concurrent metadata edits as the only cases.
/// </summary>
public static class HubMergePolicy
{
    private static int Rank(UnitState s) => s switch
    {
        UnitState.Auditada => 2,
        UnitState.Grande => 1,
        UnitState.Pendiente => 0,
        _ => 0,
    };

    /// <summary>
    /// Inventory merge (§3): entry-by-entry, the most-advanced state wins
    /// (auditada &gt; grande &gt; pendiente). Never a textual merge.
    /// </summary>
    public static InventoryCycle MergeInventory(InventoryCycle ours, InventoryCycle theirs)
    {
        var byPath = new Dictionary<string, InventoryUnit>(StringComparer.Ordinal);
        foreach (InventoryUnit u in ours.Units)
        {
            byPath[u.Path] = u;
        }

        foreach (InventoryUnit u in theirs.Units)
        {
            if (byPath.TryGetValue(u.Path, out InventoryUnit? existing))
            {
                // Strictly more advanced wins; ties keep ours for determinism.
                if (Rank(u.State) > Rank(existing.State))
                {
                    byPath[u.Path] = u;
                }
            }
            else
            {
                byPath[u.Path] = u;
            }
        }

        return new InventoryCycle
        {
            SchemaVersion = ours.SchemaVersion,
            CycleN = ours.CycleN,
            Units = byPath.Values.OrderBy(x => x.Path, StringComparer.Ordinal).ToList(),
        };
    }

    /// <summary>
    /// Finding metadata merge (§3): last-write-wins per field with <c>history</c> as a union.
    /// The side with the newer latest-history-entry wins the scalar fields; timesConfirmed
    /// takes the max; alias history and history are unioned.
    /// </summary>
    public static Finding MergeFinding(Finding ours, Finding theirs)
    {
        DateTimeOffset LatestOf(Finding f)
            => f.History.Count > 0 ? f.History.Max(h => h.Utc) : f.LastConfirmed.Utc;

        Finding winner = LatestOf(theirs) > LatestOf(ours) ? theirs : ours;

        winner.History = ours.History
            .Concat(theirs.History)
            .GroupBy(h => (h.Utc, h.Event, h.By, h.Detail))
            .Select(g => g.First())
            .OrderBy(h => h.Utc)
            .ToList();

        winner.TimesConfirmed = Math.Max(ours.TimesConfirmed, theirs.TimesConfirmed);

        winner.AliasHistory = ours.AliasHistory
            .Concat(theirs.AliasHistory)
            .Distinct()
            .ToList();

        return winner;
    }
}

/// <summary>The winner of a claim conflict and whether the local claim was dropped (§3).</summary>
/// <param name="Winner">The claim that stays — the one already in remote history.</param>
/// <param name="LocalDropped">True when our local claim lost and must be released + notified.</param>
public sealed record ClaimResolution(Claim Winner, bool LocalDropped);
