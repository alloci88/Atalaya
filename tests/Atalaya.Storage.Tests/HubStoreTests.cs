using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
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
    public void Finding_roundtrip_and_lookup_by_ulid()
    {
        Finding f = Samples.Finding();
        _store.WriteFinding("webapp", f);

        _store.TryReadFinding("webapp", f.Id.ToString())!.Title.Should().Be("Conn leaked");
        _store.ListFindings("webapp").Should().ContainSingle();
    }

    /// <summary>
    /// F5.12: el patrón silenciado es un fichero por patrón bajo la app, como todo lo demás del
    /// hub. La app es parte de la ruta: eso es lo que lo hace por-aplicación por construcción.
    /// </summary>
    [Fact]
    public void Pattern_silence_roundtrip_is_per_app()
    {
        var pattern = new PatternSilence
        {
            Id = new UlidFactory(SystemClock.Instance).NewUlid(),
            ShortId = "P-1",
            Exemplar = "bloques catch vacíos que ocultan excepciones",
            Reason = SilenceReason.DeudaAceptada,
            Notes = "no aplica",
            By = "alvaro",
            Utc = DateTimeOffset.UtcNow,
        };
        _store.WritePatternSilence("webapp", pattern);

        _store.TryReadPatternSilence("webapp", pattern.Id)!.Exemplar.Should().Be(pattern.Exemplar);
        _store.ListPatternSilences("webapp").Should().ContainSingle();
        _store.ListPatternSilences("otraapp").Should().BeEmpty("un patrón nunca es global al hub");

        _store.DeletePatternSilence("webapp", pattern.Id).Should().BeTrue();
        _store.ListPatternSilences("webapp").Should().BeEmpty();
        _store.DeletePatternSilence("webapp", pattern.Id).Should().BeFalse();
    }

    /// <summary>
    /// F7: el registro de directivas es un fichero por directiva bajo la app, merge-friendly como
    /// todo lo demás. La app es parte de la ruta —una convención de una aplicación no es la de la de
    /// al lado— y lo que se persiste es la RUTA: el contenido vive en el repo de la app.
    /// </summary>
    [Fact]
    public void Directive_roundtrip_is_per_app_and_stores_only_the_path()
    {
        var directive = new ProjectDirective
        {
            Id = new UlidFactory(SystemClock.Instance).NewUlid(),
            Path = "docs/adr/0001-usamos-postgres.md",
            Kind = "adr",
            Scope = DirectiveScope.Ambos,
            Order = 3,
            By = "alvaro",
            Utc = DateTimeOffset.UtcNow,
        };
        _store.WriteDirective("webapp", directive);

        ProjectDirective? read = _store.TryReadDirective("webapp", directive.Id);
        read!.Path.Should().Be(directive.Path);
        read.Scope.Should().Be(DirectiveScope.Ambos);
        read.Order.Should().Be(3);
        read.AppliesToAudit.Should().BeTrue();
        read.AppliesToFix.Should().BeTrue();

        _store.ListDirectives("webapp").Should().ContainSingle();
        _store.ListDirectives("otraapp").Should().BeEmpty("una directiva nunca es global al hub");

        _store.DeleteDirective("webapp", directive.Id).Should().BeTrue();
        _store.ListDirectives("webapp").Should().BeEmpty();
        _store.DeleteDirective("webapp", directive.Id).Should().BeFalse();
    }

    /// <summary>
    /// Un candidato visto y descartado se guarda con ámbito «Ninguno». Es un estado con dueño
    /// humano, no la ausencia de decisión: sin él, el re-escaneo siguiente lo volvería a anunciar.
    /// </summary>
    [Fact]
    public void Directive_with_no_scope_is_a_decision_and_persists()
    {
        var directive = new ProjectDirective
        {
            Id = new UlidFactory(SystemClock.Instance).NewUlid(),
            Path = "specs/viejo.md",
            Kind = "spec",
            Scope = DirectiveScope.Ninguno,
            By = "alvaro",
            Utc = DateTimeOffset.UtcNow,
        };
        _store.WriteDirective("webapp", directive);

        ProjectDirective? read = _store.TryReadDirective("webapp", directive.Id);
        read!.IsActive.Should().BeFalse();
        read.AppliesToAudit.Should().BeFalse();
        read.AppliesToFix.Should().BeFalse();
    }

    /// <summary>
    /// El ejemplar ES el alcance: un patrón sin frase no le diría nada al auditor y produciría
    /// supresiones que nadie podría explicar. No llega a escribirse.
    /// </summary>
    [Fact]
    public void A_pattern_without_an_exemplar_is_never_written()
    {
        Action act = () => _store.WritePatternSilence("webapp", new PatternSilence
        {
            Id = new UlidFactory(SystemClock.Instance).NewUlid(),
            ShortId = "P-1",
            Exemplar = "   ",
            By = "alvaro",
            Utc = DateTimeOffset.UtcNow,
        });

        act.Should().Throw<SchemaValidationException>();
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
