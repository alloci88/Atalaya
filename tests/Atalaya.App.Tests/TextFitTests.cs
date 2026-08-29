using System.Windows;
using System.Windows.Media;
using Atalaya.App.Controls;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F10.1 §2 — encajar un texto midiéndolo.
/// <para>
/// El defecto que arreglan estos tests salía en las capturas del usuario: nombres cortados a mitad
/// de palabra dentro de las celdas. Cortar por el final es además lo peor que se puede hacer con
/// estos nombres — <c>ControllerConfiguration.cs</c> y <c>ControllerMain.cs</c> comparten los diez
/// primeros caracteres—, así que el recorte va por el medio o no va.
/// </para>
/// </summary>
public sealed class TextFitTests
{
    private static readonly Typeface Face =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private const double Size = 11;
    private const double Dpi = 1.0;

    private static double Width(string text) => TextFit.Width(text, Face, Size, Dpi);

    private static string? Fit(string text, double available, double retention = TextFit.DefaultRetention)
        => TextFit.Fit(text, Face, Size, Dpi, available, retention);

    // ============================================ El recorte por el medio

    /// <summary>Se conservan los DOS extremos, que es donde estos nombres se distinguen.</summary>
    [Fact]
    public void El_recorte_conserva_la_cabeza_y_la_cola()
    {
        string cut = TextFit.Middle("ControllerConfiguration.cs", 12);

        cut.Should().HaveLength(13, "doce caracteres visibles más la elipsis");
        cut.Should().Contain("…");
        cut.Should().StartWith("Contro");
        cut.Should().EndWith(".cs", "la extensión es media identidad del fichero");
    }

    /// <summary>
    /// Dos nombres que comparten prefijo siguen distinguiéndose. Con recorte trasero
    /// —lo que hace <c>TextTrimming</c>— saldrían idénticos, que es el defecto entero.
    /// </summary>
    [Fact]
    public void Dos_nombres_con_el_mismo_prefijo_no_se_confunden()
    {
        const string uno = "ControllerConfiguration.cs";
        const string otro = "ControllerMain.cs";

        // El prefijo compartido: recortando por el final a esa longitud, los dos dicen lo mismo.
        uno[..10].Should().Be(otro[..10]);

        TextFit.Middle(uno, 10).Should().NotBe(TextFit.Middle(otro, 10),
            "con el mismo presupuesto, el recorte por el medio los sigue distinguiendo");
    }

    [Fact]
    public void Lo_que_cabe_entero_no_se_toca()
    {
        const string name = "Crc.cs";

        Fit(name, Width(name) + 1).Should().Be(name);
        TextFit.Middle(name, 99).Should().Be(name);
    }

    // ============================================ Fit: medir, no estimar

    /// <summary>
    /// El presupuesto de caracteres no sirve: «WWWWWWWW» y «llllllll» tienen ocho caracteres y no
    /// ocupan lo mismo ni de lejos. Por eso se mide el texto renderizado.
    /// </summary>
    [Fact]
    public void Ocho_caracteres_no_son_un_ancho()
        => Width("WWWWWWWW").Should().BeGreaterThan(Width("llllllll") * 2);

    /// <summary>
    /// Lo que no cabe con un recorte que siga identificando algo NO se escribe. Media palabra en
    /// una celda no informa: ensucia la de al lado, y el tooltip ya dice el nombre entero.
    /// </summary>
    [Fact]
    public void Si_no_cabe_nada_reconocible_no_se_escribe_nada()
    {
        const string name = "FormAdvancedVibrationModel.cs";

        Fit(name, 8).Should().BeNull();
        Fit(name, 0).Should().BeNull();
        Fit(string.Empty, 500).Should().BeNull();
    }

    /// <summary>Y lo que se escribe, cabe. Nunca se devuelve algo que se saldría de la celda.</summary>
    [Theory]
    [InlineData(40)]
    [InlineData(70)]
    [InlineData(120)]
    [InlineData(400)]
    public void Lo_que_devuelve_cabe_siempre(double available)
    {
        string? fitted = Fit("ControllerConfiguration.cs", available);

        if (fitted is not null)
        {
            Width(fitted).Should().BeLessThanOrEqualTo(available);
        }
    }

    /// <summary>
    /// La retención es la frontera entre «acortado» y «destruido»: por defecto se conserva el 70 %
    /// del nombre, y por debajo de eso se prefiere no escribir.
    /// </summary>
    [Fact]
    public void Por_defecto_se_conserva_la_mayor_parte_del_nombre()
    {
        const string name = "ControllerConfiguration.cs";
        double floor = Math.Ceiling(name.Length * TextFit.DefaultRetention);

        for (double available = 10; available < Width(name); available += 5)
        {
            if (Fit(name, available) is { } fitted)
            {
                (fitted.Length - 1).Should().BeGreaterThanOrEqualTo((int)floor,
                    "se escribió un recorte que ya no identifica el fichero");
            }
        }
    }

    /// <summary>
    /// Con retención 0 —el nombre de un módulo— se escribe lo que quepa aunque queden cuatro
    /// letras: la cabecera de un módulo no puede desaparecer, y lo que cae antes es el detalle.
    /// </summary>
    [Fact]
    public void El_nombre_de_un_modulo_se_escribe_aunque_quede_en_cuatro_letras()
    {
        string? fitted = Fit("XBLASTCustomRibbonControl", 40, retention: 0);

        fitted.Should().NotBeNull();
        fitted!.Should().Contain("…");
        fitted.Should().StartWith("X");
        Width(fitted!).Should().BeLessThanOrEqualTo(40);
    }

    /// <summary>
    /// Retención 1 es «entero o nada». Lo usa lo que tiene una forma corta propia: «+60 u…ades»
    /// no es una versión corta de «+60 unidades», es una versión estropeada — para eso está «+60».
    /// </summary>
    [Fact]
    public void Con_retencion_total_o_cabe_entero_o_no_se_escribe()
    {
        const string name = "+60 unidades";

        Fit(name, Width(name) + 1, retention: 1).Should().Be(name);
        Fit(name, Width(name) - 6, retention: 1).Should().BeNull("no hay recorte que valga aquí");
    }

    /// <summary>Y aun con retención 0, un ancho ridículo no dibuja un jeroglífico.</summary>
    [Fact]
    public void Con_un_ancho_ridiculo_tampoco_se_escribe()
        => Fit("XBLASTCustomRibbonControl", 3, retention: 0).Should().BeNull();

    /// <summary>Aprovecha el sitio: con más ancho, más nombre. Nunca al revés.</summary>
    [Fact]
    public void Cuanto_mas_sitio_hay_mas_nombre_se_escribe()
    {
        const string name = "FormAdvancedVibrationModel.cs";

        int corto = Fit(name, 90, retention: 0)?.Length ?? 0;
        int medio = Fit(name, 130, retention: 0)?.Length ?? 0;
        int largo = Fit(name, 180, retention: 0)?.Length ?? 0;

        corto.Should().BeLessThanOrEqualTo(medio);
        medio.Should().BeLessThanOrEqualTo(largo);
        largo.Should().BeGreaterThan(corto);
    }
}
