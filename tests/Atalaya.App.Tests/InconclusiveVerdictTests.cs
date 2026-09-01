using Atalaya.App.Services;
using Atalaya.App.ViewModels;
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
/// F12 §A — <b>una no-respuesta no es una confirmación</b>.
/// <para>
/// El parte del banco de pruebas: el verificador contestó literalmente «no verificable — … Sin el
/// cuerpo actual del método no se puede decidir si el defecto persiste o quedó resuelto», y la
/// aplicación lo anotó en el historial como <b>Confirmado</b>. Con eso, un hallazgo se leía más
/// sólido cuantas más veces NO se hubiera podido verificar: la métrica se alimentaba justo de la
/// ausencia de evidencia.
/// </para>
/// <para>
/// Rompía dos normas de la casa a la vez — «ningún número sin causa» y el espejo de «ninguna
/// resolución sin evidencia», que es que tampoco hay confirmación sin ella. Ahora tiene desenlace
/// propio: <see cref="FindingEvent.Inconclusive"/>, «No concluyente», que no toca ningún contador
/// y que dice qué hacer a continuación.
/// </para>
/// </summary>
public sealed class InconclusiveVerdictTests : IDisposable
{
    private const string Fuente = """
        class Informe
        {
            public int Total(IEnumerable<Linea> lineas)
            {
                int total = 0;
                foreach (Linea l in lineas)
                {
                    total += l.Importe;
                }

                return total;
            }
        }
        """;

    private const string LineaAnclada = "            foreach (Linea l in lineas)";

    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly ToastCenter _toasts = new();
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public InconclusiveVerdictTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-inconclusive", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
        Directory.CreateDirectory(_clone);
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _machines.SetClonePath("app", _clone);
        _hub.Store.WriteApp(new AppConfig { Slug = "app", Name = "App", RepoUrl = "u", CurrentCycle = 1 });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    // ------------------------------------------------------------------ utillaje

    private Finding Seed(string path = "Informe.cs")
    {
        string abs = Path.Combine(_clone, path);
        File.WriteAllText(abs, Fuente);

        var stamp = new DetectionStamp(
            DateTimeOffset.UtcNow.AddDays(-1), AuditMode.Lotes, "abc1234", "alvaro",
            HashUtil.Sha256Hex(File.ReadAllBytes(abs)));

        var f = new Finding
        {
            Id = _ulids.NewUlid(),
            DisplayId = "MEJ-0012",
            RuleId = "optimizacion.iteracion.multiples-pasadas",
            Pillar = Pillar.Optimizacion,
            Severity = Severity.Media,
            Confidence = Confidence.Media,
            Title = "Recorre la colección varias veces",
            Description = "Cada agregado hace su propia pasada.",
            Recommendation = "Acumula en una sola pasada.",
            Locations = { new Location(path, 5, CodeAnchor.ComputeSnippetHash(LineaAnclada)) },
            Symbol = "Informe.Total",
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
            TimesConfirmed = 3,
        };
        f.History.Add(new HistoryEntry(stamp.Utc, FindingEvent.Detected, "alvaro", "detected via Lotes"));
        _hub.Store.WriteFinding("app", f);
        return f;
    }

    private Finding Read(Finding f) => _hub.Store.TryReadFinding("app", f.Id.ToString())!;

    private VerifyCoordinator Coordinator(ICopilotAgent agent)
        => new(_hub, _machines, _ulids, agent);

    // ------------------------------------------------------------------ (1) el caso del parte

    /// <summary>
    /// La frase EXACTA que el verificador devolvió en el banco de pruebas. Lo que no puede pasar es
    /// que se registre como confirmación.
    /// </summary>
    private const string LaNoRespuesta =
        "Sin el cuerpo actual del método no se puede decidir si el defecto persiste o quedó resuelto";

    [Fact]
    public async Task Un_no_verificable_se_anota_como_no_concluyente_y_no_como_confirmado()
    {
        Finding f = Seed();

        var agent = new FakeCopilotAgent(verdictScript: _ => "no-verificable");
        VerifyOutcome outcome = await Coordinator(agent).RunAsync("app", new[] { f.Id }, CancellationToken.None);

        Finding after = Read(f);
        HistoryEntry last = after.History[^1];

        last.Event.Should().Be(FindingEvent.Inconclusive);
        after.History.Should().NotContain(
            h => h.Event == FindingEvent.Confirmed,
            "una no-respuesta no es una confirmación");
        outcome.Toast.Should().Contain("no concluyente");
    }

    [Fact]
    public async Task Un_no_verificable_no_toca_veces_confirmado_ni_la_confianza()
    {
        Finding f = Seed();
        DetectionStamp antes = f.LastConfirmed;

        var agent = new FakeCopilotAgent(verdictScript: _ => "no-verificable");
        await Coordinator(agent).RunAsync("app", new[] { f.Id }, CancellationToken.None);

        Finding after = Read(f);
        after.TimesConfirmed.Should().Be(3, "no se ha vuelto a ver el defecto: se ha dejado de ver");
        after.Confidence.Should().Be(Confidence.Media);
        after.LastConfirmed.Utc.Should().Be(antes.Utc, "«última confirmación» sigue siendo la de antes");
        after.Status.Should().Be(FindingStatus.Activo);
        after.NeedsReview.Should().BeTrue("lo que sí deja es la marca de que hay que mirarlo");
    }

    /// <summary>
    /// Un desenlace que no propone nada deja al usuario donde lo encontró. El paso siguiente
    /// depende de cuánto código llegó a ver el instrumento: con el símbolo delante, lo que falta es
    /// contexto; con la unidad entera delante, lo que falta es una auditoría.
    /// </summary>
    [Fact]
    public async Task El_no_concluyente_propone_el_paso_siguiente()
    {
        Finding f = Seed();

        var agent = new FakeCopilotAgent(verdictScript: _ => "no-verificable");
        VerifyOutcome outcome = await Coordinator(agent).RunAsync("app", new[] { f.Id }, CancellationToken.None);

        Finding after = Read(f);
        string detail = after.History[^1].Detail ?? string.Empty;

        detail.Should().Contain("ampliar el contexto").And.Contain("Informe.cs");
        outcome.Toast.Should().Contain("ampliar el contexto");
    }

    /// <summary>
    /// Y la causa que dio el modelo viaja con el desenlace: «no concluyente» a secas sería el mismo
    /// número sin causa que este parte vino a quitar.
    /// </summary>
    [Fact]
    public async Task El_no_concluyente_conserva_la_evidencia_que_dio_el_modelo()
    {
        Finding f = Seed();

        var agent = new FakeCopilotAgent(
            verdictScript: _ => "no-verificable", verdictEvidence: _ => LaNoRespuesta);
        await Coordinator(agent).RunAsync("app", new[] { f.Id }, CancellationToken.None);

        Read(f).History[^1].Detail.Should().Contain(LaNoRespuesta);
    }

    // ------------------------------------------------------------------ (2) se ve como tal

    [Fact]
    public void El_evento_se_llama_No_concluyente_en_la_interfaz()
    {
        FindingEventNames.Display(FindingEvent.Inconclusive).Should().Be("No concluyente");
        FindingEventNames.Display(FindingEvent.Inconclusive).Should().NotBe("Confirmado");
        FindingEventNames.Icon(FindingEvent.Inconclusive).Should().NotBe(
            FindingEventNames.Icon(FindingEvent.Confirmed),
            "con el mismo ✓ delante, el historial sigue leyéndose como una confirmación");
    }

    [Fact]
    public async Task La_ficha_ensena_el_no_concluyente_en_su_historial_y_el_contador_intacto()
    {
        Finding f = Seed();
        var agent = new FakeCopilotAgent(verdictScript: _ => "no-verificable");
        await Coordinator(agent).RunAsync("app", new[] { f.Id }, CancellationToken.None);

        FindingDetailViewModel vm = Detail();
        vm.Load("app", f.Id);

        HistoryRow row = vm.History.Should().Contain(h => h.Event == FindingEvent.Inconclusive).Subject;
        row.Label.Should().Be("No concluyente");
        row.Detail.Should().Contain("no concluyente");

        vm.Meta.Should().Contain(m => m.Label == "Veces confirmado" && m.Value == "3");
    }

    // ------------------------------------------------------------------ (3) el mismo agujero, en el barrido

    /// <summary>
    /// El barrido tenía la MITAD del agujero: un «no-verificable» del auditor también se escribía
    /// con <see cref="FindingEvent.Confirmed"/> en el historial. No subía el contador —de eso se
    /// encarga <see cref="Finding.Confirm"/>, que ahí no se llamaba— pero la ficha decía
    /// «Confirmado ✓» sobre una no-respuesta, que es exactamente la lectura que corrompe la
    /// prioridad.
    /// </summary>
    [Fact]
    public void Un_no_verificable_del_auditor_tambien_es_no_concluyente()
    {
        Finding f = Seed();
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "def5678", "alvaro");

        ReconcileOutcome outcome = new ReconciliationService(_hub).Apply(
            "app", f, ReconcileVerdict.NoVerificable, LaNoRespuesta, AuditMode.Lotes, stamp);

        outcome.Should().Be(ReconcileOutcome.NeedsReview);
        Finding after = Read(f);
        after.History[^1].Event.Should().Be(FindingEvent.Inconclusive);
        after.History.Should().NotContain(h => h.Event == FindingEvent.Confirmed);
        after.History[^1].Detail.Should().Contain(LaNoRespuesta).And.Contain("siguiente paso");
        after.TimesConfirmed.Should().Be(3);
        after.NeedsReview.Should().BeTrue();
    }

    /// <summary>
    /// Y la OTRA mitad, que no estaba rota pero estaba a un `default:` de estarlo: «presente» era
    /// la rama por defecto del switch de veredictos. Un valor que el parser no reconociera —o uno
    /// nuevo del enum— habría subido «Veces confirmado» sin que nadie mirase el código. Ahora
    /// «presente» entra por su nombre y lo ambiguo queda sin concluir.
    /// </summary>
    [Fact]
    public void Un_veredicto_ambiguo_no_cae_en_presente_por_defecto()
    {
        Finding f = Seed();
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "def5678", "alvaro");

        ReconcileOutcome outcome = new ReconciliationService(_hub).Apply(
            "app", f, (ReconcileVerdict)97, "quién sabe", AuditMode.Lotes, stamp);

        outcome.Should().Be(ReconcileOutcome.NeedsReview);
        Finding after = Read(f);
        after.TimesConfirmed.Should().Be(3, "confirmar exige que alguien haya vuelto a ver el defecto");
        after.History[^1].Event.Should().Be(FindingEvent.Inconclusive);
        after.History[^1].Detail.Should().Contain("no reconocido");
    }

    /// <summary>Y el parser sigue siendo la primera puerta: lo que no entiende, lo rechaza.</summary>
    [Fact]
    public void El_parser_de_veredictos_rechaza_lo_que_no_entiende()
    {
        ReconciliationService.TryParseVerdict("quizá", out _).Should().BeFalse();
        ReconciliationService.TryParseVerdict("", out _).Should().BeFalse();
        ReconciliationService.TryParseVerdict("presente", out ReconcileVerdict v).Should().BeTrue();
        v.Should().Be(ReconcileVerdict.Presente);
    }

    private FindingDetailViewModel Detail()
        => new(
            _hub,
            new GovernanceService(_hub, _ulids),
            _machines,
            new VerifyCoordinator(_hub, _machines, _ulids, new FakeCopilotAgent()),
            new EditorLauncher(_settings, _machines),
            _toasts,
            TestFactory.Links(_hub, _paths),
            TestFactory.LinkFlow(_hub, _paths, _toasts),
            new AnchorRepair(_hub));
}
