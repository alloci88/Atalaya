using Atalaya.App.Services;
using Atalaya.App.ViewModels;
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
/// F12 §F — <b>los silencios: visibles, explicados y de verdad reversibles</b>.
/// <para>
/// Dos defectos del banco de pruebas. Uno: un hallazgo silenciado no decía POR QUÉ lo estaba, así
/// que había que adivinar si alguien lo había mirado o si lo tapaba un patrón. Dos, y más grave:
/// retirar un patrón <b>no revivía nada de lo que había tapado</b> — el veredicto «silenciado»
/// quedaba congelado en el hallazgo y solo otra auditoría, pagada, podía cambiarlo.
/// </para>
/// <para>
/// Un silencio que se pone gratis y solo se quita pagando no es reversible: es una puerta de un solo
/// sentido con aspecto de interruptor. Mismo principio que la deriva: <b>el silencio por patrón es
/// DERIVADO</b> —un hallazgo está silenciado por patrón mientras algún patrón vigente lo cubra— y el
/// silencio individual sí es un hecho del hallazgo, así que se conserva.
/// </para>
/// </summary>
public sealed class DerivedPatternSilenceTests : IDisposable
{
    private const string Exemplar = "bloques catch vacíos que ocultan excepciones";

    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly GovernanceService _governance;
    private readonly ToastCenter _toasts = new();
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public DerivedPatternSilenceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-silence", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _governance = new GovernanceService(_hub, _ulids);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig { Slug = "alpha", Name = "Alpha", RepoUrl = "u", CurrentCycle = 1 });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    // ------------------------------------------------------------------ arnés

    private Finding Seed(string title)
    {
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow.AddDays(-3), AuditMode.Lotes, "old", "alvaro");
        var f = new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = "errores.excepciones.tragada",
            Pillar = Pillar.Errores,
            Severity = Severity.Media,
            Confidence = Confidence.Media,
            Title = title,
            Locations = { new Location("A.cs", 1) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
        _hub.Store.WriteFinding("alpha", f);
        return f;
    }

    private Finding Read(Finding f) => _hub.Store.TryReadFinding("alpha", f.Id.ToString())!;

    /// <summary>Silencia a mano un hallazgo, como lo haría una persona desde su ficha.</summary>
    private void SilenceByHand(Finding f, string notes)
        => _governance.Silence("alpha", f.Id, SilenceReason.FalsoPositivo, notes, null);

    private FindingDetailViewModel Detail()
        => new(
            _hub,
            _governance,
            _machines,
            new VerifyCoordinator(_hub, _machines, _ulids, new FakeCopilotAgent()),
            new EditorLauncher(_settings, _machines),
            _toasts,
            TestFactory.Links(_hub, _paths),
            TestFactory.LinkFlow(_hub, _paths, _toasts),
            new AnchorRepair(_hub));

    private FindingDetailViewModel Open(Finding f)
    {
        FindingDetailViewModel vm = Detail();
        vm.Load("alpha", f.Id);
        return vm;
    }

    // ============================================================ §F.1 · la ficha lo dice

    [Fact]
    public void Un_silencio_a_mano_dice_quien_cuando_y_por_que()
    {
        Finding f = Seed("catch vacío en el importador");
        SilenceByHand(f, "aquí es a propósito: el importador continúa a la unidad siguiente");

        FindingDetailViewModel vm = Open(f);

        vm.HasSilence.Should().BeTrue();
        vm.SilenceSummary.Should().Contain("Silenciado por")
            .And.Contain(DateTimeOffset.UtcNow.ToLocalTime().ToString("dd/MM/yyyy"))
            .And.Contain("Falso positivo")
            .And.Contain("el importador continúa a la unidad siguiente");
        vm.SilenceUndoHint.Should().Contain("Des-silenciar");
        vm.CanUnsilence.Should().BeTrue();
        vm.SilencedByPattern.Should().BeFalse();
    }

    [Fact]
    public void Un_silencio_por_patron_nombra_el_patron_y_lleva_a_gestionarlo()
    {
        Finding f = Seed("catch vacío");
        _governance.SilencePattern("alpha", f.Id, Exemplar, SilenceReason.DeudaAceptada, null, null);

        FindingDetailViewModel vm = Open(f);

        vm.SilencedByPattern.Should().BeTrue();
        vm.SilenceSummary.Should().Contain($"el patrón «{Exemplar}»").And.Contain("puesto por");
        vm.SilenceUndoHint.Should().Contain("retirando el patrón");

        // «Des-silenciar» no se ofrece: el patrón seguiría puesto y la auditoría siguiente volvería
        // a callarlo, así que el botón haría un gesto que se deshace solo.
        vm.CanUnsilence.Should().BeFalse();
    }

    // ============================================================ §F.2 · derivado y reversible

    [Fact]
    public void Retirar_el_patron_devuelve_a_activo_lo_que_solo_el_tapaba()
    {
        Finding origen = Seed("catch vacío en el origen");
        PatternSilence pattern = _governance
            .SilencePattern("alpha", origen.Id, Exemplar, SilenceReason.DeudaAceptada, "no aplica", null)
            .Pattern;

        Read(origen).Status.Should().Be(FindingStatus.Silenciado);

        _governance.UnsilencePattern("alpha", pattern.Id);

        Finding after = Read(origen);
        after.Status.Should().Be(FindingStatus.Activo, "al instante, y sin re-auditar nada");
        _hub.Store.TryReadSilence("alpha", origen.Id).Should().BeNull("el silencio era del patrón, no suyo");

        // Y se dice por qué volvió: nadie está deshaciendo la decisión de nadie.
        HistoryEntry last = after.History[^1];
        last.Event.Should().Be(FindingEvent.Unsilenced);
        last.Detail.Should().Contain("se retiró el patrón").And.Contain(Exemplar);
    }

    [Fact]
    public void El_silencio_individual_sobrevive_a_que_se_retire_un_patron()
    {
        Finding origen = Seed("catch vacío en el origen");
        Finding aMano = Seed("otro catch, mirado a mano");
        SilenceByHand(aMano, "revisado: aquí es correcto");

        PatternSilence pattern = _governance
            .SilencePattern("alpha", origen.Id, Exemplar, SilenceReason.DeudaAceptada, null, null)
            .Pattern;

        _governance.UnsilencePattern("alpha", pattern.Id);

        Read(origen).Status.Should().Be(FindingStatus.Activo);
        Read(aMano).Status.Should().Be(FindingStatus.Silenciado,
            "alguien miró ESE caso y decidió: retirar un patrón no deshace la decisión de nadie");
        _hub.Store.TryReadSilence("alpha", aMano.Id).Should().NotBeNull();
    }

    /// <summary>
    /// Y el gesto de silenciar a mano un hallazgo que el patrón tapaba lo convierte en individual:
    /// a partir de ahí es una decisión sobre ESE caso, y sobrevive.
    /// </summary>
    [Fact]
    public void Silenciar_a_mano_lo_que_tapaba_un_patron_lo_convierte_en_decision_propia()
    {
        Finding f = Seed("catch vacío");
        PatternSilence pattern = _governance
            .SilencePattern("alpha", f.Id, Exemplar, SilenceReason.DeudaAceptada, null, null)
            .Pattern;

        SilenceByHand(f, "lo he mirado: aquí es correcto");
        _governance.UnsilencePattern("alpha", pattern.Id);

        Read(f).Status.Should().Be(FindingStatus.Silenciado);
        Silence? silence = _hub.Store.TryReadSilence("alpha", f.Id);
        silence!.ByPatternId.Should().BeNull();
        silence.Notes.Should().Contain("lo he mirado");
    }

    /// <summary>
    /// Migración: los silencios escritos antes de F12 no traen el id del patrón, solo el texto del
    /// ejemplar. Se enganchan por él, así que también reviven — si no, el defecto seguiría vivo
    /// para todo lo que ya estaba silenciado, que es justo lo que hay en el hub.
    /// </summary>
    [Fact]
    public void Un_silencio_por_patron_anterior_a_F12_tambien_revive()
    {
        Finding f = Seed("catch vacío de antes");
        PatternSilence pattern = _governance
            .SilencePattern("alpha", Seed("origen").Id, Exemplar, SilenceReason.DeudaAceptada, null, null)
            .Pattern;

        // Tal y como lo escribía la versión anterior: con la frase y sin el id.
        _hub.Store.WriteSilence("alpha", new Silence
        {
            FindingUlid = f.Id,
            Reason = SilenceReason.DeudaAceptada,
            By = "alvaro",
            Utc = DateTimeOffset.UtcNow.AddDays(-10),
            ByPatternExemplar = Exemplar,
        });
        f.MarkSilenced(DateTimeOffset.UtcNow.AddDays(-10), "alvaro", "silenciado por el patrón");
        _hub.Store.WriteFinding("alpha", f);

        _governance.UnsilencePattern("alpha", pattern.Id);

        Read(f).Status.Should().Be(FindingStatus.Activo);
        _hub.Store.TryReadSilence("alpha", f.Id).Should().BeNull();
    }

    /// <summary>
    /// Lo derivado sigue a su origen: reescribir el ejemplar cambia lo que la ficha cita. Sin esto,
    /// un hallazgo tapado por el patrón seguiría citando la frase vieja para siempre.
    /// </summary>
    [Fact]
    public void Reescribir_el_ejemplar_actualiza_lo_que_la_ficha_cita()
    {
        Finding f = Seed("catch vacío");
        PatternSilence pattern = _governance
            .SilencePattern("alpha", f.Id, Exemplar, SilenceReason.DeudaAceptada, null, null)
            .Pattern;

        _governance.EditPatternExemplar("alpha", pattern.Id, "catch vacíos en los importadores");

        Open(f).SilenceSummary.Should().Contain("catch vacíos en los importadores")
            .And.NotContain(Exemplar);
    }

    /// <summary>
    /// Y la caducidad también manda: «vigente» lo decide el patrón, así que moverla mueve la del
    /// silencio que puso. Si no, un patrón revivido dejaría detrás silencios ya caducados.
    /// </summary>
    [Fact]
    public void La_caducidad_del_patron_manda_sobre_la_del_silencio_que_puso()
    {
        Finding f = Seed("catch vacío");
        PatternSilence pattern = _governance
            .SilencePattern("alpha", f.Id, Exemplar, SilenceReason.DeudaAceptada, null,
                DateTimeOffset.UtcNow.AddDays(30))
            .Pattern;

        _governance.SetPatternExpiry("alpha", pattern.Id, null);

        _hub.Store.TryReadSilence("alpha", f.Id)!.ExpiresUtc.Should().BeNull("el patrón pasó a permanente");
    }

    /// <summary>
    /// Retirar un patrón que no tapaba nada no inventa trabajo: ningún hallazgo cambia de estado y
    /// ningún silencio ajeno se toca.
    /// </summary>
    [Fact]
    public void Retirar_un_patron_no_toca_lo_que_no_era_suyo()
    {
        Finding origen = Seed("origen");
        Finding otro = Seed("de otro patrón");
        PatternSilence uno = _governance
            .SilencePattern("alpha", origen.Id, Exemplar, SilenceReason.DeudaAceptada, null, null).Pattern;
        _governance.SilencePattern("alpha", otro.Id, "otra cosa distinta", SilenceReason.DeudaAceptada, null, null);

        _governance.UnsilencePattern("alpha", uno.Id);

        Read(origen).Status.Should().Be(FindingStatus.Activo);
        Read(otro).Status.Should().Be(FindingStatus.Silenciado, "su patrón sigue en pie");
    }
}
