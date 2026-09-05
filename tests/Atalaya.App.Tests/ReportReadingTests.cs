using Atalaya.App.Services;
using Atalaya.App.ViewModels;
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
