using System.Text.RegularExpressions;
using Atalaya.App.Services;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F29 §2 — <b>la divisa</b>: el coste se enseña en la unidad que elija esta máquina, con UN solo
/// formateador, y los informes registran las dos.
/// <para>
/// Tests de regla (N-5, N-7): lo que protegen es que no aparezca una segunda forma de escribir un
/// coste. Ese es el defecto que se rompe en silencio — una vista con la unidad a mano seguiría
/// diciendo «AI credits» con la preferencia en dólares, y nadie lo vería hasta mirarla.
/// </para>
/// </summary>
[Collection(CurrencyCollection.Name)]
public sealed class CostCurrencyTests : IDisposable
{
    private readonly CostCurrency _previous = CostFormat.Currency;

    public void Dispose() => CostFormat.Currency = _previous;

    /// <summary>
    /// La conversión es la de D-786 y vive en un solo sitio con nombre: 1 credit = 0,01 $. Los
    /// dólares con dos decimales y el símbolo detrás; los credits con uno, como hasta hoy.
    /// </summary>
    [Fact]
    public void La_misma_cifra_en_las_dos_unidades()
    {
        CreditCalculator.UsdPerCredit.Should().Be(0.01m, "la conversión está escrita UNA vez");

        CostFormat.Currency = CostCurrency.Credits;
        CostFormat.Of(185.3m).Should().Be("185,3 credits");

        CostFormat.Currency = CostCurrency.Usd;
        CostFormat.Of(185.3m).Should().Be("1,85 $");
    }

    /// <summary>
    /// <b>El redondeo no puede escribir un cero cuando hubo gasto</b>, y la regla vale igual en la
    /// unidad nueva: por debajo del céntimo se dice el límite, no «0,00 $», que afirmaría que fue
    /// gratis (BUGFIX-REDONDEO).
    /// </summary>
    [Fact]
    public void Un_gasto_por_debajo_del_centimo_no_se_redondea_a_cero()
    {
        CostFormat.Currency = CostCurrency.Usd;

        CostFormat.Number(0.4m).Should().Be("< 0,01");
        CostFormat.Number(0m).Should().Be("0,00", "cero medido sí es cero");
    }

    /// <summary>
    /// <b>Un informe registra las dos, siempre</b>, y no mira la preferencia de la máquina: se lee
    /// dentro de años y en otro puesto. Es la cabecera del encargo, palabra por palabra.
    /// </summary>
    [Fact]
    public void El_informe_escribe_las_dos_cifras_pase_lo_que_pase_con_la_preferencia()
    {
        foreach (CostCurrency currency in new[] { CostCurrency.Credits, CostCurrency.Usd })
        {
            CostFormat.Currency = currency;
            CostFormat.Both(185.3m).Should().Be("185,3 AI credits (1,85 $)");
        }
    }

    /// <summary>
    /// <b>Ningún XAML escribe la unidad a mano.</b> Con la unidad en la vista, cambiar la
    /// preferencia dejaría media aplicación diciendo lo que ya no es — y no habría forma de
    /// enterarse sin abrir esa pantalla concreta.
    /// <para>
    /// La excepción es la CABECERA de la tabla de tarifas, y no es una divisa de presentación: los
    /// precios publicados de GitHub están en dólares por millón de tokens y ahí seguirán. Por eso
    /// también sale de <see cref="CostFormat.RateColumnUnit"/> y no de una cadena suelta.
    /// </para>
    /// </summary>
    [Fact]
    public void Ningun_xaml_escribe_la_unidad_del_coste_a_mano()
    {
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot(), "src", "Atalaya.App"), "*.xaml", SearchOption.AllDirectories))
        {
            string markup = Regex.Replace(File.ReadAllText(file), "<!--.*?-->", string.Empty, RegexOptions.Singleline);

            // Solo lo que se PINTA: un atributo de texto o un `Run`. Un nombre de recurso o de
            // propiedad puede contener «credits» sin escribir nada en pantalla.
            foreach (Match m in Regex.Matches(markup, @"Text=""([^""{][^""]*)"""))
            {
                string text = m.Groups[1].Value;
                if (Regex.IsMatch(text, @"\bcredits\b", RegexOptions.IgnoreCase) || text.Contains('$'))
                {
                    offenders.Add($"{Path.GetFileName(file)}: «{text}»");
                }
            }
        }

        offenders.Should().BeEmpty(
            "la unidad del coste la escribe CostFormat con la divisa activa, no la vista");
    }

    /// <summary>
    /// La preferencia se guarda como una PALABRA y no como el nombre de un miembro del enum:
    /// renombrar un enum no puede cambiar lo que ya está escrito en la máquina de alguien. Y lo
    /// que no reconozca son credits, que es lo de fábrica.
    /// </summary>
    [Fact]
    public void La_preferencia_se_guarda_como_palabra_y_lo_desconocido_son_credits()
    {
        new AppSettings().CostCurrency.Should().Be(CostCurrencies.Credits);

        CostCurrencies.Parse(CostCurrencies.Usd).Should().Be(CostCurrency.Usd);
        CostCurrencies.Parse("lo-que-sea").Should().Be(CostCurrency.Credits);
        CostCurrencies.Save(CostCurrency.Usd).Should().Be("usd");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }
}
