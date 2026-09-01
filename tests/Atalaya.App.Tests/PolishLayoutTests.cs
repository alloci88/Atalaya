using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F12 §H.3 y §H.4 — el pulido de interfaz que se puede comprobar sin abrir una ventana, leyendo el
/// XAML REAL de las vistas.
/// <para>
/// <b>Por qué sobre el marcado y no midiendo.</b> Los dos defectos viven en trozos que no se pueden
/// instanciar aparte: la fila del inventario está dentro de un <c>DataTemplate</c> —sin datos no
/// existe, y con datos falsos se mediría otra cosa— y la franja del aviso cuelga de una ficha que
/// arrastra el editor de código entero. Lo que sí se puede fijar es la CAUSA de cada defecto, que
/// en los dos casos es una decisión de marcado con nombre propio: un contenedor que da ancho
/// infinito, y unos elementos sin centrar. Lo que se ve —los colores en los dos temas— se mira a
/// ojo, y el informe dice qué mirar.
/// </para>
/// </summary>
public sealed class PolishLayoutTests
{
    private static string Xaml(string view)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Atalaya.App", "Views", view));
    }

    // ============================================================ §H.4 · el aviso cabe

    /// <summary>
    /// La causa exacta del recorte: el aviso vivía en un <c>StackPanel Orientation="Horizontal"</c>,
    /// y un StackPanel horizontal mide a sus hijos con ancho INFINITO. Con eso
    /// <c>TextWrapping="Wrap"</c> no envuelve nunca y el texto sale por la derecha, cortado contra
    /// el borde de la franja. El aviso de re-anclaje es de los largos, así que le tocaba siempre.
    /// </summary>
    [Fact]
    public void El_aviso_vive_en_una_rejilla_que_le_da_ancho_real()
    {
        string band = NoticeBand();

        band.Should().NotContain("StackPanel",
            "un StackPanel horizontal mide con ancho infinito y Wrap deja de envolver");
        band.Should().Contain("<Grid>");
        Regex.Matches(band, @"<ColumnDefinition\b").Should().HaveCount(2,
            "texto elástico y botón a su medida: eso es lo que le da un ancho al texto");
        band.Should().Contain(@"<ColumnDefinition Width=""*"" />", "el texto se lleva lo que sobra");
    }

    [Fact]
    public void El_aviso_envuelve_y_lleva_su_texto_entero_en_el_tooltip()
    {
        string band = NoticeBand();

        band.Should().Contain("TextWrapping=\"Wrap\"", "que quepa en dos líneas es la primera salida");
        band.Should().Contain("ToolTip=\"{Binding SnippetNotice}\"",
            "y si aun así no cupiera, el texto completo tiene que estar a un tooltip");
    }

    /// <summary>La franja del aviso, recortada del XAML de la ficha.</summary>
    private static string NoticeBand()
    {
        string xaml = Xaml("FindingDetailView.xaml");
        int start = xaml.IndexOf("Visibility=\"{Binding HasSnippetNotice", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "la franja se reconoce por su visibilidad");
        int end = xaml.IndexOf("</Border>", start, StringComparison.Ordinal);

        // Sin comentarios: llevan dentro el nombre del contenedor que se retiró.
        return Regex.Replace(xaml[start..end], @"<!--.*?-->", string.Empty, RegexOptions.Singleline);
    }

    // ============================================================ §H.3 · los indicadores, alineados

    /// <summary>
    /// La fila de una unidad del inventario: la casilla, el nombre, la píldora de estado, el
    /// indicador de deriva y el candado. TODOS centrados verticalmente.
    /// <para>
    /// Sin el centrado, un <c>Border</c> dentro de una celda de <c>Grid</c> se estira a lo alto de
    /// la fila y su texto queda pegado arriba — mientras el punto de color, que sí llevaba
    /// <c>VerticalAlignment="Center"</c>, se quedaba en el medio. El indicador se leía descolgado
    /// del nombre de la unidad y de la píldora de estado.
    /// </para>
    /// </summary>
    [Fact]
    public void La_fila_del_inventario_alinea_todo_lo_que_enseña()
    {
        string row = UnitRow();

        foreach (Match element in Regex.Matches(
                     row, @"<(CheckBox|TextBlock|Border|StackPanel|Ellipse)\b[^>]*>"))
        {
            element.Value.Should().Contain("VerticalAlignment=\"Center\"",
                $"todo lo de la fila se lee en la misma línea: {Compact(element.Value)}");
        }
    }

    /// <summary>
    /// Y vale para LOS DOS indicadores —«Cambiada desde su auditoría» y «Arreglada — pendiente de
    /// verificar»—, no para uno: los dos salen de esta misma plantilla, con el mismo texto y el
    /// mismo punto de color, así que centrarla los centra a los dos. Es la lección de D-710c
    /// aplicada al revés: un solo sitio que pintar, un solo sitio que arreglar.
    /// </summary>
    [Fact]
    public void Los_dos_indicadores_de_deriva_salen_de_la_misma_plantilla()
    {
        string row = UnitRow();

        Regex.Matches(row, @"\{Binding DriftLabel\}").Should().HaveCount(1);
        Regex.Matches(row, @"\{Binding DriftInk\}").Should().HaveCount(1);
        row.Should().Contain("{Binding DriftTooltip}", "el color nunca es el único canal");
    }

    /// <summary>La plantilla de la fila de unidad, recortada del XAML del inventario.</summary>
    private static string UnitRow()
    {
        string xaml = Xaml("InventoryView.xaml");
        int start = xaml.IndexOf(
            "<CheckBox Grid.Column=\"0\" IsChecked=\"{Binding IsSelected}\"", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "la fila de unidad se reconoce por su casilla");
        int end = xaml.IndexOf("</Grid>", start, StringComparison.Ordinal);

        return Regex.Replace(xaml[start..end], @"<!--.*?-->", string.Empty, RegexOptions.Singleline);
    }

    private static string Compact(string markup) => Regex.Replace(markup, @"\s+", " ").Trim();
}
