using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.6 §5 — el panel lateral de V2. Lo que se prueba aquí es que los dos números que
/// desconcertaban digan lo que su etiqueta promete: «Ciclo N» con desde cuándo, y «Sesiones este
/// ciclo» contando lo que una persona llamaría lanzar una auditoría.
/// </summary>
public sealed class CycleSummaryTests
{
    private static readonly UlidFactory Ulids = new(SystemClock.Instance);
    private static readonly DateTimeOffset Day = new(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);

    private static AuditSession Session(AuditMode mode, int cycleN, int hoursAfter)
        => new()
        {
            Id = Ulids.NewUlid(),
            AppSlug = "app",
            Mode = mode,
            By = "alvaro",
            Machine = "m",
            StartedUtc = Day.AddHours(hoursAfter),
            EndedUtc = Day.AddHours(hoursAfter),
            CycleN = cycleN,
        };

    // ---------------------------------------------------------------- desde cuándo

    /// <summary>
    /// El caso del piloto: los ciclos se abrieron a golpe de reset, y el reset deja su sesión con
    /// el <c>CycleN</c> del ciclo que abre. Esa sesión ES el momento en que empezó.
    /// </summary>
    [Fact]
    public void El_reset_que_abrio_el_ciclo_lo_fecha_exactamente()
    {
        var sessions = new[]
        {
            Session(AuditMode.Lotes, 4, -50),
            Session(AuditMode.Reset, 5, 0),
            Session(AuditMode.Lotes, 5, 30),
        };

        CycleStart start = CycleSummary.StartOf(sessions, 5);

        start.Source.Should().Be(CycleStartSource.Opened);
        start.When.Should().Be(Day);
        CycleSummary.Label(5, start).Should().StartWith("Ciclo 5 · iniciado ");
        CycleSummary.Tooltip(start).Should().Contain("Una vuelta completa al inventario")
            .And.Contain("los resets abren ciclo nuevo")
            .And.NotContain("pudo abrirse antes");
    }

    [Fact]
    public void El_cierre_del_anterior_tambien_lo_fecha()
    {
        var sessions = new[] { Session(AuditMode.Cierre, 3, 0) };

        CycleSummary.StartOf(sessions, 3).Source.Should().Be(CycleStartSource.Opened);
    }

    /// <summary>
    /// El ciclo 1 no lo abre nadie: nace con el primer escaneo, que no deja sesión. Lo honesto es
    /// decir «activo desde», no fingir que se sabe cuándo empezó.
    /// </summary>
    [Fact]
    public void Sin_evento_de_apertura_la_fecha_se_infiere_y_se_declara()
    {
        var sessions = new[] { Session(AuditMode.Lotes, 1, 5), Session(AuditMode.Lotes, 1, 80) };

        CycleStart start = CycleSummary.StartOf(sessions, 1);

        start.Source.Should().Be(CycleStartSource.FirstSession);
        start.When.Should().Be(Day.AddHours(5), "la primera, no la última");
        CycleSummary.Label(1, start).Should().StartWith("Ciclo 1 · activo desde ");
        CycleSummary.Tooltip(start).Should().Contain("pudo abrirse antes");
    }

    [Fact]
    public void Un_ciclo_sin_ninguna_sesion_no_finge_una_fecha()
    {
        CycleStart start = CycleSummary.StartOf(Array.Empty<AuditSession>(), 1);

        start.Source.Should().Be(CycleStartSource.Unknown);
        start.When.Should().BeNull();
        CycleSummary.Label(1, start).Should().Be("Ciclo 1", "sin fecha, solo el número");
        CycleSummary.Tooltip(start).Should().Contain("no registra cuándo se abrió");
    }

    /// <summary>Las sesiones de OTROS ciclos no pueden fechar este.</summary>
    [Fact]
    public void Solo_cuentan_las_sesiones_de_ese_ciclo()
    {
        var sessions = new[] { Session(AuditMode.Reset, 2, 0), Session(AuditMode.Reset, 3, 100) };

        CycleSummary.StartOf(sessions, 3).When.Should().Be(Day.AddHours(100));
    }

    // ---------------------------------------------------------------- sesiones este ciclo

    [Fact]
    public void Se_cuentan_los_lanzamientos_del_ciclo_no_todas_las_sesiones()
    {
        var sessions = new[]
        {
            Session(AuditMode.Lotes, 4, -50),      // otro ciclo
            Session(AuditMode.Reset, 5, 0),        // evento de sistema
            Session(AuditMode.Lotes, 5, 10),
            Session(AuditMode.Verify, 5, 20),
            Session(AuditMode.Lotes, 5, 30),
        };

        CycleSummary.LaunchesIn(sessions, 5).Should().Be(3,
            "tres lanzamientos: el reset no lo lanza nadie y el ciclo 4 es otra vuelta");
    }

    /// <summary>Las sesiones históricas en modo retirado siguen siendo lanzamientos (F5.6 §2).</summary>
    [Fact]
    public void Los_modos_retirados_cuentan_como_lanzamientos()
    {
        var sessions = new[]
        {
            Session(AuditMode.Integral, 2, 0),
            Session(AuditMode.Superficial, 2, 10),
            Session(AuditMode.Cierre, 2, -1),
        };

        CycleSummary.LaunchesIn(sessions, 2).Should().Be(2);
    }

    [Fact]
    public void Un_ciclo_recien_abierto_no_tiene_lanzamientos()
        => CycleSummary.LaunchesIn(new[] { Session(AuditMode.Reset, 7, 0) }, 7).Should().Be(0);
}
