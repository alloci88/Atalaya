using Atalaya.Domain;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.Inventory.Tests;

public class RescannerTests
{
    private static InventoryUnit Unit(string path, UnitState state, string hash)
        => new() { Path = path, Module = "M", Loc = 10, ContentHash = "sha256:" + hash, State = state };

    [Fact]
    public void Carries_audited_state_for_unchanged_units()
    {
        var previous = new InventoryCycle
        {
            CycleN = 1,
            Units = { Unit("a.cs", UnitState.Auditada, "aaa"), Unit("b.cs", UnitState.Pendiente, "bbb") },
        };
        var current = new InventoryCycle
        {
            CycleN = 1,
            Units = { Unit("a.cs", UnitState.Pendiente, "aaa"), Unit("b.cs", UnitState.Pendiente, "bbb") },
        };

        RescanResult result = Rescanner.Reconcile(previous, current);

        result.Merged.Units.Single(u => u.Path == "a.cs").State.Should().Be(UnitState.Auditada);
        result.Added.Should().BeEmpty();
        result.Removed.Should().BeEmpty();
        result.Renamed.Should().BeEmpty();
    }

    [Fact]
    public void Detects_rename_by_content_hash_and_keeps_state()
    {
        var previous = new InventoryCycle
        {
            CycleN = 1,
            Units = { Unit("old/Name.cs", UnitState.Auditada, "same") },
        };
        var current = new InventoryCycle
        {
            CycleN = 1,
            Units = { Unit("new/Name.cs", UnitState.Pendiente, "same") },
        };

        RescanResult result = Rescanner.Reconcile(previous, current);

        result.Renamed.Should().ContainSingle();
        result.Renamed[0].From.Path.Should().Be("old/Name.cs");
        result.Renamed[0].To.Path.Should().Be("new/Name.cs");
        result.Merged.Units.Single().State.Should().Be(UnitState.Auditada); // state carried over
        result.Added.Should().BeEmpty();
        result.Removed.Should().BeEmpty();
    }

    [Fact]
    public void Reports_added_and_removed()
    {
        var previous = new InventoryCycle { CycleN = 1, Units = { Unit("gone.cs", UnitState.Pendiente, "g") } };
        var current = new InventoryCycle { CycleN = 1, Units = { Unit("fresh.cs", UnitState.Pendiente, "f") } };

        RescanResult result = Rescanner.Reconcile(previous, current);

        result.Added.Should().ContainSingle().Which.Path.Should().Be("fresh.cs");
        result.Removed.Should().ContainSingle().Which.Path.Should().Be("gone.cs");
    }

    [Fact]
    public void Freshly_large_unit_overrides_carried_audited_state()
    {
        var previous = new InventoryCycle { CycleN = 1, Units = { Unit("x.cs", UnitState.Auditada, "h") } };
        var current = new InventoryCycle
        {
            CycleN = 1,
            Units = { new InventoryUnit { Path = "x.cs", Module = "M", Loc = 3000, ContentHash = "sha256:h2", State = UnitState.Grande } },
        };

        RescanResult result = Rescanner.Reconcile(previous, current);

        result.Merged.Units.Single().State.Should().Be(UnitState.Grande);
    }
}
