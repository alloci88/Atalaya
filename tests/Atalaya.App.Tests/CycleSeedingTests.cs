using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F9.2 §1 — la siembra: qué significa EMPEZAR un ciclo. Durante el ciclo la deriva informa y no
/// reabre nada; al cambiar de ciclo se cobra, y aquí se fija exactamente con qué estado nace cada
/// unidad. Se prueba sobre repositorios de verdad, como todo lo que habla con git (D-693).
/// </summary>
public sealed class CycleSeedingTests
{
    private const string FakeSha = "0123456789abcdef0123456789abcdef01234567";

    /// <summary>El cierre completo, con el clon de esta máquina detrás. Devuelve el ciclo nuevo.</summary>
    private static (CycleCloseResult Result, InventoryCycle Next) Close(DriftRepo r)
    {
        var machines = new MachineConfigStore(r.Paths.MachinesJson);
        machines.SetClonePath(r.Slug, r.Clone);

        var service = new CycleService(
            r.Hub, new UlidFactory(Domain.Abstractions.SystemClock.Instance), new DriftQuery(r.Hub), machines);

        CycleCloseResult result = service.TryCloseCycle(r.Slug, 1);
        return (result, r.Hub.Store.TryReadInventory(r.Slug, 2)!);
    }

    private static InventoryUnit Unit(InventoryCycle inv, string path)
        => inv.Units.Single(u => u.Path == path);

    private static string CloseReport(DriftRepo r)
        => r.Hub.Store.ListReports(r.Slug)
            .Select(id => File.ReadAllText(r.Hub.HubPaths.ReportFile(r.Slug, id)))
            .Single(t => t.Contains("Cierre de ciclo 1", StringComparison.Ordinal));

    [Fact]
    public void Auditada_sin_deriva_sigue_auditada_y_conserva_su_ancla()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "uno"), ("src/B.cs", "dos"));
        Ulid session = r.Audit(c1, "src/A.cs", "src/B.cs");
        r.Commit("alguien toca B", ("src/B.cs", "dos y medio"));

        (CycleCloseResult result, InventoryCycle next) = Close(r);

        result.Closed.Should().BeTrue();
        InventoryUnit a = Unit(next, "src/A.cs");
        a.State.Should().Be(UnitState.Auditada,
            "su commit de auditoría vale: re-auditar código que no ha cambiado sería quemar cuota sin causa");
        a.AuditedInSession.Should().Be(session,
            "el ancla viaja con el estado, o la deriva del ciclo nuevo no se podría medir");
    }

    [Fact]
    public void Cambiada_desde_su_auditoria_nace_pendiente_y_pierde_la_marca()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "uno"), ("src/B.cs", "dos"));
        r.Audit(c1, "src/A.cs", "src/B.cs");
        r.Commit("alguien toca B", ("src/B.cs", "dos y medio"));

        (_, InventoryCycle next) = Close(r);

        InventoryUnit b = Unit(next, "src/B.cs");
        b.State.Should().Be(UnitState.Pendiente, "la deuda de mirada se cobra al cambiar de ciclo");
        b.AuditedInSession.Should().BeNull(
            "su nueva auditoría estrenará commit: conservar el viejo haría contar dos veces el mismo rango");
    }

    [Fact]
    public void Sin_historial_disponible_nace_pendiente()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "uno"), ("src/H.cs", "sin historial"));
        r.Audit(c1, "src/A.cs");
        r.Audit(FakeSha, "src/H.cs");   // un commit que no está en este clon
        r.Commit("algo más", ("src/A.cs", "uno bis"));

        (_, InventoryCycle next) = Close(r);

        InventoryUnit h = Unit(next, "src/H.cs");
        h.State.Should().Be(UnitState.Pendiente,
            "no se puede demostrar que no cambió, y sin evidencia no hay estado (N-2)");
        h.AuditedInSession.Should().BeNull();
    }

    [Fact]
    public void Arreglada_pendiente_de_verificar_conserva_estado_y_su_accion_verificar()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "roto"), ("src/B.cs", "otro"));
        Ulid session = r.Audit(c1, "src/A.cs", "src/B.cs");

        File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), "arreglado");
        r.RecordFix("src/A.cs");
        r.Commit("fix: OPT-0001");

        (_, InventoryCycle next) = Close(r);

        InventoryUnit a = Unit(next, "src/A.cs");
        a.State.Should().Be(UnitState.Auditada,
            "el alcance está acotado por la huella: verificar sigue siendo su cierre y es más barato");
        a.AuditedInSession.Should().Be(session);

        // Y la acción sigue siendo la misma en el ciclo nuevo: no se ha degradado a re-auditar.
        UnitDrift drift = r.Of(new DriftQuery(r.Hub).Compute(r.Slug, r.Clone), "src/A.cs");
        drift.State.Should().Be(DriftState.ArregladaPendienteDeVerificar);
        drift.Label.Should().Be("Arreglada — pendiente de verificar");
    }

    [Fact]
    public void Tras_la_siembra_las_cambiadas_quedan_a_cero_y_el_panel_cuadra()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial",
            ("src/A.cs", "uno"), ("src/B.cs", "dos"), ("src/H.cs", "tres"), ("src/F.cs", "roto"));
        r.Audit(c1, "src/A.cs", "src/B.cs", "src/F.cs");
        r.Audit(FakeSha, "src/H.cs");

        File.WriteAllText(Path.Combine(r.Clone, "src", "F.cs"), "arreglado");
        r.RecordFix("src/F.cs");
        r.Commit("fix: OPT-0001", ("src/B.cs", "dos ajeno"));

        (_, InventoryCycle next) = Close(r);

        // Los conteos del inventario: las dos convertidas están en pendientes, y nadie se ha perdido.
        next.Units.Count(u => u.State == UnitState.Pendiente).Should().Be(2, "la cambiada y la sin historial");
        next.Units.Count(u => u.State == UnitState.Auditada).Should().Be(2, "la limpia y la arreglada");

        // Y los de deriva, que es lo que cuenta el panel y la tarjeta del portafolio.
        AppDrift after = new DriftQuery(r.Hub).Compute(r.Slug, r.Clone);
        after.Changed.Should().Be(0, "lo que había cambiado ya se ha cobrado como pendiente");
        after.HistoryUnavailable.Should().Be(0);
        after.FixedPendingVerify.Should().Be(1,
            "esa NO se cobra: conserva su acción, y el indicador dice la verdad hasta que se verifique");
    }

    [Fact]
    public void Sin_clon_con_el_que_comparar_el_ciclo_nuevo_nace_entero_pendiente()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "uno"));
        r.Audit(c1, "src/A.cs");

        // Ni DriftQuery ni clon: es el mismo caso que no poder demostrar que no cambió.
        var service = new CycleService(r.Hub, new UlidFactory(Domain.Abstractions.SystemClock.Instance));
        CycleCloseResult result = service.TryCloseCycle(r.Slug, 1);

        result.Closed.Should().BeTrue();
        result.Aging.Any.Should().BeFalse("no se ha podido mirar: no se inventa una foto");
        r.Hub.Store.TryReadInventory(r.Slug, 2)!.Units
            .Should().OnlyContain(u => u.State == UnitState.Pendiente);
    }

    [Fact]
    public void La_borrada_no_sobrevive_como_auditada()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "uno"), ("src/D.cs", "condenado"));
        r.Audit(c1, "src/A.cs", "src/D.cs");
        r.Commit("borra D", ("src/D.cs", null));

        (_, InventoryCycle next) = Close(r);

        Unit(next, "src/D.cs").State.Should().Be(UnitState.Pendiente,
            "el fichero no está: no hay nada que dar por auditado. La retira el re-escaneo");
    }

    [Fact]
    public void El_cierre_dice_lo_que_queda_envejecido()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial",
            ("src/A.cs", "uno"), ("src/B.cs", "dos"), ("src/F.cs", "roto"));
        r.Audit(c1, "src/A.cs", "src/B.cs", "src/F.cs");

        File.WriteAllText(Path.Combine(r.Clone, "src", "F.cs"), "arreglado");
        r.RecordFix("src/F.cs");
        r.Commit("fix: OPT-0001", ("src/B.cs", "dos ajeno"));

        (CycleCloseResult result, _) = Close(r);

        result.Aging.Should().Be(new CycleAging(Changed: 1, FixedPendingVerify: 1));
        result.Aging.Sentence.Should().Be("Cerrado con 1 cambiada desde su auditoría y 1 sin verificar.");
        CloseReport(r).Should().Contain("Cerrado con 1 cambiada desde su auditoría y 1 sin verificar.");
    }

    [Fact]
    public void A_cero_el_cierre_no_estrena_ruido()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "uno"));
        r.Audit(c1, "src/A.cs");

        (CycleCloseResult result, _) = Close(r);

        result.Aging.Any.Should().BeFalse();
        result.Aging.Sentence.Should().BeNull("una frase que informa de que no hay nada que informar es ruido");
        CloseReport(r).Should().NotContain("Cerrado con");
    }

    [Theory]
    [InlineData(3, 0, "Cerrado con 3 cambiadas desde su auditoría.")]
    [InlineData(0, 2, "Cerrado con 2 sin verificar.")]
    [InlineData(1, 0, "Cerrado con 1 cambiada desde su auditoría.")]
    [InlineData(4, 1, "Cerrado con 4 cambiadas desde su auditoría y 1 sin verificar.")]
    public void La_frase_dice_solo_lo_que_hay(int changed, int pending, string expected)
        => new CycleAging(changed, pending).Sentence.Should().Be(expected);
}
