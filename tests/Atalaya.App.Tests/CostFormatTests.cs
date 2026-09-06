using Atalaya.App.Services;
using Atalaya.App.ViewModels;
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
/// F16 §B — EL PIE Y EL INFORME DICEN LO MISMO DEL COSTE, Y CON LA CASA QUE TOCA.
/// <para>
/// El parte era literal: auditando con Claude Code, el pie de la sesión decía «coste no informado
/// por el SDK» —una frase acuñada para Copilot, y que además nombra un SDK que en esta casa no
/// existe— mientras el informe de ESA MISMA sesión decía «no calculable (tarifa no configurada)».
/// Dos respuestas a la misma pregunta, y solo una de ellas cierta.
/// </para>
/// <para>
/// <b>F16-RETOQUE §1 y la respuesta de verdad.</b> Ninguna de las dos era la buena, porque las dos
/// daban por hecho que ahí faltaba algo por configurar. No falta nada: el consumo de Claude Code va
/// contra la suscripción personal de quien lo usa y <b>no factura a la organización</b>, así que no
/// se tarifa. Lo que se fija aquí es que hay un solo criterio, que los dos sitios lo usan, y —lo
/// más importante— que a una casa no tarifada <b>no le puede salir jamás</b> un «tarifa no
/// configurada»: el aviso existe para que alguien vaya a arreglar una tabla, y aquí no hay tabla
/// que arreglar.
/// </para>
/// </summary>
public sealed class CostFormatTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;

    public CostFormatTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-coste", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
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
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// <b>El defecto exacto del parte, con la respuesta de F16-RETOQUE.</b> Una sesión con Claude
    /// Code: el pie y el informe dicen la MISMA cosa, y esa cosa es que no se tarifa.
    /// </summary>
    [Fact]
    public void El_pie_dice_lo_mismo_que_el_informe_de_esa_misma_sesion()
    {
        AuditSession session = Session(ClaudeCodeProvider.Id, model: "opus");
        var cost = CreditCalculator.Calculate(session, TestRates.Table());
        cost.Why.Should().Be(CostUnavailable.NotBilled);

        string footer = CostFormat.OfSession(cost, session.Provider);
        string report = ReportBuilder.BuildSessionReport(
            App(), session, Array.Empty<Finding>(), 0, 0, "Org", TestRates.Table());

        footer.Should().Be("incluido en tu suscripción de Claude");
        report.Should().Contain($"- **Coste**: {footer}");
    }

    /// <summary>
    /// <b>La regla, blindada donde se decide.</b> A una casa que no factura no le puede salir
    /// «tarifa no configurada» — ni «modelo no registrado», ni «sin tokens registrados»— haga lo
    /// que haga el hub: da igual que el modelo esté en la tabla, que no esté, que no haya modelo o
    /// que no haya ni un token. La pregunta que esos avisos hacen —«¿qué falta por configurar?»—
    /// no tiene sentido aquí, y un aviso que ladra sin causa se aprende a ignorar.
    /// </summary>
    [Theory]
    [InlineData(TestRates.Model)]   // este modelo SÍ tiene tarifa en la tabla
    [InlineData("opus")]            // no está en ninguna
    [InlineData(null)]              // ni siquiera hay modelo registrado
    public void A_una_casa_que_no_factura_no_le_puede_salir_una_tarifa_que_falta(string? model)
    {
        foreach (long tokens in new long[] { 0, 1000 })
        {
            CostResult cost = CreditCalculator.Calculate(
                model, ClaudeCodeProvider.Id, tokens, tokens, tokens, tokens, TestRates.Table());

            cost.Why.Should().Be(CostUnavailable.NotBilled);
            cost.Credits.Should().BeNull("un número aquí sería un cobro que nadie hace");

            string text = CostFormat.OfSession(cost, ClaudeCodeProvider.Id);
            text.Should().Be(CostFormat.SubscriptionCost);
            text.Should().NotContain("tarifa").And.NotContain("credits").And.NotContain("no calculable");
        }
    }

    /// <summary>
    /// Y el pie de una sesión que no se tarifa <b>no se queda mudo</b>: enseña las llamadas y los
    /// tokens, que son hechos medidos, además de la frase del coste. Quitar el número no puede
    /// significar quitar la magnitud — si no, no habría forma de comparar el peso de dos sesiones.
    /// </summary>
    [Fact]
    public void Sin_coste_el_pie_ensena_llamadas_y_tokens()
    {
        string footer = CostFormat.SessionFooter(
            14, 2786, 10975, 201371, 22525,
            CostResult.Unavailable(CostUnavailable.NotBilled), ClaudeCodeProvider.Id);

        footer.Should().StartWith("14 llamadas · ");
        footer.Should().Contain("2.786 entrada").And.Contain("10.975 salida");
        footer.Should().Contain("201.371 leída").And.Contain("22.525 escrita");
        // F17-RETOQUE: el orden es llamadas → coste → tokens, en las dos casas.
        footer.Should().Contain($"coste: {CostFormat.SubscriptionCost}");
        footer.IndexOf("coste:", StringComparison.Ordinal).Should()
            .BeLessThan(footer.IndexOf("2.786 entrada", StringComparison.Ordinal));
        footer.Should().NotContain("credits");
    }

    /// <summary>
    /// Con factura, el pie dice llamadas y credits, y los tokens DETRÁS del coste (F17-RETOQUE):
    /// antes iban en un segundo bloque aparte, que es como acabaron repetidos con Claude Code.
    /// </summary>
    [Fact]
    public void Con_factura_el_pie_sigue_diciendo_credits()
        => CostFormat.SessionFooter(3, 1000, 200, 0, 0, new CostResult(68.2m), RealCopilotAgent.Id)
            .Should().Be("3 llamadas · 68,2 AI credits · 1.000 entrada · 200 salida");

    /// <summary>
    /// Y la palabra «SDK» no aparece donde no aplica. Con Claude Code no hay ningún SDK: hay un CLI
    /// y un servidor MCP, y culpar a un SDK inexistente manda a mirar donde no es (N-2).
    /// </summary>
    [Theory]
    [InlineData(CostUnavailable.RateMissing)]
    [InlineData(CostUnavailable.ModelUnknown)]
    [InlineData(CostUnavailable.TokensMissing)]
    public void Ningun_texto_de_coste_nombra_un_SDK(CostUnavailable why)
    {
        // Solo se prueba con la casa que factura: a la otra no le llega nunca uno de estos tres
        // motivos —los para IsBilled antes—, y fingir que sí probaría un camino que no existe.
        CostFormat.OfSession(CostResult.Unavailable(why), RealCopilotAgent.Id)
            .Should().NotContain("SDK")
            .And.StartWith("coste no calculable (");
    }

    /// <summary>
    /// Cuando hay número, la unidad es una sola: <b>AI credits</b>, lo que factura GitHub. El
    /// «equivalente API» de D-789 se retiró con la tarifa que lo producía (F16-RETOQUE §1): existía
    /// para etiquetar una cifra que no era un cobro, y esa cifra ya no se calcula.
    /// </summary>
    [Fact]
    public void El_numero_lleva_la_unidad_de_lo_que_factura()
    {
        var cost = new CostResult(68.2m);

        CostFormat.OfSession(cost, RealCopilotAgent.Id).Should().Be("68,2 AI credits");

        // Y una sesión anterior a F14, sin proveedor escrito, es Copilot: no había otro.
        CostFormat.OfSession(cost, null).Should().Be("68,2 AI credits");
    }

    /// <summary>
    /// El pie de la sesión en vivo sale del MISMO sitio. Es lo que impide que vuelvan a nacer dos
    /// frases: lo que la vista enseña es lo que el criterio dice, sin retocarlo por el camino.
    /// </summary>
    [Fact]
    public void El_pie_de_la_sesion_en_vivo_usa_el_criterio_comun()
    {
        var agent = new FakeCopilotAgent();
        var live = new LiveSessionService(
            () => throw new NotSupportedException("no se lanza ninguna sesión en este test"),
            agent,
            new OpenSessionStore(_paths))
        {
            Provider = ClaudeCodeProvider.Id,
            CostResult = CostResult.Unavailable(CostUnavailable.NotBilled),
            InputTokens = 900,
            OutputTokens = 120,
            Calls = 4,
        };

        var view = new SessionViewModel(live);

        view.CostText.Should().Be(CostFormat.SessionFooter(
            live.Calls, live.InputTokens, live.OutputTokens,
            live.CacheReadTokens, live.CacheWriteTokens, live.CostResult, live.Provider));
        view.CostText.Should().NotContain("SDK").And.NotContain("tarifa");
    }

    private static AppConfig App() => new() { Slug = "app", Name = "App", RepoUrl = "https://github.com/org/app.git", CurrentCycle = 1 };

    private static AuditSession Session(string provider, string model) => new()
    {
        Id = new UlidFactory(SystemClock.Instance).NewUlid(),
        AppSlug = "app",
        Mode = AuditMode.Lotes,
        By = "alguien",
        Machine = "maquina",
        StartedUtc = DateTimeOffset.UtcNow,
        Provider = provider,
        Model = model,
        Usage = new UsageTotals { InputTokens = 1000, OutputTokens = 2000 },
    };
}
