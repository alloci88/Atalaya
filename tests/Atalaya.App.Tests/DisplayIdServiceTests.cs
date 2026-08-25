using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// El reparto de alias legibles (F5.6 §3). El defecto: `DisplayId.Next` llevaba escrito desde el
/// §2 y <b>no lo llamaba nadie</b>, así que los 25 hallazgos del hub real mostraban «(sin alias
/// todavía)» y el contador de la aplicación seguía a cero tras decenas de pushes (D-227).
/// </summary>
public sealed class DisplayIdServiceTests : IDisposable
{
    private readonly string _root;
    private readonly HubContext _hub;
    private readonly DisplayIdService _aliases;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public DisplayIdServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-alias", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(_root, "local"));
        var settings = new SettingsService(paths);
        settings.Load();
        _hub = TestFactory.Hub(paths, settings);
        _aliases = new DisplayIdService(_hub);

        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>El caso del parte: hallazgos guardados sin alias, y el backfill se los da.</summary>
    [Fact]
    public void El_backfill_reparte_alias_a_todo_lo_que_no_tenia()
    {
        Finding a = Seed(Pillar.Errores);
        Finding b = Seed(Pillar.Errores);
        Finding c = Seed(Pillar.Optimizacion);

        _aliases.AssignPending("app").Should().Be(3);

        Read(a).DisplayId.Should().Be("BUG-0001");
        Read(b).DisplayId.Should().Be("BUG-0002");
        Read(c).DisplayId.Should().Be("OPT-0001", "cada pilar lleva su propio contador");
    }

    /// <summary>
    /// Idempotente por construcción: solo mira los que están a <c>null</c>. Volver a pasarlo tras
    /// cada push —que es lo que hace la aplicación— no puede renumerar nada ni tocar el contador.
    /// </summary>
    [Fact]
    public void Pasarlo_dos_veces_no_reparte_nada_nuevo_ni_avanza_el_contador()
    {
        Finding a = Seed(Pillar.Errores);
        _aliases.AssignPending("app");
        string primero = Read(a).DisplayId!;

        _aliases.AssignPending("app").Should().Be(0);

        Read(a).DisplayId.Should().Be(primero);
        _hub.Store.TryReadApp("app")!.DisplayIdCounters["BUG"].Should().Be(1);
    }

    /// <summary>Un alias que ya venía puesto no se toca: el ULID es la identidad, pero el alias es el nombre.</summary>
    [Fact]
    public void Un_hallazgo_que_ya_tenia_alias_conserva_el_suyo()
    {
        Finding viejo = Seed(Pillar.Errores, displayId: "BUG-0099");
        Finding nuevo = Seed(Pillar.Errores);

        _aliases.AssignPending("app").Should().Be(1);

        Read(viejo).DisplayId.Should().Be("BUG-0099");
        Read(nuevo).DisplayId.Should().Be("BUG-0001");
    }

    /// <summary>
    /// El contador de la aplicación avanza y se persiste — que era lo que nunca pasaba: en el hub
    /// real seguía siendo <c>{}</c> después de todo un ciclo de auditorías.
    /// </summary>
    [Fact]
    public void El_contador_por_pilar_se_persiste_en_la_aplicacion()
    {
        Seed(Pillar.Errores);
        Seed(Pillar.Mejoras);
        Seed(Pillar.Mejoras);

        _aliases.AssignPending("app");

        AppConfig app = _hub.Store.TryReadApp("app")!;
        app.DisplayIdCounters["BUG"].Should().Be(1);
        app.DisplayIdCounters["MEJ"].Should().Be(2);
    }

    /// <summary>Numerar en orden de ULID es numerar en orden de detección: el alias sigue la historia.</summary>
    [Fact]
    public void Los_alias_siguen_el_orden_de_creacion_no_el_del_sistema_de_ficheros()
    {
        var creados = new List<Finding>();
        for (int i = 0; i < 5; i++)
        {
            creados.Add(Seed(Pillar.Errores));
        }

        _aliases.AssignPending("app");

        creados.Select(f => Read(f).DisplayId)
            .Should().ContainInOrder("BUG-0001", "BUG-0002", "BUG-0003", "BUG-0004", "BUG-0005");
    }

    [Fact]
    public void Una_aplicacion_que_no_existe_no_revienta_ni_reparte_nada()
        => _aliases.AssignPending("no-existe").Should().Be(0);

    [Fact]
    public void El_backfill_recorre_todas_las_aplicaciones_del_hub()
    {
        _hub.Store.WriteApp(new AppConfig { Slug = "otra", Name = "Otra", RepoUrl = "u", CurrentCycle = 1 });
        Seed(Pillar.Errores);
        Seed(Pillar.Errores, slug: "otra");

        _aliases.BackfillAll().Should().Be(2);
    }

    private Finding Read(Finding f) => _hub.Store.TryReadFinding("app", f.Id.ToString())!;

    private Finding Seed(Pillar pillar, string? displayId = null, string slug = "app")
    {
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "abc", "alvaro");
        var f = new Finding
        {
            Id = _ulids.NewUlid(),
            DisplayId = displayId,
            RuleId = "errores.recursos.no-liberado",
            Pillar = pillar,
            Severity = Severity.Alta,
            Confidence = Confidence.Media,
            Title = "un hallazgo",
            Locations = { new Location("A.cs", 2, null) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
        _hub.Store.WriteFinding(slug, f);
        return f;
    }
}
