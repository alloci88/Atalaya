using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F12 §G — <b>el cierre de ciclo deja de ser invisible</b>.
/// <para>
/// En el banco de pruebas, al llegar al 100 % el ciclo se cerró y sembró correctamente —F9.2
/// funciona con datos reales— pero lo hizo <b>en silencio</b>: el Portafolio pasó a «Ciclo 2» y ya.
/// La foto honesta existía, dentro del informe del cierre, y nadie tenía motivo para abrirlo. Un
/// hito que no se anuncia no es un hito: es un cambio de número.
/// </para>
/// </summary>
public sealed class CycleCloseNoticeTests
{
    private static CycleCloseResult Close(DriftRepo r)
    {
        var machines = new MachineConfigStore(r.Paths.MachinesJson);
        machines.SetClonePath(r.Slug, r.Clone);
        var service = new CycleService(
            r.Hub, new UlidFactory(Domain.Abstractions.SystemClock.Instance), TestFactory.Settings(r.Paths),
            new DriftQuery(r.Hub), machines);
        return service.TryCloseCycle(r.Slug, 1);
    }

    /// <summary>Un ciclo entero auditado, con UNA unidad tocada por mano ajena desde su auditoría.</summary>
    private static CycleCloseResult CierraConUnaCambiada(DriftRepo r)
    {
        string c1 = r.Commit("inicial", ("src/A.cs", "uno"), ("src/B.cs", "dos"));
        r.Audit(c1, "src/A.cs", "src/B.cs");
        r.Commit("alguien toca B", ("src/B.cs", "dos y medio"));
        return Close(r);
    }

    [Fact]
    public void El_cierre_trae_con_que_cerro_y_cuanto_sembro()
    {
        using var r = new DriftRepo();

        CycleCloseResult close = CierraConUnaCambiada(r);

        close.Closed.Should().BeTrue();
        close.Slug.Should().Be(r.Slug);
        close.ClosedCycle.Should().Be(1);
        close.NextCycle.Should().Be(2);
        close.UnitsAudited.Should().Be(2);
        close.UnitsTotal.Should().Be(2);
        close.SeededPending.Should().Be(1, "B cambió desde su auditoría: el ciclo nuevo la hereda pendiente");
        close.ReportSessionId.Should().NotBeNullOrWhiteSpace("el aviso lleva a un informe que existe");
    }

    /// <summary>
    /// La frase la redacta el RESULTADO, no la interfaz (misma regla que D-746 fijó para el aviso de
    /// versión): así el aviso no puede decir unos números distintos de los que cerraron el ciclo.
    /// </summary>
    [Fact]
    public void El_aviso_dice_cobertura_siembra_y_ciclo_nuevo()
    {
        using var r = new DriftRepo();

        string headline = CierraConUnaCambiada(r).Headline;

        headline.Should().Be(
            "Ciclo 1 cerrado · 2/2 auditadas · 1 unidad sembrada como pendiente · Ciclo 2 abierto");
    }

    [Fact]
    public void Un_cierre_limpio_lo_dice_sin_inventarse_un_pendiente()
    {
        using var r = new DriftRepo();
        string c1 = r.Commit("inicial", ("src/A.cs", "uno"));
        r.Audit(c1, "src/A.cs");

        string headline = Close(r).Headline;

        headline.Should().Contain("1/1 auditadas").And.Contain("nada sembrado como pendiente");
    }

    [Fact]
    public void Sin_cierre_no_hay_frase()
    {
        CycleCloseResult.NotClosed.Headline.Should().BeEmpty(
            "un ciclo que no se ha cerrado no estrena un aviso que diga que sí");
    }

    // ---------------------------------------------------------------- la carcasa

    /// <summary>
    /// El aviso vive en la carcasa, como el de versión: discreto, NO efímero y descartable. Un toast
    /// caduca a los 8 s y se pierde si mirabas otra pantalla, que es exactamente cómo un cierre de
    /// ciclo pasa desapercibido.
    /// </summary>
    [Fact]
    public void La_carcasa_ensena_el_aviso_con_su_enlace_al_informe()
    {
        using var r = new DriftRepo();
        CycleCloseResult close = CierraConUnaCambiada(r);

        MainViewModel vm = TestFactory.Shell(r.Paths, r.Hub);
        vm.CycleClosed.Should().BeFalse("sin cierre no hay banner");

        vm.AnnounceCycleClose(close);

        vm.CycleClosed.Should().BeTrue();
        vm.CycleClosedLabel.Should().Be(close.Headline);
        vm.CanOpenCycleReport.Should().BeTrue();

        vm.DismissCycleCloseCommand.Execute(null);
        vm.CycleClosed.Should().BeFalse("se ha leído y no vuelve a estorbar");
    }

    [Fact]
    public void Una_sesion_que_no_cierra_ciclo_no_ensena_nada()
    {
        using var r = new DriftRepo();
        MainViewModel vm = TestFactory.Shell(r.Paths, r.Hub);

        vm.AnnounceCycleClose(CycleCloseResult.NotClosed);

        vm.CycleClosed.Should().BeFalse();
        vm.CycleClosedLabel.Should().BeEmpty();
    }
}
