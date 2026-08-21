using Atalaya.Domain;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.Storage.Tests;

public sealed class HubStoreTests : IDisposable
{
    private readonly string _root;
    private readonly HubStore _store;

    public HubStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-store", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new HubStore(new HubPaths(_root));
    }

    [Fact]
    public void Hub_and_app_roundtrip()
    {
        _store.WriteHub(Samples.Hub());
        _store.WriteApp(Samples.App());

        _store.TryReadHub()!.OrganizationName.Should().Be("Contoso");
        _store.ListAppSlugs().Should().ContainSingle().Which.Should().Be("webapp");
        _store.TryReadApp("webapp")!.Stack.Should().Be(TechStack.DotNet);
    }

    [Fact]
    public void Finding_roundtrip_and_lookup_by_fingerprint()
    {
        Finding f = Samples.Finding();
        _store.WriteFinding("webapp", f);

        _store.TryReadFinding("webapp", f.Id.ToString())!.Title.Should().Be("Conn leaked");
        _store.ListFindings("webapp").Should().ContainSingle();
        _store.FindByFingerprint("webapp", f.Fingerprint).Should().ContainSingle();
    }

    [Fact]
    public void Claim_write_and_delete()
    {
        Claim c = Samples.Claim();
        _store.WriteClaim("webapp", c);

        _store.ListClaims("webapp").Should().ContainSingle();
        _store.DeleteClaim("webapp", c.UnitHash).Should().BeTrue();
        _store.ListClaims("webapp").Should().BeEmpty();
    }

    [Fact]
    public void Session_and_inventory_roundtrip()
    {
        _store.WriteSession(Samples.Session());
        _store.WriteInventory("webapp", Samples.Inventory(1, ("a.cs", UnitState.Pendiente)));

        _store.ListSessions("webapp").Should().ContainSingle();
        _store.TryReadInventory("webapp", 1)!.Units.Should().ContainSingle();
    }

    [Fact]
    public void Writing_an_invalid_finding_throws()
    {
        Finding f = Samples.Finding();
        f.Locations.Clear(); // schema requires >= 1 location

        var act = () => _store.WriteFinding("webapp", f);
        act.Should().Throw<SchemaValidationException>();
    }

    [Fact]
    public void Corrupt_file_is_skipped_not_fatal()
    {
        _store.WriteFinding("webapp", Samples.Finding());
        string dir = new HubPaths(_root).FindingsDir("webapp");
        File.WriteAllText(Path.Combine(dir, "garbage.json"), "{ not valid");

        _store.ListFindings("webapp").Should().ContainSingle(); // the good one survives
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}
