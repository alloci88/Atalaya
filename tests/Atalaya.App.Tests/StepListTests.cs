using Atalaya.App.Services;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Anchoring;
using Atalaya.Domain.Hashing;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using Atalaya.Storage;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// <b>F30 §4 — los pasos que se enseñan son los que se ejecutan.</b>
/// <para>
/// <b>La regla, y por qué se rompe en silencio.</b> Un <c>StepList</c> promete una cosa: que esa
/// columna de líneas es lo que la operación está haciendo. El día que alguien añada una fase al
/// servicio —una escritura más, otra publicación— sin declararla, la lista seguirá pintándose sin
/// fallar, sin avisar y sin que ningún test de los otros se entere: simplemente habrá un tramo de
/// segundos que la pantalla no cuenta, que es exactamente el estado del que sale esta fase. Y al
/// revés: un paso declarado que ya no se ejecuta se queda en «pendiente» para siempre, y una lista
/// que nunca termina se lee como una operación colgada.
/// </para>
/// <para>
/// <b>Cómo se comprueba.</b> Cada servicio ejecuta <b>por</b> su <c>StepList</c>
/// —<c>Run</c> revienta con un identificador que no esté declarado—, así que basta con correr la
/// operación de verdad y cuadrar lo declarado con lo ejecutado: misma lista, mismo orden. No hay
/// un segundo camino que probar porque no existe: cuando nadie pasa una lista, el servicio se hace
/// una.
/// </para>
/// </summary>
public sealed class StepListTests : IDisposable
{
    private const string Slug = "app";

    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public StepListTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-f30-pasos", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clon");
        Directory.CreateDirectory(_clone);
        File.WriteAllText(Path.Combine(_clone, "app.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        File.WriteAllText(Path.Combine(_clone, "Hex.cs"), Codigo);

        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();

        // Sin `EnsureSync` el hub no tiene remoto: `Sync` es null y publicar no toca la red (N-1).
        _settings.Current.HubUrlOverride = Path.Combine(_root, "remote");
        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _machines.SetClonePath(Slug, _clone);
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

    // ============================================================ (1) el alta

    /// <summary>
    /// El alta, con sus dos mitades y el diálogo del ciclo en medio. Se corre entera —el servicio
    /// de verdad, contra un hub de verdad— y se cuadra.
    /// </summary>
    [Fact]
    public void El_alta_ejecuta_exactamente_los_pasos_que_ensena()
    {
        AppOnboardingService service = TestFactory.OnboardingService(_hub, _machines, _ulids);
        StepList steps = AppOnboardingService.NewSteps(importing: false);

        var request = new OnboardingRequest(
            Slug, "App", "https://example.invalid/org/app.git", _clone, TechStack.DotNet);

        steps.Start();
        OnboardingScan prepared = service.Prepare(request, steps);
        service.Finish(request, prepared, CycleConfig.Default, steps);
        steps.Finish();

        steps.Executed.Should().Equal(steps.Plan);
    }

    /// <summary>
    /// Y con baseline v4, que <b>añade un paso</b>. Es la única lista de las cuatro cuyo plan
    /// depende de algo, así que es la única que podría enseñar un paso que no se ejecuta — un
    /// «Importar el baseline» en pendiente para siempre en un alta que no importa nada.
    /// </summary>
    [Fact]
    public void El_alta_con_baseline_v4_ensena_y_ejecuta_tambien_el_paso_de_importar()
    {
        string codeAudit = Path.Combine(_clone, V4Baseline.FolderName);
        Directory.CreateDirectory(codeAudit);
        File.WriteAllText(Path.Combine(codeAudit, "BASELINE.md"), "# Baseline\n");
        File.WriteAllText(Path.Combine(codeAudit, "SILENCIADOS.md"), "# Silenciados\n");

        AppOnboardingService service = TestFactory.OnboardingService(_hub, _machines, _ulids);
        StepList steps = AppOnboardingService.NewSteps(importing: true);

        var request = new OnboardingRequest(
            Slug, "App", "https://example.invalid/org/app.git", _clone, TechStack.DotNet,
            Importing: true, CodeAuditPath: codeAudit);

        steps.Start();
        OnboardingScan prepared = service.Prepare(request, steps);
        service.Finish(request, prepared, CycleConfig.Default, steps);
        steps.Finish();

        steps.Plan.Should().StartWith(new[] { AppOnboardingService.Importar });
        steps.Executed.Should().Equal(steps.Plan);
    }

    // ============================================================ (2) el re-escaneo

    [Fact]
    public void El_re_escaneo_ejecuta_exactamente_los_pasos_que_ensena()
    {
        SeedApp();
        var service = new InventoryRescanService(_hub, new InventoryScanner());
        StepList steps = InventoryRescanService.NewSteps();

        steps.Start();
        service.Rescan(Slug, _clone, steps);
        steps.Finish();

        steps.Executed.Should().Equal(steps.Plan);
    }

    // ============================================================ (3) la verificación

    /// <summary>
    /// Con un hallazgo que SÍ llega al agente: es el camino largo, el que tiene los cinco pasos.
    /// </summary>
    [Fact]
    public async Task La_verificacion_ejecuta_exactamente_los_pasos_que_ensena()
    {
        SeedApp();
        Finding f = SeedFinding();
        var agent = new FakeCopilotAgent(verdictScript: _ => "confirmado");
        var coordinator = new VerifyCoordinator(_hub, _machines, _ulids, agent);
        StepList steps = VerifyCoordinator.NewSteps();

        steps.Start();
        await coordinator.RunAsync(Slug, new[] { f.Id }, CancellationToken.None, steps);
        steps.Finish();

        steps.Executed.Should().Equal(steps.Plan);
    }

    /// <summary>
    /// Y sin nada que preguntar —el hallazgo ya no está en el hub—: la operación se acorta pero la
    /// lista <b>no</b>. Era el atajo del que salía esta regla: un <c>return</c> temprano dejaba tres
    /// líneas en pendiente hasta que la lista se iba de la pantalla.
    /// </summary>
    [Fact]
    public async Task La_verificacion_sin_objetivos_tambien_recorre_la_lista_entera()
    {
        SeedApp();
        var agent = new FakeCopilotAgent();
        var coordinator = new VerifyCoordinator(_hub, _machines, _ulids, agent);
        StepList steps = VerifyCoordinator.NewSteps();

        steps.Start();
        await coordinator.RunAsync(Slug, new[] { _ulids.NewUlid() }, CancellationToken.None, steps);
        steps.Finish();

        steps.Executed.Should().Equal(steps.Plan);
    }

    // ============================================================ (4) reconciliar costes

    [Fact]
    public void Reconciliar_costes_ejecuta_exactamente_los_pasos_que_ensena()
    {
        SeedApp();
        var rates = new ModelRatesService(_hub);
        rates.Save(TestRates.Table());
        _hub.Store.WriteSession(new AuditSession
        {
            Id = _ulids.NewUlid(),
            AppSlug = Slug,
            Mode = AuditMode.Lotes,
            By = "alguien",
            Machine = "banco",
            StartedUtc = DateTimeOffset.UtcNow,
            EndedUtc = DateTimeOffset.UtcNow,
            CycleN = 1,
            Model = ModelIds.Auto,
            Usage = new UsageTotals { OutputTokens = 4_000, Calls = 1 },
        });

        var service = new CostReconciliationService(_hub, rates);
        StepList steps = CostReconciliationService.NewSteps();

        steps.Start();
        ReconciliationOutcome outcome = service.Reconcile(Slug, TestRates.Model, steps: steps);
        steps.Finish();

        steps.Executed.Should().Equal(steps.Plan);

        // Y el diálogo puede decir CUÁNTO cerró, que es la pregunta por la que se abrió.
        outcome.Sessions.Should().Be(1);
        outcome.Credits.Should().NotBeNull();
    }

    // ============================================================ Andamiaje

    private void SeedApp()
        => _hub.Store.WriteApp(new AppConfig
        {
            Slug = Slug,
            Name = "App",
            RepoUrl = "https://example.invalid/org/app.git",
            CurrentCycle = 1,
        });

    private const string Codigo = """
        class Hex
        {
            public static byte[] HexStringToByteArray(string hex)
            {
                var bytes = new byte[hex.Length / 2];
                for (int i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber);
                }

                return bytes;
            }
        }
        """;

    private const string LineaMala =
        "            bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber);";

    /// <summary>Un hallazgo anclado al código que hay en el clon: el verify llega hasta el agente.</summary>
    private Finding SeedFinding()
    {
        string file = Path.Combine(_clone, "Hex.cs");
        var stamp = new DetectionStamp(
            DateTimeOffset.UtcNow.AddDays(-1), AuditMode.Lotes, "abc1234", "alguien",
            HashUtil.Sha256Hex(File.ReadAllBytes(file)));

        var f = new Finding
        {
            Id = _ulids.NewUlid(),
            DisplayId = "BUG-0001",
            RuleId = "errores.calculo.negocio",
            Pillar = Pillar.Errores,
            Severity = Severity.Alta,
            Confidence = Confidence.Media,
            Title = "No valida la entrada",
            Description = "No comprueba null ni longitud par.",
            Recommendation = "Valida antes de convertir.",
            Locations = { new Location("Hex.cs", 7, CodeAnchor.ComputeSnippetHash(LineaMala)) },
            Symbol = "Hex.HexStringToByteArray",
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
        _hub.Store.WriteFinding(Slug, f);
        return f;
    }
}
