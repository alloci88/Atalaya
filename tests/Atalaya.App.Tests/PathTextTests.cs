using System.Windows;
using System.Windows.Controls;
using Atalaya.App.Controls;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// La primitiva de recorte de rutas (P-01, UI-0009). Aquí se prueba <b>cuándo</b> acorta, que es lo
/// que se rompió; el <b>qué</b> conserva —el nombre del fichero— va en el mismo sitio porque son la
/// misma regla vista dos veces.
/// </summary>
public class PathTextTests
{
    private const string Larga = "XBLASTRecovery/Class/Authentication.cs";
    private const string Otra = "XBLASTLocalization/Class/MethodInfoResolver.cs";

    /// <summary>
    /// El control dentro de una columna que le impone el ancho, medido y colocado de verdad: es la
    /// única forma de que <c>ActualWidth</c> valga algo.
    /// </summary>
    private static (Grid Fila, PathText Ruta) Fila(double ancho)
    {
        var ruta = new PathText { FontSize = 12 };
        var fila = new Grid { Width = ancho };
        fila.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fila.Children.Add(ruta);

        fila.Measure(new Size(ancho, double.PositiveInfinity));
        fila.Arrange(new Rect(0, 0, ancho, fila.DesiredSize.Height));
        return (fila, ruta);
    }

    private static void Redibuja(Grid fila)
    {
        fila.Measure(new Size(fila.Width, double.PositiveInfinity));
        fila.Arrange(new Rect(0, 0, fila.Width, fila.DesiredSize.Height));
    }

    /// <summary>Lo que cabe, entero: acortar lo que cabe es esconder sin motivo.</summary>
    [Fact]
    public void Una_ruta_que_cabe_se_deja_entera()
    {
        ViewLayout.OnUiThread(() =>
        {
            (Grid fila, PathText ruta) = Fila(600);

            ruta.Full = Larga;
            Redibuja(fila);

            ruta.Text.Should().Be(Larga);
        });
    }

    /// <summary>
    /// Y lo que no cabe se acorta POR EL MEDIO, conservando el nombre del fichero: es lo que
    /// identifica la fila. Cortar por el final deja «XBLASTRecovery/Class/Authentica», que no dice
    /// de qué fichero se está hablando.
    /// </summary>
    [Fact]
    public void Una_ruta_que_no_cabe_conserva_el_nombre_del_fichero()
    {
        ViewLayout.OnUiThread(() =>
        {
            (Grid fila, PathText ruta) = Fila(140);

            ruta.Full = Larga;
            Redibuja(fila);

            ruta.Text.Should().NotBe(Larga);
            ruta.Text.Should().Contain("…", "un recorte que no se ve es una mentira (P-01)");
            ruta.Text.Should().EndWith("Authentication.cs", "el nombre del fichero es lo que identifica");
        });
    }

    /// <summary>
    /// <b>Y UNA FILA RECICLADA VUELVE A ACORTAR.</b> La lista de Hallazgos está virtualizada: el
    /// mismo control sirve a una fila detrás de otra y lo único que cambia es <c>Full</c>. Como el
    /// control vive en una columna estrella, escribir la ruta nueva NO cambia su tamaño —la columna
    /// manda—, así que el aviso de cambio de tamaño no llega y el acortado no ocurría: la segunda
    /// fila se salía por el borde, cortada en seco y sin puntos suspensivos, mientras la primera
    /// salía perfecta.
    /// </summary>
    [Fact]
    public void Una_fila_reciclada_vuelve_a_acortar_su_ruta()
    {
        ViewLayout.OnUiThread(() =>
        {
            (Grid fila, PathText ruta) = Fila(140);

            ruta.Full = Larga;
            Redibuja(fila);
            ruta.Text.Should().Contain("…");

            // El reciclado: mismo control, ancho idéntico, ruta distinta.
            ruta.Full = Otra;
            Redibuja(fila);

            ruta.Text.Should().NotBe(Otra, "la ruta nueva tampoco cabe en 140 px");
            ruta.Text.Should().Contain("…");
            ruta.Text.Should().EndWith("MethodInfoResolver.cs");
        });
    }
}
