using Atalaya.Domain;
using Atalaya.Domain.Model;
using Atalaya.Storage.Sync;
using FluentAssertions;
using Xunit;

namespace Atalaya.Storage.Tests;

public class MergePolicyTests
{
    [Fact]
    public void Inventory_merge_takes_most_advanced_state_per_unit()
    {
        InventoryCycle ours = Samples.Inventory(1,
            ("a.cs", UnitState.Auditada),
            ("b.cs", UnitState.Pendiente));
        InventoryCycle theirs = Samples.Inventory(1,
            ("a.cs", UnitState.Pendiente),
            ("b.cs", UnitState.Auditada),
            ("c.cs", UnitState.Grande));

        InventoryCycle merged = HubMergePolicy.MergeInventory(ours, theirs);

        merged.Units.Should().HaveCount(3);
        merged.Units.Single(u => u.Path == "a.cs").State.Should().Be(UnitState.Auditada);
        merged.Units.Single(u => u.Path == "b.cs").State.Should().Be(UnitState.Auditada);
        merged.Units.Single(u => u.Path == "c.cs").State.Should().Be(UnitState.Grande);
    }

    [Fact]
    public void Inventory_merge_ranks_auditada_over_grande_over_pendiente()
    {
        InventoryCycle ours = Samples.Inventory(1, ("x.cs", UnitState.Grande));
        InventoryCycle theirs = Samples.Inventory(1, ("x.cs", UnitState.Pendiente));

        HubMergePolicy.MergeInventory(ours, theirs)
            .Units.Single().State.Should().Be(UnitState.Grande);
    }

    [Fact]
    public void Finding_merge_takes_newer_scalars_and_unions_history()
    {
        Finding ours = Samples.Finding();
        Finding theirs = Samples.Finding();
        // Force same identity so it's a real metadata conflict.
        typeof(Finding).GetProperty(nameof(Finding.Id))!.SetValue(theirs, ours.Id);

        ours.Severity = Severity.Alta;
        ours.History.Add(new HistoryEntry(Samples.T0.AddMinutes(1), FindingEvent.SeverityChanged, "alvaro", "→alta"));

        theirs.Severity = Severity.Baja;
        theirs.TimesConfirmed = 5;
        theirs.History.Add(new HistoryEntry(Samples.T0.AddMinutes(2), FindingEvent.SeverityChanged, "maria", "→baja"));

        Finding merged = HubMergePolicy.MergeFinding(ours, theirs);

        // theirs has the newer latest history entry → its scalar (severity) wins.
        merged.Severity.Should().Be(Severity.Baja);
        merged.TimesConfirmed.Should().Be(5);
        merged.History.Should().HaveCount(3); // detected + two severity changes, unioned
    }
}
