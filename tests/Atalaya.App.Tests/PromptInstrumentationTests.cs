using Atalaya.Agents;
using Atalaya.App.Services;
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
/// F18 §1 — <b>la instrumentación, de punta a punta</b>: una sesión de verdad, conducida por el
/// agente falso, tiene que dejar escrito en el hub de qué estuvo hecho cada prompt y cuánto costó
/// cada pasada.
/// <para>
/// El desglose por unidad existía desde Hito 1a. Lo que faltaba —y lo que se fija aquí— es el
/// nivel de la PASADA: una unidad son N pasadas, cada una manda su prompt entero, y sin ese nivel
/// no se puede distinguir «abrir la unidad cuesta» de «insistir sobre ella cuesta».
/// </para>
/// </summary>
public sealed class PromptInstrumentationTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly SettingsService _settings;
    private readonly FindingIngestionService _ingestion;
    private readonly ReconciliationService _reconciliation;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public PromptInstrumentationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-f18e2e", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _ingestion = new FindingIngestionService(_hub, _ulids);
        _reconciliation = new ReconciliationService(_hub);
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

    private const string Codigo = "public class Medida\n{\n    public int Suma(int a, int b) => a + b;\n}\n";

    private void SeedApp(string slug)
    {
        string clone = Path.Combine(_root, slug);
        Directory.CreateDirectory(clone);
        File.WriteAllText(Path.Combine(clone, "A.cs"), Codigo);
        File.WriteAllText(Path.Combine(clone, "B.cs"), Codigo + "// otra\n");
        _machines.SetClonePath(slug, clone);
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = slug,
            Name = slug.ToUpperInvariant(),
            RepoUrl = "u",
            Stack = TechStack.DotNet,
            CurrentCycle = 1,
        });
        _hub.Store.WriteInventory(slug, new InventoryCycle
        {
            CycleN = 1,
            Units =
            {
                new InventoryUnit { Path = "A.cs", Module = "M", State = UnitState.Pendiente },
                new InventoryUnit { Path = "B.cs", Module = "M", State = UnitState.Pendiente },
            },
        });
    }

    private void MaxPasses(int n)
    {
        AppSettings s = _settings.Current;
        s.MaxPassesPerUnit = n;
        _settings.Save(s);
    }

    private Task<SessionResult> Run(IAuditorProvider agent, params string[] units)
        => new SessionCoordinator(_hub, _ingestion, _reconciliation, _machines, _ulids, agent, _settings)
            .RunAsync(new SessionRequest("app", AuditMode.Lotes, units), CancellationToken.None);

    private AuditSession Written() => _hub.Store.ListSessions("app").Single();

    private string Report(AuditSession session)
        => File.ReadAllText(_hub.HubPaths.ReportFile("app", session.Id.ToString()));

    /// <summary>
    /// Una sesión real deja escrita, por pasada, la composición del prompt que mandó. Sin esto,
    /// «el andamiaje es el 97 %» habría que calcularlo a mano leyendo un informe.
    /// </summary>
    [Fact]
    public async Task Cada_pasada_deja_escrito_de_que_estuvo_hecho_su_prompt()
    {
        SeedApp("app");
        MaxPasses(1);

        await Run(new FakeCopilotAgent(), "A.cs");

        UnitUsageBreakdown unit = Written().UsageBreakdown.Single();
        PassUsage pass = unit.Passes.Single();

        pass.Pass.Should().Be(1);
        pass.Composition.Should().NotBeNull();
        pass.Composition!.Reglas.Should().BeGreaterThan(0);
        pass.Composition.Rubrica.Should().BeGreaterThan(0);
        pass.Composition.Catalogo.Should().BeGreaterThan(0);
        pass.Composition.Unidad.Should().BeGreaterThan(0);
        pass.Composition.Estable.Should().BeGreaterThan(pass.Composition.Variable, "el andamiaje pesa más que la unidad");
        pass.Calls.Should().Be(1, "el agente falso informa un consumo por pasada");
        pass.InputTokens.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Dos pasadas, dos filas — cada una con SU consumo. Un solo total por unidad no distingue la
    /// pasada que encontró algo de la que vino seca.
    /// </summary>
    [Fact]
    public async Task Un_barrido_de_varias_pasadas_registra_una_fila_por_pasada()
    {
        SeedApp("app");
        MaxPasses(3);

        // Cada pasada aporta un hallazgo, así que el barrido no converge y agota el tope.
        int n = 0;
        await Run(new FakeCopilotAgent(auditScript: _ => new[]
        {
            new SubmitFindingArgs(
                "mejoras.estilo.nomenclatura", "mejoras", "baja", $"nombre poco claro {++n}",
                "desc", "impacto", "recomendación", new[] { new SubmitLocation("A.cs", 1, "class") }, "Suma"),
        }), "A.cs");

        UnitUsageBreakdown unit = Written().UsageBreakdown.Single();

        unit.Passes.Should().HaveCount(3);
        unit.Passes.Select(p => p.Pass).Should().Equal(1, 2, 3);
        unit.Passes.Should().OnlyContain(p => p.Calls == 1 && p.Composition != null);

        // La parte estable no se mueve entre pasadas; la variable crece porque cada pasada ve más
        // hallazgos existentes. Es exactamente el corte que la caché necesita.
        unit.Passes.Select(p => p.Composition!.Estable).Distinct().Should().ContainSingle();
        unit.Passes[^1].Composition!.Existentes.Should().BeGreaterThan(unit.Passes[0].Composition!.Existentes);
    }

    /// <summary>
    /// Y el prefijo estable es el mismo <b>entre unidades</b> de la misma sesión: es la condición
    /// sin la cual la caché de un proveedor no puede servir nada de una unidad a la siguiente.
    /// </summary>
    [Fact]
    public async Task El_prefijo_estable_no_cambia_de_una_unidad_a_otra()
    {
        SeedApp("app");
        MaxPasses(1);

        var prompts = new List<AuditUnitRequest>();
        await Run(new CapturingAgent(prompts), "A.cs", "B.cs");

        prompts.Should().HaveCount(2);
        prompts[1].StablePrefix.Should().Be(prompts[0].StablePrefix);
        prompts[1].UnitPart.Should().NotBe(prompts[0].UnitPart);

        // Y las dos piezas cuadran con el prompt que se manda, que es lo que el proveedor firma.
        prompts.Should().OnlyContain(p => p.CanSplit);
    }

    /// <summary>La duración se mide y se guarda, por unidad y por pasada.</summary>
    [Fact]
    public async Task La_duracion_queda_registrada()
    {
        SeedApp("app");
        MaxPasses(1);

        await Run(new FakeCopilotAgent(), "A.cs");

        UnitUsageBreakdown unit = Written().UsageBreakdown.Single();

        unit.DurationMs.Should().BeGreaterThanOrEqualTo(0);
        unit.Passes.Single().DurationMs.Should().BeGreaterThanOrEqualTo(0);
        unit.DurationMs.Should().BeGreaterThanOrEqualTo(unit.Passes.Single().DurationMs);
    }

    /// <summary>
    /// Y el informe de esa sesión lleva la línea de composición: es donde queda escrito para
    /// dentro de un año.
    /// </summary>
    [Fact]
    public async Task El_informe_de_la_sesion_lleva_la_linea_de_composicion()
    {
        SeedApp("app");
        MaxPasses(1);

        await Run(new FakeCopilotAgent(), "A.cs");

        AuditSession session = Written();
        string report = Report(session);

        report.Should().Contain("- **Composición**: andamiaje ≈ ");
        report.Should().Contain("llamadas por unidad");
        report.Should().Contain("### Por pasada, y de qué se compone el prompt");
    }

    /// <summary>Un agente que solo apunta lo que le mandan: es lo que estos tests miran.</summary>
    private sealed class CapturingAgent : IAuditorProvider
    {
        private readonly List<AuditUnitRequest> _seen;

        public CapturingAgent(List<AuditUnitRequest> seen) => _seen = seen;

        public string? ModelName => "capturador";

        public event Action<string>? TextStreamed;

        public event Action<UsageSample>? UsageReported;

        public Task<bool> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<AgentReadiness> CheckAsync(CancellationToken ct)
            => Task.FromResult(new AgentReadiness(true, "listo"));

        public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentModel>>(Array.Empty<AgentModel>());

        public Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
        {
            _seen.Add(request);
            TextStreamed?.Invoke("[capturador]\n");
            UsageReported?.Invoke(new UsageSample(100, 10, null, ModelName));
            toolbox.UnitDone(request.UnitPath, "Revisados: todo.");
            return Task.CompletedTask;
        }

        public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
            => Task.CompletedTask;
    }
}
