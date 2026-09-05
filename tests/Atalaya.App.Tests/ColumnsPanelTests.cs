using System.Windows;
using System.Windows.Controls;
using Atalaya.App.Controls;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F26 Parte C — <b>las tarjetas de una fila miden lo mismo</b>.
/// <para>
/// <c>ColumnsPanel</c> nació en la Parte B para el Portafolio (D-970) y llega a Métricas en la C.
/// Su contrato son dos cosas: reparte el ancho ENTERO entre las columnas que caben, y le da a
/// todas las tarjetas de una fila el alto de la más alta.
/// </para>
/// <para>
/// <b>La segunda es la que trae esta parte, y por eso se prueba.</b> Métricas tenía sus cuatro
/// cifras en un <c>UniformGrid</c> y cada tarjeta medía lo suyo, centrada en la fila: la de coste
/// —que es cuatro veces más larga— dejaba a las otras tres flotando a alturas distintas, con sus
/// bordes de arriba y sus cifras cada uno en una y. Se lee como un montón, no como una rejilla.
/// </para>
/// <para>
/// <b>Y se rompe callando.</b> Es un <c>ArrangeOverride</c>: alguien lo simplifica, las tarjetas
/// vuelven a medir su contenido y no falla nada — se ve, y solo si alguien mira. Es el mismo
/// argumento de D-963: lo que WPF hace en silencio hay que escribirlo en un test.
/// </para>
/// </summary>
public sealed class ColumnsPanelTests
{
    [Fact]
    public void Todas_las_tarjetas_de_una_fila_miden_lo_que_la_mas_alta() => ViewLayout.OnUiThread(() =>
    {
        var panel = new ColumnsPanel { MinColumnWidth = 200, MaxColumns = 4, Gap = 16 };
        panel.Children.Add(Card(40));
        panel.Children.Add(Card(120));
        panel.Children.Add(Card(60));

        Measure(panel, width: 900);

        panel.Children.Cast<FrameworkElement>()
            .Select(c => c.ActualHeight)
            .Should().AllBeEquivalentTo(120d, "el alto de una fila lo manda su tarjeta más alta");
    });

    /// <summary>
    /// Y el ancho se reparte ENTERO: es la otra mitad del control, la que arregló el portafolio de
    /// una tarjeta de 330 px con el 80 % de la pantalla en blanco.
    /// </summary>
    [Fact]
    public void El_ancho_se_reparte_entero_entre_las_columnas_que_caben() => ViewLayout.OnUiThread(() =>
    {
        var panel = new ColumnsPanel { MinColumnWidth = 200, MaxColumns = 4, Gap = 16 };
        for (int i = 0; i < 3; i++)
        {
            panel.Children.Add(Card(50));
        }

        Measure(panel, width: 800);

        // 800 con mínimo 200 y hueco 16 dan tres columnas: (800 - 2x16) / 3 = 256 cada una. Ni
        // una tarjeta de 200 con 200 px sobrando a la derecha, que es de lo que venimos.
        panel.Children.Cast<FrameworkElement>()
            .Select(c => c.ActualWidth)
            .Should().AllBeEquivalentTo(256d);
    });

    /// <summary>
    /// Lo que no cabe SE REORGANIZA, no se encoge (principio 1): a 1.280 las cuatro cifras de
    /// Métricas pasan a tres y una, y ninguna baja de su ancho mínimo.
    /// </summary>
    [Fact]
    public void Lo_que_no_cabe_baja_de_fila_en_vez_de_estrecharse() => ViewLayout.OnUiThread(() =>
    {
        var panel = new ColumnsPanel { MinColumnWidth = 280, MaxColumns = 4, Gap = 16 };
        for (int i = 0; i < 4; i++)
        {
            panel.Children.Add(Card(50));
        }

        Measure(panel, width: 900);

        var cards = panel.Children.Cast<FrameworkElement>().ToList();
        cards.Should().OnlyContain(c => c.ActualWidth >= 280);
        cards[3].TranslatePoint(new Point(0, 0), panel).Y
            .Should().BeGreaterThan(0, "la cuarta baja a la fila siguiente");
    });

    /// <summary>
    /// Una tarjeta con CONTENIDO de ese alto, no con el alto puesto. Es la diferencia que importa:
    /// una tarjeta con `Height` fijo no se estira aunque la fila le dé sitio, y el test estaría
    /// midiendo el número que él mismo escribió en vez de lo que hace el panel.
    /// </summary>
    private static Border Card(double height) => new() { Child = new Border { Height = height } };

    private static void Measure(ColumnsPanel panel, double width)
    {
        panel.Measure(new Size(width, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
        panel.UpdateLayout();
    }
}
