using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Atalaya.App.Controls;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F17.1 — LA CINTA, MEDIDA: la fila es indivisible, la vista arranca en el presente, y ningún
/// texto se corta sin remedio.
/// <para>
/// <b>El defecto que lo motiva (A).</b> En F17 el nombre de cada aplicación vivía en el mismo
/// lienzo que las bandas, y al desplazar la cinta los nombres y los tramos se movían por separado:
/// XBLAST, sin ningún ciclo, acababa junto al único tramo existente —de AtalayaBanco— y la gráfica
/// afirmaba que ese ciclo era suyo. Una gráfica que atribuye auditorías a quien no las hizo es
/// peor que no tenerla. Aquí se comprueba con el scroll APLICADO y con datos desiguales —una banda
/// con tramos y otra sin ninguno—, que es donde la correspondencia se rompe.
/// </para>
/// </summary>
public sealed class CycleRibbonLayoutTests
{
    private static readonly DateTime To = new(2026, 9, 3);
    private static readonly DateTime From = To.AddDays(-56);

    private static RibbonSlice Slice(Brush fill, DateTime a, DateTime b)
        => new(fill, a, b, new[] { "trozo" });

    private static RibbonSpan Span(string label, DateTime a, DateTime b, bool open = false, params RibbonSlice[] slices)
        => new(label, label.Split(' ')[0], slices.Length == 0 ? new[] { Slice(Brushes.SlateGray, a, b) } : slices,
            a, b, open, true, new[] { label });

    /// <summary>Datos DESIGUALES a propósito: una banda con tramos, una vacía, y otra con historia larga.</summary>
    private static IReadOnlyList<RibbonTrack> Uneven() => new[]
    {
        new RibbonTrack("XBLAST", Array.Empty<RibbonSpan>()),
        new RibbonTrack("AtalayaBanco", new[] { Span("C1 · Rendimiento → Seguridad", To.AddHours(-3), To, open: true,
            Slice(Brushes.Teal, To.AddHours(-3), To.AddHours(-1)), Slice(Brushes.Purple, To.AddHours(-1), To)) }),
        new RibbonTrack("Un nombre de aplicación larguísimo que no cabe en la columna", new[]
        {
            Span("C1 · General", From.AddDays(-20), From.AddDays(10)),
            Span("C2 · General", From.AddDays(10), From.AddDays(30)),
            Span("C3 · Fiabilidad", From.AddDays(30), To, open: true),
        }),
    };

    private static CycleRibbon Build(IReadOnlyList<RibbonTrack> tracks, double width)
    {
        var ribbon = new CycleRibbon { Tracks = tracks, From = From, To = To };
        ViewLayout.Layout(ribbon, width, 300);
        return ribbon;
    }

    /// <summary>Los anchos de página de la casa (D-819), y uno estrecho de más.</summary>
    public static TheoryData<double> Widths => new() { 1124, 658, 441, 320 };

    // ================================================================ A · la fila es indivisible

    [Theory]
    [MemberData(nameof(Widths))]
    public void Cada_nombre_queda_a_la_altura_de_su_banda_a_cualquier_ancho(double width)
        => ViewLayout.OnUiThread(() =>
        {
            CycleRibbon ribbon = Build(Uneven(), width);

            AssertRowsAligned(ribbon);
        });

    /// <summary>
    /// La comprobación que faltaba en F17: con el desplazamiento APLICADO. Los nombres están fuera
    /// del área que se mueve, así que no se mueven; y cada tramo sigue en la fila de su banda.
    /// </summary>
    [Fact]
    public void Con_el_desplazamiento_aplicado_los_nombres_no_se_mueven_y_cada_tramo_sigue_en_su_fila()
        => ViewLayout.OnUiThread(() =>
        {
            // Dos ciclos de una hora seguidos fuerzan la escala: la cinta es más ancha que la ventana.
            var packed = new[]
            {
                new RibbonTrack("Vacía", Array.Empty<RibbonSpan>()),
                new RibbonTrack("Apretada", new[] { Span("C1 · General", To.AddHours(-2), To.AddHours(-1)), Span("C2 · General", To.AddHours(-1), To, open: true) }),
            };
            CycleRibbon ribbon = Build(packed, 700);
            ribbon.Scroll.ScrollableWidth.Should().BeGreaterThan(0, "el caso solo prueba algo si hay algo que desplazar");
            Rect nameBefore = ViewLayout.BoxOf(ribbon.NameLabels[1], ribbon);

            ribbon.Scroll.ScrollToHorizontalOffset(ribbon.Scroll.ScrollableWidth / 2);
            ribbon.UpdateLayout();

            ViewLayout.BoxOf(ribbon.NameLabels[1], ribbon).Should().Be(nameBefore, "el nombre no se desplaza: vive en la columna fija");
            AssertRowsAligned(ribbon);
            ribbon.SpanShapes.Should().OnlyContain(s => s.Row == 1, "ningún tramo puede caer en la fila vacía");
        });

    /// <summary>Y la fila vacía existe, se rotula, y está a la altura de SU nombre — no la ocupa nadie.</summary>
    [Theory]
    [MemberData(nameof(Widths))]
    public void La_aplicacion_sin_ciclos_tiene_su_fila_rotulada_y_nadie_la_ocupa(double width)
        => ViewLayout.OnUiThread(() =>
        {
            CycleRibbon ribbon = Build(Uneven(), width);

            (int row, TextBlock text) = ribbon.EmptyLabels.Should().ContainSingle().Subject;
            row.Should().Be(0, "XBLAST es la primera banda");
            text.Text.Should().Be("sin ciclos en este periodo");
            Rect name = ViewLayout.BoxOf(ribbon.NameLabels[0], ribbon);
            Rect empty = ViewLayout.BoxOf(text, ribbon);
            Math.Abs(name.Top + name.Height / 2 - (empty.Top + empty.Height / 2)).Should().BeLessThan(1.5);
            ribbon.SpanShapes.Should().NotContain(s => s.Row == 0);
        });

    // ================================================================ C · arranca en el presente

    [Fact]
    public void La_vista_arranca_en_el_final_del_eje_y_respeta_a_quien_retrocede()
        => ViewLayout.OnUiThread(() =>
        {
            var packed = new[]
            {
                new RibbonTrack("Apretada", new[] { Span("C1 · General", To.AddHours(-2), To.AddHours(-1)), Span("C2 · General", To.AddHours(-1), To, open: true) }),
            };
            CycleRibbon ribbon = Build(packed, 700);

            ribbon.Scroll.HorizontalOffset.Should().BeApproximately(ribbon.Scroll.ScrollableWidth, 0.5, "hoy está a la derecha, y es lo que importa");

            ribbon.Scroll.ScrollToHorizontalOffset(0);
            ribbon.UpdateLayout();
            ViewLayout.Layout(ribbon, 800, 300); // un cambio de tamaño no es un cambio de datos

            ribbon.Scroll.HorizontalOffset.Should().Be(0, "quien retrocedió se queda donde estaba");
        });

    [Fact]
    public void Con_un_solo_ciclo_corto_la_banda_se_ve_sin_buscarla()
        => ViewLayout.OnUiThread(() =>
        {
            var single = new[] { new RibbonTrack("App", new[] { Span("C1 · General", To.AddHours(-2), To, open: true) }) };
            CycleRibbon ribbon = Build(single, 700);

            ribbon.Scroll.ScrollableWidth.Should().Be(0, "cabe en la tarjeta: nada que desplazar");
            (_, _, Rectangle shape) = ribbon.SpanShapes.Should().ContainSingle().Subject;
            shape.Width.Should().BeGreaterThanOrEqualTo(28, "un ciclo de horas se pinta con el ancho mínimo, no con dos píxeles");
            Rect box = ViewLayout.BoxOf(shape, ribbon);
            box.Right.Should().BeLessThanOrEqualTo(ribbon.ActualWidth + 0.5, "y se ve entero, sin buscarlo");
        });

    // ================================================================ D · sin truncados mudos

    [Fact]
    public void Un_nombre_que_no_cabe_va_con_elipsis_media_y_el_nombre_entero_en_el_tooltip()
        => ViewLayout.OnUiThread(() =>
        {
            CycleRibbon ribbon = Build(Uneven(), 900);
            TextBlock label = ribbon.NameLabels[2];

            label.Text.Should().Contain("…").And.NotBe(Uneven()[2].Name);
            label.Text.Should().StartWith("Un nom").And.EndWith("columna", "el final de un nombre es lo que lo distingue");
            label.ToolTip.Should().Be(Uneven()[2].Name);
            label.DesiredSize.Width.Should().BeLessThanOrEqualTo(ribbon.Names.Width);
            ribbon.NameLabels[1].Text.Should().Be("AtalayaBanco", "lo que cabe va entero");
        });

    [Fact]
    public void La_elipsis_media_conserva_principio_y_final()
    {
        CycleRibbon.MiddleEllipsis("AtalayaBanco", 20).Should().Be("AtalayaBanco");
        CycleRibbon.MiddleEllipsis("AtalayaBanco", 8).Should().Be("Atal…nco");
        CycleRibbon.MiddleEllipsis("AtalayaBanco", 3).Should().Be("A…o");
    }

    // ================================================================ B · el tramo partido

    [Fact]
    public void Un_ciclo_con_dos_lupas_se_pinta_en_dos_trozos_con_el_corte_en_la_fecha_del_cambio()
        => ViewLayout.OnUiThread(() =>
        {
            var tracks = new[]
            {
                new RibbonTrack("App", new[]
                {
                    Span("C1 · Rendimiento → Seguridad", From.AddDays(10), From.AddDays(40), open: false,
                        Slice(Brushes.Teal, From.AddDays(10), From.AddDays(20)),
                        Slice(Brushes.Purple, From.AddDays(20), From.AddDays(40))),
                }),
            };
            CycleRibbon ribbon = Build(tracks, 1000);

            ribbon.SpanShapes.Should().HaveCount(2, "dos trozos, uno por temática");
            Rect first = ViewLayout.BoxOf(ribbon.SpanShapes[0].Shape, ribbon);
            Rect second = ViewLayout.BoxOf(ribbon.SpanShapes[1].Shape, ribbon);
            ribbon.SpanShapes[0].Shape.Fill.Should().BeSameAs(Brushes.Teal);
            ribbon.SpanShapes[1].Shape.Fill.Should().BeSameAs(Brushes.Purple);
            first.Right.Should().BeApproximately(second.Left, 0.5, "el corte es un solo punto");
            (first.Width / (first.Width + second.Width)).Should().BeApproximately(10.0 / 30.0, 0.02, "el corte está en la fecha del cambio");
            first.Top.Should().Be(second.Top);
        });

    [Fact]
    public void Un_ciclo_con_una_lupa_es_un_solo_trozo_identico_a_hoy()
        => ViewLayout.OnUiThread(() =>
        {
            var tracks = new[] { new RibbonTrack("App", new[] { Span("C1 · General", From.AddDays(10), From.AddDays(40)) }) };
            CycleRibbon ribbon = Build(tracks, 1000);

            ribbon.SpanShapes.Should().ContainSingle();
        });

    // ================================================================ utilidades

    /// <summary>El centro vertical de cada nombre coincide con el de cada tramo (y cada rótulo de vacío) de su fila.</summary>
    private static void AssertRowsAligned(CycleRibbon ribbon)
    {
        for (int row = 0; row < ribbon.NameLabels.Count; row++)
        {
            Rect name = ViewLayout.BoxOf(ribbon.NameLabels[row], ribbon);
            double centre = name.Top + name.Height / 2;
            double rowCentre = CycleRibbon.RowTop(row) + CycleRibbon.RowHeight / 2;
            Math.Abs(centre - rowCentre).Should().BeLessThan(1.5, $"el nombre de la fila {row} va centrado en su fila");

            foreach ((int r, _, Rectangle shape) in ribbon.SpanShapes.Where(s => s.Row == row))
            {
                Rect box = ViewLayout.BoxOf(shape, ribbon);
                Math.Abs(box.Top + box.Height / 2 - centre).Should().BeLessThan(1.5,
                    $"el tramo de la fila {r} tiene que estar a la altura de su nombre, no del de otra app");
            }
        }
    }
}
