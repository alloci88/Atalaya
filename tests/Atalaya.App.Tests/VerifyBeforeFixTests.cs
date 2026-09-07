using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Anchoring;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F34 §1 — <b>NO SE ARREGLA LO QUE NO ESTÁ VERIFICADO.</b>
/// <para>
/// F33 (D-1038) enumeró los dos estados que piden verificación —el ancla perdida (D-225,
/// BUGFIX-ANCLA) y el arreglo sin veredicto (D-557: arreglar no resuelve)— y los usó para pintar
/// «Verificar ahora» de verde. Aquí se cierra la otra mitad: mientras uno de los dos esté puesto,
/// «Arreglar con agente» queda <b>apagado con la razón al lado</b>, por el mismo camino y con la
/// misma forma que el apagado por cambios sin commitear — <see cref="FixLaunchDecision"/>, un solo
/// método con dos llamantes, y el chip de la razón bajo el botón (P-27).
/// </para>
/// <para>
/// Todo se mide sobre el modelo: <c>CanStartFix</c> y <c>AssistedFixBlock</c> del view-model, que
/// es lo que la vista enlaza. Sin XAML.
/// </para>
/// </summary>
public sealed class VerifyBeforeFixTests : IDisposable
{
    private const string Slug = "alpha";
    private const string UnitPath = "src/Repositorio.cs";

    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly GovernanceService _governance;
    private readonly ToastCenter _toasts = new();
    private readonly AgentBusyGate _busy = new();
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    private const string Fuente = """
        using System;

        namespace Demo;

        public sealed class Repositorio
        {
            public void Guardar(string dato)
            {
                var stream = File.OpenWrite(dato);
                stream.Write(Encoding.UTF8.GetBytes(dato));
            }
        }
        """;

    private const int LineaDelHallazgo = 9;
    private const string LineaAnclada = "        var stream = File.OpenWrite(dato);";

    public VerifyBeforeFixTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-f34", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _governance = new GovernanceService(_hub, _ulids);

        TestFactory.MakeClone(_clone, "https://github.com/org/alpha.git");
        string abs = Path.Combine(_clone, UnitPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, Fuente);
        Commit();

        _machines.SetClonePath(Slug, _clone);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = Slug, Name = "Alpha", RepoUrl = "https://github.com/org/alpha.git", CurrentCycle = 1,
        });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Un temporal que no se deja borrar no invalida el test.
        }
    }

    // ---------------------------------------------------------------- utillaje

    private void Commit()
    {
        using var repo = new LibGit2Sharp.Repository(_clone);
        LibGit2Sharp.Commands.Stage(repo, "*");
        var who = new LibGit2Sharp.Signature("t", "t@t", DateTimeOffset.UtcNow);
        repo.Commit("estado", who, who, new LibGit2Sharp.CommitOptions { AllowEmptyCommit = true });
    }

    private Finding Seed(string? snippetHash = null)
    {
        var stamp = new DetectionStamp(
            DateTimeOffset.UtcNow.AddDays(-3), AuditMode.Lotes, "0123456789abcdef", "alvaro");
        var finding = new Finding
        {
            Id = _ulids.NewUlid(),
            DisplayId = "BUG-0042",
            RuleId = "errores.recursos.idisposable-no-liberado",
            Pillar = Pillar.Errores,
            Severity = Severity.Alta,
            Confidence = Confidence.Media,
            Status = FindingStatus.Activo,
            Title = "IDisposable sin liberar",
            Description = "El stream no se cierra.",
            Impact = "Fuga de descriptores.",
            Recommendation = "Envolver en using.",
            Locations =
            {
                new Location(UnitPath, LineaDelHallazgo,
                    snippetHash ?? CodeAnchor.ComputeSnippetHash(LineaAnclada)),
            },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
            TimesConfirmed = 2,
        };
        finding.History.Add(new HistoryEntry(stamp.Utc, FindingEvent.Detected, "alvaro", "detected"));
        _hub.Store.WriteFinding(Slug, finding);
        return finding;
    }

    /// <summary>El ancla que no casa, con símbolo: el estado «Reanclado» de BUGFIX-ANCLA.</summary>
    private Finding SeedAnclaPerdida()
    {
        Finding f = Seed(snippetHash: CodeAnchor.ComputeSnippetHash("lo que se auditó y ya no está"));
        f.Symbol = "Repositorio.Guardar";
        _hub.Store.WriteFinding(Slug, f);
        return f;
    }

    /// <summary>Un arreglo sin veredicto después, con el ancla intacta (D-557).</summary>
    private Finding SeedArregloSinVerificar()
    {
        Finding f = Seed();
        f.Record(new HistoryEntry(DateTimeOffset.UtcNow, FindingEvent.FixCommitted, "alvaro", "commit"));
        _hub.Store.WriteFinding(Slug, f);
        return f;
    }

    private FindingDetailViewModel Open(Finding f)
    {
        var launcher = new AssistedFixLauncher(
            _settings, new CloneLinkService(_hub, _machines), _machines, _busy);
        var vm = new FindingDetailViewModel(
            _hub,
            _governance,
            _machines,
            new VerifyCoordinator(_hub, _machines, _ulids, new FakeCopilotAgent()),
            new EditorLauncher(_settings, _machines),
            _toasts,
            TestFactory.Links(_hub, _paths),
            TestFactory.LinkFlow(_hub, _paths, _toasts),
            new AnchorRepair(_hub),
            references: null,
            fixLauncher: launcher,
            fix: new LiveFixService(
                _hub, () => new FakeCopilotAgent(), _machines, _ulids, _settings,
                new ReferenceCollector(), new FixSnapshotStore(_paths), launcher, _busy,
                new BuildRunner(new NoProcess())));
        vm.Load(Slug, f.Id);
        return vm;
    }

    // ========================================================= el apagado y su razón

    /// <summary>
    /// El punto de partida, que es lo que hace que el resto signifique algo: con el ancla en su
    /// sitio, sin arreglo pendiente y el árbol limpio, el botón está <b>encendido y sin razón</b>.
    /// </summary>
    [Fact]
    public void Sin_nada_que_verificar_y_con_el_arbol_limpio_se_puede_arreglar()
    {
        FindingDetailViewModel vm = Open(Seed());

        vm.IsVerificationPending.Should().BeFalse();
        vm.CanStartFix.Should().BeTrue();
        vm.AssistedFixBlock.Should().BeEmpty("un botón encendido no lleva razón al lado");
    }

    /// <summary>
    /// <b>Con el ancla perdida no se arregla</b>, y la razón dice CUÁL de los dos estados es: un
    /// agente que escribe sobre un ancla perdida edita una línea que ya no es la del hallazgo.
    /// </summary>
    [Fact]
    public void Con_el_ancla_perdida_el_arreglo_se_apaga_y_la_razon_lo_dice()
    {
        FindingDetailViewModel vm = Open(SeedAnclaPerdida());

        vm.IsVerificationPending.Should().BeTrue();
        vm.CanStartFix.Should().BeFalse();
        vm.AssistedFixBlock.Should().StartWith("Verifica primero: el ancla se ha perdido");
        vm.AssistedFixTooltip.Should().Be(vm.AssistedFixBlock,
            "el tooltip de un botón apagado ES su razón, como en los otros cuatro motivos");
    }

    /// <summary>
    /// <b>Y con un arreglo sin verificar tampoco</b>, aunque el ancla esté intacta (D-557):
    /// encargar un segundo arreglo sin saber si el primero funcionó es apilar trabajo a ciegas.
    /// </summary>
    [Theory]
    [InlineData(FindingEvent.FixProposed)]
    [InlineData(FindingEvent.FixCommitted)]
    public void Con_un_arreglo_sin_verificar_el_arreglo_se_apaga_y_la_razon_lo_dice(FindingEvent arreglo)
    {
        Finding f = Seed();
        f.Record(new HistoryEntry(DateTimeOffset.UtcNow, arreglo, "alvaro", "arreglo asistido"));
        _hub.Store.WriteFinding(Slug, f);

        FindingDetailViewModel vm = Open(f);

        vm.SnippetState.Should().Be(SnippetState.Anclado, "el ancla no se ha movido");
        vm.CanStartFix.Should().BeFalse();
        vm.AssistedFixBlock.Should().StartWith("Verifica primero: hay un arreglo sin verificar");
    }

    /// <summary>
    /// <b>Exactamente esos dos, y ninguno más.</b> En cuanto un veredicto cierra el arreglo, el
    /// botón vuelve a encenderse: es la mitad que impide que el apagado se quede puesto para
    /// siempre — la misma lección que el verde de F33.
    /// </summary>
    [Theory]
    [InlineData(FindingEvent.Confirmed)]
    [InlineData(FindingEvent.Inconclusive)]
    [InlineData(FindingEvent.Disputed)]
    public void Un_veredicto_posterior_vuelve_a_encender_el_arreglo(FindingEvent veredicto)
    {
        Finding f = Seed();
        f.Record(new HistoryEntry(DateTimeOffset.UtcNow.AddMinutes(-5), FindingEvent.FixCommitted, "alvaro", "commit"));
        f.Record(new HistoryEntry(DateTimeOffset.UtcNow, veredicto, "alvaro", "verificado"));
        _hub.Store.WriteFinding(Slug, f);

        FindingDetailViewModel vm = Open(f);

        vm.IsVerificationPending.Should().BeFalse();
        vm.CanStartFix.Should().BeTrue();
        vm.AssistedFixBlock.Should().BeEmpty();
    }

    /// <summary>
    /// <b>Cuando coinciden, gana la razón de los commits</b>: los cambios sin commitear son los que
    /// bloquean de verdad —sin árbol limpio, «Descartar todo» no puede distinguir lo del agente de
    /// lo del usuario (F6.9 §1)—, y la otra se enseña al resolverse la primera.
    /// </summary>
    [Fact]
    public void Con_los_dos_a_la_vez_manda_la_razon_de_los_commits_pendientes()
    {
        Finding f = SeedArregloSinVerificar();
        File.AppendAllText(
            Path.Combine(_clone, UnitPath.Replace('/', Path.DirectorySeparatorChar)),
            "// tocado a mano" + Environment.NewLine);

        FindingDetailViewModel vm = Open(f);

        vm.IsVerificationPending.Should().BeTrue("el arreglo sigue sin veredicto");
        vm.CanStartFix.Should().BeFalse();
        vm.AssistedFixBlock.Should().Contain("sin commitear");
        vm.AssistedFixBlock.Should().NotContain("Verifica primero",
            "una sola razón a la vez: la que bloquea de verdad");

        // Y al resolverse la primera, aparece la segunda: no se ha perdido por el camino.
        Commit();
        FindingDetailViewModel limpio = Open(f);
        limpio.CanStartFix.Should().BeFalse();
        limpio.AssistedFixBlock.Should().StartWith("Verifica primero: hay un arreglo sin verificar");
    }

    /// <summary>
    /// <b>Lo que NO se apaga.</b> «Generar prompt de arreglo» no cuesta nada y sirve para mirarlo a
    /// mano; «Verificar ahora» es justamente el camino de salida, así que sigue encendido —y verde
    /// (F33)—. Apagar la salida junto con la entrada dejaría el hallazgo sin ninguna acción.
    /// </summary>
    [Fact]
    public void El_prompt_de_arreglo_y_Verificar_ahora_siguen_encendidos()
    {
        FindingDetailViewModel vm = Open(SeedAnclaPerdida());

        vm.CanStartFix.Should().BeFalse();
        vm.GenerateFixPromptCommand.CanExecute(null).Should().BeTrue(
            "no cuesta nada y sirve para mirarlo a mano");
        vm.CanAudit.Should().BeTrue("«Verificar ahora» es la salida del estado, no puede apagarse");
        vm.IsVerificationPending.Should().BeTrue("y sigue verde mientras lo pida (F33)");
    }

    /// <summary>
    /// Y el botón <b>no desaparece</b>: se apaga. Es la diferencia entre «aquí no se puede» y «aquí
    /// no hay nada», y la razón al lado solo se lee si el botón sigue estando (P-27).
    /// </summary>
    [Fact]
    public void El_boton_se_apaga_pero_no_se_esconde()
    {
        Open(SeedAnclaPerdida()).ShowAssistedFix.Should().BeTrue();
    }

    /// <summary>
    /// El doble de proceso: aquí no se compila nada de verdad. Lo que se mide es el apagado del
    /// botón, no MSBuild.
    /// </summary>
    private sealed class NoProcess : IProcessRunner
    {
        public ProcessOutcome Run(
            string fileName, string arguments, string workingDirectory, TimeSpan timeout, CancellationToken ct)
            => new(0, "Compilación correcta.", false);
    }
}
