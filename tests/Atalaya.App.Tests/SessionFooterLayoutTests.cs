using System.Windows;
using System.Windows.Controls;
using Atalaya.App.Controls;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.ClaudeCode;
using Atalaya.Copilot;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F17-RETOQUE — EL PIE NO REPITE LOS TOKENS Y NO CORTA SIN AVISAR.
/// <para>
/// <b>El defecto.</b> Con Claude Code el pie decía «20 llamadas · 28.050 entrada · 15.670 salida
/// · caché … · coste: incluido en tu suscripción de Claude» y, detrás, el bloque antiguo «tokens
/// 28.050 in / … out», cortado por falta de sitio. Era el resto de la versión anterior: el retoque
/// del coste dejó vivo el viejo. Con Copilot el bloque viejo era el único que enseñaba tokens, así
/// que la corrección no es «quitar el bloque» sino que el criterio común los diga UNA vez para las
/// dos casas, detrás del coste.
/// </para>
/// <para>
/// <b>Y la regla de la casa, medida:</b> o cabe, o se abrevia con acceso al detalle; nunca texto
/// truncado a media palabra. A anchos pequeños ceden por este orden: tokens, coste, y las llamadas
/// nunca. Como con la cabecera (D-819), se mide sobre el control real a los anchos alcanzables.
/// </para>
/// </summary>
public sealed class SessionFooterLayoutTests
{
    private static readonly CostResult Subscription = CostResult.Unavailable(CostUnavailable.NotBilled);

    /// <summary>Los números del parte: 20 llamadas, 28.050 / 15.670, caché 235.327 / 51.077.</summary>
    private static IReadOnlyList<FooterSegment> Claude()
        => CreditText.UsageSegments(20, 28050, 15670, 235327, 51077, Subscription, ClaudeCodeProvider.Id);

    private static IReadOnlyList<FooterSegment> Copilot()
        => CreditText.UsageSegments(20, 28050, 15670, 0, 0, new CostResult(68.2m), RealCopilotAgent.Id);

    private static int Count(string text, string needle)
    {
        int n = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }

    // ================================================================ una sola vez

    [Fact]
    public void Con_Claude_Code_los_tokens_van_una_vez_y_desglosados()
    {
        string full = string.Join(" · ", Claude().Select(s => s.Full));

        full.Should().Be("20 llamadas · coste: incluido en tu suscripción de Claude · "
                         + "28.050 entrada · 15.670 salida · caché 235.327 leída / 51.077 escrita");
        Count(full, "28.050").Should().Be(1, "los tokens no se dicen dos veces");
        full.Should().NotContain(" in ").And.NotContain(" out", "el bloque antiguo se fue");
    }

    [Fact]
    public void Con_Copilot_los_tokens_tambien_van_una_vez_detras_del_coste()
    {
        string full = string.Join(" · ", Copilot().Select(s => s.Full));

        full.Should().Be("20 llamadas · 68,2 AI credits · 28.050 entrada · 15.670 salida");
        Count(full, "28.050").Should().Be(1);
    }

    [Fact]
    public void El_orden_es_llamadas_coste_tokens_en_las_dos_casas()
    {
        foreach (IReadOnlyList<FooterSegment> segments in new[] { Claude(), Copilot() })
        {
            segments.Should().HaveCount(3);
            segments[0].Full.Should().Be("20 llamadas");
            segments[0].Priority.Should().Be(0, "las llamadas no ceden nunca");
            segments[1].Priority.Should().Be(1, "el coste cede después de los tokens");
            segments[2].Priority.Should().Be(2, "los tokens ceden primero");
            segments[2].Candidates.Last().Should().EndWith(" tokens",
                "lo desglosado colapsa al total con su unidad, nunca a un texto cortado; el desglose sigue en el tooltip y en el informe");
        }
    }

    /// <summary>Las formas del coste, de larga a corta, en las dos casas.</summary>
    [Fact]
    public void El_coste_tiene_su_forma_corta()
    {
        Claude()[1].Candidates.Should().Equal("coste: incluido en tu suscripción de Claude", "coste: suscripción");
        Copilot()[1].Candidates.Should().Equal("68,2 AI credits", "68,2 credits");
    }

    /// <summary>Ya no hay ningún «tokens X in / Y out» en las vistas ni en los view-models.</summary>
    [Theory]
    [InlineData("SessionView.xaml")]
    [InlineData("AssistedFixView.xaml")]
    public void La_vista_usa_el_pie_que_no_trunca(string view)
    {
        string xaml = ViewLayout.Xaml(view);

        xaml.Should().Contain("<c:FooterLine").And.Contain("Segments=\"{Binding Footer}\"");
        xaml.Should().NotContain("TokensText").And.NotContain("{Binding CostText}",
            "el pie sale entero del criterio común, no de trozos sueltos");
    }

    [Fact]
    public void El_view_model_de_sesion_ya_no_tiene_el_bloque_antiguo()
        => typeof(SessionViewModel).GetProperty("TokensText").Should().BeNull();

    // ================================================================ la elección, sin WPF

    /// <summary>
    /// La regla, sobre una medida de mentira (cada carácter = 1): con sitio, todo entero; al
    /// faltar, primero los tokens (a total, luego fuera), después el coste;
    /// las llamadas siempre.
    /// </summary>
    [Fact]
    public void Al_faltar_sitio_ceden_los_tokens_luego_el_coste_y_las_llamadas_nunca()
    {
        IReadOnlyList<FooterSegment> segments = Claude();
        static double Chars(string t, bool _) => t.Length;
        const double sep = 3;
        int full = segments.Sum(s => s.Full.Length) + 2 * (int)sep;

        FooterLine.Choose(segments, double.PositiveInfinity, Chars, sep).Should().Equal(segments.Select(s => s.Full));
        FooterLine.Choose(segments, full, Chars, sep).Should().Equal(segments.Select(s => s.Full));

        IReadOnlyList<string?> tight = FooterLine.Choose(segments, full - 1, Chars, sep);
        tight[0].Should().Be("20 llamadas");
        tight[1].Should().Be(segments[1].Full, "el coste no cede mientras los tokens puedan ceder");
        tight[2].Should().Be(segments[2].Candidates[1], "los tokens pasan a su total");

        IReadOnlyList<string?> tighter = FooterLine.Choose(segments, 70, Chars, sep);
        tighter[2].Should().BeNull("sin sitio ni para el total, los tokens se retiran antes de tocar el coste");
        tighter[1].Should().Be(segments[1].Full);

        IReadOnlyList<string?> minimal = FooterLine.Choose(segments, 35, Chars, sep);
        minimal[0].Should().Be("20 llamadas");
        minimal[1].Should().Be("coste: suscripción");
        minimal[2].Should().BeNull("agotadas sus formas, los tokens se retiran: siguen en el tooltip y en el informe");

        IReadOnlyList<string?> impossible = FooterLine.Choose(segments, 5, Chars, sep);
        impossible[0].Should().Be("20 llamadas", "lo que no cede se pinta entero aunque no quepa: nunca cortado");
    }

    // ================================================================ y medido de verdad

    /// <summary>Los anchos del área de página (D-819): 1366 → 1124, 900 → 658, media pantalla → 441.</summary>
    public static TheoryData<string, double> Widths => new()
    {
        { "claude", 1124 }, { "claude", 658 }, { "claude", 441 },
        { "copilot", 1124 }, { "copilot", 658 }, { "copilot", 441 },
    };

    private static IReadOnlyList<FooterSegment> SessionFooterOf(string provider)
    {
        var segments = new List<FooterSegment>
        {
            FooterSegment.Of("Unidad 12 de 40", bold: true),
            FooterSegment.Of("1h 05m 09s", opacity: 0.85),
        };
        segments.AddRange(provider == "claude" ? Claude() : Copilot());
        if (provider == "copilot")
        {
            segments.Add(FooterSegment.Of("media 3,41/unidad", priority: 3, opacity: 0.7));
        }

        return segments;
    }

    /// <summary>
    /// El pie real, a cada ancho: lo que se pinta cabe, cada trozo es una forma entera, y las
    /// llamadas están siempre. A 1124 con Claude cabe todo; a 441 los tokens ya se han ido.
    /// </summary>
    [Theory]
    [MemberData(nameof(Widths))]
    public void Lo_que_se_pinta_cabe_y_es_una_forma_entera(string provider, double width)
        => ViewLayout.OnUiThread(() =>
        {
            IReadOnlyList<FooterSegment> segments = SessionFooterOf(provider);
            var line = new FooterLine { Segments = segments };
            var host = new Border { Padding = new Thickness(12, 7, 12, 7), Child = line };

            ViewLayout.Layout(host, width, 40);

            line.DesiredSize.Width.Should().BeLessThanOrEqualTo(width - 24 + 0.5,
                $"{provider} a {width}px: el pie no puede salirse de su caja");
            line.Chosen.Should().HaveCount(segments.Count);
            for (int i = 0; i < segments.Count; i++)
            {
                if (line.Chosen[i] is { } text)
                {
                    segments[i].Candidates.Should().Contain(text, "cada trozo pintado es una forma entera, no un recorte");
                }
            }

            line.Chosen[0].Should().Be("Unidad 12 de 40");
            line.Chosen[2].Should().Be("20 llamadas", "las llamadas no ceden nunca");
            line.Chosen[3].Should().NotBeNull("el coste se abrevia pero no desaparece");
            line.FullText.Should().Contain("28.050 entrada", "el detalle entero está en el tooltip");
            line.ToolTip.Should().Be(line.FullText);
        });

    /// <summary>A 1366×768 (1124 de página) el pie de las dos casas se lee ENTERO, sin abreviar nada.</summary>
    [Theory]
    [InlineData("claude")]
    [InlineData("copilot")]
    public void En_el_portatil_de_la_casa_cabe_entero(string provider)
        => ViewLayout.OnUiThread(() =>
        {
            IReadOnlyList<FooterSegment> segments = SessionFooterOf(provider);
            var line = new FooterLine { Segments = segments };
            ViewLayout.Layout(line, 1100, 40);

            line.Chosen.Should().Equal(segments.Select(s => s.Full));
        });

    /// <summary>Con la ventana a la mitad (441) ceden los tokens antes que el coste, y nada se corta.</summary>
    [Theory]
    [InlineData("claude")]
    [InlineData("copilot")]
    public void A_media_pantalla_ceden_los_tokens_antes_que_el_coste(string provider)
        => ViewLayout.OnUiThread(() =>
        {
            IReadOnlyList<FooterSegment> segments = SessionFooterOf(provider);
            var line = new FooterLine { Segments = segments };
            ViewLayout.Layout(line, 441 - 24, 40);

            line.Chosen[4].Should().NotBe(segments[4].Full, "los tokens desglosados no caben a media pantalla");
            line.Chosen[3].Should().NotBeNull();
            if (line.Chosen[3] != segments[3].Full)
            {
                line.Chosen[4].Should().BeNull("el coste solo se abrevia cuando los tokens ya han cedido del todo");
            }
        });
}
