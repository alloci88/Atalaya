using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.ClaudeCode;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F18 §1 — <b>dónde se lee «adónde van los tokens»</b>: en el informe, en la sesión en vivo y en
/// Métricas.
/// <para>
/// El número por sí solo no es el entregable. Lo es que aparezca en los tres sitios donde alguien
/// se hace la pregunta: mientras la sesión corre (que es cuando se nota que algo se ha disparado),
/// en el informe de esa sesión (que es el historial) y en Métricas (que es donde se decide). Y que
/// en ninguno de los tres se invente cuando no lo sabe.
/// </para>
/// </summary>
public sealed class PromptBudgetSurfaceTests
{
    private static AppConfig App() => new()
    {
        Slug = "app",
        Name = "App",
        RepoUrl = "https://github.com/org/app.git",
        CurrentCycle = 1,
    };

    private static AuditSession Session(bool withComposition, AuditMode mode = AuditMode.Lotes)
    {
        var s = new AuditSession
        {
            Id = new UlidFactory(SystemClock.Instance).NewUlid(),
            AppSlug = "app",
            Mode = mode,
            By = "alguien",
            Machine = "maquina",
            StartedUtc = DateTimeOffset.UtcNow,
            Provider = RealCopilotAgent.Id,
            Model = "gpt-5",
        };

        s.Usage.Add(570_000, 24_000, 450_000, 100_000, null, calls: 20);
        var unit = new UnitUsageBreakdown
        {
            Unit = "src/A.cs",
            Calls = 20,
            InputTokens = 570_000,
            DurationMs = 95_400,
        };

        if (withComposition)
        {
            unit.Passes.Add(new PassUsage(
                1, Calls: 20, InputTokens: 570_000, OutputTokens: 24_000,
                CacheReadTokens: 450_000, CacheWriteTokens: 100_000,
                Composition: new PromptComposition(Reglas: 1413, Rubrica: 710, Catalogo: 714, Unidad: 600),
                DurationMs: 95_400));
        }

        s.UsageBreakdown.Add(unit);
        return s;
    }

    /// <summary>
    /// La línea del enunciado, en el informe: andamiaje por llamada, código auditado con su
    /// porcentaje, y llamadas por unidad. Es lo que hasta F18 había que sacar a mano.
    /// </summary>
    [Fact]
    public void El_informe_dice_de_que_esta_hecha_una_llamada()
    {
        string report = ReportBuilder.BuildSessionReport(
            App(), Session(withComposition: true), Array.Empty<Finding>(), 0, 0, "Org", TestRates.Table());

        report.Should().Contain("- **Composición**: andamiaje ≈ 27900 tokens/llamada");
        report.Should().Contain("código auditado ≈ 600 (2.1 %)");
        report.Should().Contain("20 llamadas por unidad");
    }

    /// <summary>
    /// Y el diagnóstico de la caché: cuánto se escribió, cuánto era inevitable y cuánto es
    /// re-escritura. Es el termómetro del prefijo inestable de §2.
    /// </summary>
    [Fact]
    public void El_informe_diagnostica_la_escritura_de_cache()
    {
        string report = ReportBuilder.BuildSessionReport(
            App(), Session(withComposition: true), Array.Empty<Finding>(), 0, 0, "Org", TestRates.Table());

        report.Should().Contain("- **Caché**: 100000 escritos, 450000 leídos");
        report.Should().Contain("suelo inevitable ≈ 3437");
        report.Should().Contain("re-escrituras ≈ 96563");
    }

    /// <summary>El desglose por pasada, con su duración y su fracción de código.</summary>
    [Fact]
    public void El_informe_desglosa_pasada_a_pasada()
    {
        string report = ReportBuilder.BuildSessionReport(
            App(), Session(withComposition: true), Array.Empty<Finding>(), 0, 0, "Org", TestRates.Table());

        report.Should().Contain("### Por pasada, y de qué se compone el prompt");
        report.Should().Contain("| src/A.cs | 1 | 20 | 570000 | 24000 | 450000 | 100000 | 95,4 s | 2837 | 0 | 600 | 17,5 % |");
    }

    /// <summary>
    /// <b>Una sesión anterior a F18 no gana una línea inventada.</b> Sin desglose por pasada no se
    /// puede separar código de andamiaje, y un «0 %» diría que no viajó código.
    /// </summary>
    [Fact]
    public void Sin_desglose_el_informe_no_escribe_la_linea()
    {
        string report = ReportBuilder.BuildSessionReport(
            App(), Session(withComposition: false), Array.Empty<Finding>(), 0, 0, "Org", TestRates.Table());

        report.Should().NotContain("- **Composición**:");
        report.Should().NotContain("### Por pasada");
        report.Should().Contain("- **Tokens**: entrada 570000", "lo que sí se sabe se sigue diciendo");
    }

    /// <summary>
    /// El pie de la sesión en vivo lo resume: la fracción de código y las llamadas por unidad. Es
    /// donde se nota que algo se ha disparado — en el informe se lee cuando ya está pagado.
    /// </summary>
    [Fact]
    public void El_pie_en_vivo_resume_la_composicion()
    {
        PromptBudget budget = PromptBudget.From(Session(withComposition: true));

        string footer = CostFormat.SessionFooter(
            20, 570_000, 24_000, 450_000, 100_000, new CostResult(1.933m), TestProviders.CopilotLens);
        footer.Should().NotContain("código");

        IReadOnlyList<FooterSegment> segments = CostFormat.UsageSegments(
            20, 570_000, 24_000, 450_000, 100_000, new CostResult(1.933m), TestProviders.CopilotLens, budget);

        // F23 §6 — la composición es diagnóstico y pasa al TOOLTIP, entera. Ya no hace falta una
        // forma abreviada para cuando falta sitio: no compite por el sitio de la línea.
        FooterSegment last = segments[^1];
        last.Full.Should().Be("código 2,1 % · 20 llamadas/unidad");
        last.TooltipOnly.Should().BeTrue("no se pinta en la línea; se lee en el tooltip");
    }

    /// <summary>Sin composición, el pie no gana un trozo con un cero.</summary>
    [Fact]
    public void Sin_composicion_el_pie_no_ensena_nada_de_mas()
    {
        PromptBudget budget = PromptBudget.From(Session(withComposition: false));

        CostFormat.UsageSegments(
                20, 570_000, 24_000, 450_000, 100_000, new CostResult(1.933m), TestProviders.CopilotLens, budget)
            .Should().NotContain(s => s.Full.Contains("código", StringComparison.Ordinal));

        CostFormat.BudgetShort(null).Should().BeEmpty();
    }

    /// <summary>
    /// El pie de la vista en vivo sale del criterio común, con el presupuesto incluido: si la vista
    /// lo compusiera por su cuenta volveríamos a tener dos frases para el mismo número.
    /// </summary>
    [Fact]
    public void La_vista_en_vivo_pasa_el_presupuesto_al_criterio_comun()
    {
        string root = Path.Combine(Path.GetTempPath(), "atalaya-f18", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(Path.Combine(root, "local"));
            var agent = new FakeCopilotAgent();
            var live = new LiveSessionService(
                () => throw new NotSupportedException("no se lanza ninguna sesión en este test"),
                agent,
                new OpenSessionStore(paths))
            {
                Provider = RealCopilotAgent.Id,
                CostResult = new CostResult(193.3m),
                InputTokens = 570_000,
                OutputTokens = 24_000,
                CacheReadTokens = 450_000,
                CacheWriteTokens = 100_000,
                Calls = 20,
                Budget = PromptBudget.From(Session(withComposition: true)),
            };

            var view = new SessionViewModel(live);

            view.Footer.Should().Contain(s =>
                s.Full == "código 2,1 % · 20 llamadas/unidad" && s.TooltipOnly);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
