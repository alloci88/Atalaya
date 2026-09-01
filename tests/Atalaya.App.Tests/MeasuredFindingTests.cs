using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Ingestion;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.16 — el ciclo de vida de los hallazgos que MIDE la aplicación.
/// <para>
/// El caso real (xblast, 2026-08-26): un compañero troceó <c>ExtensionMethods.cs</c>; tras el pull y
/// el re-escaneo la unidad salió correctamente de «Grandes» —de 2.983 LOC a 978— y su hallazgo
/// automático se quedó activo, describiendo un tamaño que ya no existía. Al usuario le pareció que
/// había desaparecido; lo que había desaparecido era su motivo.
/// </para>
/// <para>
/// La regla que se fija aquí: <b>cada hallazgo se verifica con el instrumento que lo detectó</b>.
/// Estos los detecta una medida, así que una medida los resuelve, los reabre y los confirma.
/// </para>
/// </summary>
public sealed class MeasuredFindingTests : IDisposable
{
    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private readonly MachineConfigStore _machines;
    private readonly FindingIngestionService _ingestion;
    private readonly MeasuredFindingService _measured;
    private readonly InventoryRescanService _rescan;
    private const string RepoUrl = "https://example.invalid/org/app.git";

    /// <summary>El umbral por defecto: 1500 LOC. Los ficheros de prueba se escriben contra él.</summary>
    private const int Threshold = 1500;

    public MeasuredFindingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-medida", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _settings = new SettingsService(_paths);
        _settings.Load();

        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "app", Name = "App", RepoUrl = RepoUrl, Stack = TechStack.DotNet, CurrentCycle = 1,
        });

        _clone = Path.Combine(_root, "clone");
        TestFactory.MakeClone(_clone, RepoUrl);
        _machines.SetClonePath("app", _clone);

        _ingestion = new FindingIngestionService(_hub, _ulids);
        _measured = new MeasuredFindingService(_hub, _ingestion, _machines, _settings);
        _rescan = new InventoryRescanService(_hub, new InventoryScanner(), _settings, _measured);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    /// <summary>Escribe una unidad de <paramref name="lines"/> líneas en el clon.</summary>
    private string WriteUnit(string relative, int lines)
    {
        string abs = Path.Combine(_clone, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, string.Join('\n', Enumerable.Range(0, lines).Select(i => $"// linea {i}")));
        return relative;
    }

    private Finding SeedSizeFinding(string path, int loc)
    {
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow.AddDays(-1), AuditMode.Lotes, "viejo", "alvaro");
        SubmittedFinding submitted = InventoryScanner.BuildLargeUnitFinding(
            path, loc, _settings.Current.Thresholds);
        return _ingestion.Create(submitted, "app", AuditMode.Lotes, stamp);
    }

    private Finding Read(Ulid id) => _hub.Store.TryReadFinding("app", id.ToString())!;

    // ================================================================ §0 · el caso real

    /// <summary>
    /// <b>Caso de aceptación (a).</b> La secuencia exacta de MEJ-0037: un hallazgo de tamaño que
    /// dos «Verificar ahora» dejaron marcado «por revisar», y cuya unidad se troceó después. Tras el
    /// re-escaneo tiene que quedar RESUELTO, con la medida escrita, y sin la marca de revisión.
    /// </summary>
    [Fact]
    public void El_caso_de_ExtensionMethods_queda_resuelto_con_su_medida()
    {
        string path = WriteUnit("XBLASTCommon/Class/ExtensionMethods.cs", 978);
        Finding f = SeedSizeFinding(path, 2983);

        // Lo que le hicieron los dos verify con el instrumento equivocado.
        f.NeedsReview = true;
        f.History.Add(new HistoryEntry(DateTimeOffset.UtcNow, FindingEvent.Reopened, "alvaro", "verify: no verificable"));
        _hub.Store.WriteFinding("app", f);

        _rescan.Rescan("app", _clone);

        Finding after = Read(f.Id);
        after.Status.Should().Be(FindingStatus.Resuelto, "978 LOC está por debajo del umbral");
        after.Resolved!.Via.Should().Be(ResolutionVia.Medida, "lo resolvió un número, no un auditor");
        after.Resolved.Justification.Should().Contain("978 LOC < umbral 1500").And.Contain("re-escaneo");
        after.NeedsReview.Should().BeFalse(
            "la marca decía «el instrumento equivocado no supo verificarlo»; la medida es la respuesta");
        after.History.Should().Contain(h => h.Detail!.Contains("por revisar") && h.Detail.Contains("retirado"));
    }

    /// <summary>
    /// <b>Caso de aceptación (b).</b> Verificar una unidad que SIGUE siendo grande responde con el
    /// número, nunca «no verificable».
    /// </summary>
    [Fact]
    public void Verificar_una_unidad_que_sigue_grande_responde_confirmado_con_el_numero()
    {
        string path = WriteUnit("src/Grande.cs", 2000);
        Finding f = SeedSizeFinding(path, 2000);
        f.NeedsReview = true;
        _hub.Store.WriteFinding("app", f);

        MeasuredVerdict verdict = _measured.Verify("app", Read(f.Id));

        verdict.Applied.Should().BeTrue();
        verdict.Message.Should().Be("Confirmado: 2000 LOC ≥ umbral 1500.");
        verdict.Message.Should().NotContain("no verificable");

        Finding after = Read(f.Id);
        after.Status.Should().Be(FindingStatus.Activo);
        after.TimesConfirmed.Should().Be(2, "la medida refresca la última confirmación");
        after.NeedsReview.Should().BeFalse("medir es la respuesta definitiva");
    }

    // ================================================================ §1 · re-escaneo

    [Fact]
    public void El_reescaneo_resuelve_lo_que_bajo_del_umbral_y_lo_narra()
    {
        string path = WriteUnit("src/Troceada.cs", 400);
        Finding f = SeedSizeFinding(path, 3000);

        RescanOutcome outcome = _rescan.Rescan("app", _clone);

        Read(f.Id).Status.Should().Be(FindingStatus.Resuelto);
        outcome.Measured.Resolved.Should().ContainSingle().Which.Should().Contain("Troceada.cs").And.Contain("400 LOC");
        outcome.Measured.Summary.Should().Contain("salió de Grandes").And.Contain("se resolvió");
    }

    [Fact]
    public void El_reescaneo_crea_el_hallazgo_de_una_unidad_nueva_que_supera_el_umbral()
    {
        WriteUnit("src/Nueva.cs", 2500);

        RescanOutcome outcome = _rescan.Rescan("app", _clone);

        Finding created = _hub.Store.ListFindings("app").Should().ContainSingle().Subject;
        created.RuleId.Should().Be(InventoryScanner.LargeUnitRuleId);
        created.Locations[0].Path.Should().Be("src/Nueva.cs");
        created.Status.Should().Be(FindingStatus.Activo);
        outcome.Measured.Created.Should().ContainSingle().Which.Should().Contain("Nueva.cs");
        outcome.Measured.Summary.Should().Contain("supera el umbral");
    }

    [Fact]
    public void El_reescaneo_reabre_el_resuelto_cuando_la_unidad_vuelve_a_crecer()
    {
        string path = WriteUnit("src/Vuelve.cs", 400);
        Finding f = SeedSizeFinding(path, 3000);
        _rescan.Rescan("app", _clone);
        Read(f.Id).Status.Should().Be(FindingStatus.Resuelto);

        WriteUnit("src/Vuelve.cs", 2400);
        RescanOutcome outcome = _rescan.Rescan("app", _clone);

        Finding after = Read(f.Id);
        after.Status.Should().Be(FindingStatus.Activo, "se reabre el mismo, no se crea otro");
        after.Resolved.Should().BeNull();
        after.History.Should().Contain(h => h.Event == FindingEvent.Reopened
                                            && h.Detail!.Contains("2400 LOC vuelve a superar"));
        outcome.Measured.Reopened.Should().ContainSingle();
        _hub.Store.ListFindings("app").Should().ContainSingle("nunca hay dos hallazgos para la misma unidad");
    }

    [Fact]
    public void Un_reescaneo_sin_cambios_no_narra_nada()
    {
        WriteUnit("src/Grande.cs", 2000);
        _rescan.Rescan("app", _clone);

        RescanOutcome second = _rescan.Rescan("app", _clone);

        second.Measured.Total.Should().Be(0, "nada cambió: no hay nada que anunciar");
        second.Measured.Summary.Should().BeEmpty();
    }

    /// <summary>
    /// Una unidad que ya no está en el inventario NO se da por resuelta: puede haberse movido o
    /// renombrado, y «no la he visto» no es «ya no es grande».
    /// </summary>
    [Fact]
    public void Una_unidad_que_desaparece_del_inventario_no_se_da_por_resuelta()
    {
        Finding f = SeedSizeFinding("src/Fantasma.cs", 3000);
        WriteUnit("src/Otra.cs", 100);

        _rescan.Rescan("app", _clone);

        Read(f.Id).Status.Should().Be(FindingStatus.Activo, "no se midió: no se decide");
    }

    /// <summary>Los hallazgos que NO mide la app no los toca nadie: son del auditor.</summary>
    [Fact]
    public void El_reescaneo_no_toca_los_hallazgos_del_auditor()
    {
        WriteUnit("src/Peque.cs", 50);
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "c", "alvaro");
        Finding otro = _ingestion.Create(
            new SubmittedFinding(
                "errores.null.desreferencia", Pillar.Errores, FindingTag.Checklist, Severity.Alta,
                "posible nulo", "d", "i", "r", new[] { new Location("src/Peque.cs", 1) }, null),
            "app", AuditMode.Lotes, stamp);

        _rescan.Rescan("app", _clone);

        Read(otro.Id).Status.Should().Be(FindingStatus.Activo, "un defecto lógico no lo resuelve una regla de tamaño");
    }

    // ================================================================ §2 · verificar re-midiendo

    [Fact]
    public void Verificar_una_unidad_que_bajo_del_umbral_la_resuelve_con_el_numero()
    {
        string path = WriteUnit("src/Troceada.cs", 300);
        Finding f = SeedSizeFinding(path, 3000);

        MeasuredVerdict verdict = _measured.Verify("app", Read(f.Id));

        verdict.Applied.Should().BeTrue();
        verdict.Message.Should().Be("Resuelto: 300 LOC < umbral 1500.");
        Finding after = Read(f.Id);
        after.Status.Should().Be(FindingStatus.Resuelto);
        after.Resolved!.Via.Should().Be(ResolutionVia.Medida);
        after.Resolved.Justification.Should().Contain("300 LOC < umbral 1500");
    }

    /// <summary>
    /// El umbral es «LOC o caracteres»: una unidad de pocas líneas pero muy pesada sigue siendo
    /// grande, y la frase tiene que nombrar el criterio que DE VERDAD decide. Decir
    /// «1269 LOC ≥ 1500» sería mentir con un número correcto.
    /// </summary>
    [Fact]
    public void Una_unidad_grande_por_caracteres_se_confirma_nombrando_los_caracteres()
    {
        string abs = Path.Combine(_clone, "src", "Pesada.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, string.Join('\n', Enumerable.Range(0, 200).Select(_ => new string('x', 400))));
        Finding f = SeedSizeFinding("src/Pesada.cs", 200);

        MeasuredVerdict verdict = _measured.Verify("app", Read(f.Id));

        verdict.Applied.Should().BeTrue();
        verdict.Message.Should().Contain("caracteres ≥ umbral 60000").And.Contain("200 LOC");
        Read(f.Id).Status.Should().Be(FindingStatus.Activo);
    }

    [Fact]
    public void Verificar_sin_clon_dice_que_hay_que_vincularlo_y_no_toca_nada()
    {
        string path = WriteUnit("src/Grande.cs", 2000);
        Finding f = SeedSizeFinding(path, 2000);
        _machines.SetClonePath("app", string.Empty);

        MeasuredVerdict verdict = _measured.Verify("app", Read(f.Id));

        verdict.Applied.Should().BeFalse();
        verdict.Message.Should().Contain("no se puede medir sin el clon local").And.Contain("vincúlalo");
        Finding after = Read(f.Id);
        after.Status.Should().Be(FindingStatus.Activo);
        after.NeedsReview.Should().BeFalse("no poder medir NO ensucia el hallazgo: ese fue el error original");
    }

    [Fact]
    public void Verificar_una_unidad_que_ya_no_esta_no_la_da_por_resuelta()
    {
        Finding f = SeedSizeFinding("src/Fantasma.cs", 3000);

        MeasuredVerdict verdict = _measured.Verify("app", Read(f.Id));

        verdict.Applied.Should().BeFalse();
        verdict.Message.Should().Contain("ya no está en el clon").And.Contain("movido o renombrado");
        Read(f.Id).Status.Should().Be(FindingStatus.Activo);
    }

    /// <summary>Verificar un resuelto que volvió a crecer lo reabre, con la medida.</summary>
    [Fact]
    public void Verificar_un_resuelto_que_volvio_a_crecer_lo_reabre()
    {
        string path = WriteUnit("src/Vuelve.cs", 2400);
        Finding f = SeedSizeFinding(path, 2400);
        f.Resolve(new ResolutionStamp(DateTimeOffset.UtcNow.AddHours(-1), ResolutionVia.Medida,
            AuditMode.Verify, "c", "alvaro", "estaba pequeña"));
        _hub.Store.WriteFinding("app", f);

        MeasuredVerdict verdict = _measured.Verify("app", Read(f.Id));

        verdict.Message.Should().Be("Confirmado: 2400 LOC ≥ umbral 1500.");
        Finding after = Read(f.Id);
        after.Status.Should().Be(FindingStatus.Activo);
        after.Resolved.Should().BeNull();
    }

    // ================================================================ §3 · el desvío del verify

    /// <summary>
    /// «Verificar ahora» sobre un hallazgo medido NO llega al agente. Se comprueba con un agente
    /// que grita si alguien lo llama: gastar tokens en contar líneas es el error de fondo.
    /// </summary>
    [Fact]
    public async Task El_verify_de_un_hallazgo_medido_no_llama_al_agente()
    {
        string path = WriteUnit("src/Troceada.cs", 300);
        Finding f = SeedSizeFinding(path, 3000);
        var agent = new ThrowingAgent();
        var coordinator = new VerifyCoordinator(_hub, _machines, _ulids, agent, _measured);

        VerifyOutcome outcome = await coordinator.RunAsync("app", new[] { f.Id }, CancellationToken.None);

        agent.Called.Should().BeFalse("medir no se le pregunta a un modelo");
        outcome.Applied.Should().Be(1);
        outcome.Measured.Should().Be("Resuelto: 300 LOC < umbral 1500.");
        Read(f.Id).Status.Should().Be(FindingStatus.Resuelto);
    }

    /// <summary>Y los del auditor siguen yendo al auditor: el desvío es por clase, no por capricho.</summary>
    [Fact]
    public async Task El_verify_de_un_hallazgo_del_auditor_sigue_llegando_al_agente()
    {
        WriteUnit("src/Peque.cs", 50);
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "c", "alvaro");
        Finding otro = _ingestion.Create(
            new SubmittedFinding(
                "errores.null.desreferencia", Pillar.Errores, FindingTag.Checklist, Severity.Alta,
                "posible nulo", "d", "i", "r", new[] { new Location("src/Peque.cs", 1) }, null),
            "app", AuditMode.Lotes, stamp);

        var agent = new FakeCopilotAgent(verdictScript: _ => "resuelto");
        var coordinator = new VerifyCoordinator(_hub, _machines, _ulids, agent, _measured);

        VerifyOutcome outcome = await coordinator.RunAsync("app", new[] { otro.Id }, CancellationToken.None);

        outcome.Applied.Should().Be(1);
        outcome.Measured.Should().BeNull("no hubo medida que contar");
        Read(otro.Id).Resolved!.Via.Should().Be(ResolutionVia.Verify);
    }

    /// <summary>La ficha nombra la acción por lo que hace: medir no es preguntar.</summary>
    [Fact]
    public void La_ficha_llama_medir_a_medir()
    {
        string path = WriteUnit("src/Grande.cs", 2000);
        Finding medido = SeedSizeFinding(path, 2000);
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "c", "alvaro");
        Finding delAuditor = _ingestion.Create(
            new SubmittedFinding(
                "errores.null.desreferencia", Pillar.Errores, FindingTag.Checklist, Severity.Alta,
                "posible nulo", "d", "i", "r", new[] { new Location("src/Grande.cs", 1) }, null),
            "app", AuditMode.Lotes, stamp);

        FindingDetailViewModel vm = Detail();
        vm.Load("app", medido.Id);
        vm.IsMeasured.Should().BeTrue();
        vm.VerifyActionLabel.Should().Be("Medir ahora");
        vm.VerifyHelp.Should().Contain("No consulta al auditor ni gasta tokens");

        vm.Load("app", delAuditor.Id);
        vm.IsMeasured.Should().BeFalse();
        vm.VerifyActionLabel.Should().Be("Verificar ahora");
    }

    private FindingDetailViewModel Detail()
        => new(
            _hub, new GovernanceService(_hub, _ulids), _machines,
            new VerifyCoordinator(_hub, _machines, _ulids, new FakeCopilotAgent(), _measured),
            new EditorLauncher(_settings, _machines), new ToastCenter(),
            TestFactory.Links(_hub, _paths), TestFactory.LinkFlow(_hub, _paths, new ToastCenter()));

    /// <summary>Un agente que no admite que se le llame: prueba que el desvío es real.</summary>
    private sealed class ThrowingAgent : ICopilotAgent
    {
        public bool Called { get; private set; }

        public string? ModelName => "no-usar";

        public event Action<string>? TextStreamed;

        public event Action<UsageSample>? UsageReported;

        public Task<bool> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<AgentReadiness> CheckAsync(CancellationToken ct)
            => Task.FromResult(new AgentReadiness(true, "listo"));

        public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentModel>>(Array.Empty<AgentModel>());

        public Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
        {
            Called = true;
            TextStreamed?.Invoke(string.Empty);
            UsageReported?.Invoke(new UsageSample(0, 0, null, null));
            throw new InvalidOperationException("no se debe auditar aquí");
        }

        public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
        {
            Called = true;
            throw new InvalidOperationException("no se debe preguntar por algo que se mide");
        }
    }
}
