using Atalaya.App.Services;
using Atalaya.ClaudeCode;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// <b>PROV-2 §3 — el coste se cuenta en dinero, y se tarifa por proveedor + modelo.</b>
/// <para>
/// <b>Qué regla protege.</b> Que el importe de un periodo es la suma de lo que costó cada sesión
/// <b>con la tarifa de su casa y su modelo</b>, y que un modelo sin tarifa se declara «parcial» en
/// vez de desaparecer de la suma (D-787). Hasta PROV-2 nada de esto podía romperse porque solo
/// había una casa que produjera un número: la unidad era el AI credit de Copilot y el resto no
/// llegaba a tarifarse.
/// </para>
/// <para>
/// <b>Qué se rompería en silencio sin esto.</b> Que Métricas cambiara de cifra sin que nadie
/// hubiera cambiado de gasto. Es la consecuencia más cara de esta entrega y la menos visible: el
/// dominio pasa de contar credits a contar dólares, la equivalencia se muda de una constante a la
/// declaración de una casa, y las 31 tarifas de un hub que ya existe cambian de forma. Cualquiera
/// de esas tres cosas mal hecha mueve todos los paneles a la vez, y sin síntoma.
/// </para>
/// </summary>
public sealed class MixedProviderCostTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly HubContext _hub;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    /// <summary>El modelo con el que corre la casa de fábrica en estos tests.</summary>
    private const string ModeloDeFabrica = "gpt-x";

    /// <summary>Y el de la opcional.</summary>
    private const string ModeloOpcional = "opus";

    public MixedProviderCostTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-mixto", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        var settings = new SettingsService(_paths);
        settings.Load();
        _hub = TestFactory.Hub(_paths, settings);
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "app", Name = "App", RepoUrl = "u/app", CurrentCycle = 1,
        });
    }

    public void Dispose()
    {
        CostFormat.Currency = CostCurrency.Credits;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ================================================================ la regla

    /// <summary>
    /// <b>Una sesión de cada casa: el importe del periodo es la suma de sus dos tarifas</b>, y un
    /// modelo sin tarifa no entra en la suma pero sí en el aviso de «parcial».
    /// <para>
    /// La sesión de la casa de fábrica es el <b>caso de control de F15</b>, con los números de una
    /// sesión real del hub: In 538.468 / Out 42.371 / CacheRead 368.618 a 1,25 / 10 / 0,125 $/M.
    /// Costaba 68,21 credits antes de esta entrega y tiene que seguir costando 68,21 credits
    /// después — con la equivalencia de 0,01 $ que ahora declara su proveedor.
    /// </para>
    /// </summary>
    [Fact]
    public void El_coste_de_una_sesion_mixta_se_calcula_por_proveedor_y_modelo()
    {
        Rates(
            new ModelRate(ModeloDeFabrica, RealCopilotAgent.Id, 1.25m, 10.00m, 0.125m),
            new ModelRate(ModeloOpcional, ClaudeCodeProvider.Id, 0m, 1000.00m, 0m));

        // El caso de control de F15, con la casa de fábrica: 0,6821 $ = 68,21 credits.
        Session(RealCopilotAgent.Id, ModeloDeFabrica, input: 538_468, output: 42_371, read: 368_618);

        // Y la otra casa, con SU tarifa: 2.000 tokens de salida a 1.000 $/M = 2 $.
        Session(ClaudeCodeProvider.Id, ModeloOpcional, input: 0, output: 2_000, read: 0);

        // Y una tercera cuyo modelo no tiene tarifa para nadie: no suma, y lo dice.
        Session(RealCopilotAgent.Id, "modelo-sin-tarifa", input: 0, output: 5_000, read: 0);

        MetricsDashboard d = new MetricsQuery(_hub, new FakeTime(Now))
            .Build(new MetricsFilter(null, MetricsRange.All));

        // La cifra de la casa de fábrica NO SE HA MOVIDO: los mismos tokens, la misma tarifa, el
        // mismo número que enseñaba el panel antes de PROV-2 — solo que ahora en dólares.
        ProviderCost fabrica = d.CostByProvider.Single(p => p.ProviderId == RealCopilotAgent.Id);
        fabrica.Cost.Should().BeApproximately(0.6821m, 0.0001m);
        (fabrica.Cost!.Value / TestRates.UsdPerCredit).Should().BeApproximately(68.21m, 0.01m);

        ProviderCost opcional = d.CostByProvider.Single(p => p.ProviderId == ClaudeCodeProvider.Id);
        opcional.Cost.Should().Be(2m);

        // Y el total es la SUMA de las dos, que es lo único que se puede sumar: dólares.
        d.CostInPeriod.Should().BeApproximately(0.6821m + 2m, 0.0001m);

        // La tercera no se pierde: no está en la suma y el panel lo dice con su número (D-787).
        d.CostIsPartial.Should().BeTrue();
        d.PartialCostSessions.Should().Be(1);
        d.PartialCostNotice.Should().Contain("1 sesión sin tarifa");
    }

    /// <summary>
    /// <b>La misma tarifa de otra casa no vale</b> (PROV-2 §3). Es la mitad de «por proveedor +
    /// modelo» que no se ve en la suma: con la columna de proveedor obligatoria, una tarifa
    /// escrita para la casa de fábrica no le pone precio al consumo de la otra.
    /// <para>
    /// Lo que se rompería en silencio sin esto: la casa que declara que su consumo no lleva precio
    /// empezaría a facturarle a la organización un dinero que nadie le cobra, en cuanto alguien
    /// usara el mismo modelo desde las dos.
    /// </para>
    /// </summary>
    [Fact]
    public void La_tarifa_de_una_casa_no_le_pone_precio_a_otra()
    {
        Rates(new ModelRate(ModeloOpcional, RealCopilotAgent.Id, 0m, 1000.00m, 0m));

        Session(ClaudeCodeProvider.Id, ModeloOpcional, input: 0, output: 2_000, read: 0);

        MetricsDashboard d = new MetricsQuery(_hub, new FakeTime(Now))
            .Build(new MetricsFilter(null, MetricsRange.All));

        d.CostInPeriod.Should().BeNull("ninguna sesión del periodo tiene tarifa suya");
        d.CostIsPartial.Should().BeFalse("no es un hueco: su casa declara que no lleva precio");
        d.HasUntariffed.Should().BeTrue();
        d.UntariffedNotice.Should().Contain(TestProviders.ClaudeNote);
    }

    // ================================================================ la divisa que se enseña

    /// <summary>
    /// <b>Los créditos se enseñan solo cuando todo el gasto del periodo es de la casa que los
    /// usa</b> (PROV-2 §3). Con una sola casa, la pantalla dice exactamente lo que decía antes;
    /// en cuanto hay dos, sumar credits sería sumar unidades distintas, así que se pasa a dólares
    /// y el sitio lo dice en una línea.
    /// <para>
    /// Lo que se rompería en silencio sin esto: un total en «credits» que fuera la suma del gasto
    /// de dos casas — un número que no existe, con una unidad que no es de nadie.
    /// </para>
    /// </summary>
    [Fact]
    public void Los_creditos_solo_cuando_todo_el_gasto_es_de_su_casa()
    {
        CostFormat.Currency = CostCurrency.Credits;
        Rates(
            new ModelRate(ModeloDeFabrica, RealCopilotAgent.Id, 0m, 1000.00m, 0m),
            new ModelRate(ModeloOpcional, ClaudeCodeProvider.Id, 0m, 1000.00m, 0m));

        Session(RealCopilotAgent.Id, ModeloDeFabrica, input: 0, output: 2_000, read: 0);

        MetricsDashboard sola = new MetricsQuery(_hub, new FakeTime(Now))
            .Build(new MetricsFilter(null, MetricsRange.All));

        sola.CostLens.IsOwnUnit.Should().BeTrue();
        sola.CostUnit.Should().Be("credits");
        CostFormat.Number(sola.CostInPeriod, sola.CostLens).Should().Be("200,0");
        sola.CostCurrencyNote.Should().BeEmpty("con una sola casa no hay nada que explicar");

        // Y entra la segunda.
        Session(ClaudeCodeProvider.Id, ModeloOpcional, input: 0, output: 2_000, read: 0);

        MetricsDashboard mezcla = new MetricsQuery(_hub, new FakeTime(Now))
            .Build(new MetricsFilter(null, MetricsRange.All));

        mezcla.CostLens.IsOwnUnit.Should().BeFalse();
        mezcla.CostUnit.Should().Be(CostFormat.UsdSymbol);
        CostFormat.Number(mezcla.CostInPeriod, mezcla.CostLens).Should().Be("4,00");
        mezcla.CostCurrencyNote.Should().Be(CostLens.MixedNote);
    }

    // ================================================================ andamiaje

    private void Rates(params ModelRate[] rates)
        => _hub.Store.WriteModelRates(new ModelRateTable
        {
            Source = "Tarifas de este test, una por casa.",
            Rates = rates.ToList(),
        });

    private void Session(string provider, string model, long input, long output, long read)
    {
        var session = new AuditSession
        {
            Id = _ulids.NewUlid(),
            AppSlug = "app",
            Mode = AuditMode.Lotes,
            By = "alvaro",
            Machine = "PC",
            Provider = provider,
            Model = model,
            StartedUtc = Now.AddHours(-1),
            EndedUtc = Now,
            CycleN = 1,
        };

        session.Usage.Add(input, output, read, 0, null);
        _hub.Store.WriteSession(session);
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
