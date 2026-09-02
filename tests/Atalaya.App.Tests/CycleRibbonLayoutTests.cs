using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Atalaya.App.Controls;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F17.2 — LA SECUENCIA DE CICLOS, MEDIDA: capítulos en orden, de ancho fijo, con sus fechas y
/// sus huecos contados; la fila indivisible de F17.1 se conserva.
/// <para>
/// Con datos DESIGUALES a propósito (D-843): una app con varios ciclos y huecos, una con uno solo,
/// una sin ninguno, y un ciclo con dos temáticas. Es donde se rompe la correspondencia, y es donde
/// se mide.
/// </para>
/// </summary>
public sealed class CycleRibbonLayoutTests
{
    private static readonly DateTime Today = new(2026, 9, 2, 12, 0, 0);

    private static RibbonSlice Slice(Brush fill, DateTime a, DateTime b) => new(fill, a, b, new[] { "trozo" });

    private static RibbonSpan Span(string label, DateTime a, DateTime b, bool open = false, string dates = "14 ago – 2 sept", params RibbonSlice[] slices)
        => new(label, dates, slices.Length == 0 ? new[] { Slice(Brushes.SlateGray, a, b) } : slices, a, b, open, true, new[] { label });

    /// <summary>Varios ciclos con un hueco grande entre el segundo y el tercero.</summary>
    private static RibbonTrack Several() => new("Voladura", new[]
    {
        Span("C1 · General", Today.AddDays(-120), Today.AddDays(-100)),
        Span("C2 · Fiabilidad", Today.AddDays(-99), Today.AddDays(-80)),
        Span("C3 · Mantenibilidad", Today.AddDays(-30), Today, open: true, dates: "3 ago – en curso"),
    });

    private static RibbonTrack Single() => new("AtalayaBanco", new[]
    {
        Span("C1 · Rendimiento → Seguridad", Today.AddHours(-3), Today, open: true, dates: "2 sept – en curso",
            Slice(Brushes.Teal, Today.AddHours(-3), Today.AddHours(-1)), Slice(Brushes.Purple, Today.AddHours(-1), Today)),
    });

    private static RibbonTrack None() => new("XBLAST", Array.Empty<RibbonSpan>());

    private static IReadOnlyList<RibbonTrack> Uneven() => new[] { Several(), Single(), None() };

    private static CycleRibbon Build(IReadOnlyList<RibbonTrack> tracks, double width)
    {
        var ribbon = new CycleRibbon { Tracks = tracks };
        ViewLayout.Layout(ribbon, width, 400);
        return ribbon;
    }

    /// <summary>Los anchos de página de la casa (D-819), y uno estrecho de más.</summary>
    public static TheoryData<double> Widths => new() { 1124, 658, 441, 320 };

    // ================================================================ orden y ancho fijo

    [Theory]
    [MemberData(nameof(Widths))]
    public void Los_ciclos_van_en_orden_como_bloques_del_mismo_ancho(double width)
        => ViewLayout.OnUiThread(() =>
        {
            CycleRibbon ribbon = Build(Uneven(), width);

            var blocks = ribbon.SpanShapes.Where(s => s.Row == 0).GroupBy(s => s.Span)
                .Select(g => (Span: g.Key, Left: g.Min(s => ViewLayout.BoxOf(s.Shape, ribbon.Plot).Left), Right: g.Max(s => ViewLayout.BoxOf(s.Shape, ribbon.Plot).Right)))
                .OrderBy(b => b.Left)
                .ToList();

            blocks.Select(b => b.Span.Label).Should().Equal("C1 · General", "C2 · Fiabilidad", "C3 · Mantenibilidad"); // en orden, uno tras otro
            blocks.Should().OnlyContain(b => Math.Abs(b.Right - b.Left - CycleRibbon.BlockWidth) < 0.5,
                "ancho legible fijo: un ciclo de horas y uno de semanas cuentan lo mismo como capítulo");
            for (int i = 1; i < blocks.Count; i++)
            {
                blocks[i].Left.Should().BeGreaterThan(blocks[i - 1].Right, "no se solapan");
            }
        });

    /// <summary>La fila indivisible de F17.1, conservada: nombre y bloques a la misma altura, con y sin scroll.</summary>
    [Theory]
    [MemberData(nameof(Widths))]
    public void Cada_nombre_queda_a_la_altura_de_su_fila(double width)
        => ViewLayout.OnUiThread(() =>
        {
            CycleRibbon ribbon = Build(Uneven(), width);
            if (ribbon.Scroll.ScrollableWidth > 0)
            {
                ribbon.Scroll.ScrollToHorizontalOffset(ribbon.Scroll.ScrollableWidth / 2);
                ribbon.UpdateLayout();
            }

            for (int row = 0; row < ribbon.NameLabels.Count; row++)
            {
                Rect name = ViewLayout.BoxOf(ribbon.NameLabels[row], ribbon);
                double centre = name.Top + name.Height / 2;
                Math.Abs(centre - (CycleRibbon.RowTop(row) + CycleRibbon.RowHeight / 2)).Should().BeLessThan(1.5);
                foreach ((int r, _, Rectangle shape) in ribbon.SpanShapes.Where(s => s.Row == row))
                {
                    Rect box = ViewLayout.BoxOf(shape, ribbon);
                    Math.Abs(box.Top + box.Height / 2 - centre).Should().BeLessThan(1.5, $"el bloque de la fila {r} va a la altura de su nombre");
                }
            }

            ribbon.SpanShapes.Should().NotContain(s => s.Row == 2, "XBLAST no tiene bloques y nadie ocupa su fila");
        });

    // ================================================================ el bloque partido

    [Fact]
    public void Un_ciclo_con_dos_lupas_se_parte_en_los_colores_y_proporciones_de_sus_periodos()
        => ViewLayout.OnUiThread(() =>
        {
            CycleRibbon ribbon = Build(new[] { Single() }, 900);

            var parts = ribbon.SpanShapes.Where(s => s.Row == 0).ToList();
            parts.Should().HaveCount(2);
            parts[0].Shape.Fill.Should().BeSameAs(Brushes.Teal);
            Rect first = ViewLayout.BoxOf(parts[0].Shape, ribbon.Plot);
            Rect second = ViewLayout.BoxOf(parts[1].Shape, ribbon.Plot);
            first.Right.Should().BeApproximately(second.Left, 0.5, "el corte es un solo punto");
            (first.Width / (first.Width + second.Width)).Should().BeApproximately(2.0 / 3.0, 0.03, "dos horas de tres");
            (first.Width + second.Width).Should().BeApproximately(CycleRibbon.BlockWidth, 0.5);
            ribbon.BlockLabels.Single().Label.Text.Should().Be("C1 · Rendimiento → Seguridad");
        });

    [Fact]
    public void Un_ciclo_con_una_lupa_es_un_solo_bloque()
        => ViewLayout.OnUiThread(() =>
        {
            CycleRibbon ribbon = Build(new[] { new RibbonTrack("App", new[] { Span("C1 · General", Today.AddDays(-9), Today.AddDays(-2)) }) }, 900);

            ribbon.SpanShapes.Should().ContainSingle().Which.Shape.Width.Should().BeApproximately(CycleRibbon.BlockWidth, 0.5);
        });

    // ================================================================ los huecos se cuentan

    [Fact]
    public void El_hueco_por_encima_del_umbral_se_escribe_y_por_debajo_no()
        => ViewLayout.OnUiThread(() =>
        {
            CycleRibbon ribbon = Build(new[] { Several() }, 1200);

            (int row, TextBlock text) = ribbon.GapLabels.Should().ContainSingle("solo entre C2 y C3 hay más de una semana").Subject;
            row.Should().Be(0);
            text.Text.Should().Be("7 semanas sin auditar");

            // Y el separador va ENTRE los dos bloques que separa.
            double c2Right = ribbon.SpanShapes.Where(s => s.Span.Label == "C2 · Fiabilidad").Max(s => ViewLayout.BoxOf(s.Shape, ribbon.Plot).Right);
            double c3Left = ribbon.SpanShapes.Where(s => s.Span.Label == "C3 · Mantenibilidad").Min(s => ViewLayout.BoxOf(s.Shape, ribbon.Plot).Left);
            Rect gap = ViewLayout.BoxOf(text, ribbon.Plot);
            gap.Left.Should().BeGreaterThan(c2Right);
            gap.Right.Should().BeLessThan(c3Left);
        });

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, null)]
    [InlineData(6, null)]
    [InlineData(7, "7 días sin auditar")]
    [InlineData(13, "13 días sin auditar")]
    [InlineData(21, "3 semanas sin auditar")]
    [InlineData(60, "9 semanas sin auditar")]
    [InlineData(61, "2 meses sin auditar")]
    [InlineData(200, "7 meses sin auditar")]
    public void El_hueco_se_dice_en_la_unidad_que_se_lee_de_un_vistazo(int days, string? expected)
        => CycleRibbon.GapText(Today, Today.AddDays(days)).Should().Be(expected);

    [Fact]
    public void El_umbral_del_separador_es_una_semana()
        => CycleRibbon.GapThreshold.Should().Be(TimeSpan.FromDays(7));

    // ================================================================ arranque, fila vacía, aviso

    [Fact]
    public void Con_muchos_ciclos_la_vista_arranca_por_el_final_y_respeta_a_quien_retrocede()
        => ViewLayout.OnUiThread(() =>
        {
            var many = new RibbonTrack("Larga", Enumerable.Range(1, 12)
                .Select(n => Span($"C{n} · General", Today.AddDays(-13 * (13 - n)), Today.AddDays(-13 * (12 - n)), open: n == 12))
                .ToList());
            CycleRibbon ribbon = Build(new[] { many }, 700);

            ribbon.Scroll.ScrollableWidth.Should().BeGreaterThan(0, "doce bloques no caben en 700 px");
            ribbon.Scroll.HorizontalOffset.Should().BeApproximately(ribbon.Scroll.ScrollableWidth, 0.5, "el ciclo más reciente es lo relevante");

            ribbon.Scroll.ScrollToHorizontalOffset(0);
            ribbon.UpdateLayout();
            ViewLayout.Layout(ribbon, 800, 400); // un cambio de tamaño no es un cambio de datos

            ribbon.Scroll.HorizontalOffset.Should().Be(0, "quien retrocedió se queda donde estaba");
        });

    /// <summary>
    /// Las filas se alinean por el FINAL: el último capítulo de cada aplicación queda en el mismo
    /// borde derecho. Así, al arrancar por el final, se ve el ciclo más reciente de TODAS las
    /// filas —y la fila vacía con su rótulo—, no solo la cola de la fila más larga.
    /// </summary>
    [Fact]
    public void Las_filas_se_alinean_por_el_final_y_al_arrancar_se_ve_el_ultimo_capitulo_de_todas()
        => ViewLayout.OnUiThread(() =>
        {
            CycleRibbon ribbon = Build(Uneven(), 441);

            double lastOfSeveral = ribbon.SpanShapes.Where(s => s.Row == 0).Max(s => ViewLayout.BoxOf(s.Shape, ribbon.Plot).Right);
            double lastOfSingle = ribbon.SpanShapes.Where(s => s.Row == 1).Max(s => ViewLayout.BoxOf(s.Shape, ribbon.Plot).Right);
            double emptyRight = ViewLayout.BoxOf(ribbon.EmptyLabels.Single().Text, ribbon.Plot).Right;
            lastOfSingle.Should().BeApproximately(lastOfSeveral, 0.5);
            emptyRight.Should().BeApproximately(lastOfSeveral, 8, "el rótulo en cursiva mide unos píxeles distinto de su medida en redonda");

            ribbon.Scroll.ScrollableWidth.Should().BeGreaterThan(0, "tres bloques y dos huecos no caben en 441 px");
            double viewLeft = ribbon.Scroll.HorizontalOffset;
            double viewRight = viewLeft + ribbon.Scroll.ViewportWidth;
            foreach (Rectangle shape in new[] { ribbon.SpanShapes.Last(s => s.Row == 0).Shape, ribbon.SpanShapes.Last(s => s.Row == 1).Shape })
            {
                Rect box = ViewLayout.BoxOf(shape, ribbon.Plot);
                box.Right.Should().BeLessThanOrEqualTo(viewRight + 0.5).And.BeGreaterThan(viewLeft, "el último capítulo de cada fila está a la vista al arrancar");
            }

            emptyRight.Should().BeLessThanOrEqualTo(viewRight + 0.5).And.BeGreaterThan(viewLeft, "y la fila vacía dice lo suyo sin desplazar nada");
        });

    [Theory]
    [MemberData(nameof(Widths))]
    public void La_aplicacion_sin_ciclos_tiene_su_fila_rotulada(double width)
        => ViewLayout.OnUiThread(() =>
        {
            CycleRibbon ribbon = Build(Uneven(), width);

            (int row, TextBlock text) = ribbon.EmptyLabels.Should().ContainSingle().Subject;
            row.Should().Be(2);
            text.Text.Should().Be("sin ciclos en este periodo");
            Rect name = ViewLayout.BoxOf(ribbon.NameLabels[2], ribbon);
            Rect empty = ViewLayout.BoxOf(text, ribbon);
            Math.Abs(name.Top + name.Height / 2 - (empty.Top + empty.Height / 2)).Should().BeLessThan(1.5);
        });

    [Fact]
    public void Lo_que_el_periodo_deja_fuera_se_dice_al_principio_de_la_fila()
        => ViewLayout.OnUiThread(() =>
        {
            var track = new RibbonTrack("App", new[] { Span("C3 · General", Today.AddDays(-5), Today, open: true) }, Notice: "2 ciclos anteriores fuera del periodo");
            CycleRibbon ribbon = Build(new[] { track }, 900);

            (int row, TextBlock text) = ribbon.NoticeLabels.Should().ContainSingle().Subject;
            row.Should().Be(0);
            text.Text.Should().Be("2 ciclos anteriores fuera del periodo");
            ViewLayout.BoxOf(text, ribbon.Plot).Right.Should().BeLessThan(ViewLayout.BoxOf(ribbon.SpanShapes.Single().Shape, ribbon.Plot).Left);
        });

    // ================================================================ sin truncados mudos

    [Fact]
    public void El_identificador_y_la_tematica_siempre_se_leen_y_las_fechas_caen_primero()
        => ViewLayout.OnUiThread(() =>
        {
            var track = new RibbonTrack("App", new[]
            {
                Span("C1 · General", Today.AddDays(-9), Today.AddDays(-2), dates: "24 ago – 31 ago"),
                Span("C12 · Rendimiento → Concurrencia y asincronía", Today.AddDays(-1), Today, open: true, dates: "1 sept – en curso"),
            });
            CycleRibbon ribbon = Build(new[] { track }, 900);

            (RibbonSpan _, TextBlock shortLabel, TextBlock? shortDates) = ribbon.BlockLabels[0];
            shortLabel.Text.Should().Be("C1 · General");
            shortDates.Should().NotBeNull("cabe: rótulo y fechas");
            shortDates!.Text.Should().Be("24 ago – 31 ago");

            (RibbonSpan longSpan, TextBlock longLabel, TextBlock? longDates) = ribbon.BlockLabels[1];
            longLabel.Text.Should().Be("C12 · Rendimiento → Concurrencia y asincronía", "nunca se recorta el identificador ni la temática");
            longLabel.DesiredSize.Height.Should().BeGreaterThan(shortLabel.DesiredSize.Height * 1.5, "envuelve a dos líneas en vez de recortarse");
            longLabel.TextWrapping.Should().Be(TextWrapping.Wrap);
            longLabel.TextTrimming.Should().Be(TextTrimming.None);
            longDates.Should().BeNull("el rótulo se lleva las dos líneas: las fechas caen, y el tooltip las trae");
            longSpan.Dates.Should().Be("1 sept – en curso");
        });

    [Fact]
    public void Un_nombre_que_no_cabe_va_con_elipsis_media_y_el_nombre_entero_en_el_tooltip()
        => ViewLayout.OnUiThread(() =>
        {
            string longName = "Un nombre de aplicación larguísimo que no cabe en la columna";
            CycleRibbon ribbon = Build(new[] { new RibbonTrack(longName, Array.Empty<RibbonSpan>()), None() }, 900);

            TextBlock label = ribbon.NameLabels[0];
            label.Text.Should().Contain("…").And.NotBe(longName);
            label.ToolTip.Should().Be(longName);
            label.DesiredSize.Width.Should().BeLessThanOrEqualTo(ribbon.Names.Width);
            ribbon.NameLabels[1].Text.Should().Be("XBLAST", "lo que cabe va entero");
        });

    [Fact]
    public void La_elipsis_media_conserva_principio_y_final()
    {
        CycleRibbon.MiddleEllipsis("AtalayaBanco", 20).Should().Be("AtalayaBanco");
        CycleRibbon.MiddleEllipsis("AtalayaBanco", 8).Should().Be("Atal…nco");
        CycleRibbon.MiddleEllipsis("AtalayaBanco", 3).Should().Be("A…o");
    }
}
