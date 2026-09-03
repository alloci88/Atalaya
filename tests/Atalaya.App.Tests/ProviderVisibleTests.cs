using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.ClaudeCode;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F16 §C — EL PROVEEDOR ACOMPAÑA AL MODELO, EN LOS CUATRO SITIOS.
/// <para>
/// El informe decía «Modelo: claude-opus-4.6» y nada más. Con una sola casa eso bastaba; con dos no
/// dice lo que hace falta saber: de qué bolsa de cuota salió, qué superficie tuvo el agente y —lo
/// que más importa— si dos veredictos que discrepan vienen de casas distintas, que es lo más
/// parecido a una segunda opinión que existe, o de la misma, que puede compartir punto ciego
/// (D-781).
/// </para>
/// </summary>
public sealed class ProviderVisibleTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public ProviderVisibleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-proveedor", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(App());
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

    /// <summary>Sitio 1: el informe de sesión, junto al modelo y antes que él.</summary>
    [Fact]
    public void El_informe_de_sesion_nombra_la_casa()
    {
        string report = ReportBuilder.BuildSessionReport(
            App(), Session(ClaudeCodeProvider.Id, "opus"), Array.Empty<Finding>(), 0, 0, "Org");

        // Desde F23 la casa y el modelo comparten línea: son la misma pregunta («con qué se
        // auditó») y separarlos gastaba dos renglones de cabecera para dos palabras.
        report.Should().Contain("- **Proveedor**: Claude Code · **Modelo**: opus");
    }

    /// <summary>Sitio 2: los metadatos de la ficha del hallazgo.</summary>
    [Fact]
    public void La_ficha_del_hallazgo_dice_con_que_se_juzgo()
    {
        Finding f = Finding(ClaudeCodeProvider.Id, "opus");
        _hub.Store.WriteFinding("app", f);

        FindingDetailViewModel view = Detail();
        view.Load("app", f.Id);

        view.Meta.Should().Contain(m => m.Label == "Detectado con" && m.Value == "Claude Code · modelo opus");
    }

    /// <summary>
    /// Y un hallazgo de antes de que se registrara la casa no sale como «desconocido»: era Copilot,
    /// porque no había otro (D-780). Tratarlo de otra forma partiría el histórico en dos.
    /// </summary>
    [Fact]
    public void Un_hallazgo_de_antes_de_F14_se_nombra_como_Copilot()
    {
        Finding f = Finding(provider: null, model: null);
        _hub.Store.WriteFinding("app", f);

        FindingDetailViewModel view = Detail();
        view.Load("app", f.Id);

        view.Meta.Should().Contain(m => m.Label == "Detectado con" && m.Value == "GitHub Copilot");
    }

    /// <summary>Sitio 3: la actividad de sesiones de Métricas, con su columna propia.</summary>
    [Fact]
    public void La_actividad_de_sesiones_trae_la_casa_de_cada_fila()
    {
        TestRates.Seed(_hub);
        _hub.Store.WriteSession(Session(ClaudeCodeProvider.Id, TestRates.Model));
        _hub.Store.WriteSession(Session(RealCopilotAgent.Id, TestRates.Model));

        MetricsDashboard dashboard = new MetricsQuery(_hub).Build(new MetricsFilter(null, MetricsRange.All));

        dashboard.Sessions.Select(s => s.Provider).Should()
            .BeEquivalentTo("Claude Code", "GitHub Copilot");
    }

    /// <summary>Sitio 4: el informe de un arreglo asistido.</summary>
    [Fact]
    public void El_informe_de_arreglo_nombra_la_casa()
    {
        AuditSession session = Session(ClaudeCodeProvider.Id, "opus");
        session.Mode = AuditMode.Fix;

        string report = ReportBuilder.BuildFixReport(
            App(), session, Finding(ClaudeCodeProvider.Id, "opus"),
            Array.Empty<(string, string, bool)>(),
            "resumen", null, "título", "descripción", null, "Org");

        report.Should().Contain("- **Proveedor**: Claude Code");
    }

    // ---------------------------------------------------------------- ayudas

    private FindingDetailViewModel Detail()
    {
        var machines = new MachineConfigStore(_paths.MachinesJson);
        var toasts = new ToastCenter();
        return new FindingDetailViewModel(
            _hub,
            new GovernanceService(_hub, _ulids),
            machines,
            new VerifyCoordinator(_hub, machines, _ulids, new FakeCopilotAgent()),
            new EditorLauncher(_settings, machines),
            toasts,
            TestFactory.Links(_hub, _paths),
            TestFactory.LinkFlow(_hub, _paths, toasts),
            new AnchorRepair(_hub));
    }

    private static AppConfig App() => new()
    {
        Slug = "app",
        Name = "App",
        RepoUrl = "https://github.com/org/app.git",
        CurrentCycle = 1,
    };

    private AuditSession Session(string provider, string model) => new()
    {
        Id = _ulids.NewUlid(),
        AppSlug = "app",
        Mode = AuditMode.Lotes,
        By = "alguien",
        Machine = "maquina",
        StartedUtc = DateTimeOffset.UtcNow,
        Provider = provider,
        Model = model,
        Usage = new UsageTotals { InputTokens = 1000, OutputTokens = 2000 },
    };

    private Finding Finding(string? provider, string? model)
    {
        var stamp = new DetectionStamp(
            DateTimeOffset.UtcNow, AuditMode.Lotes, "abc1234", "auditor", null, model, provider);

        return new Finding
        {
            Id = _ulids.NewUlid(),
            DisplayId = "BUG-0001",
            RuleId = "err.validacion",
            Pillar = Pillar.Errores,
            Tag = FindingTag.Checklist,
            Severity = Severity.Alta,
            Confidence = Confidence.Media,
            Title = "Un defecto",
            Description = "d",
            Impact = "i",
            Recommendation = "r",
            Locations = { new Location { Path = "src/A.cs", Line = 3 } },
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
    }
}
