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
/// R2 §2 — <b>una instalación limpia audita con coste desde la primera sesión</b>.
/// <para>
/// <b>Qué pasaba.</b> Las tarifas viven en el hub (D-786) y la siembra colgaba del constructor de la
/// pantalla que las enseña: hasta que alguien abría Métricas → Tarifas · Gestionar, el hub no tenía
/// <c>model-rates.json</c> y TODA sesión salía con «tarifa no configurada». Un precio publicado es
/// un dato, no una decisión del usuario, así que la aplicación lo aplica sola.
/// </para>
/// <para>
/// Los dos casos que fijan el comportamiento son los dos que se pueden estropear el uno al otro:
/// que la siembra ocurra sin que nadie la pida, y que no pise lo que alguien haya corregido.
/// </para>
/// </summary>
public sealed class FreshInstallRatesTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly ModelRatesService _rates;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    /// <summary>Un modelo que la siembra trae, para poder preguntar por su tarifa por su nombre.</summary>
    private const string ModeloSembrado = "claude-sonnet-4.5";

    public FreshInstallRatesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-fresco", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
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

    /// <summary>
    /// <b>La prueba de la instalación limpia</b>: hub sin el fichero → se abre el hub → una sesión
    /// sale con su coste calculado. Antes de R2 salía con <c>RateMissing</c>, y así se quedaba hasta
    /// que alguien visitara la pantalla de tarifas.
    /// </summary>
    [Fact]
    public void La_primera_sesion_de_una_instalacion_limpia_sale_con_coste()
    {
        File.Exists(Path.Combine(_hub.HubPaths.Root, "model-rates.json"))
            .Should().BeFalse("un hub recién clonado no trae la tabla");

        // Lo que hace la aplicación al abrir el hub, sin preguntar y sin pantalla de por medio.
        _hub.SeedModelRates();

        CostResult cost = _rates.CostOf(Session(ModeloSembrado));

        cost.Why.Should().Be(CostUnavailable.None, "la tarifa de ese modelo estaba publicada");
        cost.Usd.Should().NotBeNull().And.NotBe(0m);
    }

    /// <summary>
    /// Y sin la siembra, la misma sesión no vale nada: es el estado del que viene R2, escrito para
    /// que el test de arriba no pueda pasar por otro motivo.
    /// </summary>
    [Fact]
    public void Sin_sembrar_esa_misma_sesion_no_tiene_coste()
    {
        _rates.CostOf(Session(ModeloSembrado)).Why.Should().Be(CostUnavailable.RateMissing);
    }

    /// <summary>
    /// <b>La otra mitad</b>: un hub con una tarifa que alguien editó no se pisa. Lo corregido es lo
    /// que alguien fue a comprobar, y vale más que lo que traiga la versión — incluso cuando lo que
    /// trae la versión es más nuevo.
    /// </summary>
    [Fact]
    public void Una_tarifa_editada_manda_sobre_la_sembrada()
    {
        _rates.Save(new ModelRateTable
        {
            Rates = { new ModelRate(ModeloSembrado, string.Empty, 1m, 1m, 1m, CacheWritePerMillion: 1m) },
        });

        _hub.SeedModelRates();

        ModelRate vigente = _rates.Current!.Find(ModeloSembrado, "copilot")!;
        vigente.InputPerMillion.Should().Be(1m);
        vigente.OutputPerMillion.Should().Be(1m);
        _rates.Current!.Rates.Count(r => string.Equals(r.Model, ModeloSembrado, StringComparison.OrdinalIgnoreCase))
            .Should().Be(1, "ni se pisa ni se duplica");
    }

    /// <summary>
    /// <b>No queda ningún paso de activación</b>: abrir la pantalla ya no siembra nada. Era el
    /// «habilitar» de facto —el único camino que escribía el fichero— y por eso se comprueba que ha
    /// desaparecido, no solo que la siembra automática funciona.
    /// </summary>
    [Fact]
    public void Abrir_la_pantalla_ya_no_es_lo_que_siembra()
    {
        _ = new ModelRatesViewModel(_rates);

        File.Exists(Path.Combine(_hub.HubPaths.Root, "model-rates.json"))
            .Should().BeFalse("sembrar dejó de ser un gesto de nadie");
        typeof(ModelRatesService).GetMethod("EnsureSeeded")
            .Should().BeNull("el paso manual se retira, no se esconde");
    }

    /// <summary>
    /// El fichero <b>no se muda a la configuración local</b>. Lo que R2 cambia es dónde se EDITA
    /// —Ajustes— y no dónde se guarda: un precio es del contrato de la organización con su
    /// proveedor, así que sigue en la raíz del hub, versionado, con el commit como atribución.
    /// </summary>
    [Fact]
    public void La_tabla_sembrada_sigue_en_el_hub_y_no_en_la_maquina()
    {
        _hub.SeedModelRates();

        Directory.EnumerateFiles(_paths.Root, "model-rates.json", SearchOption.AllDirectories)
            .Should().ContainSingle().Which
            .Should().Be(Path.Combine(_hub.HubPaths.Root, "model-rates.json"),
                "el hub es el único sitio; la configuración de esta máquina no guarda precios");

        _settings.Save(_settings.Current);
        File.ReadAllText(_paths.SettingsJson)
            .Should().NotContain("PerMillion", "ningún precio se ha mudado a los ajustes locales");
    }

    private AuditSession Session(string model)
    {
        var s = new AuditSession
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
        s.Usage.Add(120_000, 8_000, 60_000, 20_000, null, calls: 12);
        _hub.Store.WriteSession(s);
        return s;
    }
}
