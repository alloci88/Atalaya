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
    /// La conversión sigue viviendo en UN solo sitio con nombre —1 credit = 0,01 $— pero desde
    /// PROV-2 §3 ese sitio es la <c>ProviderBilling</c> de la casa que los usa, no una constante
    /// del dominio: el día que cambie, cambia donde ella vive. Los dólares con dos decimales y el
    /// símbolo detrás; los credits con uno, como hasta hoy.
    /// </summary>
    [Fact]
    public void La_misma_cifra_en_las_dos_unidades()
    {
        TestProviders.Copilot.Billing.UsdPerUnit.Should()
            .Be(0.01m, "la equivalencia está escrita UNA vez, y la declara quien la usa");

        CostFormat.Currency = CostCurrency.Credits;
        CostFormat.Of(1.853m).Should().Be("185,3 credits");

        CostFormat.Currency = CostCurrency.Usd;
        CostFormat.Of(1.853m).Should().Be("1,85 $");
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

        CostFormat.Number(0.004m).Should().Be("< 0,01");
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
            CostFormat.Both(1.853m).Should().Be("185,3 AI credits (1,85 $)");
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
    /// <b>Y NINGÚN TEXTO QUE ALIMENTA UNA PLANTILLA, TAMPOCO</b> (R11 §1f). La regla de arriba
    /// mira los XAML, y por ahí no se coló: se coló por el C# que los llena.
    /// <para>
    /// <b>El defecto que lo trae.</b> La cabecera de una unidad de la sesión en vivo decía
    /// «coste 15» mientras el pie de esa misma pantalla decía «0,12 $»: el mismo número, dos
    /// unidades, y la de arriba sin ninguna. Salía de un <c>" · coste {c:0.##}"</c> escrito a mano
    /// en <c>LiveSessionService</c>, que no pasa por <see cref="CostFormat"/> y por tanto no se
    /// entera de la divisa. F29 §2 cerró esa puerta en las vistas y ésta se quedó abierta.
    /// </para>
    /// <para>
    /// Se recorren los ficheros que dan de comer a Sesión en vivo, Última sesión y Arreglo
    /// asistido —las plantillas donde se le escapó—, y se busca lo que sabe escribir un coste sin
    /// preguntar: la palabra «coste» o «credits» dentro de una cadena, con una cifra formateada
    /// dentro. La lista es corta a propósito: no es un barrido del repositorio, es la puerta por
    /// la que ya se coló una vez.
    /// </para>
    /// </summary>
    [Fact]
    public void Ningun_texto_de_la_sesion_ni_del_arreglo_escribe_un_coste_a_mano()
    {
        string[] fuentes =
        {
            "src/Atalaya.App/Services/LiveSessionService.cs",
            "src/Atalaya.App/Services/LiveSessionModels.cs",
            "src/Atalaya.App/Services/LiveFixService.cs",
            "src/Atalaya.App/ViewModels/SessionViewModel.cs",
            "src/Atalaya.App/ViewModels/AssistedFixViewModel.cs",
        };

        var offenders = new List<string>();
        foreach (string relative in fuentes)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(path).Should().BeTrue($"{relative} tiene que existir para que esto mida algo");

            // Fuera los comentarios: hablan del coste todo el rato y no escriben nada en pantalla.
            string code = Regex.Replace(
                File.ReadAllText(path), @"^\s*(?://|///).*$", string.Empty, RegexOptions.Multiline);

            foreach (Match m in Regex.Matches(code, "\"[^\"\n]*(?:coste|credits)[^\"\n]*\""))
            {
                // Una cadena que nombra el coste y mete una cifra con formato numérico dentro —o
                // que escribe la unidad a mano— es exactamente la forma del defecto.
                if (Regex.IsMatch(m.Value, @"\{[^}]*:[0#.,]+\}")
                    || Regex.IsMatch(m.Value, @"\bcredits\b", RegexOptions.IgnoreCase))
                {
                    offenders.Add($"{relative}: {m.Value}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "el coste lo escribe CostFormat, que es lo único que sabe en qué divisa está esta "
            + "máquina; una cifra formateada a mano dice otra cosa que el pie de la misma pantalla");
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
