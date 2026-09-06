using Atalaya.App.Services;
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
/// F20 §1 — <b>dónde se lee el reparto del coste</b>: en el informe de la sesión y en el pie de la
/// sesión en vivo.
/// <para>
/// El total dice cuánto; el reparto dice de qué, y con tarifas que difieren doce veces entre leer
/// caché y escribirla es lo único que permite decidir dónde apretar. En la aceptación de F19 la
/// escritura era el 60 % de la factura y la lectura el 5 %, con tokens casi iguales: sin esta
/// línea, esa conclusión hay que sacarla a mano.
/// </para>
/// </summary>
public sealed class CostSplitSurfaceTests
{
    /// <summary>Las tarifas de Opus con las que se reprodujo la aceptación de F19.</summary>
    private static ModelRateTable Opus() => new()
    {
        Rates = { new ModelRate("opus", 5.00m, 25.00m, 0.50m, CacheWritePerMillion: 6.25m) },
    };

    private static AppConfig App() => new()
    {
        Slug = "app",
        Name = "App",
        RepoUrl = "https://github.com/org/app.git",
        CurrentCycle = 1,
    };

    private static AuditSession Session(string provider = RealCopilotAgent.Id, string? model = "opus")
    {
        var s = new AuditSession
        {
            Id = new UlidFactory(SystemClock.Instance).NewUlid(),
            AppSlug = "app",
            Mode = AuditMode.Lotes,
            By = "alguien",
            Machine = "maquina",
            StartedUtc = DateTimeOffset.UtcNow,
            Provider = provider,
            Model = model,
        };

        s.Usage.Add(246_541, 18_139, 119_583, 126_904, null, calls: 9);
        return s;
    }

    /// <summary>El informe lo dice con sus cuatro conceptos y sus porcentajes.</summary>
    [Fact]
    public void El_informe_reparte_el_coste_por_concepto()
    {
        string report = ReportBuilder.BuildSessionReport(
            App(), Session(), Array.Empty<Finding>(), 0, 0, "Org", Opus());

        report.Should().Contain("- **Reparto del coste**: escritura de caché 79,3 (61 %)");
        report.Should().Contain("salida 45,3 (35 %)");
        report.Should().Contain("lectura de caché 6,0 (4,6 %)");
        report.Should().Contain("entrada fresca < 0,1", "lo que casi no cuesta se dice, no se redondea a cero");
    }

    /// <summary>
    /// <b>El reparto vive en el ANEXO</b> (F23 §1), no pegado al coste de la cabecera. Sigue
    /// escrito y sigue completo; lo que cambia es para quién: la cabecera contesta «cuánto» a quien
    /// tiene que arreglar su código, y «de qué» es diagnóstico de Atalaya.
    /// </summary>
    [Fact]
    public void El_reparto_vive_en_el_anexo_tecnico()
    {
        string report = ReportBuilder.BuildSessionReport(
            App(), Session(), Array.Empty<Finding>(), 0, 0, "Org", Opus());

        int coste = report.IndexOf("- **Coste**:", StringComparison.Ordinal);
        int anexo = report.IndexOf("## Anexo técnico", StringComparison.Ordinal);
        int reparto = report.IndexOf("- **Reparto del coste**:", StringComparison.Ordinal);

        coste.Should().BeGreaterThan(0);
        anexo.Should().BeGreaterThan(coste, "el anexo va al final, después del cuerpo");
        reparto.Should().BeGreaterThan(anexo, "el reparto es diagnóstico y va dentro del anexo");
        report[coste..anexo].Should().NotContain("Reparto del coste",
            "la cabecera dice cuánto costó, no de qué se compone");
    }

    /// <summary>
    /// Una casa que no factura no tiene reparto que enseñar: no le falta una tarifa, es que no hay
    /// factura. Un desglose de ceros ahí pediría configurar algo que no debe existir.
    /// </summary>
    [Fact]
    public void Sin_factura_el_informe_no_reparte_nada()
    {
        string report = ReportBuilder.BuildSessionReport(
            App(), Session(ClaudeCodeProvider.Id), Array.Empty<Finding>(), 0, 0, "Org", Opus());

        report.Should().NotContain("Reparto del coste");
    }

    /// <summary>
    /// El pie de la sesión en vivo lo lleva también, porque es mientras se gasta cuando sirve. Cede
    /// antes que el coste y su forma mínima es el concepto que manda con su porcentaje.
    /// </summary>
    [Fact]
    public void El_pie_en_vivo_lleva_el_reparto_y_cede_antes_que_el_coste()
    {
        CostResult cost = CreditCalculator.Calculate(Session(), Opus());

        IReadOnlyList<FooterSegment> segments = CostFormat.UsageSegments(
            9, 246_541, 18_139, 119_583, 126_904, cost, RealCopilotAgent.Id);

        // F23 §6 — el pie en vivo se rige por el criterio del cuerpo: el reparto es diagnóstico y
        // pasa al TOOLTIP. Sigue estando entero y a un gesto de distancia; lo que ya no hace es
        // competir por una línea que se lee de reojo mientras la auditoría corre.
        FooterSegment reparto = segments.Single(s => s.Full.StartsWith("escritura", StringComparison.Ordinal));
        reparto.TooltipOnly.Should().BeTrue();

        FooterSegment coste = segments.Single(s => s.Full.Contains("AI credits", StringComparison.Ordinal));
        coste.TooltipOnly.Should().BeFalse("el coste sí se lee en la línea");
    }

    /// <summary>Sin coste no hay trozo de reparto en el pie: no se pinta un cero.</summary>
    [Fact]
    public void Sin_coste_el_pie_no_reparte()
        => CostFormat.UsageSegments(
                9, 1000, 100, 0, 0,
                CostResult.Unavailable(CostUnavailable.NotBilled), ClaudeCodeProvider.Id)
            .Should().NotContain(s => s.Full.Contains("escritura de caché", StringComparison.Ordinal));
}
