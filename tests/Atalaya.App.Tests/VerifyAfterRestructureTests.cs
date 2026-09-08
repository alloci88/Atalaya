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
/// F16 §E — VERIFICAR DESPUÉS DE UN ARREGLO QUE REESTRUCTURA LA CLASE.
/// <para>
/// <b>El caso real.</b> Un arreglo elimina el código anclado <b>y</b> el símbolo del hallazgo —por
/// ejemplo, quitar el campo estático que el defecto nombraba—. La ficha se quedaba en «No
/// localizado… Verifica para re-anclarlo o cerrarlo» y verificar no cerraba el ciclo: volvía a
/// decir «no localizado». Un callejón sin salida, y encima de pago.
/// </para>
/// <para>
/// <b>La regla ya estaba escrita</b> (MANUAL y D-7xx de F12-B): se juzga el código que hay AHORA
/// —desaparecer es lo que hace el código arreglado— con la cadena método → margen → <b>unidad
/// entera si cambió</b>, y «no localizado» solo cuando no queda nada que juzgar. Aquí la unidad
/// existía y había cambiado, así que la cadena tenía que llegar hasta el tercer eslabón.
/// </para>
/// </summary>
public sealed class VerifyAfterRestructureTests : IDisposable
{
    private const string Slug = "banco";
    private const string UnitPath = "src/Cache.cs";
    private const string AnchoredLine = "    private static Dictionary<string, byte[]> _cache = new();";

    private static readonly string BeforeFix = string.Join("\r\n", new[]
    {
        "namespace Banco;",
        "",
        "public sealed class Cache",
        "{",
        AnchoredLine,
        "",
        "    public byte[] Get(string key) => _cache[key];",
        "",
        "    public void Put(string key, byte[] value) => _cache[key] = value;",
        "}",
        "",
    });

    /// <summary>
    /// El arreglo: el campo estático DESAPARECE —era el defecto— y la clase se reestructura para
    /// no necesitarlo. Ni el fragmento anclado ni el símbolo del hallazgo quedan en el fichero.
    /// </summary>
    private static readonly string AfterFix = string.Join("\r\n", new[]
    {
        "namespace Banco;",
        "",
        "public sealed class Cache",
        "{",
        "    private readonly Dictionary<string, byte[]> _entries = new();",
        "",
        "    public byte[] Get(string key) => _entries[key];",
        "",
        "    public void Put(string key, byte[] value) => _entries[key] = value;",
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

    public VerifyAfterRestructureTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-verify-restruct", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _machines = new MachineConfigStore(_paths.MachinesJson);

        TestFactory.MakeClone(_clone, "https://github.com/org/banco.git");
        Directory.CreateDirectory(Path.Combine(_clone, "src"));
        File.WriteAllText(Path.Combine(_clone, UnitPath), BeforeFix);
        Commit("estado auditado");

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
    /// <b>El caso reproducido, sin commitear.</b> Es el estado exacto en el que Atalaya deja el
    /// clon después de un arreglo asistido —«los cambios están en tu clon, sin commitear»— y el
    /// que tiene un arreglo hecho por prompt manual antes de publicarlo.
    /// <para>
    /// Aquí se cortaba la cadena, y no en el eslabón que parecía: el fallback a la unidad EXISTE y
    /// se usa, pero la pregunta «¿ha cambiado la unidad?» se contestaba con el COMMIT antes que con
    /// el CONTENIDO. Sin commitear, el commit es el mismo y el contenido no: la guarda decía «no ha
    /// cambiado» sobre un fichero que había cambiado entero, y el verify se rendía sin mirar.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Un_arreglo_sin_commitear_que_borra_ancla_y_simbolo_se_juzga_con_la_unidad()
    {
        Fix();

        var agent = new RecordingVerifier("resuelto", "El campo estático ya no existe: la caché es de instancia.");
        VerifyOutcome outcome = await Verify(agent);

        // (1) Se le PREGUNTÓ: no hay callejón sin salida.
        agent.Targets.Should().ContainSingle();

        // (2) Y se le enseñó la UNIDAD ENTERA, que es lo único que queda que juzgar.
        agent.Targets[0].Basis.Should().Be(VerifyBasis.Unidad);
        agent.Targets[0].Snippet.Should().Contain("_entries", "se juzga el código que hay AHORA");
        agent.Targets[0].Snippet.Should().NotContain("_cache");

        // (3) Y el veredicto es normal, no «no localizado».
        outcome.Applied.Should().Be(1);
        Finding stored = _hub.Store.TryReadFinding(Slug, _findingId.ToString())!;
        stored.Status.Should().Be(FindingStatus.Resuelto);
        stored.History.Should().NotContain(h => h.Event == FindingEvent.NotLocated);
    }

    /// <summary>
    /// Y commiteado, igual: es el recorrido de aceptación (arreglar → commitear → verificar).
    /// </summary>
    [Fact]
    public async Task Y_commiteado_tambien_se_juzga_con_la_unidad()
    {
        Fix();
        Commit("arreglo publicado");

        var agent = new RecordingVerifier("resuelto", "Ya no hay estado estático.");
        VerifyOutcome outcome = await Verify(agent);

        agent.Targets.Should().ContainSingle().Which.Basis.Should().Be(VerifyBasis.Unidad);
        outcome.Applied.Should().Be(1);
        _hub.Store.TryReadFinding(Slug, _findingId.ToString())!.Status.Should().Be(FindingStatus.Resuelto);
    }

    /// <summary>
    /// <b>Y commiteado por «Me quedo los cambios», también</b> (F32). Es el recorrido de
    /// aceptación entero por el camino nuevo: arreglar → pulsar el botón → verificar. Importa
    /// tenerlo aparte del commit hecho a mano porque el botón commitea <b>solo los ficheros del
    /// arreglo</b> con <c>git commit --only</c>, y si eso dejara el árbol o el índice en un
    /// estado raro, quien se lo comería sería el verify — que es el paso siguiente y el que
    /// cuesta dinero.
    /// </summary>
    [Fact]
    public async Task Y_commiteado_por_el_boton_del_arreglo_tambien_se_juzga_con_la_unidad()
    {
        Fix();

        FixCommitResult commit = new FixCommitter().Commit(
            _clone, new[] { UnitPath }, "Hace la caché de instancia (OPT-0002)",
            "El campo estático compartía estado entre instancias.");
        commit.Ok.Should().BeTrue(commit.Error);

        var agent = new RecordingVerifier("resuelto", "Ya no hay estado estático.");
        VerifyOutcome outcome = await Verify(agent);

        agent.Targets.Should().ContainSingle().Which.Basis.Should().Be(VerifyBasis.Unidad);
        agent.Targets[0].Snippet.Should().Contain("_entries").And.NotContain("_cache");
        outcome.Applied.Should().Be(1);
        _hub.Store.TryReadFinding(Slug, _findingId.ToString())!.Status
            .Should().Be(FindingStatus.Resuelto);
    }

    /// <summary>
    /// <b>Y la salida sigue siendo un veredicto, no un callejón, aunque el modelo no cierre.</b> Si
    /// con la unidad entera delante sigue sin poder decidir, el desenlace es «no concluyente» con
    /// el paso siguiente escrito —re-auditar—, que es lo que F12 §A dejó decidido.
    /// </summary>
    [Fact]
    public async Task Si_ni_con_la_unidad_se_puede_decidir_el_desenlace_es_no_concluyente_con_salida()
    {
        Fix();

        VerifyOutcome outcome = await Verify(new RecordingVerifier("no-verificable", "No veo el contexto."));

        outcome.Applied.Should().Be(1);
        Finding stored = _hub.Store.TryReadFinding(Slug, _findingId.ToString())!;
        stored.History.Last().Event.Should().Be(FindingEvent.Inconclusive);
        stored.History.Last().Detail.Should().Contain("re-auditar");
        stored.History.Should().NotContain(h => h.Event == FindingEvent.NotLocated);
    }

    /// <summary>
    /// <b>La guarda de evidencia de cambio NO se relaja: se informa mejor.</b> El contenido que
    /// difiere es una prueba MÁS FUERTE que un commit que se movió, así que se mira antes. Lo que
    /// sigue sin poder resolverse es lo que de verdad no cambió: mismo fichero, byte a byte.
    /// </summary>
    [Fact]
    public async Task Sobre_una_unidad_que_NO_ha_cambiado_un_arreglado_se_sigue_degradando()
    {
        // Nada se toca: el fichero es exactamente el que se auditó.
        VerifyOutcome outcome = await Verify(new RecordingVerifier("resuelto", "yo creo que ya está"));

        outcome.Applied.Should().Be(1);
        Finding stored = _hub.Store.TryReadFinding(Slug, _findingId.ToString())!;
        stored.Status.Should().Be(FindingStatus.Activo, "no puede haberse arreglado lo que no ha cambiado");
        stored.History.Should().Contain(h => h.Detail != null && h.Detail.Contains("degradado a presente"));
    }

    // ---------------------------------------------------------------- ayudas

    private Task<VerifyOutcome> Verify(IAuditorProvider agent)
        => new VerifyCoordinator(_hub, _machines, _ulids, agent)
            .RunAsync(Slug, new[] { _findingId }, CancellationToken.None);

    private void Fix() => File.WriteAllText(Path.Combine(_clone, UnitPath), AfterFix);

    private Ulid WriteFinding()
    {
        Ulid id = _ulids.NewUlid();
        string commit = GitInfo.HeadSha(_clone);
        var stamp = new DetectionStamp(
            DateTimeOffset.UtcNow.AddDays(-3), AuditMode.Lotes, commit, "auditor",
            HashUtil.Sha256Hex(File.ReadAllBytes(Path.Combine(_clone, UnitPath))),
            "modelo", "copilot");

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
            Description = "El campo estático _cache comparte estado entre todos los usuarios.",
            Impact = "Fugas de datos entre sesiones.",
            Recommendation = "Hacer la caché de instancia.",
            Symbol = "Cache._cache",
            Locations =
            {
                new Location
                {
                    Path = UnitPath,
                    Line = 5,
                    SnippetHash = CodeAnchor.ComputeSnippetHash(AnchoredLine),
                },
            },
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };

        _hub.Store.WriteFinding(Slug, finding);
        return id;
    }

    private void Commit(string message)
    {
        using var repo = new LibGit2Sharp.Repository(_clone);
        LibGit2Sharp.Commands.Stage(repo, "*");
        var who = new LibGit2Sharp.Signature("t", "t@t", DateTimeOffset.UtcNow);
        repo.Commit(message, who, who, new LibGit2Sharp.CommitOptions { AllowEmptyCommit = true });
    }

    /// <summary>Un verificador que anota QUÉ código se le enseñó y contesta lo pactado.</summary>
    private sealed class RecordingVerifier : IAuditorProvider
    {
        private readonly string _verdict;
        private readonly string _evidence;

        public RecordingVerifier(string verdict, string evidence)
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
