using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.14 — la narración de V5 no puede contar una historia distinta de la sesión.
/// <para>
/// El parte: durante la primera pasada, la columna de actividad marcaba con ⚖ hallazgos que no
/// estaban disputados; al terminar, el resumen decía 0 disputados y los hallazgos persistidos
/// tampoco lo estaban. Dos relatos del mismo hecho, y el que el usuario ve primero era el falso.
/// </para>
/// <para>
/// Es la misma lección del bug de selección: dos cálculos paralelos de la misma verdad acaban
/// divergiendo. Aquí el invariante es que <b>lo que se narra en vivo y lo que se escribe en el hub
/// son el mismo suceso contado dos veces</b>, así que sus cuentas tienen que cuadrar.
/// </para>
/// </summary>
public sealed class LiveNarrationTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private readonly ServiceProvider _provider;
    private readonly MachineConfigStore _machines;
    private const string RepoUrl = "https://example.invalid/org/app.git";
    private const string UnitPath = "src/A.cs";

    public LiveNarrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-narration", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _settings = new SettingsService(_paths);
        _settings.Load();
        AppSettings s = _settings.Current;
        s.MaxPassesPerUnit = 1;
        _settings.Save(s);

        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "app", Name = "App", RepoUrl = RepoUrl, Stack = TechStack.DotNet, CurrentCycle = 1,
        });

        string clone = Path.Combine(_root, "clone");
        TestFactory.MakeClone(clone, RepoUrl);
        Directory.CreateDirectory(Path.Combine(clone, "src"));
        File.WriteAllText(Path.Combine(clone, "src", "A.cs"), "class A { void M() { } }");
        _machines.SetClonePath("app", clone);
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units = { new InventoryUnit { Path = UnitPath, Module = "M", State = UnitState.Pendiente } },
        });

        var services = new ServiceCollection();
        services.AddSingleton(_hub);
        services.AddSingleton(_paths);
        services.AddSingleton(_settings);
        services.AddSingleton(_machines);
        services.AddSingleton<IUlidFactory>(_ulids);
        services.AddSingleton<FindingIngestionService>();
        services.AddSingleton<ReconciliationService>();
        services.AddSingleton<OpenSessionStore>();
        services.AddTransient<SessionCoordinator>();
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    /// <summary>Un hallazgo ya existente en la unidad, visto en una sesión anterior.</summary>
    private Finding SeedFinding(string title)
    {
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow.AddDays(-3), AuditMode.Lotes, "viejo", "alvaro");
        var f = new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = "errores.null.desreferencia",
            Pillar = Pillar.Errores,
            Severity = Severity.Alta,
            Confidence = Confidence.Media,
            Title = title,
            Locations = { new Location(UnitPath, 1) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
        _hub.Store.WriteFinding("app", f);
        return f;
    }

    private static SubmitFindingArgs NewFinding(string title)
        => new("errores.recursos.no-liberado", "errores", "media", title, "desc", "impacto", "reco",
            new[] { new SubmitLocation(UnitPath, 1, null) }, "M");

    /// <summary>
    /// Corre una sesión de verdad a través del servicio en vivo, de modo que la narración se puebla
    /// por el mismo camino que en producción, y devuelve el servicio ya cerrado.
    /// </summary>
    private async Task<(LiveSessionService Live, SessionResult Result)> Run(
        Func<AuditUnitRequest, IEnumerable<SubmitFindingArgs>>? audit = null,
        Func<AuditUnitRequest, IEnumerable<VerdictArgs>>? reconcile = null)
    {
        var agent = new FakeCopilotAgent(auditScript: audit, reconcileScript: reconcile);
        SessionResult? captured = null;
        var live = new LiveSessionService(
            () => new SessionCoordinator(
                _hub, _provider.GetRequiredService<FindingIngestionService>(),
                _provider.GetRequiredService<ReconciliationService>(), _machines, _ulids, agent, _settings),
            agent, _provider.GetRequiredService<OpenSessionStore>(), _hub);
        live.Completed += r => captured = r;

        await live.StartAsync(new SessionRequest("app", AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath });
        for (int i = 0; i < 400 && live.IsRunning; i++)
        {
            await Task.Delay(25);
        }

        live.IsRunning.Should().BeFalse("la sesión de prueba tiene que haber terminado");
        captured.Should().NotBeNull("sin resultado no hay con qué contrastar la narración");
        return (live, captured!);
    }

    /// <summary>Todas las líneas de actividad narradas en la sesión, de todas las unidades y pasadas.</summary>
    private static List<ActivityEntry> Narration(LiveSessionService live)
        => live.Units.SelectMany(u => u.Passes).SelectMany(p => p.Entries).Where(e => e.IsEvent).ToList();

    private static int Glyphs(LiveSessionService live, string glyph)
        => Narration(live).Count(e => e.Glyph == glyph);

    private static SummaryLine? Line(LiveSessionService live, string label)
        => live.Summary.FirstOrDefault(l => l.Label == label);

    // ================================================================ el parte

    /// <summary>
    /// <b>El bug.</b> Una sesión que solo encuentra hallazgos nuevos no puede narrar ni una sola
    /// disputa. El ⚖ colgaba de <c>Disputes.Count</c> a través de un conversor que daba «visible»
    /// a cualquier valor no nulo — el cero incluido—, así que se pintaba en TODOS.
    /// </summary>
    [Fact]
    public async Task Sin_disputas_no_se_narra_ninguna_disputa()
    {
        (LiveSessionService live, SessionResult result) = await Run(
            audit: _ => new[] { NewFinding("fuga de stream"), NewFinding("handle sin cerrar") });

        result.Counters.Disputed.Should().Be(0, "nadie ha disputado nada");
        result.Counters.New.Should().Be(2);

        Glyphs(live, "⚖").Should().Be(0, "la narración no puede inventarse disputas");
        Glyphs(live, "＋").Should().Be(2);
        Line(live, "Disputados").Should().BeNull("una línea de resumen sin contenido no se pinta");

        live.Findings.Should().OnlyContain(f => f.Disputes.Count == 0);
        _hub.Store.ListFindings("app").Should().OnlyContain(f => f.Disputes.Count == 0);
    }

    /// <summary>
    /// Y la insignia ⚖ de la fila: con cero disputas se esconde. Es el conversor el que mentía, así
    /// que se interroga directamente con lo que la vista le enlaza.
    /// </summary>
    [Fact]
    public void La_insignia_de_disputa_se_esconde_con_cero_disputas()
    {
        IValueConverter converter = new Atalaya.App.NotEmptyToVisibilityConverter();

        object Convert(object value) => converter.Convert(value, typeof(Visibility), null!, CultureInfo.InvariantCulture);

        Convert(0).Should().Be(Visibility.Collapsed, "cero disputas es NINGUNA disputa");
        Convert(1).Should().Be(Visibility.Visible);
        Convert(new List<int>()).Should().Be(Visibility.Collapsed, "una colección vacía está vacía");
        Convert(new List<int> { 1 }).Should().Be(Visibility.Visible);
        Convert(false).Should().Be(Visibility.Collapsed);
        Convert(null!).Should().Be(Visibility.Collapsed);
        // Y lo de siempre sigue igual: es un conversor de cadenas en la mayoría de sus usos.
        Convert("  ").Should().Be(Visibility.Collapsed);
        Convert("algo").Should().Be(Visibility.Visible);
    }

    /// <summary>La vista enlaza la insignia al recuento de disputas del propio hallazgo, y a nada más.</summary>
    [Fact]
    public void La_insignia_se_enlaza_al_recuento_de_disputas()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        string xaml = File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "Atalaya.App", "Views", "SessionView.xaml"));

        xaml.Should().Contain("{Binding Disputes.Count, Converter={StaticResource NotEmptyToVisibility}}");
    }

    // ================================================================ el invariante

    /// <summary>
    /// <b>Una sola verdad.</b> Con una sesión que produce de todo —una reconfirmación, una disputa
    /// y un «arreglado» sin evidencia de cambio—, cada glifo de la narración en vivo tiene que
    /// aparecer tantas veces como dice el contador de la sesión.
    /// <para>
    /// Son DOS sesiones a propósito: para que un «arreglado» se degrade hace falta que el hallazgo
    /// se hubiera visto en este mismo commit y con esta misma unidad (F5.1b), y eso solo lo consigue
    /// una sesión anterior de verdad — sembrarlo a mano deja un sello viejo que sí acredita cambio.
    /// </para>
    /// </summary>
    [Fact]
    public async Task La_narracion_en_vivo_cuadra_con_los_contadores_de_la_sesion()
    {
        Finding presente = SeedFinding("sigue ahí");
        Finding disputado = SeedFinding("el auditor discrepa");

        // Primera sesión: nace un hallazgo con el sello de ESTE commit y ESTA unidad.
        (LiveSessionService first, SessionResult firstResult) = await Run(
            audit: _ => new[] { NewFinding("fuga de stream") },
            reconcile: _ => new[]
            {
                new VerdictArgs(presente.Id.ToString(), "presente", "sigue"),
                new VerdictArgs(disputado.Id.ToString(), "presente", "sigue"),
            });
        firstResult.Counters.New.Should().Be(1);
        Glyphs(first, "＋").Should().Be(1, "también en la primera: un ＋ por hallazgo nuevo");
        Glyphs(first, "⚖").Should().Be(0, "y ni una disputa donde no la hubo");

        Finding recien = _hub.Store.ListFindings("app").Single(f => f.Title == "fuga de stream");

        // Segunda sesión, sin tocar el código: el «arreglado» sobre el recién nacido no tiene
        // evidencia de cambio y se degrada; el otro se disputa.
        (LiveSessionService live, SessionResult result) = await Run(
            reconcile: _ => new[]
            {
                new VerdictArgs(presente.Id.ToString(), "presente", "sigue en el código"),
                new VerdictArgs(disputado.Id.ToString(), "no-es-defecto", "esto nunca fue un defecto"),
                new VerdictArgs(recien.Id.ToString(), "arreglado", "ya no está"),
            });

        SessionCounters c = result.Counters;
        c.New.Should().Be(0);
        c.Disputed.Should().Be(1);
        c.ResolutionsRefused.Should().Be(1, "la unidad no cambió: «arreglado» se degrada a presente");

        Glyphs(live, "＋").Should().Be(c.New, "un ＋ por hallazgo nuevo");
        Glyphs(live, "⚖").Should().Be(c.Disputed, "un ⚖ por disputa REAL");
        Glyphs(live, "✔").Should().Be(c.Resolved, "un ✔ por resolución");
        Glyphs(live, "⚠").Should().Be(c.ResolutionsRefused, "un ⚠ por «arreglado» degradado");
    }

    /// <summary>
    /// Y la pantalla de cierre cuadra con lo que se ESCRIBIÓ en el hub, no con lo que la narración
    /// creyó ver: es el mismo suceso contado dos veces y las dos cuentas tienen que dar lo mismo.
    /// </summary>
    [Fact]
    public async Task El_resumen_en_vivo_cuadra_con_la_sesion_persistida()
    {
        Finding presente = SeedFinding("sigue ahí");
        Finding disputado = SeedFinding("el auditor discrepa");

        (LiveSessionService live, SessionResult result) = await Run(
            audit: _ => new[] { NewFinding("fuga de stream") },
            reconcile: _ => new[]
            {
                new VerdictArgs(presente.Id.ToString(), "presente", "sigue en el código"),
                new VerdictArgs(disputado.Id.ToString(), "no-es-defecto", "nunca fue un defecto"),
            });

        AuditSession persisted = _hub.Store.ListSessions("app").Should().ContainSingle().Subject;
        SessionCounters c = persisted.Counters;

        Line(live, "Nuevos")!.Count.Should().Be(c.New);
        Line(live, "Confirmados")!.Count.Should().Be(c.Confirmed);
        Line(live, "Disputados")!.Count.Should().Be(c.Disputed);

        // Y el detalle de una línea nombra exactamente tantos casos como cuenta, allí donde el
        // contador y la lista narran lo mismo. «Confirmados» queda fuera a propósito: suma también
        // los «arreglado» degradados, que tienen su propia línea y su propio detalle.
        foreach (string label in new[] { "Nuevos", "Disputados" })
        {
            SummaryLine line = Line(live, label)!;
            line.Named.Should().HaveCount(line.Count, $"«{label}» dice {line.Count}: tiene que nombrar {line.Count}");
        }

        // El estado persistido manda: la disputa está en el hallazgo, y solo en ese.
        _hub.Store.TryReadFinding("app", disputado.Id.ToString())!.Disputes.Should().ContainSingle();
        _hub.Store.TryReadFinding("app", presente.Id.ToString())!.Disputes.Should().BeEmpty();
    }

    /// <summary>
    /// Las ubicaciones añadidas se narran con su propio glifo y no se confunden con un hallazgo
    /// nuevo: extender un defecto sistémico no crea nada.
    /// </summary>
    [Fact]
    public async Task Las_ubicaciones_anadidas_se_narran_como_tales()
    {
        Finding existente = SeedFinding("no valida argumentos nulos");

        var agent = new FakeCopilotAgent(
            reconcileScript: _ => new[] { new VerdictArgs(existente.Id.ToString(), "presente", "sigue") },
            extendScript: _ => new[]
            {
                new AddLocationsArgs(existente.Id.ToString(), new[] { new SubmitLocation(UnitPath, 1, null) }),
            });

        var live = new LiveSessionService(
            () => new SessionCoordinator(
                _hub, _provider.GetRequiredService<FindingIngestionService>(),
                _provider.GetRequiredService<ReconciliationService>(), _machines, _ulids, agent, _settings),
            agent, _provider.GetRequiredService<OpenSessionStore>(), _hub);
        SessionResult? result = null;
        live.Completed += r => result = r;

        await live.StartAsync(new SessionRequest("app", AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath });
        for (int i = 0; i < 400 && live.IsRunning; i++)
        {
            await Task.Delay(25);
        }

        Glyphs(live, "＋").Should().Be(0, "extender no crea un hallazgo");
        Glyphs(live, "⚖").Should().Be(0);
        result!.Counters.New.Should().Be(0);
        result.Counters.Disputed.Should().Be(0);
    }

    /// <summary>
    /// Todo glifo que la narración pinta pertenece al repertorio conocido: los de hallazgo (＋ ⊕ ⚖
    /// ⚠ ✔) y los de cierre de pasada (✓ seca, ↻ con aportación). Si algún día se añade un suceso y
    /// se olvida su caso —o se cuela una etiqueta de otro sitio—, se ve aquí en vez de en una
    /// insignia que miente.
    /// </summary>
    [Fact]
    public async Task La_narracion_no_pinta_glifos_que_nadie_emite()
    {
        Finding presente = SeedFinding("sigue ahí");
        (LiveSessionService live, _) = await Run(
            audit: _ => new[] { NewFinding("fuga de stream") },
            reconcile: _ => new[] { new VerdictArgs(presente.Id.ToString(), "presente", "sigue") });

        Narration(live).Select(e => e.Glyph).Distinct()
            .Should().BeSubsetOf(new[] { "＋", "⊕", "⚖", "⚠", "✔", "✓", "↻" });
    }

    /// <summary>
    /// «Presente» NO tiene glifo propio en la columna: se cuenta en el cierre de pasada
    /// («veredictos: N presente») y en la línea «Confirmados» del resumen. Queda fijado aquí porque
    /// es la pregunta natural al leer el resto de casos, y la respuesta —«se narra, pero agregado»—
    /// no se deduce mirando el switch.
    /// </summary>
    [Fact]
    public async Task Presente_se_narra_agregado_en_el_cierre_de_pasada()
    {
        Finding presente = SeedFinding("sigue ahí");
        (LiveSessionService live, SessionResult result) = await Run(
            reconcile: _ => new[] { new VerdictArgs(presente.Id.ToString(), "presente", "sigue") });

        result.Counters.Confirmed.Should().Be(1);
        Narration(live).Should().Contain(e => e.Text.Contains("1 presente"),
            "el cierre de pasada es donde se ven las reconfirmaciones");
        Line(live, "Confirmados")!.Count.Should().Be(result.Counters.Confirmed);
    }
}
