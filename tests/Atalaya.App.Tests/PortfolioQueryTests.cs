using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Storage;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

public sealed class PortfolioQueryTests : IDisposable
{
    private readonly string _root;
    private readonly HubStore _store;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    public PortfolioQueryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-pq", Guid.NewGuid().ToString("N"));
        _store = new HubStore(new HubPaths(_root));
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private Finding Finding(string slug, Severity sev, FindingStatus status = FindingStatus.Activo)
    {
        var stamp = new DetectionStamp(Now.AddDays(-2), AuditMode.Lotes, "abc", "alvaro");
        var f = new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = "errores.x",
            Pillar = Pillar.Errores,
            Severity = sev,
            Confidence = Confidence.Media,
            Status = status,
            Title = "t",
            Locations = { new Location("a.cs", 1, "sha256:s") },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
        if (status == FindingStatus.Resuelto)
        {
            f.Resolve(new ResolutionStamp(Now, ResolutionVia.Manual, AuditMode.Lotes, "abc", "alvaro", "fixed"));
        }

        return f;
    }

    [Fact]
    public void Card_computes_progress_and_active_severity_counts()
    {
        _store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
        _store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units =
            {
                new InventoryUnit { Path = "a.cs", Module = "M", State = UnitState.Auditada },
                new InventoryUnit { Path = "b.cs", Module = "M", State = UnitState.Pendiente },
                new InventoryUnit { Path = "big.cs", Module = "M", State = UnitState.Grande },
            },
        });
        _store.WriteFinding("app", Finding("app", Severity.Critica));
        _store.WriteFinding("app", Finding("app", Severity.Alta));
        _store.WriteFinding("app", Finding("app", Severity.Baja, FindingStatus.Resuelto)); // not active

        AppCard card = new PortfolioQuery(_store, new FakeTime(Now)).Build("app")!;

        card.Critica.Should().Be(1);
        card.Alta.Should().Be(1);
        card.ActiveTotal.Should().Be(2); // resolved excluded
        card.LargeUnits.Should().Be(1);
        // auditable = total(3) - large(1) = 2; audited = 1 → 50%.
        card.Progress.Should().BeApproximately(0.5, 0.001);
    }

    [Fact]
    public void AuditingNow_true_when_a_live_claim_exists()
    {
        _store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
        _store.WriteClaim("app", new Claim { Unit = "a.cs", Module = "M", By = "maria", Machine = "PC", Utc = Now, TtlMinutes = 30 });

        AppCard card = new PortfolioQuery(_store, new FakeTime(Now.AddMinutes(5))).Build("app")!;

        card.AuditingNow.Should().BeTrue();
    }

    [Fact]
    public void Apps_with_criticals_sort_first()
    {
        _store.WriteApp(new AppConfig { Slug = "calm", Name = "Calm", RepoUrl = "u", CurrentCycle = 1 });
        _store.WriteApp(new AppConfig { Slug = "risky", Name = "Risky", RepoUrl = "u", CurrentCycle = 1 });
        _store.WriteFinding("risky", Finding("risky", Severity.Critica));

        var cards = new PortfolioQuery(_store, new FakeTime(Now)).BuildAll();

        cards[0].Slug.Should().Be("risky");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void La_tarjeta_dice_QUIEN_esta_auditando_y_cuantas_unidades()
    {
        _store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
        _store.WriteClaim("app", new Claim
        {
            Unit = "a.cs", Module = "M", By = "Daniel Rodriguez", Machine = "PC", Utc = Now, TtlMinutes = 30,
        });
        _store.WriteClaim("app", new Claim
        {
            Unit = "b.cs", Module = "M", By = "Daniel Rodriguez", Machine = "PC", Utc = Now, TtlMinutes = 30,
        });
        _store.WriteClaim("app", new Claim
        {
            Unit = "c.cs", Module = "M", By = "Maria Lopez", Machine = "PC2", Utc = Now, TtlMinutes = 30,
        });

        AppCard card = new PortfolioQuery(_store, new FakeTime(Now.AddMinutes(5))).Build("app")!;

        card.AuditingNow.Should().BeTrue();
        card.HasAuditors.Should().BeTrue();
        card.AuditingWithoutName.Should().BeFalse("hay nombres que poner");

        // LAS DOS PERSONAS, no la primera, y la que mas lleva delante.
        card.Auditors.Select(a => a.Name).Should().Equal("Daniel Rodriguez", "Maria Lopez");
        card.Auditors[0].Label.Should().Be("Daniel Rodriguez está auditando ahora · 2 unidades");
        card.Auditors[1].Label.Should().Be("Maria Lopez está auditando ahora · 1 unidad");
    }

    [Fact]
    public void Una_reclamacion_callada_deja_de_anunciar_a_su_dueno()
    {
        _store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
        _store.WriteClaim("app", new Claim
        {
            Unit = "a.cs", Module = "M", By = "Daniel Rodriguez", Machine = "PC", Utc = Now, TtlMinutes = 600,
        });

        // El TTL lo escribe quien crea la reclamacion y aqui son diez horas; el margen de lectura
        // son treinta minutos. Manda el mas estricto: un portatil cerrado no tiene al equipo
        // entero viendo «auditando ahora» hasta que a su TTL le de la gana.
        AppCard card = new PortfolioQuery(_store, new FakeTime(Now.AddHours(2))).Build("app")!;

        card.AuditingNow.Should().BeFalse();
        card.Auditors.Should().BeEmpty();
    }
}
