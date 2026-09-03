using System.Text.RegularExpressions;
using Atalaya.App.Services;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F23 §1 — NINGÚN NOMBRE DE FASE EN LO QUE LEE UN USUARIO.
/// <para>
/// El informe llevaba «(F18)» en un título y «instrumentación Hito 1a» en otro. Para quien mantiene
/// Atalaya son referencias útiles —y por eso siguen en el código y en <c>DECISIONS.md</c>, que es
/// donde viven—; para quien abre el informe de su aplicación son ruido de la cocina de otro: no
/// puede buscarlas en ningún sitio ni le dicen nada.
/// </para>
/// <para>
/// Este test recorre lo que un usuario ve —el informe entero, anexo incluido, el pie en vivo y los
/// textos de las vistas— y se pone en rojo si vuelve a colarse una.
/// </para>
/// </summary>
public sealed class NoPhaseNamesTests
{
    /// <summary>
    /// Cómo se nombran las fases en esta casa: <c>F18</c>, <c>F5.12</c>, <c>Hito 1a</c>,
    /// <c>D-865</c>, <c>BUGFIX-CUOTA</c>. Se busca con límites de palabra para no cazar un
    /// <c>F1</c> que fuera parte de otra cosa.
    /// </summary>
    private static readonly Regex PhaseName = new(
        @"\b(F\d+(\.\d+)?[a-z]?|Hito\s+\d+[a-z]?|D-\d{3}|BUGFIX-[A-ZÁÉÍÓÚÑ]+)\b",
        RegexOptions.Compiled);

    [Fact]
    public void El_informe_de_sesion_entero_no_nombra_ninguna_fase()
    {
        string report = ReportBuilder.BuildSessionReport(
            App(), Session(), new[] { Finding() }, pendingUnits: 3, largeUnits: 1, "Org", TestRates.Table());

        report.Should().Contain("## Anexo técnico", "el anexo tiene que estar para que el test valga");
        Offenders(report).Should().BeEmpty();
    }

    [Fact]
    public void El_pie_en_vivo_tampoco()
    {
        string footer = string.Join(" · ", CreditText.UsageSegments(
                9, 246_541, 18_139, 119_583, 126_904,
                new CostResult(185.3m), RealCopilotAgent.Id)
            .Select(s => s.Full));

        Offenders(footer).Should().BeEmpty();
    }

    /// <summary>
    /// Y las vistas. Se leen los ficheros de verdad: un literal en XAML no pasa por ningún método
    /// que se pueda ejercitar, así que la única forma de vigilarlo es mirar el fichero.
    /// </summary>
    [Theory]
    [InlineData("Views/MetricsView.xaml")]
    [InlineData("Views/SessionView.xaml")]
    [InlineData("Views/ReportsView.xaml")]
    [InlineData("Views/FindingDetailView.xaml")]
    public void Las_vistas_que_lee_el_usuario_tampoco(string relative)
    {
        string path = Path.Combine(SourceRoot(), relative);
        if (!File.Exists(path))
        {
            return;
        }

        // Solo los atributos que PINTAN texto. Barrer todas las comillas del fichero caza colores
        // («#F1F1F1»), nombres de fila y trozos de binding, que no los lee nadie.
        var visible = new Regex(
            @"(?:Text|Content|ToolTip|Header|Title)\s*=\s*""([^""]{4,})""", RegexOptions.Compiled);

        foreach (Match m in visible.Matches(File.ReadAllText(path)))
        {
            string text = m.Groups[1].Value;
            if (text.StartsWith("{", StringComparison.Ordinal))
            {
                continue;               // Un binding no es texto: lo que se pinta viene de otro sitio.
            }

            Offenders(text).Should().BeEmpty($"en {relative}: «{text}»");
        }
    }

    /// <summary>El view-model de Métricas: lo que pinta son cadenas suyas, no del XAML.</summary>
    [Fact]
    public void Los_textos_de_Metricas_tampoco()
    {
        string path = Path.Combine(SourceRoot(), "ViewModels/MetricsViewModel.cs");
        foreach (string literal in Literals(File.ReadAllText(path)))
        {
            Offenders(literal).Should().BeEmpty($"en Métricas: «{literal}»");
        }
    }

    // ---------------------------------------------------------------- ayudas

    /// <summary>
    /// Los literales de un fuente, <b>sin sus comentarios</b>: en el código las referencias a fases
    /// son documentación y tienen que quedarse. Lo que no puede llevarlas es lo que se pinta.
    /// </summary>
    private static IEnumerable<string> Literals(string source)
    {
        foreach (string line in source.Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("///", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match m in Regex.Matches(line, "\"([^\"]{4,})\""))
            {
                yield return m.Groups[1].Value;
            }
        }
    }

    private static IReadOnlyList<string> Offenders(string text)
        => PhaseName.Matches(text).Select(m => m.Value).Distinct().ToList();

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "src", "Atalaya.App");
    }

    private static AppConfig App() => new() { Slug = "app", Name = "App", RepoUrl = "r" };

    private static AuditSession Session()
    {
        var s = new AuditSession
        {
            Id = Ulid.Empty,
            AppSlug = "app",
            By = "quien",
            Machine = "maquina",
            Mode = AuditMode.Lotes,
            StartedUtc = new DateTimeOffset(2026, 9, 3, 14, 14, 0, TimeSpan.Zero),
            EndedUtc = new DateTimeOffset(2026, 9, 3, 14, 20, 0, TimeSpan.Zero),
            Commit = "abc1234",
            Model = "claude-opus-4.7",
            Provider = RealCopilotAgent.Id,
            CycleN = 1,
            MaxPassesPerUnit = 6,
        };

        s.Usage.Add(246_541, 18_139, 119_583, 126_904, null, 9);
        s.Units.Add(new UnitVerdictRecord(
            "src/Servicios/Cosa.cs", "Servicios", "auditada", "Revisados: Metodo.",
            Passes: new List<UnitPassRecord>
            {
                new(1, 2, 0, 0, 0, 0, false, "Revisados: Metodo. Dos hallazgos."),
                new(2, 0, 2, 0, 0, 0, true, "Revisados: Metodo. Nada nuevo."),
            }));

        var breakdown = new UnitUsageBreakdown { Unit = "src/Servicios/Cosa.cs", Calls = 9 };
        breakdown.Passes.Add(new PassUsage(1, 5, 100, 20, 30, 40, new PromptComposition(1, 1, 1, 1, 1, 1, 1, 1), 900));
        s.UsageBreakdown.Add(breakdown);
        return s;
    }

    private static Finding Finding() => new()
    {
        Id = Ulid.Empty,
        RuleId = "errores.null.desreferencia",
        Title = "Desreferencia sin comprobar",
        Description = "Descripción.",
        Recommendation = "Comprobar.",
        Severity = Severity.Alta,
        Symbol = "Metodo",
        Locations = new List<Location> { new("src/Servicios/Cosa.cs", 11, null) },
        FirstDetected = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "abc1234", "quien"),
        LastConfirmed = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "abc1234", "quien"),
    };
}
