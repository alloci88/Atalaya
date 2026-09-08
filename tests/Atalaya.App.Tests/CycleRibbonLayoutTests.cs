using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Atalaya.App.Controls;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F35-4 — LA CINTA SOBRE UN EJE DE TIEMPO, MEDIDA: cada bloque en la x de su fecha y con el
/// ancho de su duración; los huecos ocupando lo que duraron; el relleno diciendo la cobertura.
/// <para>
/// Hasta F17.2 la cinta era una lista de capítulos de ancho fijo y estos tests medían eso. La
/// pregunta que no podía contestar —«cuánto duró cada ciclo y cuánto se tardó en volver»— es la
/// que trae el eje, y es lo que se mide aquí. Con datos DESIGUALES a propósito (D-843): una app
/// con varios ciclos y un hueco largo, una con uno solo y recién abierto, y una sin ninguno.
/// </para>
/// <para>
/// Y se mide <b>lo que se dibuja</b>, no solo lo que se calcula (D-1042): la geometría se afirma
/// sin pintar, y el dibujo se cuenta sobre el control ya colocado.
/// </para>
/// </summary>
public sealed class CycleRibbonLayoutTests
{
    private static readonly DateTime Today = new(2026, 9, 2, 12, 0, 0);

    private static System.Globalization.CultureInfo Cultura => System.Globalization.CultureInfo.CurrentCulture;

    private static RibbonSlice Slice(Brush fill, DateTime a, DateTime b) => new(fill, a, b, new[] { "trozo" });

    private static RibbonSpan Span(
        string label, DateTime a, DateTime b, bool open = false, string dates = "14 ago – 2 sept",
        double? coverage = null, string shortLabel = "C1", params RibbonSlice[] slices)
        => new(
            label, dates, slices.Length == 0 ? new[] { Slice(Brushes.SlateGray, a, b) } : slices,
            a, b, open, true, new[] { label }, null, shortLabel, coverage);

    /// <summary>Varios ciclos con un hueco de 20 días entre el segundo y el tercero.</summary>
    private static RibbonTrack Several() => new("Voladura", new[]
    {
        Span("C1 · General", Today.AddDays(-120), Today.AddDays(-100), coverage: 1.0, shortLabel: "C1"),
        Span("C2 · Fiabilidad", Today.AddDays(-100), Today.AddDays(-50), coverage: 0.5, shortLabel: "C2"),
        Span("C3 · Mantenibilidad", Today.AddDays(-30), Today, open: true,
            dates: "3 ago – en curso", coverage: 0.25, shortLabel: "C3"),
    }, Dot: Brushes.OrangeRed);

    private static RibbonTrack Single() => new("AtalayaBanco", new[]
    {
        Span("C1 · Rendimiento → Seguridad", Today.AddHours(-3), Today, open: true,
            dates: "2 sept – en curso", coverage: 0.5, shortLabel: "C1",
            Slice(Brushes.Teal, Today.AddHours(-3), Today.AddHours(-1)),
            Slice(Brushes.Purple, Today.AddHours(-1), Today)),
    }, Dot: Brushes.SteelBlue);

    private static RibbonTrack None() => new("XBLAST", Array.Empty<RibbonSpan>(), Dot: Brushes.Green);

    private static IReadOnlyList<RibbonTrack> Uneven() => new[] { Several(), Single(), None() };

    private static CycleRibbon Build(IReadOnlyList<RibbonTrack> tracks, double width)
    {
        var ribbon = new CycleRibbon { Tracks = tracks, Today = Today };
        ViewLayout.Layout(ribbon, width, 400);
        return ribbon;
    }

    /// <summary>Los anchos de página de la casa (D-819), y uno estrecho de más.</summary>
    public static TheoryData<double> Widths => new() { 1124, 658, 441, 320 };

    // ================================================================ el eje

    /// <summary>
    /// <b>Un eje compartido por todas las aplicaciones</b>: del inicio del primer tramo de
    /// cualquiera de ellas hasta hoy. El suelo son <b>siete días</b>, la misma regla que el resto
    /// de ejes del panel (D-1040): el eje empieza donde empieza el primer tramo, y solo se alarga
    /// hacia atrás cuando ese inicio queda a menos de una semana de hoy.
    /// </summary>
    [Fact]
    public void El_eje_va_del_primer_tramo_hasta_hoy_y_nunca_mide_menos_de_siete_dias()
    {
        RibbonGeometry largo = RibbonGeometry.For(Uneven(), Today, 1000);
        largo.From.Should().Be(Today.AddDays(-120), "el primer tramo de CUALQUIER aplicación");
        largo.To.Should().Be(Today, "el eje llega hasta hoy (D-593)");
        largo.TodayX.Should().Be(1000);

        // Un ciclo de VEINTE días: el suelo no pinta nada, el eje empieza donde empieza el tramo.
        var veinte = new[] { new RibbonTrack("Una", new[] { Span("C1 · General", Today.AddDays(-20), Today, open: true) }) };
        RibbonGeometry sinSuelo = RibbonGeometry.For(veinte, Today, 1000);
        sinSuelo.Days.Should().Be(20);
        sinSuelo.From.Should().Be(Today.AddDays(-20));
        sinSuelo.Blocks.Single().Left.Should().BeApproximately(0, 0.5, "el primer tramo abre el eje");

        // Uno de DOS días: ahí sí manda el suelo, y el eje se alarga hacia atrás hasta siete.
        var corto = new[] { new RibbonTrack("Una", new[] { Span("C1 · General", Today.AddDays(-2), Today, open: true) }) };
        RibbonGeometry suelo = RibbonGeometry.For(corto, Today, 1000);
        suelo.Days.Should().Be(RibbonGeometry.MinAxisDays).And.Be(7);
        suelo.From.Should().Be(Today.AddDays(-7));
        suelo.To.Should().Be(Today);

        // Y el bloque ocupa lo suyo: dos días de siete, contra el borde derecho.
        RibbonBlock block = suelo.Blocks.Single();
        block.Left.Should().BeApproximately(1000 * 5 / 7.0, 0.5);
        block.Right.Should().BeApproximately(1000, 0.5);
    }

    /// <summary>Sin ningún tramo, el eje sigue siendo de cuatro semanas y no divide por cero.</summary>
    [Fact]
    public void Sin_tramos_el_eje_sigue_existiendo()
    {
        RibbonGeometry vacia = RibbonGeometry.For(new[] { None() }, Today, 800);

        vacia.Days.Should().Be(RibbonGeometry.MinAxisDays);
        vacia.Blocks.Should().BeEmpty();
        vacia.Gaps.Should().BeEmpty();
        vacia.Ticks.Should().NotBeEmpty("un eje sin datos sigue teniendo fechas");
    }

    /// <summary>
    /// <b>El grano de las marcas sale del rango</b> (F35-4-R): hasta dos semanas, una por día;
    /// hasta tres meses, una por semana; más, una por mes. La regla vive en un solo sitio, con
    /// nombre, y se comprueba en sus dos bordes — que es donde se equivoca una implementación.
    /// </summary>
    [Theory]
    [InlineData(1, AxisGrain.Daily)]
    [InlineData(13.9, AxisGrain.Daily)]
    [InlineData(14, AxisGrain.Daily)]
    [InlineData(14.1, AxisGrain.Weekly)]
    [InlineData(60, AxisGrain.Weekly)]
    [InlineData(92, AxisGrain.Weekly)]
    [InlineData(92.1, AxisGrain.Monthly)]
    [InlineData(365, AxisGrain.Monthly)]
    public void El_grano_de_las_marcas_sale_del_rango(double days, AxisGrain expected)
        => RibbonGeometry.GrainFor(days).Should().Be(expected);

    /// <summary>
    /// Y <b>cuántas marcas salen</b> de cada rango, con la fecha redonda que le toca a su grano.
    /// </summary>
    [Fact]
    public void Cada_rango_trae_su_numero_de_marcas()
    {
        // Diez días → una marca por DÍA. El eje empieza el 23 a mediodía, así que la primera
        // medianoche que cae DENTRO es la del 24: del 24 de agosto al 2 de septiembre, diez.
        RibbonGeometry dias = Axis(10);
        dias.Grain.Should().Be(AxisGrain.Daily);
        dias.Ticks.Should().HaveCount(10);
        dias.Ticks.Select(t => t.When).Should().BeInAscendingOrder();
        dias.Ticks.Select(t => t.When.Date).Should().OnlyHaveUniqueItems();
        dias.Ticks.Should().OnlyContain(t => t.When.TimeOfDay == TimeSpan.Zero, "medianoche de cada día");
        dias.Ticks[0].Text.Should().Be(Today.AddDays(-9).Date.ToString("d MMM", Cultura));
        dias.Ticks[^1].When.Date.Should().Be(Today.Date, "la última marca es la de hoy");

        // Sesenta días → una por SEMANA, en lunes: nueve lunes entre el 4 de julio y el 2 de sept.
        RibbonGeometry semanas = Axis(60);
        semanas.Grain.Should().Be(AxisGrain.Weekly);
        semanas.Ticks.Should().HaveCount(9);
        semanas.Ticks.Should().OnlyContain(t => t.When.DayOfWeek == DayOfWeek.Monday);

        // Doscientos días → una por MES, el día 1: de marzo a septiembre, siete.
        RibbonGeometry meses = Axis(200);
        meses.Grain.Should().Be(AxisGrain.Monthly);
        meses.Ticks.Should().HaveCount(7);
        meses.Ticks.Should().OnlyContain(t => t.When.Day == 1);

        // Dos años, también por mes: veinticuatro meses cumplidos más el del extremo.
        RibbonGeometry anios = Axis(730);
        anios.Grain.Should().Be(AxisGrain.Monthly);
        anios.Ticks.Should().HaveCount(24);
        anios.Ticks.Should().OnlyContain(t => t.When.Day == 1);

        // Y ninguna marca se sale del eje, con cualquier rango.
        foreach (RibbonGeometry g in new[] { dias, semanas, meses, anios })
        {
            g.Ticks.Should().OnlyContain(t => t.When >= g.From && t.When <= g.To);
            g.Ticks.Should().OnlyContain(t => t.X >= 0 && t.X <= g.Width);
        }

        static RibbonGeometry Axis(int days) => RibbonGeometry.For(
            new[] { new RibbonTrack("Una", new[] { Span("C1", Today.AddDays(-days), Today) }) }, Today, 1000);
    }

    /// <summary>
    /// Los rótulos que no caben se DILUYEN —se escribe uno de cada n—, pero las guías se dibujan
    /// todas: son las que sitúan. Contando desde la última, así que la marca más reciente sale
    /// siempre.
    /// </summary>
    [Fact]
    public void Los_rotulos_del_eje_que_no_caben_se_diluyen()
        => ViewLayout.OnUiThread(() =>
        {
            // Dos años por mes en una cinta estrecha: veinticuatro rótulos no caben en 320 px.
            var largo = new[] { new RibbonTrack("Una", new[] { Span("C1", Today.AddDays(-730), Today) }) };
            CycleRibbon estrecha = Build(largo, 320);

            estrecha.Geometry.Ticks.Should().HaveCount(24, "las marcas son las que dice el grano");
            estrecha.AxisLabels.Count.Should().BeLessThan(
                estrecha.Geometry.Ticks.Count, "los rótulos que se pisarían no se escriben");
            estrecha.AxisLabels.Should().NotBeEmpty();

            // Con sitio de sobra se escriben todas.
            CycleRibbon ancha = Build(new[] { new RibbonTrack("Una", new[] { Span("C1", Today.AddDays(-60), Today) }) }, 1124);
            ancha.AxisLabels.Should().HaveCount(ancha.Geometry.Ticks.Count);
        });

    // ================================================================ posición y ancho

    /// <summary>
    /// <b>Cada bloque en la x de su inicio y con el ancho de su duración</b>, sobre el mismo eje
    /// que las demás aplicaciones. Es lo que la cinta de capítulos no podía decir: ahí un ciclo de
    /// cuatro días y uno de seis meses medían lo mismo y estaban en el mismo sitio.
    /// </summary>
    [Theory]
    [MemberData(nameof(Widths))]
    public void Cada_bloque_queda_en_la_fecha_de_su_inicio_y_mide_su_duracion(double width)
    {
        RibbonGeometry g = RibbonGeometry.For(Uneven(), Today, width);
        double perDay = width / 120.0;

        var fila = g.Blocks.Where(b => b.Row == 0).OrderBy(b => b.Left).ToList();
        fila.Select(b => b.Span.Label).Should().Equal("C1 · General", "C2 · Fiabilidad", "C3 · Mantenibilidad");

        fila[0].Left.Should().BeApproximately(0, 0.5, "el primer tramo abre el eje");
        fila[0].Width.Should().BeApproximately(20 * perDay, 0.5, "veinte días");
        fila[1].Left.Should().BeApproximately(20 * perDay, 0.5);
        fila[1].Width.Should().BeApproximately(50 * perDay, 0.5, "cincuenta días");
        fila[2].Width.Should().BeApproximately(30 * perDay, 0.5, "treinta días");
        fila[2].Right.Should().BeApproximately(g.TodayX, 0.5, "el ciclo abierto llega a la línea de hoy");

        // Y la fila de otra aplicación usa EL MISMO eje: su ciclo de tres horas es un hilo contra
        // el borde derecho, no un bloque del mismo tamaño que uno de cincuenta días.
        RibbonBlock banco = g.Blocks.Single(b => b.Row == 1);
        banco.Right.Should().BeApproximately(g.TodayX, 0.5);
        banco.Width.Should().BeLessThan(fila[1].Width / 10);
        banco.Width.Should().BeGreaterThanOrEqualTo(RibbonGeometry.MinBlockWidth, "siempre hay dónde pulsar");
    }

    /// <summary>La fila indivisible de F17.1, conservada: nombre y bloques a la misma altura.</summary>
    [Theory]
    [MemberData(nameof(Widths))]
    public void Cada_nombre_queda_a_la_altura_de_su_fila(double width)
        => ViewLayout.OnUiThread(() =>
        {
            CycleRibbon ribbon = Build(Uneven(), width);

            for (int row = 0; row < ribbon.NameLabels.Count; row++)
            {
                Rect name = ViewLayout.BoxOf(ribbon.NameLabels[row], ribbon);
                double centre = name.Top + name.Height / 2;
                Math.Abs(centre - (CycleRibbon.RowTop(row) + CycleRibbon.RowHeight / 2)).Should().BeLessThan(1.5);
                foreach ((int r, _, Rectangle shape) in ribbon.SpanShapes.Where(s => s.Row == row))
                {
                    Rect box = ViewLayout.BoxOf(shape, ribbon);
                    Math.Abs(box.Top + box.Height / 2 - centre).Should()
                        .BeLessThan(1.5, $"el bloque de la fila {r} va a la altura de su nombre");
                }
            }

            ribbon.SpanShapes.Should().NotContain(s => s.Row == 2, "XBLAST no tiene bloques y nadie ocupa su fila");
        });

    // ================================================================ el relleno de cobertura

    /// <summary>
    /// <b>El relleno dice la cobertura</b> (F35-4 §1.2): de izquierda a derecha, proporcional a
    /// auditadas / auditables. Sin inventario conservado no hay relleno — y no es un 0 %: es que
    /// no se sabe (D-318).
    /// </summary>
    [Fact]
    public void El_relleno_es_proporcional_a_la_cobertura_y_sin_inventario_no_lo_hay()
    {
        RibbonGeometry g = RibbonGeometry.For(Uneven(), Today, 1000);

        RibbonBlock completo = g.Blocks.Single(b => b.Span.Label == "C1 · General");
        RibbonBlock medio = g.Blocks.Single(b => b.Span.Label == "C2 · Fiabilidad");
        RibbonBlock cuarto = g.Blocks.Single(b => b.Span.Label == "C3 · Mantenibilidad");

        completo.FilledWidth.Should().BeApproximately(completo.Width, 0.001, "cobertura 1: relleno entero");
        medio.FilledWidth.Should().BeApproximately(medio.Width / 2, 0.001);
        cuarto.FilledWidth.Should().BeApproximately(cuarto.Width / 4, 0.001);

        // Sin inventario conservado: ni relleno ni porcentaje.
        var sinInventario = new[]
        {
            new RibbonTrack("Una", new[] { Span("C1 · General", Today.AddDays(-40), Today.AddDays(-10)) }),
        };
        RibbonGeometry sin = RibbonGeometry.For(sinInventario, Today, 1000);
        sin.Blocks.Single().Span.Coverage.Should().BeNull();
        sin.Blocks.Single().FilledWidth.Should().Be(0);
    }

    /// <summary>Y el relleno se DIBUJA: sobre el neutro, y solo hasta donde llega la cobertura.</summary>
    [Fact]
    public void El_relleno_se_dibuja_sobre_el_neutro()
        => ViewLayout.OnUiThread(() =>
        {
            var tracks = new[]
            {
                new RibbonTrack("Una", new[]
                {
                    Span("C1 · General", Today.AddDays(-28), Today, open: true, coverage: 0.25),
                }),
            };

            var ribbon = new CycleRibbon
            {
                Tracks = tracks,
                Today = Today,
                PendingBrush = Brushes.Gainsboro,
            };
            ViewLayout.Layout(ribbon, 900, 200);

            var shapes = ribbon.SpanShapes.Select(s => s.Shape).ToList();
            shapes.Should().HaveCount(2, "el fondo neutro y el relleno de la temática");

            Rectangle fondo = shapes.Single(r => ReferenceEquals(r.Fill, Brushes.Gainsboro));
            Rectangle relleno = shapes.Single(r => !ReferenceEquals(r.Fill, Brushes.Gainsboro));
            relleno.Width.Should().BeApproximately(fondo.Width / 4, 1.0, "un cuarto de cobertura");
            Canvas.GetLeft(relleno).Should().BeApproximately(Canvas.GetLeft(fondo), 0.5, "de izquierda a derecha");
        });

    // ================================================================ los huecos

    /// <summary>
    /// <b>Entre dos ciclos separados, el hueco se ve</b> y ocupa lo que duró (F35-4 §1.3). Sin
    /// separación no hay hueco que dibujar.
    /// </summary>
    [Fact]
    public void El_hueco_entre_dos_ciclos_ocupa_lo_que_duro_y_dice_sus_dias()
    {
        RibbonGeometry g = RibbonGeometry.For(Uneven(), Today, 1000);
        double perDay = 1000 / 120.0;

        RibbonGap hueco = g.Gaps.Should().ContainSingle().Subject;
        hueco.Row.Should().Be(0);
        hueco.Days.Should().Be(20, "del fin del C2 al inicio del C3");
        hueco.Text.Should().Be("20 d");
        hueco.Width.Should().BeApproximately(20 * perDay, 0.5, "el hueco mide lo que duró");

        // Y encaja EXACTAMENTE entre los dos bloques: ni se solapa ni deja aire.
        var fila = g.Blocks.Where(b => b.Row == 0).OrderBy(b => b.Left).ToList();
        hueco.Left.Should().BeApproximately(fila[1].Right, 0.5);
        hueco.Right.Should().BeApproximately(fila[2].Left, 0.5);
    }

    /// <summary>Dos ciclos sin separación —el cierre abre el siguiente— no tienen hueco.</summary>
    [Fact]
    public void Sin_separacion_no_hay_hueco()
    {
        var seguidos = new[]
        {
            new RibbonTrack("Una", new[]
            {
                Span("C1 · General", Today.AddDays(-40), Today.AddDays(-20)),
                Span("C2 · General", Today.AddDays(-20), Today, open: true),
            }),
        };

        RibbonGeometry g = RibbonGeometry.For(seguidos, Today, 1000);

        g.Gaps.Should().BeEmpty("el fin de uno es el inicio del otro");
        g.Blocks[0].Right.Should().BeApproximately(g.Blocks[1].Left, 0.001, "van pegados");
    }

    /// <summary>Un hueco de menos de un día no lleva número: no hay cifra que valga la pena.</summary>
    [Fact]
    public void Un_hueco_de_horas_no_escribe_un_cero()
    {
        var horas = new[]
        {
            new RibbonTrack("Una", new[]
            {
                Span("C1 · General", Today.AddDays(-40), Today.AddDays(-20)),
                Span("C2 · General", Today.AddDays(-20).AddHours(6), Today, open: true),
            }),
        };

        RibbonGap hueco = RibbonGeometry.For(horas, Today, 1000).Gaps.Should().ContainSingle().Subject;
        hueco.Days.Should().Be(0);
        hueco.Text.Should().BeEmpty();
    }

    // ================================================================ lo que se dibuja

    /// <summary>
    /// <b>Lo que se dibuja, no solo lo que se calcula</b> (D-1042): los bloques, los huecos, la
    /// línea de hoy y las marcas del eje están ahí, y en su sitio.
    /// </summary>
    [Theory]
    [MemberData(nameof(Widths))]
    public void La_cinta_dibuja_sus_bloques_sus_huecos_y_la_linea_de_hoy(double width)
        => ViewLayout.OnUiThread(() =>
        {
            CycleRibbon ribbon = Build(Uneven(), width);

            // Un bloque por ciclo: cuatro en total, y ninguno en la fila sin ciclos.
            ribbon.SpanShapes.Select(s => s.Span).Distinct().Should().HaveCount(4);
            ribbon.Geometry.Blocks.Should().HaveCount(4);
            ribbon.EmptyLabels.Should().ContainSingle().Which.Row.Should().Be(2);

            // El hueco, dibujado y a trazos.
            ribbon.GapLines.Should().ContainSingle();
            ribbon.GapLines.Single().Line.StrokeDashArray.Should().NotBeEmpty("el hueco va a trazos");

            // La línea de hoy, en el extremo derecho del eje.
            ribbon.TodayLine.Should().NotBeNull();
            ribbon.TodayLine!.X1.Should().BeApproximately(ribbon.Geometry.TodayX, 0.5);

            // Y el dibujo cuadra con la geometría: cada bloque, donde dijo que iría.
            foreach (RibbonBlock block in ribbon.Geometry.Blocks)
            {
                var cajas = ribbon.SpanShapes
                    .Where(s => ReferenceEquals(s.Span, block.Span))
                    .Select(s => Canvas.GetLeft(s.Shape))
                    .ToList();
                cajas.Should().NotBeEmpty();
                cajas.Min().Should().BeApproximately(block.Left, 0.5);
            }
        });

    /// <summary>
    /// La cinta dibuja aunque el ancho llegue DESPUÉS de los datos: nace dentro de un bloque que
    /// empieza colapsado, y el primer intento se encuentra sin sitio (la regla de D-1042).
    /// </summary>
    [Fact]
    public void La_cinta_dibuja_aunque_el_ancho_llegue_despues()
        => ViewLayout.OnUiThread(() =>
        {
            var ribbon = new CycleRibbon { Visibility = Visibility.Collapsed };
            var host = new Border { Width = 900, Height = 300, Child = ribbon };
            ViewLayout.Layout(host, 900, 300);

            ribbon.Tracks = Uneven();
            ribbon.Today = Today;
            ribbon.Visibility = Visibility.Visible;
            host.UpdateLayout();

            ribbon.SpanShapes.Should().NotBeEmpty("con sitio y datos, la cinta tiene que haber pintado");
            ribbon.Geometry.Width.Should().BeGreaterThan(0);
        });

    // ================================================================ los rótulos

    /// <summary>
    /// El rótulo entero cuando cabe; el corto («C1») cuando solo cabe él; nada —y el tooltip—
    /// cuando ni eso. Con eje de tiempo hay bloques estrechos por definición, así que quedarse sin
    /// identificador sería quedarse sin poder señalar el ciclo.
    /// </summary>
    [Fact]
    public void El_rotulo_cae_al_corto_antes_que_desaparecer()
        => ViewLayout.OnUiThread(() =>
        {
            CycleRibbon ribbon = Build(Uneven(), 1124);

            (RibbonSpan Span, TextBlock Label, TextBlock? Dates) ancho =
                ribbon.BlockLabels.Single(b => b.Span.Label == "C2 · Fiabilidad");
            ancho.Label.Text.Should().Be("C2 · Fiabilidad", "cincuenta días dan de sobra");

            // El de tres horas no da ni para el corto sobre un eje de 120 días: se calla y deja el
            // tooltip, que es lo que no se pierde nunca.
            (RibbonSpan Span, TextBlock Label, TextBlock? Dates) estrecho =
                ribbon.BlockLabels.Single(b => b.Span.Label.StartsWith("C1 · Rendimiento"));
            estrecho.Label.Text.Should().BeEmpty();
            estrecho.Span.TooltipLines.Should().NotBeEmpty();
        });

    /// <summary>La aplicación sin ciclos conserva su fila, rotulada, con y sin sitio.</summary>
    [Theory]
    [MemberData(nameof(Widths))]
    public void La_aplicacion_sin_ciclos_tiene_su_fila_rotulada(double width)
        => ViewLayout.OnUiThread(() =>
        {
            CycleRibbon ribbon = Build(Uneven(), width);

            ribbon.NameLabels.Should().HaveCount(3);
            (int Row, TextBlock Text) empty = ribbon.EmptyLabels.Should().ContainSingle().Subject;
            empty.Row.Should().Be(2);
            empty.Text.Text.Should().Be("sin ciclos registrados");
        });

    /// <summary>El punto de color de la aplicación (D-314) va delante de su nombre, en su fila.</summary>
    [Fact]
    public void Cada_fila_lleva_el_punto_de_color_de_su_aplicacion()
        => ViewLayout.OnUiThread(() =>
        {
            CycleRibbon ribbon = Build(Uneven(), 1124);

            var dots = ribbon.Names.Children.OfType<Rectangle>().ToList();
            dots.Should().HaveCount(3, "un punto por aplicación");
            dots.Select(d => d.Fill).Should().BeEquivalentTo(
                new Brush[] { Brushes.OrangeRed, Brushes.SteelBlue, Brushes.Green });

            // Y va DELANTE del nombre, no encima.
            foreach (TextBlock name in ribbon.NameLabels)
            {
                Canvas.GetLeft(name).Should().BeGreaterThanOrEqualTo(CycleRibbon.DotSize);
            }
        });

    /// <summary>Un nombre que no cabe va con elipsis media y el nombre entero en el tooltip.</summary>
    [Fact]
    public void Un_nombre_que_no_cabe_va_con_elipsis_media_y_el_nombre_entero_en_el_tooltip()
        => ViewLayout.OnUiThread(() =>
        {
            var largo = new RibbonTrack(
                "AplicacionDeNombreInterminableQueNoCabeEnLaColumnaDeNingunaManera",
                new[] { Span("C1 · General", Today.AddDays(-10), Today, open: true) });

            CycleRibbon ribbon = Build(new[] { largo }, 900);
            TextBlock name = ribbon.NameLabels.Single();

            name.Text.Should().NotBe(largo.Name).And.Contain("…");
            name.Text.Should().StartWith("Aplicacion");
            name.ToolTip.Should().Be(largo.Name);
        });

    /// <summary>La elipsis media conserva principio y final: es lo que distingue dos nombres largos.</summary>
    [Fact]
    public void La_elipsis_media_conserva_principio_y_final()
    {
        CycleRibbon.MiddleEllipsis("AtalayaBanco", 20).Should().Be("AtalayaBanco");
        CycleRibbon.MiddleEllipsis("AtalayaBanco", 8).Should().Be("Atal…nco");
        CycleRibbon.MiddleEllipsis("AtalayaBanco", 3).Should().Be("A…o");
    }
}
