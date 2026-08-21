using Atalaya.Domain;
using Atalaya.Domain.Model;
using Atalaya.Storage.Sync;
using FluentAssertions;
using Xunit;

namespace Atalaya.Storage.Tests;

/// <summary>
/// Two clones against one bare remote — the §11 convergence + conflict-policy harness.
/// </summary>
public sealed class TwoCloneSyncTests : IDisposable
{
    private readonly TempRepo _remote = new();

    private (HubSyncService Sync, HubStore Store) Clone(string name, string who)
    {
        var paths = new HubPaths(_remote.NewClonePath(name));
        var sync = new HubSyncService(paths, (who, $"{who}@example.com"));
        sync.EnsureCloned(_remote.BareRemotePath);
        return (sync, new HubStore(paths));
    }

    [Fact]
    public void Findings_and_sessions_converge_between_two_users()
    {
        (HubSyncService aSync, HubStore aStore) = Clone("a", "alvaro");
        aStore.WriteHub(Samples.Hub());
        aStore.WriteApp(Samples.App());
        Finding finding = Samples.Finding();
        aStore.WriteFinding("webapp", finding);
        aSync.CommitAndPush("session: lotes webapp seed").Should().BeTrue();

        // B clones after the seed and immediately sees it.
        (HubSyncService bSync, HubStore bStore) = Clone("b", "maria");
        bStore.ListFindings("webapp").Should().ContainSingle();

        // B adds a session and pushes.
        bStore.WriteSession(Samples.Session(by: "maria"));
        bSync.CommitAndPush("session: lotes webapp 1 unidad").Should().BeTrue();

        // A pulls and converges.
        aSync.Pull();
        aStore.ListSessions("webapp").Should().ContainSingle();
        aStore.TryReadFinding("webapp", finding.Id.ToString()).Should().NotBeNull();
    }

    [Fact]
    public void Claim_conflict_resolves_in_favor_of_the_already_pushed_claim()
    {
        // Seed a shared baseline.
        (HubSyncService aSync, HubStore aStore) = Clone("a", "alvaro");
        aStore.WriteHub(Samples.Hub());
        aStore.WriteApp(Samples.App());
        aSync.CommitAndPush("seed").Should().BeTrue();

        (HubSyncService bSync, HubStore bStore) = Clone("b", "maria");

        // Both claim the SAME unit (same unitHash → same file) without pulling in between.
        aStore.WriteClaim("webapp", Samples.Claim("src/Db/Pool.cs", "alvaro"));
        aSync.Commit("claim: alvaro Pool.cs");

        bStore.WriteClaim("webapp", Samples.Claim("src/Db/Pool.cs", "maria"));
        bSync.Commit("claim: maria Pool.cs");

        // Alvaro pushes first — his claim reaches remote history.
        aSync.Push().Should().BeTrue();

        // Maria pushes: rebase hits the claim conflict; Alvaro's already-published claim wins.
        var notifications = new List<string>();
        bSync.Pulled += r => notifications.AddRange(r.Notifications);
        bSync.Push().Should().BeTrue();

        // The surviving claim on both sides belongs to Alvaro.
        Claim survivor = bStore.ListClaims("webapp").Should().ContainSingle().Subject;
        survivor.By.Should().Be("alvaro");
        notifications.Should().ContainMatch("*reclamó*antes que tú*");

        // And the remote agrees (A pulls, still sees only alvaro's claim).
        aSync.Pull();
        aStore.ListClaims("webapp").Should().ContainSingle().Which.By.Should().Be("alvaro");
    }

    [Fact]
    public void Inventory_conflict_merges_per_unit()
    {
        (HubSyncService aSync, HubStore aStore) = Clone("a", "alvaro");
        aStore.WriteHub(Samples.Hub());
        aStore.WriteApp(Samples.App());
        aStore.WriteInventory("webapp", Samples.Inventory(1,
            ("a.cs", UnitState.Pendiente), ("b.cs", UnitState.Pendiente)));
        aSync.CommitAndPush("seed inventory").Should().BeTrue();

        (HubSyncService bSync, HubStore bStore) = Clone("b", "maria");

        // A audits a.cs; B audits b.cs — both edit inventory/cycle1.json from the same base.
        InventoryCycle aInv = aStore.TryReadInventory("webapp", 1)!;
        aInv.Units.Single(u => u.Path == "a.cs").State = UnitState.Auditada;
        aStore.WriteInventory("webapp", aInv);
        aSync.Commit("audit a.cs");

        InventoryCycle bInv = bStore.TryReadInventory("webapp", 1)!;
        bInv.Units.Single(u => u.Path == "b.cs").State = UnitState.Auditada;
        bStore.WriteInventory("webapp", bInv);
        bSync.Commit("audit b.cs");

        aSync.Push().Should().BeTrue();
        bSync.Push().Should().BeTrue(); // rebase merges the inventory per unit

        InventoryCycle merged = bStore.TryReadInventory("webapp", 1)!;
        merged.Units.Single(u => u.Path == "a.cs").State.Should().Be(UnitState.Auditada);
        merged.Units.Single(u => u.Path == "b.cs").State.Should().Be(UnitState.Auditada);
    }

    public void Dispose() => _remote.Dispose();
}
