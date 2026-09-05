using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Anchoring;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F12 §H — el pulido de interfaz que salió del banco de pruebas. Lo que se puede comprobar sin
/// abrir una ventana: qué dicen los textos y cómo se agrupan los datos. La alineación de los
/// indicadores y el ancho del aviso viven en el XAML y se verifican a ojo, con captura.
/// </summary>
public sealed class SessionPolishTests
{
    // ============================================================ §H.1 · resumen por clase

    private static SummaryItem It(string unit, Severity sev, string text) => new(unit, sev, text);

    /// <summary>
    /// El desglose agrupa por clase y ordena como la vista de Hallazgos: primero la que trae lo más
    /// grave. Salía como una lista corrida, y con veinte hallazgos de seis ficheros no había forma
    /// de ver de dónde venían.
    /// </summary>
    [Fact]
    public void El_desglose_agrupa_por_clase_y_pone_delante_la_mas_grave()
    {
        IReadOnlyList<SummaryGroup> groups = SummaryLine.GroupOf(new[]
        {
            It("src/Baja.cs", Severity.Baja, "nombre poco claro"),
            It("src/Grave.cs", Severity.Alta, "desreferencia nula"),
            It("src/Grave.cs", Severity.Media, "stream sin liberar"),
            It("src/Baja.cs", Severity.Baja, "comentario desfasado"),
        });

        groups.Select(g => g.Unit).Should().Equal(new[] { "src/Grave.cs", "src/Baja.cs" });
        groups[0].FileName.Should().Be("Grave.cs");
        groups[0].Subtitle.Should().Be("src/Grave.cs", "la ruta entera sigue estando, debajo");
        groups[0].CountLabel.Should().Be("2 hallazgos");
        groups[0].Items.Select(i => i.Severity).Should().Equal(new[] { Severity.Alta, Severity.Media });
    }

    [Fact]
    public void Cada_grupo_trae_su_recuento_por_severidad_y_solo_de_las_que_tiene()
    {
        SummaryGroup group = SummaryLine.GroupOf(new[]
        {
            It("A.cs", Severity.Critica, "credenciales en el código"),
            It("A.cs", Severity.Media, "recurso sin liberar"),
            It("A.cs", Severity.Media, "catch vacío"),
        }).Single();

        group.Chips.Select(c => c.Label).Should().Equal(new[] { "1 Crítica", "2 Medias" });
        group.WorstSeverity.Should().Be(Severity.Critica);
    }

    /// <summary>
    /// Agrupar no puede romper la comprobación de «ningún número sin causa»: lo que la línea NOMBRA
    /// se sigue pudiendo contar de un tirón, esté agrupado o no.
    /// </summary>
    [Fact]
    public void Lo_que_una_linea_nombra_se_cuenta_igual_este_agrupado_o_no()
    {
        var agrupada = new SummaryLine
        {
            Label = "Nuevos",
            Count = 3,
            Explanation = "x",
            Groups = SummaryLine.GroupOf(new[]
            {
                It("A.cs", Severity.Alta, "uno"), It("A.cs", Severity.Baja, "dos"),
                It("B.cs", Severity.Media, "tres"),
            }),
        };

        var suelta = new SummaryLine
        {
            Label = "Incidencias por unidad",
            Count = 1,
            Explanation = "x",
            Details = { "A.cs — incompleta" },
        };

        agrupada.Named.Should().HaveCount(3);
        agrupada.HasGroups.Should().BeTrue();
        agrupada.HasFlatDetails.Should().BeFalse("las dos listas no se pintan a la vez");

        suelta.Named.Should().HaveCount(1);
        suelta.HasGroups.Should().BeFalse("una incidencia por unidad ya ES la unidad");
        suelta.HasFlatDetails.Should().BeTrue();
    }

    // ============================================================ §H.2 · lo confirmado por pasada

    /// <summary>
    /// El caso del banco: una pasada de reconciliación que confirma siete hallazgos se titulaba
    /// «seca / 0 hallazgos», que se lee como «aquí no ha pasado nada». Lo que no aportó fueron
    /// NUEVOS; confirmar siete es trabajo hecho y pagado.
    /// </summary>
    [Fact]
    public void El_titular_de_la_pasada_cuenta_nuevos_confirmados_y_disputados()
    {
        string headline = Headline(new UnitPassRecord(
            2, New: 0, Confirmed: 7, Resolved: 0, NonVerifiable: 0, Rejected: 0,
            Dry: true, Summary: null, LocationsAdded: 0, Disputed: 1));

        headline.Should().Contain("0 nuevo(s)")
            .And.Contain("7 confirmado(s)")
            .And.Contain("1 disputado(s)");
        headline.Should().Contain("seca", "sigue siendo seca: lo que no aportó fueron hallazgos nuevos");
        headline.Should().NotStartWith("Pasada 2 — seca",
            "el titular no puede empezar diciendo que no pasó nada cuando pasaron ocho cosas");
    }

    [Fact]
    public void Una_pasada_con_hallazgos_tambien_dice_los_tres_numeros()
    {
        string headline = Headline(new UnitPassRecord(
            1, New: 3, Confirmed: 2, Resolved: 0, NonVerifiable: 0, Rejected: 0,
            Dry: false, Summary: null, LocationsAdded: 1, Disputed: 0));

        headline.Should().Contain("3 nuevo(s)").And.Contain("2 confirmado(s)").And.Contain("0 disputado(s)");
        headline.Should().Contain("1 ubicación(es)");
        headline.Should().NotContain("seca");
    }

    /// <summary>
    /// La misma frase que construye <c>LiveSessionService.OnPassFinished</c>. Se replica aquí porque
    /// el titular se escribe sobre el objeto de progreso, que solo existe con una sesión corriendo;
    /// lo que este test protege es la REGLA —los tres números, siempre y en este orden—, y si la
    /// frase cambia sin actualizar esto, el test que la ejercita de verdad
    /// (<see cref="LiveNarrationTests"/>) lo señala.
    /// </summary>
    private static string Headline(UnitPassRecord record)
        => $"Pasada {record.Index} — {record.New} nuevo(s) · {record.Confirmed} confirmado(s) · "
           + $"{record.Disputed} disputado(s)"
           + (record.LocationsAdded > 0 ? $" · {record.LocationsAdded} ubicación(es)" : "")
           + (record.Dry ? " · seca" : "");

    // ============================================================ §H.5 · el arreglo propio

    private const string Arreglado = """
        class Hex
        {
            public static byte[] Convertir(string hex)
            {
                ArgumentNullException.ThrowIfNull(hex);
                return Convert.FromHexString(hex);
            }
        }
        """;

    /// <summary>
    /// El aviso de re-anclaje decía «el código de la línea X ya no es el que se auditó», que es
    /// cierto y desorientador cuando quien lo cambió fue Atalaya media hora antes. La huella del
    /// arreglo lo sabe, así que el aviso lo dice.
    /// </summary>
    [Fact]
    public void El_aviso_reconoce_el_arreglo_propio()
    {
        using var t = new Clone();
        Finding f = t.SeedFinding();
        File.WriteAllText(t.Path_, Arreglado);
        FixRecord fix = t.RecordFixFor(f, new DateTimeOffset(2026, 8, 30, 10, 0, 0, TimeSpan.Zero));

        SnippetPanel panel = SnippetReader.ForFinding(t.Root, f, new[] { fix });

        panel.Notice.Should().Contain("lo cambió el arreglo de Atalaya")
            .And.Contain("30/08/2026")
            .And.Contain("pendiente de verificar");
        panel.Notice.Should().NotContain("ya no es el que se auditó",
            "la aplicación no se extraña de su propio trabajo");
    }

    /// <summary>
    /// Y es una PRUEBA, no una suposición: si el usuario enmendó lo que el agente dejó, el contenido
    /// ya no casa con la huella y el aviso vuelve a ser el genérico. La duda va hacia avisar de más.
    /// </summary>
    [Fact]
    public void Si_el_codigo_ya_no_es_el_que_dejo_el_arreglo_el_aviso_vuelve_a_ser_el_de_siempre()
    {
        using var t = new Clone();
        Finding f = t.SeedFinding();
        File.WriteAllText(t.Path_, Arreglado);
        FixRecord fix = t.RecordFixFor(f, DateTimeOffset.UtcNow);

        File.WriteAllText(t.Path_, Arreglado.Replace("ThrowIfNull", "ThrowIfNullOrEmpty"));

        SnippetPanel panel = SnippetReader.ForFinding(t.Root, f, new[] { fix });

        panel.Notice.Should().NotContain("arreglo de Atalaya");
    }

    /// <summary>Sin huellas —o con las de otro hallazgo— el aviso es el de siempre.</summary>
    [Fact]
    public void Un_arreglo_de_otro_hallazgo_no_se_atribuye_a_este()
    {
        using var t = new Clone();
        Finding f = t.SeedFinding();
        File.WriteAllText(t.Path_, Arreglado);
        FixRecord ajeno = t.RecordFixFor(f, DateTimeOffset.UtcNow);
        ajeno.FindingId = Guid.NewGuid().ToString();

        SnippetReader.ForFinding(t.Root, f, new[] { ajeno }).Notice
            .Should().NotContain("arreglo de Atalaya");
        SnippetReader.ForFinding(t.Root, f).Notice
            .Should().NotContain("arreglo de Atalaya");
    }

    /// <summary>Un clon de un solo fichero, con el hallazgo anclado a la línea del defecto.</summary>
    private sealed class Clone : IDisposable
    {
        private const string Malo = """
            class Hex
            {
                public static byte[] Convertir(string hex)
                {
                    return HexAMano(hex);
                }
            }
            """;

        private static readonly string LineaMala = "            return HexAMano(hex);";

        private readonly UlidFactory _ulids = new(SystemClock.Instance);

        public Clone()
        {
            Root = Path.Combine(Path.GetTempPath(), "atalaya-h5", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            System.IO.File.WriteAllText(File_(), Malo);
        }

        public string Root { get; }

        public string Path_ => File_();

        private string File_() => Path.Combine(Root, "Hex.cs");

        public Finding SeedFinding()
        {
            var stamp = new DetectionStamp(
                DateTimeOffset.UtcNow.AddDays(-2), AuditMode.Lotes, "abc1234", "alvaro");
            return new Finding
            {
                Id = _ulids.NewUlid(),
                DisplayId = "BUG-0001",
                RuleId = "errores.calculo.negocio",
                Severity = Severity.Alta,
                Confidence = Confidence.Media,
                Title = "Conversión hexadecimal a mano",
                Symbol = "Hex.Convertir",
                Locations = { new Location("Hex.cs", 5, CodeAnchor.ComputeSnippetHash(LineaMala)) },
                Origin = AuditMode.Lotes,
                FirstDetected = stamp,
                LastConfirmed = stamp,
            };
        }

        public FixRecord RecordFixFor(Finding f, DateTimeOffset utc) => new()
        {
            Id = _ulids.NewUlid(),
            AppSlug = "app",
            By = "alvaro",
            Utc = utc,
            FindingId = f.Id.ToString(),
            Files =
            {
                new FixFileStamp(
                    "Hex.cs",
                    Atalaya.Domain.Hashing.HashUtil.NormalizedContentHash(System.IO.File.ReadAllBytes(File_()))),
            },
        };

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
