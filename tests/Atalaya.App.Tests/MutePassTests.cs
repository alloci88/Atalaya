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
/// F20 — <b>una pasada MUDA no es una pasada seca</b>.
/// <para>
/// «Seca» significa que el auditor miró y no sacó nada más, y es un resultado: dos seguidas
/// terminan el barrido (F12 §E). Pero la definición no exigía que el auditor hubiera hecho nada, y
/// una pasada sin una sola llamada a herramienta —ni siquiera <c>unit_done</c>— cumplía las tres
/// condiciones. Dos turnos mudos seguidos cerraban una unidad que nadie había barrido, con
/// veredicto «auditada» y sin una sola cifra fuera de sitio.
/// </para>
/// <para>
/// No es hipotético: es lo que produjo el modelo al medir la conversación compartida de F20 §3, dos
/// unidades seguidas. La hipótesis se cayó; el agujero que destapó se queda cerrado.
/// </para>
/// </summary>
public sealed class MutePassTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly SettingsService _settings;
    private readonly FindingIngestionService _ingestion;
    private readonly ReconciliationService _reconciliation;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public MutePassTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-f20m", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        AppSettings s = _settings.Current;
        s.MaxPassesPerUnit = 4;
        _settings.Save(s);

        _hub = TestFactory.Hub(_paths, _settings);
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _ingestion = new FindingIngestionService(_hub, _ulids);
        _reconciliation = new ReconciliationService(_hub);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "app", Name = "App", RepoUrl = "u", Stack = TechStack.DotNet, CurrentCycle = 1,
        });

        string clone = Path.Combine(_root, "app");
        Directory.CreateDirectory(clone);
        File.WriteAllText(Path.Combine(clone, "A.cs"), "class A { void M() { } }");
        _machines.SetClonePath("app", clone);
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units = { new InventoryUnit { Path = "A.cs", Module = "M", State = UnitState.Pendiente } },
        });
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

    private Task<SessionResult> Run(IAuditorProvider agent)
        => new SessionCoordinator(_hub, _ingestion, _reconciliation, _machines, _ulids, agent, _settings)
            .RunAsync(new SessionRequest("app", AuditMode.Lotes, new[] { "A.cs" }), CancellationToken.None);

    private AuditSession Written() => _hub.Store.ListSessions("app").Single();

    /// <summary>
    /// <b>El defecto, tal cual apareció.</b> Un auditor que gasta el turno y no llama a nada: antes
    /// convergía en dos pasadas y la unidad quedaba «auditada». Ahora el barrido agota su tope, la
    /// cobertura se declara incompleta, y cada turno perdido queda nombrado.
    /// </summary>
    [Fact]
    public async Task Dos_pasadas_mudas_no_cierran_una_unidad_que_nadie_ha_barrido()
    {
        await Run(new MuteAgent());

        AuditSession session = Written();

        session.UsageBreakdown.Single().Passes.Should().HaveCount(4, "ninguna pasada muda convergió");
        session.Units.Single().Verdict.Should().Be("cobertura posiblemente incompleta");
        session.Notes.Should().Contain(n => n.Contains("no llamó a ninguna herramienta"));
    }

    /// <summary>
    /// Y una pasada HONESTA sigue siendo seca de toda la vida: el auditor cierra con
    /// <c>unit_done</c> sin reportar nada, el barrido converge en dos y la unidad queda auditada.
    /// La guarda no puede convertir una convergencia legítima en un barrido eterno.
    /// </summary>
    [Fact]
    public async Task Una_pasada_que_cierra_sin_hallazgos_sigue_siendo_seca()
    {
        await Run(new FakeCopilotAgent());

        AuditSession session = Written();

        session.UsageBreakdown.Single().Passes.Should().HaveCount(2, "dos secas seguidas terminan");
        session.UsageBreakdown.Single().Passes.Should().OnlyContain(p => p.Calls >= 0);
        session.Units.Single().Verdict.Should().Be("auditada");
        session.Notes.Should().NotContain(n => n.Contains("no llamó a ninguna herramienta"));
    }

    /// <summary>Un auditor que gasta el turno sin llamar a nada: ni reporta, ni reconcilia, ni cierra.</summary>
    private sealed class MuteAgent : IAuditorProvider
    {
        public string? ModelName => "mudo";

        public event Action<string>? TextStreamed;

        public event Action<UsageSample>? UsageReported;

        public Task<bool> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<AgentReadiness> CheckAsync(CancellationToken ct)
            => Task.FromResult(new AgentReadiness(true, "listo"));

        public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentModel>>(Array.Empty<AgentModel>());

        public Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
        {
            // Razona largo y tendido, y no llama a nada. Es lo que se midió de verdad: 12.000 y
            // 21.000 tokens de salida sin una sola herramienta.
            TextStreamed?.Invoke("He revisado la unidad y no veo nada más que añadir.\n");
            UsageReported?.Invoke(new UsageSample(1_000, 12_000, null, ModelName));
            return Task.CompletedTask;
        }

        public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
            => Task.CompletedTask;
    }
}
