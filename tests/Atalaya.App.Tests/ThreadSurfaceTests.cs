using Atalaya.App.Services;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F25 §5 — <b>lo que el usuario ve del hilo</b>, que es poco y a propósito.
/// <para>
/// El pie sigue diciendo «pasada n»: quien mira una sesión correr no tiene por qué saber que una
/// pasada viaja como un turno de una conversación. Lo que sí cambia es el desglose de caché del
/// tooltip, que pasa a decirse <b>por turno</b> —es la unidad en la que ahora se paga—, y el anexo
/// técnico, que gana una línea por unidad con los turnos y los reinicios.
/// </para>
/// <para>
/// Y el cuerpo del informe no gana nada: el criterio de D-886 no ha cambiado, y esto es
/// instrumentación.
/// </para>
/// </summary>
public sealed class ThreadSurfaceTests
{
    private static AppConfig App() => new()
    {
        Slug = "app",
        Name = "App",
        RepoUrl = "https://github.com/org/app.git",
        CurrentCycle = 1,
    };

    private static AuditSession Session(int turns, int restarts, params string[] reasons)
    {
        var s = new AuditSession
        {
            Id = new UlidFactory(SystemClock.Instance).NewUlid(),
            AppSlug = "app",
            Mode = AuditMode.Lotes,
            By = "alguien",
            Machine = "maquina",
            StartedUtc = DateTimeOffset.UtcNow,
            Provider = RealCopilotAgent.Id,
            Model = "gpt-5",
        };

        s.Usage.Add(1_000, 500, 40_000, 6_000, null, calls: 4);
        var unit = new UnitUsageBreakdown
        {
            Unit = "src/A.cs",
            Calls = 4,
            InputTokens = 1_000,
            ThreadTurns = turns,
            ThreadRestarts = restarts,
        };
        unit.ThreadRestartReasons.AddRange(reasons);
        unit.Passes.Add(new PassUsage(1, Calls: 4, InputTokens: 1_000));
        s.UsageBreakdown.Add(unit);
        return s;
    }

    private static string Report(AuditSession session)
        => ReportBuilder.BuildSessionReport(
            App(), session, Array.Empty<Finding>(), 0, 0, "Org", TestRates.Table());

    /// <summary>Cuatro turnos y ninguna avería: la línea lo dice y no dice nada más.</summary>
    [Fact]
    public void El_anexo_dice_cuantos_turnos_tuvo_cada_unidad()
    {
        string report = Report(Session(turns: 4, restarts: 0));

        report.Should().Contain("### El hilo, unidad a unidad");
        report.Should().Contain("- **src/A.cs** — hilo: 4 turnos");
        report.Should().NotContain("reinicio", "sin reinicios no hay nada que explicar");
    }

    /// <summary>
    /// <b>Y cuando hubo que reabrir, con el motivo.</b> «2 reinicios» a secas obliga a adivinar si
    /// el proveedor falla o si la unidad no cabe en un hilo, y son problemas distintos.
    /// </summary>
    [Fact]
    public void Un_hilo_reiniciado_dice_cuantas_veces_y_por_que()
    {
        string report = Report(Session(
            turns: 4, restarts: 2, "la pasada se cortó en unit_done", "techo de contexto (120000 tokens)"));

        report.Should().Contain(
            "- **src/A.cs** — hilo: 4 turnos · 2 reinicios "
            + "(la pasada se cortó en unit_done, techo de contexto (120000 tokens))");
    }

    /// <summary>
    /// Un motivo repetido se dice una vez. Cuatro reinicios por lo mismo son un dato; cuatro veces
    /// la misma frase es ruido.
    /// </summary>
    [Fact]
    public void Un_motivo_repetido_se_nombra_una_sola_vez()
    {
        string report = Report(Session(
            turns: 3, restarts: 3,
            "la pasada se cortó en unit_done", "la pasada se cortó en unit_done",
            "la pasada se cortó en unit_done"));

        report.Should().Contain("hilo: 3 turnos · 3 reinicios (la pasada se cortó en unit_done)");
    }

    /// <summary>
    /// <b>Y una sesión sin hilo no lo finge.</b> Un proveedor que no sabe hilar barre con una
    /// petición por pasada, que cuesta el triple; el anexo no puede callarlo, pero tampoco puede
    /// escribir un bloque de hilo donde no hubo ninguno.
    /// </summary>
    [Fact]
    public void Sin_hilo_el_anexo_no_escribe_el_bloque()
    {
        Report(Session(turns: 0, restarts: 0)).Should().NotContain("### El hilo");
    }

    /// <summary>Y nada de esto se cuela en el cuerpo (D-886).</summary>
    [Fact]
    public void El_cuerpo_del_informe_no_habla_del_hilo()
    {
        string report = Report(Session(turns: 4, restarts: 1, "techo de contexto (120000 tokens)"));
        string cuerpo = report[..report.IndexOf("## Anexo técnico", StringComparison.Ordinal)];

        cuerpo.Should().NotContain("hilo:");
        cuerpo.Should().NotContain("turnos");
    }

    /// <summary>
    /// <b>El desglose de caché del pie, por turno.</b> El total sigue estando —es dato primario—,
    /// pero lo que dice si el hilo está haciendo su trabajo es lo que se escribe en cada turno.
    /// </summary>
    [Fact]
    public void El_tooltip_del_pie_desglosa_la_cache_por_turno()
    {
        CostFormat.Tokens(28_050, 15_670, 235_327, 51_077, turns: 17)
            .Should().Be(
                "28.050 entrada · 15.670 salida · caché 235.327 leída / 51.077 escrita · 3.004 escrita/turno");
    }

    /// <summary>Sin turnos no se divide nada: la línea es la de siempre.</summary>
    [Fact]
    public void Sin_turnos_el_tooltip_no_divide_nada()
    {
        CostFormat.Tokens(28_050, 15_670, 235_327, 51_077)
            .Should().Be("28.050 entrada · 15.670 salida · caché 235.327 leída / 51.077 escrita");
    }

    /// <summary>
    /// Y sigue siendo de tooltip: el pie pintado lleva progreso, tiempo, llamadas y coste, y nada
    /// más (D-886). Un desglose por turno en la línea sería exactamente lo que F23 quitó de ahí.
    /// </summary>
    [Fact]
    public void El_desglose_por_turno_no_se_pinta_en_la_linea()
    {
        IReadOnlyList<FooterSegment> segments = CostFormat.UsageSegments(
            20, 28_050, 15_670, 235_327, 51_077,
            CostResult.Unavailable(CostUnavailable.RateMissing), null, null, turns: 17);

        segments.Single(x => x.Full.Contains("escrita/turno")).TooltipOnly.Should().BeTrue();
    }
}
