using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Atalaya.App.Controls;
using Atalaya.App.Services;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// R6 §7 y §8 — <b>dos reglas nuevas</b>, y solo dos (N-7).
/// <para>
/// La primera: <b>ningún botón ni enlace del sistema se queda sin estados</b>. La sesión en vivo y
/// el arreglo asistido usaban los botones de la librería, que no reciben el estilo implícito de la
/// casa, y ahí no pasaba nada al pasar el ratón ni al pulsar. Se arregla cambiándolos, pero lo que
/// impide que vuelva a pasar es exigirle a cada estilo de la casa que declare hover y pulsado —de
/// lo contrario, el día que alguien escriba una variante nueva partiendo de una plantilla propia,
/// nadie se enterará hasta verla en el dist.
/// </para>
/// <para>
/// La segunda: <b>un coste se escribe por un solo sitio</b>. La gráfica de coste no pasaba por
/// <see cref="CostFormat"/> y, con la divisa en dólares, decía «490 $» donde la tarjeta del mismo
/// periodo decía «4,91 $». Es exactamente lo que F29 §2 quiso impedir con un único formateador.
/// </para>
/// </summary>
[Collection(CurrencyCollection.Name)]
public sealed class ButtonStatesTests : IDisposable
{
    private readonly CostCurrency _previous = CostFormat.Currency;

    public void Dispose() => CostFormat.Currency = _previous;

    // ================================================================ §7 · los estados

    /// <summary>
    /// Todos los estilos de botón y de enlace del sistema declaran <c>IsMouseOver</c> e
    /// <c>IsPressed</c>. Se resuelven de verdad —siguiendo <c>BasedOn</c> hasta la plantilla que
    /// cada uno acaba usando—, que es lo único que dice si una variante hereda los estados o se ha
    /// escrito una plantilla propia y se los ha dejado por el camino.
    /// </summary>
    [Fact]
    public void Ningun_boton_ni_enlace_del_sistema_se_queda_sin_hover_y_sin_pulsado()
    {
        var sinEstados = new List<string>();

        ViewLayout.OnUiThread(() =>
        {
            ResourceDictionary styles = LoadStyles();

            foreach (object key in styles.Keys)
            {
                if (styles[key] is not Style style || style.TargetType != typeof(Button))
                {
                    continue;
                }

                ControlTemplate? template = TemplateOf(style);
                if (template is null)
                {
                    sinEstados.Add($"{key}: sin plantilla");
                    continue;
                }

                var properties = template.Triggers
                    .OfType<Trigger>()
                    .Select(t => t.Property)
                    .ToList();

                if (!properties.Contains(UIElement.IsMouseOverProperty))
                {
                    sinEstados.Add($"{key}: sin hover");
                }

                if (!properties.Contains(ButtonBase.IsPressedProperty))
                {
                    sinEstados.Add($"{key}: sin pulsado");
                }
            }
        });

        sinEstados.Should().BeEmpty(
            "un control que no acusa el ratón ni el clic no parece pulsable, y eso no se ve en "
            + "verde: se ve abriendo la pantalla");
    }

    /// <summary>
    /// Y el estilo IMPLÍCITO existe, que es lo que hace que un <c>&lt;Button&gt;</c> escrito sin
    /// estilo no salga de fábrica. Sin él, la regla de arriba pasaría en verde mientras media
    /// aplicación usa los botones de Windows.
    /// </summary>
    [Fact]
    public void Un_boton_sin_estilo_recibe_el_del_sistema()
    {
        ViewLayout.OnUiThread(() =>
        {
            ResourceDictionary styles = LoadStyles();
            styles[typeof(Button)].Should().BeOfType<Style>(
                "el estilo implícito de Button es lo que viste a los que nadie estiló");
        });
    }

    // ================================================================ §8 · un solo formateador

    /// <summary>
    /// <b>La gráfica de coste escribe lo mismo que la tarjeta.</b> Se pinta de verdad con una serie
    /// conocida y se leen sus marcas del eje: en credits y en dólares tienen que salir por
    /// <see cref="CostFormat"/>, no de un formato numérico suelto con la unidad pegada detrás.
    /// </summary>
    [Theory]
    [InlineData(CostCurrency.Credits)]
    [InlineData(CostCurrency.Usd)]
    public void El_eje_de_la_grafica_de_coste_dice_lo_mismo_que_CostFormat(CostCurrency currency)
    {
        var marcas = new List<string>();

        ViewLayout.OnUiThread(() =>
        {
            CostFormat.Currency = currency;

            var plot = new ChartPlot
            {
                IsCost = true,
                Width = 600,
                Height = 240,
                AxisBrush = Brushes.Gray,
                GridBrush = Brushes.Gray,
                Labels = new[] { "L" },
                Series = new[]
                {
                    new ChartSeries("app", "App", Brushes.Blue, new[] { 400d }, ChartSeriesKind.Line, false),
                },
            };

            plot.Measure(new Size(600, 240));
            plot.Arrange(new Rect(0, 0, 600, 240));
            plot.UpdateLayout();

            marcas.AddRange(plot.Children.OfType<TextBlock>().Select(t => t.Text));
        });

        // Las marcas del eje de una serie que llega a 400 credits: 0, 100, 200, 300 y 400.
        foreach (decimal tick in new[] { 100m, 200m, 400m })
        {
            marcas.Should().Contain(CostFormat.Tick(tick),
                $"la marca de {tick} credits la escribe CostFormat, no la gráfica");
        }

        if (currency == CostCurrency.Usd)
        {
            marcas.Should().NotContain("400",
                "con la divisa en dólares, 400 credits son 4,00 $ — pintar el 400 tal cual es "
                + "multiplicar por cien lo que dice la tarjeta del periodo");
        }
    }

    /// <summary>
    /// Y las gráficas que NO son de coste siguen contando cosas: un recuento de hallazgos no se
    /// convierte a dólares por estar en la misma pantalla.
    /// </summary>
    [Fact]
    public void Una_grafica_que_no_es_de_coste_no_convierte_nada()
    {
        var marcas = new List<string>();

        ViewLayout.OnUiThread(() =>
        {
            CostFormat.Currency = CostCurrency.Usd;

            var plot = new ChartPlot
            {
                ValueFormat = "0",
                Width = 600,
                Height = 240,
                AxisBrush = Brushes.Gray,
                GridBrush = Brushes.Gray,
                Labels = new[] { "L" },
                Series = new[]
                {
                    new ChartSeries("a", "A", Brushes.Blue, new[] { 400d }, ChartSeriesKind.Line, false),
                },
            };

            plot.Measure(new Size(600, 240));
            plot.Arrange(new Rect(0, 0, 600, 240));
            plot.UpdateLayout();

            marcas.AddRange(plot.Children.OfType<TextBlock>().Select(t => t.Text));
        });

        marcas.Should().Contain("400", "400 hallazgos son 400, no 4,00");
    }

    // ================================================================ andamiaje

    private static ResourceDictionary LoadStyles()
    {
        var dictionaries = new ResourceDictionary();
        foreach (string src in new[]
                 {
                     "Themes/Converters.xaml", "Themes/Tokens.xaml", "Themes/Palette.Dark.xaml",
                     "Themes/Styles.xaml",
                 })
        {
            dictionaries.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Atalaya;component/{src}", UriKind.Absolute),
            });
        }

        // Los estilos viven en el ÚLTIMO diccionario; los demás son sus tokens y su paleta.
        return dictionaries.MergedDictionaries[^1];
    }

    /// <summary>La plantilla que un estilo acaba usando, siguiendo <c>BasedOn</c> hacia arriba.</summary>
    private static ControlTemplate? TemplateOf(Style? style)
    {
        for (Style? s = style; s is not null; s = s.BasedOn)
        {
            foreach (SetterBase setter in s.Setters)
            {
                if (setter is Setter { Property.Name: "Template", Value: ControlTemplate template })
                {
                    return template;
                }
            }
        }

        return null;
    }
}
