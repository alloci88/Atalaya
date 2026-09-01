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
/// Desde F15 el coste se DERIVA de los tokens con la tarifa del modelo, así que cuando no hay
/// número el motivo es siempre uno de tres y ninguno tiene que ver con lo que informe un
/// proveedor. Lo que se fija aquí es que hay un solo criterio y que los dos sitios lo usan.
/// </para>
/// </summary>
public sealed class CostTextTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;

    public CostTextTests()
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
    /// <b>El defecto exacto del parte.</b> Una sesión con Claude Code cuyo modelo no tiene tarifa:
    /// el pie y el informe tienen que decir la MISMA cosa, y esa cosa es el motivo real.
    /// </summary>
    [Fact]
    public void Sin_tarifa_el_pie_dice_lo_mismo_que_el_informe_de_esa_misma_sesion()
    {
        AuditSession session = Session(ClaudeCodeProvider.Id, model: "opus");
        var cost = CreditCalculator.Calculate(session, TestRates.Table());
        cost.Why.Should().Be(CostUnavailable.RateMissing, "«opus» no está en la tabla de tarifas");

        string footer = CreditText.OfSession(cost, session.Provider);
        string report = ReportBuilder.BuildSessionReport(
            App(), session, Array.Empty<Finding>(), 0, 0, "Org", TestRates.Table());

        footer.Should().Be("coste no calculable (tarifa no configurada)");
        report.Should().Contain($"- **Coste**: {footer}");
    }

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
        foreach (string provider in new[] { RealCopilotAgent.Id, ClaudeCodeProvider.Id })
        {
            CreditText.OfSession(CostResult.Unavailable(why), provider)
                .Should().NotContain("SDK")
                .And.StartWith("coste no calculable (");
        }
    }

    /// <summary>
    /// Cuando SÍ hay número, la unidad es la de su casa (D-789): lo de Copilot es una factura, lo
    /// de una suscripción es un equivalente, y presentarlos con la misma palabra sería decir que
    /// uno cuesta lo que no cuesta.
    /// </summary>
    [Fact]
    public void El_numero_lleva_la_unidad_de_su_casa()
    {
        var cost = new CostResult(68.2m);

        CreditText.OfSession(cost, RealCopilotAgent.Id).Should().Be("68,2 AI credits");
        CreditText.OfSession(cost, ClaudeCodeProvider.Id).Should().Be("68,2 credits (equivalente API)");

        // Y una sesión anterior a F14, sin proveedor escrito, es Copilot: no había otro.
        CreditText.OfSession(cost, null).Should().Be("68,2 AI credits");
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
            CostResult = CostResult.Unavailable(CostUnavailable.RateMissing),
        };

        var view = new SessionViewModel(live);

        view.CostText.Should().EndWith(CreditText.OfSession(live.CostResult, live.Provider));
        view.CostText.Should().NotContain("SDK");
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
