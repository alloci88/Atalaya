using System.Globalization;
using Atalaya.App.Services;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// BUGFIX-REDONDEO — el redondeo nunca crea un extremo falso.
/// <para>
/// El parte: con 3 unidades auditadas de 1.335, la cobertura salía como «0 %» en la tarjeta de
/// Métricas, en los roscos y en el Portafolio. El dato real es 0,2 %.
/// </para>
/// <para>
/// <b>Estos tests no dependen de la cultura de la máquina.</b> Lo que se compara es el separador
/// decimal QUE LA APLICACIÓN usa (<c>AppCulture.Display</c>), no la coma de un equipo español: eso
/// ya rompió la CI una vez (F8.1, D-522) y no se vuelve a escribir un literal «0,2 %» a mano.
/// </para>
/// </summary>
public sealed class PercentTextTests
{
    /// <summary>El separador decimal de la aplicación, sea cual sea la máquina que corre el test.</summary>
    private static string Sep => AppCulture.Display.NumberFormat.NumberDecimalSeparator;

    /// <summary>«0.2» escrito con el separador de la aplicación, y su signo detrás.</summary>
    private static string Pct(string digits) => digits.Replace(".", Sep, StringComparison.Ordinal) + " %";

    // ================================================================ la tabla del parte

    [Theory]
    [InlineData(3, 1335, "0.2")]        // el caso observado: NO es 0 %
    [InlineData(423, 1000, "42")]       // ≥ 10 % → entero, sin decimales de adorno
    [InlineData(43, 1000, "4.3")]       // 1–10 % → un decimal
    [InlineData(0, 1335, "0")]          // cero EXACTO: es verdad y significa algo
    [InlineData(1335, 1335, "100")]     // cien EXACTO: idem
    public void Cada_proporcion_se_escribe_con_la_precision_minima_que_no_miente(
        int part, int whole, string expected)
        => PercentText.Of(part, whole).Should().Be(Pct(expected));

    [Fact]
    public void Lo_diminuto_pero_no_nulo_se_dice_como_menor_que()
        => PercentText.Of(1, 100_000).Should().Be("< " + Pct("0.1"),
            "«0,0 %» sería la misma mentira con una coma dentro");

    [Fact]
    public void Y_lo_casi_completo_como_mayor_que()
        => PercentText.Of(1334, 1335).Should().Be("> " + Pct("99.9"),
            "un 100 % falso es peor que un 0 % falso: cierra la pregunta");

    // ================================================================ los extremos, uno a uno

    [Fact]
    public void Con_una_sola_unidad_auditada_jamas_se_escribe_cero()
    {
        for (int whole = 2; whole <= 5000; whole += 7)
        {
            PercentText.Of(1, whole).Should().NotBe(Pct("0"),
                $"hay trabajo hecho (1 de {whole}) y decir 0 % lo borra");
        }
    }

    [Fact]
    public void Con_una_sola_unidad_pendiente_jamas_se_escribe_cien()
    {
        for (int whole = 2; whole <= 5000; whole += 7)
        {
            PercentText.Of(whole - 1, whole).Should().NotBe(Pct("100"),
                $"queda una sin auditar de {whole}: un 100 % ahí cierra la pregunta");
        }
    }

    /// <summary>
    /// El caso que el redondeo a un decimal se llevaría por delante: 99,96 % redondea a 100,0.
    /// </summary>
    [Fact]
    public void Noventa_y_nueve_coma_noventa_y_seis_no_es_cien()
        => PercentText.Of(2499, 2500).Should().Be("> " + Pct("99.9"));

    /// <summary>Y su espejo por abajo, que el redondeo a entero convertiría en 0 %.</summary>
    [Fact]
    public void Cuatro_decimas_de_uno_por_ciento_no_es_cero()
        => PercentText.Of(4, 1000).Should().Be(Pct("0.4"));

    /// <summary>
    /// Un 99,9 % exacto con algo pendiente SÍ puede escribirse: es verdad y no es un extremo.
    /// </summary>
    [Fact]
    public void El_noventa_y_nueve_coma_nueve_exacto_se_escribe_tal_cual()
        => PercentText.Of(999, 1000).Should().Be(Pct("99.9"));

    // ================================================================ sin denominador no hay dato

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Sin_nada_que_repartir_no_se_inventa_un_cero(int whole)
        => PercentText.Of(1, whole).Should().Be("—",
            "sin denominador no hay proporción, y un 0 % ahí sería un dato inventado");

    [Fact]
    public void Fuera_de_rango_se_recorta_en_vez_de_escribir_un_disparate()
    {
        PercentText.Of(7, 5).Should().Be(Pct("100"));
        PercentText.Of(-3, 5).Should().Be(Pct("0"));
    }

    // ================================================================ la variante con fracción

    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(1.0, "100")]
    [InlineData(0.423, "42")]
    [InlineData(0.043, "4.3")]
    public void La_fraccion_sigue_las_mismas_reglas(double fraction, string expected)
        => PercentText.Of(fraction).Should().Be(Pct(expected));

    [Fact]
    public void Una_fraccion_minuscula_pero_viva_tampoco_es_cero()
        => PercentText.Of(0.000001).Should().Be("< " + Pct("0.1"));

    [Fact]
    public void Una_fraccion_casi_entera_tampoco_es_cien()
        => PercentText.Of(0.99999).Should().Be("> " + Pct("99.9"));

    [Fact]
    public void Lo_que_no_es_un_numero_se_declara()
        => PercentText.Of(double.NaN).Should().Be("—");

    // ================================================================ la cultura

    /// <summary>
    /// El formateador usa SIEMPRE la cultura de la aplicación, aunque el hilo venga en otra. Es la
    /// frontera de F8.1: texto para personas en es-ES, pase lo que pase en la máquina.
    /// </summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public void No_depende_de_la_cultura_del_hilo(string hostile)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(hostile);
            PercentText.Of(3, 1335).Should().Be(Pct("0.2"));
            PercentText.Of(423, 1000).Should().Be(Pct("42"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>Y con la cultura de la casa, la coma es la coma.</summary>
    [Fact]
    public void En_la_cultura_de_la_aplicacion_el_separador_es_la_coma()
    {
        if (AppCulture.Display.Name == "es-ES")
        {
            PercentText.Of(3, 1335).Should().Be("0,2 %");
        }
    }

    // ================================================================ nadie formatea por su cuenta

    /// <summary>
    /// El formateador es UNO. Si alguien vuelve a escribir un <c>:0%</c> suelto, el defecto vuelve
    /// solo a ese sitio y nadie se entera hasta que un usuario lo ve — que es como llegó éste.
    /// </summary>
    [Fact]
    public void No_queda_ningun_formateo_de_porcentaje_suelto_en_el_codigo()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Atalaya.sln")))
        {
            root = root.Parent;
        }

        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(root!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
                 .Concat(Directory.EnumerateFiles(
                     Path.Combine(root.FullName, "src"), "*.xaml", SearchOption.AllDirectories)))
        {
            if (Path.GetFileName(file) == "PercentText.cs" || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            foreach (string pattern in new[] { ":0%", ":0.0%", "\"P0\"", "\"P1\"" })
            {
                if (text.Contains(pattern, StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)} · {pattern}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "todo porcentaje pasa por PercentText: un formateo suelto es un sitio donde el "
            + "redondeo puede volver a inventarse un extremo");
    }

    /// <summary>Un solo espacio antes del signo, y ninguno raro dentro.</summary>
    [Fact]
    public void El_formato_no_lleva_espacios_raros()
    {
        foreach (string text in new[]
                 {
                     PercentText.Of(3, 1335), PercentText.Of(0, 10), PercentText.Of(10, 10),
                     PercentText.Of(1, 100_000), PercentText.Of(999, 1000),
                 })
        {
            text.Should().EndWith(" %");
            text.Should().NotContain(" ").And.NotContain(" ").And.NotContain("  ");
        }
    }
}
