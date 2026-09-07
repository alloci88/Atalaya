using Atalaya.App.Services;
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
/// <b>BUGFIX-ANCLA §1.2 — verificar con veredicto que confirma RE-ANCLA en disco.</b>
/// <para>
/// <b>El síntoma medido.</b> El usuario verificaba, el veredicto era «sigue activo», y al volver a
/// abrir la ficha el aviso seguía ahí: la línea guardada no cambiaba. Medido sobre BUG-0101 del hub
/// de xblast —verificado y confirmado— <c>lastConfirmed.commit</c> pasó de <c>5488137</c> a
/// <c>5b659a4</c> y <c>Locations[0]</c> se quedó igual: <c>Confirm</c> refresca el sello y no toca
/// el ancla.
/// </para>
/// <para>
/// <b>Por qué aquí sí y en la ficha no.</b> D-226 reservó el re-anclaje por símbolo en disco para
/// cuando hubiera una persona o una evidencia detrás — «re-anclar por símbolo en disco silenciaría
/// para siempre el único caso que necesita una persona»—. Un veredicto es esa evidencia: el agente
/// acaba de mirar el código de hoy y ha dicho que el defecto sigue en ese miembro. La ficha solo
/// tiene una coincidencia de nombre; el verify tiene un juicio.
/// </para>
/// </summary>
public sealed class VerifyReanchorTests : IDisposable
{
    private const string Slug = "app";
    private const string UnitPath = "src/Cache.cs";

    /// <summary>El fichero de HOY. El miembro existe; el texto anclado ya no.</summary>
    private static readonly string Fuente = string.Join("\r\n", new[]
    {
        "namespace Banco;",
        "",
        "public sealed class Cache",
        "{",
        "    private readonly Dictionary<string, byte[]> _entries = new();",
        "",
        "    public void Put(string key, byte[] value)",
        "    {",
        "        _entries[key] = value;",
        "        Log(key);",
        "    }",
        "",
        "    private static void Log(string key)",
        "    {",
        "    }",
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

    public VerifyReanchorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-reancla", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _machines = new MachineConfigStore(_paths.MachinesJson);

        TestFactory.MakeClone(_clone, "https://github.com/org/app.git");
        Directory.CreateDirectory(Path.Combine(_clone, "src"));
        File.WriteAllText(Path.Combine(_clone, UnitPath), Fuente);
        Commit();

        _machines.SetClonePath(Slug, _clone);
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = Slug, Name = "App", RepoUrl = "https://github.com/org/app.git", CurrentCycle = 1,
        });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// <b>Veredicto que confirma sobre un objetivo sacado del SÍMBOLO: se guarda la línea nueva, el
    /// hash nuevo y el commit de hoy, y el historial lo dice en el MISMO evento.</b>
    /// </summary>
    [Fact]
    public async Task Un_veredicto_que_confirma_reancla_en_disco_y_lo_dice_en_su_evento()
    {
        Ulid id = WriteFinding(line: 11, hash: CodeAnchor.ComputeSnippetHash("lo que se auditó, que ya no está"));
        string commitAntes = Finding(id).LastConfirmed.Commit;

        var agent = new ScriptedVerifier("confirmado", "El defecto sigue en Put.");
        VerifyOutcome outcome = await Verify(agent);

        outcome.Applied.Should().Be(1);
        agent.Targets.Should().ContainSingle().Which.Basis.Should().Be(VerifyBasis.Simbolo);

        Finding stored = Finding(id);
        stored.Status.Should().Be(FindingStatus.Activo, "confirmar no resuelve");
        stored.Locations[0].Line.Should().Be(9, "la primera línea ejecutable de «Put»");
        stored.Locations[0].SnippetHash.Should().Be(
            CodeAnchor.ComputeSnippetHash("        _entries[key] = value;"),
            "el ancla nueva es el código que el agente acaba de juzgar");
        stored.LastConfirmed.Commit.Should().NotBe(commitAntes, "y el commit de hoy");

        // En el MISMO evento, no en uno aparte: es el mismo hecho.
        HistoryEntry last = stored.History[^1];
        last.Event.Should().Be(FindingEvent.Confirmed);
        last.Detail.Should().Contain("re-anclado 11 → 9");
    }

    /// <summary>
    /// <b>Idempotente</b>, como el re-anclaje de D-226: con el ancla ya en su sitio no se escribe
    /// nada — ni la línea, ni el historial—. Verificar dos veces no puede inventarse un movimiento
    /// que no hubo.
    /// </summary>
    [Fact]
    public async Task Con_el_ancla_ya_buena_no_se_reancla_ni_se_anota_movimiento()
    {
        // El ancla apunta ya a la primera línea ejecutable de Put, pero su texto no casa: el
        // objetivo sigue saliendo del símbolo, así que este camino se recorre entero.
        Ulid id = WriteFinding(line: 9, hash: CodeAnchor.ComputeSnippetHash("otro texto"));

        VerifyOutcome outcome = await Verify(new ScriptedVerifier("confirmado", "sigue ahí"));

        outcome.Applied.Should().Be(1);
        Finding stored = Finding(id);
        stored.Locations[0].Line.Should().Be(9);
        stored.History[^1].Detail.Should().NotContain("re-anclado");
    }

    /// <summary>
    /// <b>Un «no verificable» NO toca el ancla.</b> Una no-respuesta no es evidencia de dónde está
    /// el código, y F12 §A ya decidió que tampoco lo es de nada más.
    /// </summary>
    [Fact]
    public async Task Un_no_verificable_deja_el_ancla_intacta()
    {
        string hash = CodeAnchor.ComputeSnippetHash("lo que se auditó, que ya no está");
        Ulid id = WriteFinding(line: 11, hash: hash);

        VerifyOutcome outcome = await Verify(new ScriptedVerifier("no-verificable", "no lo veo claro"));

        outcome.Applied.Should().Be(1);
        Finding stored = Finding(id);
        stored.Locations[0].Line.Should().Be(11, "no se mueve el ancla sin un juicio detrás");
        stored.Locations[0].SnippetHash.Should().Be(hash);
        stored.History[^1].Event.Should().Be(FindingEvent.Inconclusive);
        stored.History[^1].Detail.Should().NotContain("re-anclado");
        stored.NeedsReview.Should().BeTrue();
    }

    // ---------------------------------------------------------------- ayudas

    private Task<VerifyOutcome> Verify(IAuditorProvider agent)
        => new VerifyCoordinator(_hub, _machines, _ulids, agent)
            .RunAsync(Slug, new[] { _findingId }, CancellationToken.None);

    private Finding Finding(Ulid id) => _hub.Store.TryReadFinding(Slug, id.ToString())!;

    private Ulid _findingId;

    private Ulid WriteFinding(int line, string hash)
    {
        Ulid id = _ulids.NewUlid();
        _findingId = id;
        var stamp = new DetectionStamp(
            DateTimeOffset.UtcNow.AddDays(-3), AuditMode.Lotes, "0000000", "auditor",
            HashUtil.Sha256Hex(File.ReadAllBytes(Path.Combine(_clone, UnitPath))), "modelo", "copilot");

        var finding = new Finding
        {
            Id = id,
            DisplayId = "BUG-0007",
            RuleId = "err.recursos",
            Pillar = Pillar.Errores,
            Tag = FindingTag.Checklist,
            Severity = Severity.Alta,
            Confidence = Confidence.Alta,
            Title = "El valor se guarda sin validar",
            Description = "d",
            Impact = "i",
            Recommendation = "r",
            Symbol = "Cache.Put",
            Locations = { new Location(UnitPath, line, hash) },
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
        repo.Commit("estado auditado", who, who, new LibGit2Sharp.CommitOptions { AllowEmptyCommit = true });
    }

    private sealed class ScriptedVerifier : IAuditorProvider
    {
        private readonly string _verdict;
        private readonly string _evidence;

        public ScriptedVerifier(string verdict, string evidence)
        {
            _verdict = verdict;
            _evidence = evidence;
        }

        public List<VerifyTarget> Targets { get; } = new();

        public string? ModelName => "modelo";

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
            Targets.AddRange(request.Targets);
            TextStreamed?.Invoke(string.Empty);
            UsageReported?.Invoke(new UsageSample(10, 5, null, null));
            foreach (VerifyTarget target in request.Targets)
            {
                toolbox.SubmitVerdict(target.FindingUlid, _verdict, _evidence);
            }

            return Task.CompletedTask;
        }
    }
}
