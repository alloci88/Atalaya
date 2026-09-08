using System.Windows.Documents;
using Atalaya.App;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Domain;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F26 Parte C — <b>el informe abierto se lee</b>: el anexo técnico va plegado y la gravedad se ve
/// sin leer.
/// <para>
/// Las dos reglas de aquí se rompen EN SILENCIO, que es la razón de que tengan test. Ninguna falla
/// ni deja rastro: el anexo simplemente vuelve a salir desplegado —media pantalla de tablas de
/// tokens delante del resumen— y la gravedad vuelve a ser texto entre corchetes. Nadie abre una
/// incidencia por eso; se deja de mirar el informe, que es exactamente lo que pasaba.
/// </para>
/// <para>
/// <b>Lo que NO se comprueba aquí, a propósito</b> (N-5): cómo se ve la pastilla, que el cuerpo
/// mida 15 y que un encabezado normal no se pinte de nada. Los tres salen en la primera captura de
/// un informe abierto — un cuerpo encogido, una pastilla donde no toca o un anexo desplegado se ven
/// sin buscarlos. Lo que se comprueba es qué se reconoce como gravedad y qué no, y por dónde se
/// corta el anexo: eso no se ve, se lee bien y significa otra cosa.
/// </para>
/// </summary>
public sealed class ReportReadingTests
{
    // ================================================================ El anexo técnico

    /// <summary>
    /// <b>El corte existe y el generador lo sigue escribiendo.</b> Es la mitad importante: el
    /// visor parte por un encabezado literal, así que renombrarlo en <c>ReportBuilder</c> dejaría
    /// el anexo desplegado para siempre sin que nada fallara. Este test ata las dos puntas.
    /// </summary>
    [Fact]
    public void El_encabezado_por_el_que_se_parte_es_el_que_el_generador_escribe()
    {
        ReportsViewModel.AnnexHeading.Should().Be("## Anexo técnico");

        // Y el generador lo escribe. No se monta una sesión entera: se lee el propio código, que
        // es donde vive la cadena, para que el test siga valiendo el día que el informe cambie de
        // contenido pero no de estructura.
        string builder = File.ReadAllText(Path.Combine(Root(), "src", "Atalaya.App", "Services", "ReportBuilder.cs"));
        builder.Should().Contain(
            "\"## Anexo técnico",
            "el visor pliega el anexo cortando por este encabezado; si el generador lo renombra, "
            + "el anexo vuelve a salir desplegado y nada falla");
    }

    /// <summary>
    /// El cuerpo se queda con lo que hay que leer y el anexo con lo demás. La raya horizontal que
    /// el generador pone delante del anexo se va con él: si no, el cuerpo acabaría en una línea de
    /// separación que ya no separa de nada.
    /// </summary>
    [Fact]
    public void El_anexo_se_separa_del_cuerpo_y_la_raya_se_va_con_el()
    {
        const string informe = """
            # Informe de sesión — App

            ## Resumen

            - Nuevos: 3

            ---

            ## Anexo técnico — diagnóstico

            - Tokens: entrada 1
            """;

        (string body, string? annex) = ReportsViewModel.SplitAnnex(informe);

        body.Should().Contain("## Resumen").And.NotContain("Anexo técnico");
        body.TrimEnd().Should().NotEndWith("---", "la raya de separación viaja con el anexo");
        annex.Should().NotBeNull();
        annex!.Should().StartWith("## Anexo técnico").And.Contain("Tokens");
    }

    /// <summary>Un informe sin anexo —uno importado de v4, un arreglo— no estrena panel vacío.</summary>
    [Fact]
    public void Un_informe_sin_anexo_no_trae_panel_que_abrir()
    {
        (string body, string? annex) = ReportsViewModel.SplitAnnex("# Informe\n\n## Resumen\n\n- Nada\n");

        annex.Should().BeNull();
        body.Should().Contain("## Resumen");
    }

    // ================================================================ Las gravedades

    /// <summary>
    /// <b>Lo que se reconoce como gravedad son DOS formas y ninguna más</b>: la línea de recuento
    /// del resumen de F23 —«3 Críticas · 27 Altas»— y el corchete que abre el encabezado de un
    /// hallazgo. El informe no cambia (F23); cambia cómo se pinta lo que ya dice.
    /// </summary>
    [Theory]
    [InlineData("Gravedad: 3 Críticas · 27 Altas", 2)]
    [InlineData("Gravedad: 1 Media", 1)]
    [InlineData("Nuevos: 15 · Confirmados: 0", 0)]
    public void Solo_se_pintan_las_gravedades_CONTADAS_del_resumen(string linea, int esperadas)
    {
        MarkdownFlowDocument.SeveritySpans(linea)
            .Count(s => s.Severity is not null)
            .Should().Be(esperadas);
    }

    /// <summary>
    /// Y una palabra suelta NO es una gravedad. «La cobertura es baja» no lleva pastilla: pintar
    /// de rojo una palabra de una frase es peor que no pintar nada — el color dejaría de
    /// significar «esto es un hallazgo grave» y pasaría a significar «esta palabra existe».
    /// </summary>
    [Theory]
    [InlineData("La cobertura es baja en este ciclo")]
    [InlineData("Confianza alta")]
    [InlineData("Prioridad media del catálogo")]
    public void Una_palabra_suelta_no_es_una_gravedad(string frase)
    {
        MarkdownFlowDocument.SeveritySpans(frase)
            .Should().OnlyContain(s => s.Severity == null);
    }

    /// <summary>
    /// El corchete del encabezado de un hallazgo se resuelve mirando el ENCABEZADO ENTERO y no el
    /// texto suelto, y ésa es la parte que se rompe en silencio: Markdig ve <c>[Alta]</c> como una
    /// referencia de enlace sin destino y la parte en tres trozos, así que ningún literal contiene
    /// nunca la marca completa. Con el reconocimiento hecho sobre el texto plano del encabezado sí
    /// aparece.
    /// </summary>
    [Fact]
    public void El_corchete_del_encabezado_de_un_hallazgo_se_reconoce_entero()
    {
        var doc = Markdig.Markdown.Parse("#### [Alta] Posible NullReferenceException — línea 427");
        var heading = doc.OfType<Markdig.Syntax.HeadingBlock>().Single();

        MarkdownFlowDocument.HeadingSeverity(heading).Should().NotBeNull();
        MarkdownFlowDocument.HeadingSeverity(heading)!.Value.Severity.Should().Be("Alta");
        MarkdownFlowDocument.HeadingSeverity(heading)!.Value.Title
            .Should().StartWith("Posible NullReferenceException");
    }

    /// <summary>
    /// <b>LOS CUATRO NIVELES PINTAN PASTILLA</b> (R10 §4). En el <c>dist</c> «Media» y «Baja»
    /// salían como pastilla de color y «[Critica]» como texto plano en negrita, en el mismo
    /// informe y a tres líneas de distancia.
    /// <para>
    /// Eran dos defectos encadenados y este test ata los dos. <b>Uno</b>: el escritor del informe
    /// ponía el enum en crudo —«Critica», sin tilde— y era el único sitio que se había quedado
    /// fuera del rotulado único de F27 raíz 1. <b>Dos</b>: el resolutor solo entendía la grafía con
    /// tilde, así que ni siquiera reconocía lo que el propio escritor había escrito. Las otras tres
    /// gravedades no llevan tilde y por eso funcionaban — lo que hacía que el fallo pareciera
    /// cosmético de una y no de la regla entera.
    /// </para>
    /// <para>
    /// Se cuenta sobre <c>HeadingSeverity</c> porque es exactamente lo que decide la pastilla: el
    /// renderizador pinta una por encabezado en el que esto devuelve algo, y ninguna donde
    /// devuelve <c>null</c>. Contarlas así mide la regla sin levantar una <c>Application</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void Un_informe_con_los_cuatro_niveles_pinta_cuatro_pastillas()
    {
        // Escritas como las escribe el informe, por el mismo sitio del que salen allí.
        string informe = string.Join("\n\n", Enum.GetValues<Severity>()
            .Select(s => $"#### [{SeverityNames.Display(s)}] Un hallazgo de ejemplo — línea 12"));

        var doc = Markdig.Markdown.Parse(informe);
        var headings = doc.OfType<Markdig.Syntax.HeadingBlock>().ToList();

        headings.Should().HaveCount(4, "hay cuatro niveles de gravedad y cuatro encabezados");
        headings.Select(h => MarkdownFlowDocument.HeadingSeverity(h)?.Severity)
            .Should().Equal("Crítica", "Alta", "Media", "Baja");
    }

    /// <summary>
    /// Y un informe YA ESCRITO no se reescribe (F29): los que salieron antes de R10 dicen
    /// «[Critica]» sin tilde, y son el registro de lo que pasó. El resolutor acepta las dos
    /// grafías y devuelve siempre la buena, para que la pastilla de un informe viejo no se pinte
    /// sin tilde al lado de las de uno nuevo.
    /// </summary>
    [Fact]
    public void La_grafia_vieja_sin_tilde_se_sigue_reconociendo_y_se_rotula_bien()
    {
        var doc = Markdig.Markdown.Parse("#### [Critica] Credenciales embebidas en el código");
        var heading = doc.OfType<Markdig.Syntax.HeadingBlock>().Single();

        MarkdownFlowDocument.HeadingSeverity(heading)!.Value.Severity.Should().Be("Crítica");
    }

    // ================================================================ Las citas (F36 §1.4)

    /// <summary>
    /// <b>Una cita se pinta según lo que DICE</b> (F36 §1.4).
    /// <para>
    /// Las citas de nuestros informes iban las tres con la misma raya azul: el aviso de que un
    /// arreglo sigue SIN COMMITEAR se leía igual que la nota que explica qué hace verificar. Es el
    /// mismo defecto que F27 arregló con la gravedad —una cosa importante escrita en el mismo tono
    /// que el resto— y se arregla igual: reconociéndola por texto, sin tocar el texto.
    /// </para>
    /// <para>
    /// Este test es de REGLA y no de forma: no comprueba de qué ámbar es el ámbar, sino qué frase
    /// se lee como aviso, cuál como cerrado y cuál como explicación. Se rompe en silencio —la
    /// cita simplemente vuelve a salir neutra— y por eso tiene test.
    /// </para>
    /// </summary>
    [Theory]
    // Arreglo asistido: lo que queda abierto, en ámbar.
    [InlineData(
        "**Estos cambios NO están commiteados.** El arreglo asistido escribe en el clon local",
        MarkdownFlowDocument.CalloutTone.Warning)]
    [InlineData(
        "El proyecto afectado (`X.csproj`) **no tiene proyecto de tests** que lo cubra.",
        MarkdownFlowDocument.CalloutTone.Warning)]
    // Arreglo asistido: lo que se cerró, en verde. Y va en verde AUNQUE siga diciendo «sin
    // publicar»: lo que la cita afirma es que el commit está hecho.
    [InlineData(
        "**Commiteados en `abc1234`.** El arreglo está commiteado en el clon local de quien lo "
        + "lanzó, **sin publicar**: el push sigue siendo suyo.",
        MarkdownFlowDocument.CalloutTone.Done)]
    // Verificación: una explicación, en neutro.
    [InlineData(
        "Verificar **juzga el código que hay ahora**. Que el código anclado haya desaparecido es "
        + "precisamente lo que hace un arreglo.",
        MarkdownFlowDocument.CalloutTone.Neutral)]
    [InlineData(
        "**No hay cambios en el clon.** Esta sesión terminó sin tocar ningún fichero.",
        MarkdownFlowDocument.CalloutTone.Neutral)]
    internal void Una_cita_se_clasifica_por_lo_que_dice(string quote, MarkdownFlowDocument.CalloutTone expected)
        => MarkdownFlowDocument.ToneOf(quote).Should().Be(expected);

    /// <summary>
    /// <b>Y las que el generador escribe de verdad</b>, no una paráfrasis: las dos constantes de
    /// <c>ReportBuilder</c> se leen desde ahí, para que cambiarlas allí y no aquí sea un cambio que
    /// se ve y no uno que se pierde.
    /// </summary>
    [Fact]
    public void Las_dos_citas_del_arreglo_son_las_que_el_generador_escribe()
    {
        MarkdownFlowDocument.ToneOf(ReportBuilder.UncommittedNotice)
            .Should().Be(MarkdownFlowDocument.CalloutTone.Warning);

        MarkdownFlowDocument.ToneOf(ReportBuilder.CommittedNotice("abc1234"))
            .Should().Be(MarkdownFlowDocument.CalloutTone.Done);
    }

    /// <summary>
    /// <b>Y no se toca una palabra.</b> Lo que cambia es el color de la raya y del fondo; el texto
    /// que llega al documento es el que el informe escribió, entero (D-441).
    /// </summary>
    [Fact]
    public void Clasificar_una_cita_no_le_cambia_el_texto()
    {
        const string markdown = "> **Estos cambios NO están commiteados.** El push sigue siendo suyo.";

        FlowDocument doc = MarkdownFlowDocument.Build(markdown);
        string text = new TextRange(doc.ContentStart, doc.ContentEnd).Text;

        text.Should().Contain("Estos cambios NO están commiteados.")
            .And.Contain("El push sigue siendo suyo.");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }
}
