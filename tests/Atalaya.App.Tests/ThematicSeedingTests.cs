using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F17 §4 — <b>la siembra con temática</b>, que extiende la tabla de F9.2.
/// <para>
/// Misma temática → la siembra de F9.2, intacta (auditada sin deriva sigue auditada; cambiada,
/// pendiente; arreglada-pendiente conserva su Verificar). Temática distinta → todas las auditables
/// a pendiente: auditada bajo otra lupa no es auditada bajo esta; las grandes siguen grandes; la
/// arreglada-pendiente pasa a pendiente PERO su hallazgo conserva la acción Verificar. Y cambiar
/// de temática a mitad de ciclo es la MISMA mecánica, con el mismo aviso.
/// </para>
/// </summary>
public sealed class ThematicSeedingTests
{
    private const int Large = 1500;

    private static readonly UlidFactory Ulids = new(SystemClock.Instance);

    private static InventoryCycle Closing(AuditTheme theme = AuditTheme.General)
    {
        var inv = new InventoryCycle { CycleN = 1, Theme = theme, PreferredProvider = "copilot", PreferredModel = "gpt-5" };
        inv.Units.Add(new InventoryUnit { Path = "clean.cs", Module = "M", Loc = 10, State = UnitState.Auditada, AuditedInSession = Ulids.NewUlid() });
        inv.Units.Add(new InventoryUnit { Path = "changed.cs", Module = "M", Loc = 10, State = UnitState.Auditada, AuditedInSession = Ulids.NewUlid() });
        inv.Units.Add(new InventoryUnit { Path = "fixed.cs", Module = "M", Loc = 10, State = UnitState.Auditada, AuditedInSession = Ulids.NewUlid() });
        inv.Units.Add(new InventoryUnit { Path = "big.cs", Module = "M", Loc = 5000, State = UnitState.Grande });
        inv.Units.Add(new InventoryUnit { Path = "pending.cs", Module = "M", Loc = 10, State = UnitState.Pendiente });
        return inv;
    }

    private static AppDrift Drift() => new("app", new[]
    {
        new UnitDrift("clean.cs", DriftState.SinCambios),
        new UnitDrift("changed.cs", DriftState.Modificada, Commits: 2),
        new UnitDrift("fixed.cs", DriftState.ArregladaPendienteDeVerificar, OwnFixes: 1),
        new UnitDrift("pending.cs", DriftState.SinCambios),
    }, Array.Empty<OrphanFinding>());

    private static InventoryUnit Unit(InventoryCycle inv, string path) => inv.Units.Single(u => u.Path == path);

    // ---------------------------------------------------------------- misma temática

    /// <summary>La regresión de F9.2: con la misma temática, la siembra es la de siempre, unidad a unidad.</summary>
    [Fact]
    public void Con_la_misma_tematica_la_siembra_de_F92_queda_intacta()
    {
        InventoryCycle closing = Closing(AuditTheme.Rendimiento);

        InventoryCycle next = CycleSeeding.Seed(closing, 2, Large, Drift(), closing.Config);
        InventoryCycle legacy = CycleSeeding.Seed(closing, 2, Large, Drift());

        Unit(next, "clean.cs").State.Should().Be(UnitState.Auditada);
        Unit(next, "clean.cs").AuditedInSession.Should().Be(Unit(closing, "clean.cs").AuditedInSession);
        Unit(next, "changed.cs").State.Should().Be(UnitState.Pendiente);
        Unit(next, "changed.cs").AuditedInSession.Should().BeNull();
        Unit(next, "fixed.cs").State.Should().Be(UnitState.Auditada, "conserva su Verificar");
        Unit(next, "big.cs").State.Should().Be(UnitState.Grande);
        Unit(next, "pending.cs").State.Should().Be(UnitState.Pendiente);

        // Y pasar la configuración heredada es lo mismo que no pasar nada: hereda.
        next.Units.Select(u => (u.Path, u.State, u.AuditedInSession))
            .Should().Equal(legacy.Units.Select(u => (u.Path, u.State, u.AuditedInSession)));
        legacy.Config.Should().Be(closing.Config);
    }

    // ---------------------------------------------------------------- temática distinta

    [Theory]
    [InlineData("clean.cs", UnitState.Pendiente)]
    [InlineData("changed.cs", UnitState.Pendiente)]
    [InlineData("fixed.cs", UnitState.Pendiente)]
    [InlineData("big.cs", UnitState.Grande)]
    [InlineData("pending.cs", UnitState.Pendiente)]
    public void Con_otra_tematica_toda_auditable_nace_pendiente_y_las_grandes_siguen_grandes(string path, UnitState expected)
    {
        InventoryCycle closing = Closing(AuditTheme.General);
        var config = new CycleConfig(AuditTheme.Rendimiento, "copilot", "gpt-5");

        InventoryCycle next = CycleSeeding.Seed(closing, 2, Large, Drift(), config);

        Unit(next, path).State.Should().Be(expected, "auditada bajo otra lupa no es auditada bajo esta");
        Unit(next, path).AuditedInSession.Should().BeNull("la unidad que vuelve a la cola pierde el ancla");
        next.Theme.Should().Be(AuditTheme.Rendimiento);
    }

    [Fact]
    public void El_ciclo_nuevo_nace_con_su_configuracion_y_su_fecha()
    {
        InventoryCycle closing = Closing();
        var when = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

        InventoryCycle next = CycleSeeding.Seed(closing, 2, Large, Drift(), openedUtc: when);

        next.CycleN.Should().Be(2);
        next.Config.Should().Be(closing.Config, "hereda");
        next.OpenedUtc.Should().Be(when);
    }

    // ---------------------------------------------------------------- re-siembra a mitad de ciclo

    [Fact]
    public void Cambiar_de_tematica_a_mitad_de_ciclo_es_la_misma_mecanica()
    {
        InventoryCycle current = Closing(AuditTheme.General);
        current.OpenedUtc = DateTimeOffset.UtcNow.AddDays(-3);

        InventoryCycle reseeded = CycleSeeding.Reseed(current, new CycleConfig(AuditTheme.Seguridad, null, null), Large);

        reseeded.CycleN.Should().Be(1, "es el mismo ciclo, con otra lupa");
        reseeded.OpenedUtc.Should().Be(current.OpenedUtc);
        reseeded.Theme.Should().Be(AuditTheme.Seguridad);
        reseeded.Units.Where(u => u.Path != "big.cs").Should().OnlyContain(u => u.State == UnitState.Pendiente && u.AuditedInSession == null);
        Unit(reseeded, "big.cs").State.Should().Be(UnitState.Grande);
    }

    [Fact]
    public void Cambiar_solo_el_modelo_preferido_no_toca_ninguna_unidad()
    {
        InventoryCycle current = Closing(AuditTheme.Rendimiento);

        InventoryCycle reseeded = CycleSeeding.Reseed(current, new CycleConfig(AuditTheme.Rendimiento, "claude-code", "opus"), Large);

        reseeded.PreferredModel.Should().Be("opus");
        reseeded.Units.Select(u => (u.Path, u.State, u.AuditedInSession))
            .Should().Equal(current.Units.Select(u => (u.Path, u.State, u.AuditedInSession)));
    }

    // ---------------------------------------------------------------- el servicio, sobre el hub

    [Fact]
    public void Aplicar_otra_tematica_re_siembra_y_deja_los_hallazgos_intactos()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "uno"), ("src/B.cs", "dos"));
        r.Audit(c1, "src/A.cs", "src/B.cs");
        Ulid finding = r.SeedFinding("src/A.cs");
        string before = File.ReadAllText(r.Hub.HubPaths.FindingFile(r.Slug, finding.ToString()));
        var service = new CycleConfigService(r.Hub);

        CycleConfigPreview preview = service.Preview(r.Slug)!;
        preview.AuditedUnits.Should().Be(2);
        CycleConfigService.ChangeWarning(preview.AuditedUnits)
            .Should().Be("2 unidades auditadas pasarán a pendientes; los hallazgos existentes no se tocan.");

        CycleConfigResult result = service.Apply(r.Slug, new CycleConfig(AuditTheme.Rendimiento, "fake", "fake-model"));

        result.Applied.Should().BeTrue();
        result.Reseeded.Should().Be(2);
        result.Message.Should().Contain("Rendimiento").And.Contain("2 unidades auditadas han pasado a pendientes");
        InventoryCycle inv = r.Hub.Store.TryReadInventory(r.Slug, 1)!;
        inv.Theme.Should().Be(AuditTheme.Rendimiento);
        inv.Units.Should().OnlyContain(u => u.State == UnitState.Pendiente && u.AuditedInSession == null);
        File.ReadAllText(r.Hub.HubPaths.FindingFile(r.Slug, finding.ToString())).Should().Be(before, "ni un byte del hallazgo cambia");
    }

    /// <summary>F17.1 — el cambio de temática queda en el historial del ciclo, con autor y fecha; lo anterior no se borra.</summary>
    [Fact]
    public void Cambiar_de_tematica_escribe_su_entrada_en_el_historial_del_ciclo()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "uno"));
        r.Audit(c1, "src/A.cs");
        InventoryCycle before = r.Hub.Store.TryReadInventory(r.Slug, 1)!;
        before.Config = new CycleConfig(AuditTheme.Rendimiento, null, null);
        before.OpenedUtc = DateTimeOffset.UtcNow.AddHours(-2);
        r.Hub.Store.WriteInventory(r.Slug, before);

        new CycleConfigService(r.Hub).Apply(r.Slug, new CycleConfig(AuditTheme.Seguridad, null, null));

        InventoryCycle after = r.Hub.Store.TryReadInventory(r.Slug, 1)!;
        after.Theme.Should().Be(AuditTheme.Seguridad);
        after.ThemeHistory.Should().HaveCount(2, "el periodo de Rendimiento se conserva cerrado y el de Seguridad se abre");
        after.ThemeHistory[0].Theme.Should().Be(AuditTheme.Rendimiento);
        after.ThemeHistory[0].FromUtc.Should().Be(before.OpenedUtc);
        after.ThemeHistory[0].ToUtc.Should().NotBeNull();
        after.ThemeHistory[1].Theme.Should().Be(AuditTheme.Seguridad);
        after.ThemeHistory[1].FromUtc.Should().Be(after.ThemeHistory[0].ToUtc, "el corte es el mismo instante");
        after.ThemeHistory[1].ToUtc.Should().BeNull();
        after.ThemeHistory[1].By.Should().NotBeNullOrWhiteSpace("una decisión de gobernanza lleva autor");
        after.OpenedUtc.Should().Be(before.OpenedUtc);
    }

    [Fact]
    public void El_ciclo_nuevo_abre_su_historial_con_la_lupa_heredada()
    {
        InventoryCycle closing = Closing(AuditTheme.Concurrencia);
        var when = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

        InventoryCycle next = CycleSeeding.Seed(closing, 2, Large, Drift(), openedUtc: when);

        next.ThemeHistory.Should().ContainSingle().Which.Should().Be(new ThemePeriod(AuditTheme.Concurrencia, when, null, null));
    }

    [Fact]
    public void Aplicar_la_misma_configuracion_no_cambia_nada()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "uno"));
        r.Audit(c1, "src/A.cs");
        var service = new CycleConfigService(r.Hub);

        CycleConfigResult result = service.Apply(r.Slug, CycleConfig.Default);

        result.Should().Be(CycleConfigResult.Unchanged);
        r.Hub.Store.TryReadInventory(r.Slug, 1)!.Units.Single().State.Should().Be(UnitState.Auditada);
    }

    [Fact]
    public void Cambiar_solo_el_modelo_no_re_siembra_ni_avisa()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "uno"));
        r.Audit(c1, "src/A.cs");
        var service = new CycleConfigService(r.Hub);

        CycleConfigResult result = service.Apply(r.Slug, new CycleConfig(AuditTheme.General, "fake", "fake-model"));

        result.Applied.Should().BeTrue();
        result.Reseeded.Should().Be(0);
        r.Hub.Store.TryReadInventory(r.Slug, 1)!.Units.Single().State.Should().Be(UnitState.Auditada);
        CycleConfigService.ChangeWarning(0).Should().BeEmpty("a cero no se avisa de nada");
    }

    /// <summary>
    /// La arreglada-pendiente pasa a pendiente con la lupa nueva, pero su hallazgo —que es lo que se
    /// verifica— sigue activo y con su huella de arreglo: la acción Verificar no depende de la lupa.
    /// </summary>
    [Fact]
    public void La_arreglada_pendiente_pasa_a_pendiente_y_su_hallazgo_conserva_el_verificar()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "roto"));
        r.Audit(c1, "src/A.cs");
        File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), "arreglado");
        Ulid finding = r.RecordFix("src/A.cs");
        r.Commit("fix: OPT-0001");
        r.Of(r.Drift(), "src/A.cs").State.Should().Be(DriftState.ArregladaPendienteDeVerificar);

        new CycleConfigService(r.Hub).Apply(r.Slug, new CycleConfig(AuditTheme.Fiabilidad, null, null));

        r.Hub.Store.TryReadInventory(r.Slug, 1)!.Units.Single().State.Should().Be(UnitState.Pendiente);
        Finding f = r.Hub.Store.TryReadFinding(r.Slug, finding.ToString())!;
        f.Status.Should().Be(FindingStatus.Activo, "el arreglo sigue necesitando su cierre");
        r.Hub.Store.ListFixes(r.Slug).Should().ContainSingle(x => x.FindingId == finding.ToString(),
            "la huella del arreglo no se toca: Verificar sigue teniendo con qué comparar");
    }

    // ---------------------------------------------------------------- el cierre hereda

    [Fact]
    public void El_cierre_automatico_hereda_la_configuracion_y_fecha_el_ciclo_nuevo()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "uno"));
        r.Audit(c1, "src/A.cs");
        InventoryCycle inv = r.Hub.Store.TryReadInventory(r.Slug, 1)!;
        inv.Config = new CycleConfig(AuditTheme.Seguridad, "claude-code", "opus");
        r.Hub.Store.WriteInventory(r.Slug, inv);
        var machines = new MachineConfigStore(r.Paths.MachinesJson);
        machines.SetClonePath(r.Slug, r.Clone);
        var service = new CycleService(r.Hub, Ulids, new DriftQuery(r.Hub), machines);

        CycleCloseResult result = service.TryCloseCycle(r.Slug, 1);

        result.Closed.Should().BeTrue();
        result.NextConfig.Should().Be(inv.Config, "el cierre no espera a nadie: hereda");
        InventoryCycle next = r.Hub.Store.TryReadInventory(r.Slug, 2)!;
        next.Config.Should().Be(inv.Config);
        next.OpenedUtc.Should().NotBeNull();
        next.Units.Single().State.Should().Be(UnitState.Auditada, "misma temática: la siembra de F9.2");

        AuditSession close = r.Hub.Store.ListSessions(r.Slug).Single(s => s.Mode == AuditMode.Cierre);
        close.Theme.Should().Be(AuditTheme.Seguridad);
        string report = File.ReadAllText(r.Hub.HubPaths.ReportFile(r.Slug, result.ReportSessionId));
        report.Should().Contain("**Temática del ciclo**: Seguridad")
            .And.Contain("**Modelo preferido**: opus (Claude Code)")
            .And.Contain("El ciclo 2 hereda esta configuración");
    }
}
