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
        _hub.SeedModelRates();

        File.Exists(Path.Combine(_hub.HubPaths.Root, "model-rates.json")).Should().BeTrue();
        Directory.EnumerateFiles(Path.Combine(_hub.HubPaths.Root, "apps"), "model-rates.json",
            SearchOption.AllDirectories).Should().BeEmpty();
    }

    /// <summary>
    /// <b>Abrir el hub siembra</b> (R2 §2), sin preguntar y sin que nadie visite ninguna pantalla.
    /// Hasta R2 esto lo hacía el constructor de la pantalla de tarifas, así que un hub recién
    /// clonado no tenía tabla hasta que a alguien se le ocurría abrirla.
    /// </summary>
    [Fact]
    public void Abrir_el_hub_siembra_la_tabla_sin_que_nadie_la_pida()
    {
        _rates.Current.Should().BeNull("el hub recién clonado no trae el fichero");

        _hub.SeedModelRates();

        _rates.IsSeeded.Should().BeTrue();
        _rates.Current!.Rates.Should().NotBeEmpty();
    }

    /// <summary>
    /// Sembrar NO pisa lo que la organización ya haya corregido. Una tarifa editada a mano vale más
    /// que la que trae la versión: es la que alguien fue a comprobar.
    /// </summary>
    [Fact]
    public void Sembrar_no_pisa_una_tarifa_editada()
    {
        // Un modelo que la siembra SÍ trae, con un precio que alguien corrigió a mano.
        _rates.Save(new ModelRateTable { Rates = { new ModelRate("gpt-5.4", string.Empty, 99m, 98m, 97m) } });

        _hub.SeedModelRates();

        ModelRate corregida = _rates.Current!.Find("gpt-5.4", "copilot")!;
        corregida.InputPerMillion.Should().Be(99m, "lo editado manda sobre lo sembrado, siempre");
        corregida.OutputPerMillion.Should().Be(98m);
    }

    /// <summary>
    /// <b>Y rellena lo que falte</b>: el criterio es por MODELO y no por tabla. La regla por tabla
    /// dejaba a un hub que ya tenía tarifas sin recibir nunca las de un modelo nuevo — que es lo que
    /// se traduce en «tarifa no configurada» y en un agregado parcial.
    /// </summary>
    [Fact]
    public void Sembrar_rellena_los_modelos_que_la_tabla_no_conocia()
    {
        _rates.Save(new ModelRateTable { Rates = { new ModelRate("mio", string.Empty, 1m, 2m, 0.1m) } });

        _hub.SeedModelRates();

        _rates.Current!.Find("mio", "copilot").Should().NotBeNull("lo de casa no se toca");
        _rates.Current!.Find("gpt-5.4", "copilot").Should().NotBeNull("y lo que faltaba, se añade");
    }

    /// <summary>
    /// Con la tabla al día no se escribe nada. Sin esto, cada apertura del hub dejaría un commit
    /// idéntico al anterior en el historial que ES la atribución de esta tabla (D-786).
    /// </summary>
    [Fact]
    public void Sembrar_dos_veces_no_vuelve_a_escribir()
    {
        _hub.SeedModelRates();
        string path = Path.Combine(_hub.HubPaths.Root, "model-rates.json");
        DateTime first = File.GetLastWriteTimeUtc(path);

        _hub.SeedModelRates();

        File.GetLastWriteTimeUtc(path).Should().Be(first, "no había nada que añadir");
    }

    // ================================================================ el modelo sin tarifa

    /// <summary>
    /// Un modelo usado y sin tarifa se SEÑALA con cuántas sesiones lo esperan. Sin esta lista, un
    /// modelo nuevo se traduce en agregados parciales y nadie sabe qué falta por añadir.
    /// </summary>
    [Fact]
    public void Los_modelos_usados_sin_tarifa_se_señalan_con_su_recuento()
    {
        _rates.Save(new ModelRateTable { Rates = { new ModelRate("conocido", string.Empty, 1m, 2m, 0.1m) } });
        WriteSession("conocido");
        WriteSession("recien-salido");
        WriteSession("recien-salido");

        IReadOnlyList<(string Model, string? Provider, int Sessions)> missing = _rates.ModelsWithoutRate();

        missing.Should().ContainSingle();
        missing[0].Model.Should().Be("recien-salido");
        missing[0].Sessions.Should().Be(2);
    }

    /// <summary>
    /// <b>Y «modelos sin tarifa» no lista modelos de una casa que no factura</b> (F16-RETOQUE §1).
    /// A un modelo que solo se ha usado con Claude Code no le falta ninguna tarifa: es que no lleva
    /// ninguna. Listarlo aquí pediría configurar un precio que no debe existir — y quien lo
    /// configurara empezaría a ver un coste donde no lo hay.
    /// </summary>
    [Fact]
    public void Los_modelos_de_una_casa_que_no_factura_no_estan_esperando_tarifa()
    {
        _rates.Save(new ModelRateTable { Rates = { new ModelRate("conocido", string.Empty, 1m, 2m, 0.1m) } });
        WriteSession("claude-opus-5", provider: "claude-code");
        WriteSession("recien-salido");

        IReadOnlyList<(string Model, string? Provider, int Sessions)> missing = _rates.ModelsWithoutRate();

        missing.Should().ContainSingle().Which.Model.Should().Be("recien-salido");
        missing.Should().NotContain(m => m.Model.Contains("claude", StringComparison.OrdinalIgnoreCase));
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
        _hub.SeedModelRates();

        var vm = new ModelRatesViewModel(_rates);

        vm.Rows.Should().NotBeEmpty();
        vm.Source.Should().Contain("docs.github.com");
    }

    [Fact]
    public void Guardar_escribe_en_el_hub_y_lo_dice()
    {
        var vm = new ModelRatesViewModel(_rates);
        vm.Rows.Clear();
        vm.Rows.Add(new RateRow
        {
            Model = "nuevo", Provider = "copilot", Input = 1m, Output = 5m, CachedInput = 0.1m,
        });

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
        vm.Rows.Add(new RateRow
        {
            Model = "sin-escritura", Provider = "copilot", Input = 1m, Output = 5m,
            CacheWrite = string.Empty,
        });
        vm.Rows.Add(new RateRow
        {
            Model = "con-escritura", Provider = "copilot", Input = 1m, Output = 5m, CacheWrite = "0",
        });

        vm.SaveCommand.Execute(null);

        _rates.Current!.Find("sin-escritura", "copilot")!.CacheWritePerMillion.Should().BeNull();
        _rates.Current!.Find("con-escritura", "copilot")!.CacheWritePerMillion.Should().Be(0m);
    }

    /// <summary>Una tarifa negativa se rechaza ENTERA: no se guarda media tabla.</summary>
    [Fact]
    public void Una_tarifa_negativa_se_rechaza_y_no_se_guarda_nada()
    {
        _rates.Save(new ModelRateTable { Rates = { new ModelRate("previo", string.Empty, 1m, 2m, 0.1m) } });

        var vm = new ModelRatesViewModel(_rates);
        vm.Rows.Clear();
        vm.Rows.Add(new RateRow { Model = "malo", Provider = "copilot", Input = -1m, Output = 5m });

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
        vm.Rows.Add(new RateRow { Model = "dup", Provider = "copilot", Input = 1m, Output = 2m });
        vm.Rows.Add(new RateRow { Model = "dup", Provider = "copilot", Input = 3m, Output = 4m });

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
        vm.Rows.Add(new RateRow
        {
            Model = "claude-sonnet-5", Provider = "copilot", Input = 2m, Output = 10m, CacheWrite = "2,5",
        });
        vm.Rows.Add(new RateRow { Model = "claude-sonnet-5", Provider = "otra-reventa", Input = 2m, Output = 10m, CacheWrite = "4" });

        vm.SaveCommand.Execute(null);

        vm.Saved.Should().BeTrue();
        _rates.Current!.Find("claude-sonnet-5", "copilot")!.CacheWritePerMillion.Should().Be(2.5m);
        _rates.Current!.Find("claude-sonnet-5", "otra-reventa")!.CacheWritePerMillion.Should().Be(4m);
    }

    /// <summary>
    /// <b>Y una tarifa SIN PROVEEDOR se rechaza entera</b> (PROV-2 §3). Aquí se rechazaba la de
    /// una casa que no facturaba; ahora la de cualquier casa es legítima, y lo que no se puede es
    /// dejarla sin dueño: el coste se tarifa por proveedor + modelo, así que una tarifa sin
    /// proveedor no dice a quién le cobra. Se dice cuál es y no se guarda nada.
    /// <para>
    /// Lo que se rompería en silencio sin esto: una fila en blanco se guardaría como genérica y le
    /// pondría precio al consumo de TODAS las casas, incluida la que declara que no lleva ninguno.
    /// </para>
    /// </summary>
    [Fact]
    public void Una_tarifa_sin_proveedor_se_rechaza_entera()
    {
        _rates.Save(new ModelRateTable { Rates = { new ModelRate("previo", "copilot", 1m, 2m, 0.1m) } });

        var vm = new ModelRatesViewModel(_rates);
        vm.Rows.Clear();
        vm.Rows.Add(new RateRow { Model = "gpt-5.4", Input = 2.5m, Output = 15m });
        vm.Rows.Add(new RateRow { Model = "claude-opus-5", Provider = "claude-code", Input = 5m, Output = 25m });

        vm.SaveCommand.Execute(null);

        vm.Saved.Should().BeFalse();
        vm.Status.Should().Contain("gpt-5.4").And.Contain("falta el proveedor");
        vm.Status.Should().Contain("No se ha guardado nada",
            "media tabla guardada es peor que ninguna: nadie sabría qué quedó dentro");
        _rates.Current!.Rates.Should().ContainSingle().Which.Model.Should().Be("previo");
    }

    /// <summary>
    /// Un hub sembrado ANTES de este cambio tiene escritas las cuatro tarifas de <c>claude-code</c>
    /// que traía la siembra vieja. No hacen daño —el cálculo ya no las mira—, pero sí confunden:
    /// son filas editables que no gobiernan nada. La pantalla no las enseña, y desaparecen del hub
    /// la primera vez que alguien guarda.
    /// </summary>
    [Fact]
    public void La_tarifa_de_cualquier_casa_se_ensena_y_sobrevive_al_guardado()
    {
        var legacy = new ModelRateTable
        {
            Rates =
            {
                new ModelRate("gpt-5.4", "copilot", 2.5m, 15m, 0.25m),
                new ModelRate("claude-opus-5", "claude-code", 5m, 25m, 0.5m, CacheWritePerMillion: 10m),
            },
        };
        _rates.Save(legacy);

        var vm = new ModelRatesViewModel(_rates);

        // PROV-2 §3 — LA PUERTA QUE SE ABRE. Aquí se escondía la tarifa de la casa que no
        // facturaba y desaparecía al primer guardado: con el coste tarifado por proveedor +
        // modelo, esa tarifa gobierna lo suyo y borrarla sería perder lo que alguien escribió.
        vm.Rows.Should().HaveCount(2);

        vm.SaveCommand.Execute(null);

        vm.Saved.Should().BeTrue();
        _rates.Current!.Rates.Select(r => r.Provider).Should()
            .BeEquivalentTo(new[] { "copilot", "claude-code" });
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

    private void WriteSession(string model, long outputTokens = 1000, string provider = "copilot")
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
            Provider = provider,
        };

        session.Units.Add(new UnitVerdictRecord("src/U.cs", "src", "auditada", null));
        session.Usage.Add(0, outputTokens, 0, 0, null);
        _hub.Store.WriteSession(session);
    }

    // ================================================================ el filtro por proveedor

    /// <summary>
    /// <b>El filtro es de VISTA</b> (R-PROV2): «Todos» existe, es la primera opción y el arranque
    /// (F5.4 §2), y con una casa elegida solo se ven las suyas — pero la tabla que se guarda sigue
    /// siendo la entera.
    /// <para>
    /// <b>Lo que se rompería en silencio sin este test:</b> que «Guardar tarifas» recorriera lo
    /// que está pintado en vez de la colección completa. Filtrar por una casa, corregir un precio
    /// y guardar borraría del hub las tarifas de todas las demás. No hay error, no hay aviso y la
    /// pantalla se queda igual de bien: se descubre semanas después, cuando a alguien le sale
    /// «tarifa no configurada» en un modelo que sí tenía precio.
    /// </para>
    /// </summary>
    [Fact]
    public void El_filtro_por_proveedor_es_de_vista_y_guardar_no_pierde_las_otras_casas()
    {
        DateOnly hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        _rates.Save(new ModelRateTable
        {
            Rates =
            {
                new ModelRate("gpt-5", "copilot", 1m, 5m, 0.1m, null, hoy, null),
                new ModelRate("opus", "otra-reventa", 2m, 8m, 0.2m, null, hoy, null),
            },
        });

        var vm = new ModelRatesViewModel(_rates);

        vm.ProviderOptions.First().Should().Be(ModelRatesViewModel.AllProviders,
            "la opción neutra va la primera, y vale null");
        vm.SelectedProvider.Should().Be(ModelRatesViewModel.AllProviders,
            "y es con la que se abre: filtrar no puede ser un viaje sin billete de vuelta");
        vm.Visible.Cast<RateRow>().Select(r => r.Model).Should().BeEquivalentTo(
            new[] { "gpt-5", "opus" }, "con «Todos» se ven todas");
        vm.ShowProviderColumn.Should().BeTrue("y la columna que dice de quién es cada una, también");

        vm.SelectedProvider = vm.ProviderOptions.First(o =>
            string.Equals(o.Id, "otra-reventa", StringComparison.OrdinalIgnoreCase));

        vm.Visible.Cast<RateRow>().Select(r => r.Model).Should().Equal(
            new[] { "opus" }, "con una casa elegida solo se ven las suyas");
        vm.ShowProviderColumn.Should().BeFalse(
            "su columna diría lo mismo en todas las filas, así que se esconde");
        vm.Rows.Should().HaveCount(2, "pero la tabla sigue entera por debajo");

        vm.SaveCommand.Execute(null);

        vm.Saved.Should().BeTrue();
        _rates.Current!.Rates.Should().HaveCount(2,
            "guardar con el filtro puesto escribe la tabla ENTERA, no lo que se estaba mirando");
        _rates.Current.Find("gpt-5", "copilot").Should().NotBeNull(
            "la tarifa de la casa que el filtro escondía sigue en el hub");
    }
}
