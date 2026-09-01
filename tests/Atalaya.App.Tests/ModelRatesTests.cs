using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F15 — la tabla de tarifas: que viva en el hub, que se pueda editar, que valide, y que un modelo
/// sin tarifa se SEÑALE en vez de desaparecer.
/// </summary>
public sealed class ModelRatesTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly ModelRatesService _rates;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public ModelRatesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-tarifas", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
        _rates = new ModelRatesService(_hub);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ================================================================ vive en el hub

    /// <summary>
    /// Las tarifas son de la ORGANIZACIÓN: se escriben en la raíz del hub, no en cada app. Un
    /// precio no es una propiedad de la aplicación auditada, y por app habría que corregir el mismo
    /// número tantas veces como apps haya.
    /// </summary>
    [Fact]
    public void La_tabla_vive_en_la_raiz_del_hub_y_no_por_aplicacion()
    {
        _rates.EnsureSeeded();

        File.Exists(Path.Combine(_hub.HubPaths.Root, "model-rates.json")).Should().BeTrue();
        Directory.EnumerateFiles(Path.Combine(_hub.HubPaths.Root, "apps"), "model-rates.json",
            SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public void Sin_sembrar_no_hay_tabla_y_se_puede_ofrecer_sembrarla()
    {
        _rates.IsSeeded.Should().BeFalse();
        _rates.Current.Should().BeNull("«no hay tabla» y «hay tabla vacía» tienen remedios distintos");

        _rates.EnsureSeeded();

        _rates.IsSeeded.Should().BeTrue();
        _rates.Current!.Rates.Should().NotBeEmpty();
    }

    /// <summary>
    /// Sembrar NO pisa lo que la organización ya haya corregido. Una tarifa editada a mano vale más
    /// que la que trae la versión: es la que alguien fue a comprobar.
    /// </summary>
    [Fact]
    public void Sembrar_no_pisa_una_tabla_existente()
    {
        _rates.Save(new ModelRateTable { Rates = { new ModelRate("mio", 1m, 2m, 0.1m) } });

        _rates.EnsureSeeded().Rates.Should().ContainSingle().Which.Model.Should().Be("mio");
    }

    // ================================================================ el modelo sin tarifa

    /// <summary>
    /// Un modelo usado y sin tarifa se SEÑALA con cuántas sesiones lo esperan. Sin esta lista, un
    /// modelo nuevo se traduce en agregados parciales y nadie sabe qué falta por añadir.
    /// </summary>
    [Fact]
    public void Los_modelos_usados_sin_tarifa_se_señalan_con_su_recuento()
    {
        _rates.Save(new ModelRateTable { Rates = { new ModelRate("conocido", 1m, 2m, 0.1m) } });
        WriteSession("conocido");
        WriteSession("recien-salido");
        WriteSession("recien-salido");

        IReadOnlyList<(string Model, string? Provider, int Sessions)> missing = _rates.ModelsWithoutRate();

        missing.Should().ContainSingle();
        missing[0].Model.Should().Be("recien-salido");
        missing[0].Sessions.Should().Be(2);
    }

    /// <summary>
    /// Y el agregado que las contiene sale PARCIAL, con su recuento. Un total al que le falta gasto
    /// se lee como si fuera el gasto entero, y ése es el error que hay que impedir.
    /// </summary>
    [Fact]
    public void El_agregado_con_un_modelo_sin_tarifa_se_declara_parcial()
    {
        _rates.Save(TestRates.Table());
        WriteSession(TestRates.Model, outputTokens: TestRates.OutputFor(10m));
        WriteSession("sin-tarifa", outputTokens: 50_000);

        MetricsDashboard d = new MetricsQuery(_hub).Build(new MetricsFilter(null, MetricsRange.All));

        d.CostIsPartial.Should().BeTrue();
        d.PartialCostSessions.Should().Be(1);
        d.PartialCostNotice.Should().Contain("1 sesión").And.Contain("no está contada");
        d.CostInPeriod.Should().Be(10m, "lo que sí se sabe se sigue contando");
    }

    /// <summary>Con todo tarifado, nada de parcial: el aviso no aparece porque no hay nada que avisar.</summary>
    [Fact]
    public void Con_todo_tarifado_el_agregado_no_se_declara_parcial()
    {
        _rates.Save(TestRates.Table());
        WriteSession(TestRates.Model, outputTokens: TestRates.OutputFor(10m));

        MetricsDashboard d = new MetricsQuery(_hub).Build(new MetricsFilter(null, MetricsRange.All));

        d.CostIsPartial.Should().BeFalse();
        d.PartialCostSessions.Should().Be(0);
    }

    /// <summary>
    /// Una sesión SIN tokens no es «parcial»: es que no hay nada que valorar. Contarla como parcial
    /// mancharía el aviso con sesiones que nunca tuvieron coste, y un aviso que salta siempre se
    /// aprende a ignorar.
    /// </summary>
    [Fact]
    public void Una_sesion_sin_tokens_no_cuenta_como_parcial()
    {
        _rates.Save(TestRates.Table());
        WriteSession(TestRates.Model, outputTokens: TestRates.OutputFor(10m));
        WriteSession("sin-tarifa", outputTokens: 0);

        MetricsDashboard d = new MetricsQuery(_hub).Build(new MetricsFilter(null, MetricsRange.All));

        d.CostIsPartial.Should().BeFalse();
    }

    // ================================================================ la pantalla

    [Fact]
    public void La_pantalla_abre_sembrada_y_dice_de_donde_salen_las_tarifas()
    {
        var vm = new ModelRatesViewModel(_rates);

        vm.Rows.Should().NotBeEmpty();
        vm.Source.Should().Contain("docs.github.com");
    }

    [Fact]
    public void Guardar_escribe_en_el_hub_y_lo_dice()
    {
        var vm = new ModelRatesViewModel(_rates);
        vm.Rows.Clear();
        vm.Rows.Add(new RateRow { Model = "nuevo", Input = 1m, Output = 5m, CachedInput = 0.1m });

        vm.SaveCommand.Execute(null);

        vm.Saved.Should().BeTrue();
        vm.Status.Should().Contain("Guardadas 1");
        _rates.Current!.Find("nuevo", "copilot")!.OutputPerMillion.Should().Be(5m);
    }

    /// <summary>
    /// La caché escrita en blanco significa «este modelo no la cobra aparte», y eso NO es cero.
    /// Cero afirmaría que escribir en caché es gratis, que es otra cosa y que nadie ha dicho.
    /// </summary>
    [Fact]
    public void La_cache_escrita_en_blanco_no_es_cero_sino_ausencia()
    {
        var vm = new ModelRatesViewModel(_rates);
        vm.Rows.Clear();
        vm.Rows.Add(new RateRow { Model = "sin-escritura", Input = 1m, Output = 5m, CacheWrite = string.Empty });
        vm.Rows.Add(new RateRow { Model = "con-escritura", Input = 1m, Output = 5m, CacheWrite = "0" });

        vm.SaveCommand.Execute(null);

        _rates.Current!.Find("sin-escritura", null)!.CacheWritePerMillion.Should().BeNull();
        _rates.Current!.Find("con-escritura", null)!.CacheWritePerMillion.Should().Be(0m);
    }

    /// <summary>Una tarifa negativa se rechaza ENTERA: no se guarda media tabla.</summary>
    [Fact]
    public void Una_tarifa_negativa_se_rechaza_y_no_se_guarda_nada()
    {
        _rates.Save(new ModelRateTable { Rates = { new ModelRate("previo", 1m, 2m, 0.1m) } });

        var vm = new ModelRatesViewModel(_rates);
        vm.Rows.Clear();
        vm.Rows.Add(new RateRow { Model = "malo", Input = -1m, Output = 5m });

        vm.SaveCommand.Execute(null);

        vm.Saved.Should().BeFalse();
        vm.Status.Should().Contain("no puede ser negativa");
        _rates.Current!.Rates.Should().ContainSingle().Which.Model.Should().Be("previo");
    }

    [Fact]
    public void Un_modelo_repetido_para_el_mismo_proveedor_se_rechaza()
    {
        var vm = new ModelRatesViewModel(_rates);
        vm.Rows.Clear();
        vm.Rows.Add(new RateRow { Model = "dup", Input = 1m, Output = 2m });
        vm.Rows.Add(new RateRow { Model = "dup", Input = 3m, Output = 4m });

        vm.SaveCommand.Execute(null);

        vm.Saved.Should().BeFalse();
        vm.Status.Should().Contain("repetido");
    }

    /// <summary>
    /// El mismo modelo con proveedores distintos SÍ se admite: es lo que permite que Claude Sonnet 5
    /// cueste una cosa por Copilot (caché de 5 min) y otra por Claude Code (caché de 1 h).
    /// </summary>
    [Fact]
    public void El_mismo_modelo_con_dos_proveedores_es_legitimo()
    {
        var vm = new ModelRatesViewModel(_rates);
        vm.Rows.Clear();
        vm.Rows.Add(new RateRow { Model = "claude-sonnet-5", Input = 2m, Output = 10m, CacheWrite = "2,5" });
        vm.Rows.Add(new RateRow { Model = "claude-sonnet-5", Provider = "claude-code", Input = 2m, Output = 10m, CacheWrite = "4" });

        vm.SaveCommand.Execute(null);

        vm.Saved.Should().BeTrue();
        _rates.Current!.Find("claude-sonnet-5", "copilot")!.CacheWritePerMillion.Should().Be(2.5m);
        _rates.Current!.Find("claude-sonnet-5", "claude-code")!.CacheWritePerMillion.Should().Be(4m);
    }

    [Fact]
    public void Una_tabla_vacia_se_rechaza_porque_dejaria_todo_sin_calcular()
    {
        var vm = new ModelRatesViewModel(_rates);
        vm.Rows.Clear();

        vm.SaveCommand.Execute(null);

        vm.Saved.Should().BeFalse();
        vm.Status.Should().Contain("se quedaría vacía");
    }

    // ================================================================ ayudas

    private void WriteSession(string model, long outputTokens = 1000)
    {
        var session = new AuditSession
        {
            Id = _ulids.NewUlid(),
            AppSlug = "app",
            Mode = AuditMode.Lotes,
            By = "alvaro",
            Machine = "PC",
            StartedUtc = DateTimeOffset.UtcNow.AddHours(-1),
            EndedUtc = DateTimeOffset.UtcNow,
            CycleN = 1,
            Model = model,
            Provider = "copilot",
        };

        session.Units.Add(new UnitVerdictRecord("src/U.cs", "src", "auditada", null));
        session.Usage.Add(0, outputTokens, 0, 0, null);
        _hub.Store.WriteSession(session);
    }
}
