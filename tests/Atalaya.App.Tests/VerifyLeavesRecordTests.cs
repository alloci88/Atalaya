using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.ClaudeCode;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Anchoring;
using Atalaya.Domain.Hashing;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F16 §F — VERIFICAR DEJA CONSTANCIA.
/// <para>
/// Verificar era una acción fantasma: costaba dinero, decidía estados —resolvía hallazgos, abría
/// disputas, ponía marcas de revisión— y no dejaba informe. De las tres acciones que gastan cuota,
/// era la única cuyo veredicto no se podía releer meses después.
/// </para>
/// <para>
/// Lo que se fija aquí: <b>siempre</b> hay evento en el historial —también cuando el desenlace es
/// frustrante—, con la casa y el modelo y apuntando a su sesión; hay informe en Informes con qué
/// código se le enseñó y qué contestó el modelo; y ese informe se filtra por su tipo.
/// </para>
/// </summary>
public sealed class VerifyLeavesRecordTests : IDisposable
{
    private const string Slug = "banco";
    private const string UnitPath = "src/Cache.cs";

    private static readonly string Code = string.Join("\r\n", new[]
    {
        "namespace Banco;",
        "",
        "public sealed class Cache",
        "{",
        "    private static Dictionary<string, byte[]> _cache = new();",
        "",
        "    public byte[] Get(string key) => _cache[key];",
        "}",
        "",
    });

    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private readonly Ulid _findingId;

    public VerifyLeavesRecordTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-verify-record", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _machines = new MachineConfigStore(_paths.MachinesJson);
        TestRates.Seed(_hub);

        TestFactory.MakeClone(_clone, "https://github.com/org/banco.git");
        Directory.CreateDirectory(Path.Combine(_clone, "src"));
        File.WriteAllText(Path.Combine(_clone, UnitPath), Code);
        Commit();

        MachineConfig machine = _machines.Load();
        machine.ClonePaths[Slug] = _clone;
        _machines.Save(machine);
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = Slug,
            Name = "Banco",
            RepoUrl = "https://github.com/org/banco.git",
            CurrentCycle = 1,
        });

        _findingId = WriteFinding();
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
    /// El evento del historial dice el desenlace, con qué casa y qué modelo se juzgó, y APUNTA a su
    /// sesión — que es lo que convierte una línea de texto en un camino al informe.
    /// </summary>
    [Fact]
    public async Task El_evento_dice_el_desenlace_la_casa_el_modelo_y_apunta_a_su_sesion()
    {
        await Verify("confirmado", "El campo estático sigue ahí.");

        Finding stored = _hub.Store.TryReadFinding(Slug, _findingId.ToString())!;
        HistoryEntry last = stored.History.Last();

        last.Event.Should().Be(FindingEvent.Confirmed);
        last.Detail.Should().Contain("Claude Code").And.Contain("opus");
        last.SessionId.Should().NotBeNullOrWhiteSpace();

        AuditSession session = _hub.Store.ListSessions(Slug).Single(s => s.Mode == AuditMode.Verify);
        last.SessionId.Should().Be(session.Id.ToString());
        File.Exists(_hub.HubPaths.ReportFile(Slug, session.Id.ToString())).Should().BeTrue();
    }

    /// <summary>
    /// <b>También cuando el desenlace es frustrante.</b> Un «no concluyente» es la respuesta que más
    /// cuesta releer meses después, así que es la que menos se puede quedar sin rastro.
    /// </summary>
    [Fact]
    public async Task Un_no_concluyente_deja_el_mismo_rastro_que_un_veredicto_util()
    {
        await Verify("no-verificable", "No puedo decidirlo desde aquí.");

        Finding stored = _hub.Store.TryReadFinding(Slug, _findingId.ToString())!;
        HistoryEntry last = stored.History.Last();

        last.Event.Should().Be(FindingEvent.Inconclusive);
        last.Detail.Should().Contain("Claude Code");
        last.SessionId.Should().NotBeNullOrWhiteSpace();

        string report = ReadReport();
        report.Should().Contain("**Veredicto**: no concluyente");
        report.Should().Contain("No puedo decidirlo desde aquí.");
    }

    /// <summary>
    /// El informe lleva lo que nadie más guarda: <b>qué código se le enseñó</b>. Sin eso, un «no
    /// concluyente» no permite saber si al instrumento le faltó contexto o le faltó criterio — y son
    /// dos cosas con dos remedios distintos.
    /// </summary>
    [Fact]
    public async Task El_informe_dice_que_codigo_se_le_enseño_y_que_contesto()
    {
        await Verify("confirmado", "El campo estático sigue ahí, línea 5.");

        string report = ReadReport();

        report.Should().StartWith("# Verificación — Banco");
        report.Should().Contain("- **Proveedor**: Claude Code");
        report.Should().Contain("- **Modelo**: opus");
        report.Should().Contain("OPT-0002");
        report.Should().Contain("**Código que se le enseñó**: el código anclado");
        report.Should().Contain("**Veredicto**: confirmado");
        report.Should().Contain("El campo estático sigue ahí, línea 5.");
        report.Should().Contain("**Tokens**: entrada 1000, salida 2000");
        report.Should().Contain("- **Coste**:");
    }

    /// <summary>
    /// Y el informe se filtra por SU tipo. Con las verificaciones mezcladas entre las auditorías,
    /// «qué se ha verificado esta semana» obligaba a bucear.
    /// </summary>
    [Fact]
    public async Task El_informe_de_verificacion_se_filtra_por_su_tipo()
    {
        await Verify("confirmado", "sigue");

        var reports = new ReportsQuery(_hub);
        ReportEntry entry = reports.All().Should().ContainSingle().Subject;

        entry.Kind.Should().Be(ReportKind.Verificacion);
        ReportKinds.Display(entry.Kind).Should().Be("Verificación");
        reports.Filter(new ReportsFilter(Kind: ReportKind.Verificacion)).Should().ContainSingle();
        reports.Filter(new ReportsFilter(Kind: ReportKind.Sesion)).Should().BeEmpty();
    }

    /// <summary>
    /// La fila de «Actividad de sesiones» de Métricas enlaza al informe: es lo que hace que una
    /// verificación se pueda abrir desde el panel como cualquier otra sesión.
    /// </summary>
    [Fact]
    public async Task La_fila_de_actividad_de_sesiones_lleva_a_su_informe()
    {
        await Verify("confirmado", "sigue");

        MetricsDashboard dashboard = new MetricsQuery(_hub).Build(new MetricsFilter(null, MetricsRange.All));
        SessionRow row = dashboard.Sessions.Should().ContainSingle().Subject;

        row.Provider.Should().Be("Claude Code");
        File.Exists(_hub.HubPaths.ReportFile(row.Slug, row.SessionId)).Should().BeTrue(
            "la fila abre el informe de su sesión, y ahora las verificaciones tienen uno");
    }

    // ---------------------------------------------------------------- ayudas

    private Task<VerifyOutcome> Verify(string verdict, string evidence)
        => new VerifyCoordinator(_hub, _machines, _ulids, new ScriptedVerifier(verdict, evidence))
            .RunAsync(Slug, new[] { _findingId }, CancellationToken.None);

    private string ReadReport()
    {
        AuditSession session = _hub.Store.ListSessions(Slug).Single(s => s.Mode == AuditMode.Verify);
        return File.ReadAllText(_hub.HubPaths.ReportFile(Slug, session.Id.ToString()));
    }

    private Ulid WriteFinding()
    {
        Ulid id = _ulids.NewUlid();
        string anchored = "    private static Dictionary<string, byte[]> _cache = new();";
        var stamp = new DetectionStamp(
            DateTimeOffset.UtcNow.AddDays(-2), AuditMode.Lotes, GitInfo.HeadSha(_clone), "auditor",
            HashUtil.Sha256Hex(File.ReadAllBytes(Path.Combine(_clone, UnitPath))), "gpt-5", "copilot");

        var finding = new Finding
        {
            Id = id,
            DisplayId = "OPT-0002",
            RuleId = "rend.estado",
            Pillar = Pillar.Optimizacion,
            Tag = FindingTag.Checklist,
            Severity = Severity.Alta,
            Confidence = Confidence.Alta,
            Title = "Caché estática compartida entre instancias",
            Description = "Estado compartido entre usuarios.",
            Impact = "Fugas.",
            Recommendation = "Hacerla de instancia.",
            Symbol = "Cache._cache",
            Locations =
            {
                new Location
                {
                    Path = UnitPath,
                    Line = 5,
                    SnippetHash = CodeAnchor.ComputeSnippetHash(anchored),
                },
            },
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };

        _hub.Store.WriteFinding(Slug, finding);
        return id;
    }

    private void Commit()
    {
        using var repo = new LibGit2Sharp.Repository(_clone);
        LibGit2Sharp.Commands.Stage(repo, "*");
        var who = new LibGit2Sharp.Signature("t", "t@t", DateTimeOffset.UtcNow);
        repo.Commit("inicial", who, who, new LibGit2Sharp.CommitOptions { AllowEmptyCommit = true });
    }

    /// <summary>Un verificador de Claude Code guionizado, con tokens para que haya coste que contar.</summary>
    private sealed class ScriptedVerifier : IAuditorProvider
    {
        private readonly string _verdict;
        private readonly string _evidence;

        public ScriptedVerifier(string verdict, string evidence)
        {
            _verdict = verdict;
            _evidence = evidence;
        }

        public string ProviderId => ClaudeCodeProvider.Id;

        public string ProviderName => "Claude Code";

        public string? ModelName => "opus";

        public event Action<string>? TextStreamed;

        public event Action<UsageSample>? UsageReported;

        public Task<bool> EnsureReadyAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<AgentReadiness> CheckAsync(CancellationToken ct)
            => Task.FromResult(new AgentReadiness(true, "listo"));

        public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentModel>>(Array.Empty<AgentModel>());

        public Task AuditUnitAsync(AuditUnitRequest request, IAuditToolbox toolbox, CancellationToken ct)
            => throw new NotSupportedException();

        public Task VerifyAsync(VerifyRequest request, IVerifyToolbox toolbox, CancellationToken ct)
        {
            TextStreamed?.Invoke(string.Empty);
            UsageReported?.Invoke(new UsageSample(1000, 2000, null, null));
            foreach (VerifyTarget target in request.Targets)
            {
                toolbox.SubmitVerdict(target.FindingUlid, _verdict, _evidence);
            }

            return Task.CompletedTask;
        }
    }
}
