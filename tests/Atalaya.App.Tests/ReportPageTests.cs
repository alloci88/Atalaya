using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Atalaya.App.Controls;
using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F36 — <b>la página de un informe</b>: la portada, las cifras y las tarjetas que se componen del
/// registro de la sesión con el markdown como cuerpo.
/// <para>
/// <b>Qué se comprueba aquí, y por qué es de regla</b> (N-5). Lo único que hace fiable esta
/// pantalla es que <b>diga lo mismo que el documento</b>: la página y el <c>.md</c> son dos
/// lecturas del mismo hecho, y una cifra que se desvíe no falla, no avisa, y convierte la portada
/// en una segunda versión de la verdad (D-591). Por eso los números se comprueban contra el informe
/// que <c>ReportBuilder</c> escribió para <b>la misma sesión</b>, no contra constantes escritas a
/// mano: un test que afirmara «10» seguiría pasando el día que las dos fuentes se separen.
/// </para>
/// <para>
/// <b>Lo que NO se comprueba, a propósito</b>: el aspecto. Que las tarjetas queden a la misma
/// altura, que el rosco se lea en los dos temas y que el carril baje debajo del cuerpo en una
/// ventana estrecha se ven en la primera captura, y esta fase es cambio → build → tests →
/// <c>dist</c> → parar (N-8).
/// </para>
/// </summary>
public sealed class ReportPageTests
{
    // ================================================================ el caso de referencia

    private static readonly UlidFactory Ulids = new(SystemClock.Instance);

    private static readonly DateTimeOffset Start = new(2026, 9, 8, 8, 41, 17, TimeSpan.Zero);

    private static AppConfig App() => new()
    {
        Slug = "app", Name = "App", RepoUrl = "u", Stack = TechStack.DotNet, CurrentCycle = 1,
    };

    /// <summary>
    /// Una sesión con lo que hace falta para que el informe escriba todas sus líneas: dos unidades
    /// que cerraron de formas distintas —una convergida y otra por tope, que es la diferencia que
    /// D-887 protege—, coste derivable y final registrado.
    /// </summary>
    private static AuditSession Session() => new()
    {
        Id = Ulids.NewUlid(),
        AppSlug = "app",
        Mode = AuditMode.Lotes,
        By = "alguien",
        Machine = "maquina",
        Model = TestRates.Model,
        Provider = "copilot",
        CycleN = 1,
        MaxPassesPerUnit = 6,
        StartedUtc = Start,
        EndedUtc = Start.AddSeconds(153),
        Usage = new UsageTotals { InputTokens = 1000, OutputTokens = TestRates.OutputFor(58m), Calls = 9 },
        Counters = new SessionCounters { New = 3, Confirmed = 1, Resolved = 0 },
        Units =
        {
            new UnitVerdictRecord(
                "src/Uno.cs", "Mod", "auditada", "Revisados: Uno, Dos.",
                Passes: new List<UnitPassRecord>
                {
                    new(1, 2, 0, 0, 0, 0, false, "Revisados: Uno, Dos."),
                    new(2, 0, 0, 0, 0, 0, true, "Revisados: Uno, Dos."),
                    new(3, 0, 0, 0, 0, 0, true, "Revisados: Uno, Dos."),
                }),
            new UnitVerdictRecord(
                "src/sub/Dos.cs", "Mod", "auditada", "Revisados: Tres.",
                Passes: new List<UnitPassRecord>
                {
                    new(1, 1, 0, 0, 0, 0, false, "Revisados: Tres."),
                },
                CoverageIncomplete: true),
        },
    };

    private static Finding Find(Severity severity, string alias, string rule, FindingTag tag, string unit)
    {
        var stamp = new DetectionStamp(Start, AuditMode.Lotes, "abc1234", "alguien");
        return new Finding
        {
            Id = Ulids.NewUlid(),
            DisplayId = alias,
            RuleId = rule,
            Pillar = Pillar.Errores,
            Tag = tag,
            Severity = severity,
            Confidence = Confidence.Media,
            Title = $"Título de {alias}",
            Description = $"Lo que pasa en {alias}.",
            Recommendation = $"Lo que habría que hacer en {alias}.",
            Locations = { new Location(unit, 42) },
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
    }

    private static IReadOnlyList<Finding> Findings() => new[]
    {
        Find(Severity.Alta, "ERR-0001", "COR-001", FindingTag.Checklist, "src/Uno.cs"),
        Find(Severity.Baja, "ERR-0002", "COR-002", FindingTag.Criterio, "src/Uno.cs"),
        Find(Severity.Media, "ERR-0003", "COR-003", FindingTag.Checklist, "src/sub/Dos.cs"),
    };

    /// <summary>El informe tal y como el hub lo guarda, y la fila que lo lista.</summary>
    private static (ReportEntry Entry, AuditSession Session, string Body) Case(int pendingUnits = 849)
    {
        AuditSession session = Session();
        string markdown = ReportBuilder.BuildSessionReport(
            App(), session, Findings(), pendingUnits, largeUnits: 2, "Org", TestRates.Table());

        (string body, string? annex) = Atalaya.App.ViewModels.ReportsViewModel.SplitAnnex(markdown);
        annex.Should().NotBeNull("el informe de sesión trae anexo, y la página parte por él (F27)");

        var entry = new ReportEntry(
            "app", "App", session.Id.ToString(), "no-existe.md", "Informe de sesión — App",
            ReportKind.Sesion, session.StartedUtc, ReportDateSource.Session, session.By, "Lotes",
            session.Units.Count, session.Counters.New, session.Counters.Resolved,
            CreditCalculator.Calculate(session, TestRates.Table()).Credits,
            CostFormat.BillingUnit, HasSession: true, Session: session);

        return (entry, session, body);
    }

    private static ReportPage Page(int pendingUnits = 849)
    {
        (ReportEntry entry, AuditSession session, string body) = Case(pendingUnits);
        return ReportPage.Compose(entry, session, body, Hub());
    }

    /// <summary>
    /// El hub conoce dos de los tres hallazgos, y por las DOS llaves: uno por su alias y otro por
    /// unidad y título, que es lo que los informes ya archivados escriben. El tercero no está.
    /// </summary>
    private static ReportFindingIndex Hub() => new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ERR-0001"] = Ulids.NewUlid().ToString(),
        },
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ReportFindingIndex.Key("src/sub/Dos.cs", "Título de ERR-0003")] = Ulids.NewUlid().ToString(),
        });

    // ================================================================ dos fuentes, una cifra

    /// <summary>
    /// <b>Cada cifra de la página está escrita en el informe</b>, con las mismas palabras.
    /// <para>
    /// Es la regla que sostiene toda la fase. Las cifras que el registro tiene —unidades, hallazgos
    /// nuevos, coste, duración— salen de él, y el informe las escribió del mismo sitio y con la
    /// misma función; las que el registro NO tiene —el reparto por gravedad, el origen, las
    /// pendientes del inventario— se leen del cuerpo. En los dos casos el resultado tiene que
    /// aparecer LITERALMENTE en el markdown, porque si no aparece es que la página se lo ha
    /// inventado.
    /// </para>
    /// </summary>
    [Fact]
    public void Cada_cifra_de_las_tarjetas_esta_escrita_en_el_informe()
    {
        (ReportEntry entry, AuditSession session, string body) = Case();
        ReportPage page = ReportPage.Compose(entry, session, body, Hub());

        ReportStat findings = page.Stats.Single(s => s.Key == "findings");
        findings.Value.Should().Be("3");
        body.Should().Contain("Nuevos: 3", "la cifra de la tarjeta es la que el informe escribió");
        findings.Subtitle.Should().Be("1 alta · 1 media · 1 baja");
        body.Should().Contain("**Gravedad**: 1 Alta · 1 Media · 1 Baja",
            "el reparto de la tarjeta es el que el informe cuenta, con la misma concordancia");

        ReportStat units = page.Stats.Single(s => s.Key == "units");
        units.Value.Should().Be("2");
        units.Subtitle.Should().Be("1 completa, 1 con cobertura posiblemente incompleta · 849 pendientes");
        body.Should().Contain(
            "Unidades: 2 auditadas: 1 completa, 1 con cobertura posiblemente incompleta · 849 pendientes",
            "el subtítulo no reescribe la línea del informe: la lee");

        ReportStat cost = page.Stats.Single(s => s.Key == "cost");
        cost.Value.Should().Be("58,0");
        body.Should().Contain("58,0 AI credits", "el coste sale del mismo CreditCalculator y del mismo CostFormat");
        cost.Subtitle.Should().Be("29 por unidad");
        body.Should().Contain("29 por unidad");

        ReportStat duration = page.Stats.Single(s => s.Key == "duration");
        duration.Value.Should().Be("2 min 33 s");
        body.Should().Contain("2 min 33 s");
    }

    /// <summary>
    /// La frase de portada dice lo mismo que las tarjetas, concordado y sin nombrar lo que no hay.
    /// «de 851» son las 2 auditadas más las 849 pendientes: los dos sumandos están en la misma
    /// línea del informe, y sin la cifra de pendientes el total no se escribe (D-318).
    /// </summary>
    [Fact]
    public void La_frase_ejecutiva_sale_de_las_mismas_cifras()
    {
        (ReportEntry entry, AuditSession session, string body) = Case();
        ReportPage page = ReportPage.Compose(entry, session, body, Hub());

        // LAS LLAMADAS AL MODELO VAN EN LA FRASE (F36-2b §1.6): es lo que explica por qué una
        // sesión costó lo que costó, y el coste y el reloj dejan de ir pegados con un «en».
        page.Lead.Should().Be(
            "Se auditaron 2 unidades de 851 · 3 hallazgos nuevos · 58,0 AI credits · 9 llamadas · 2 min 33 s");
        session.Usage.Calls.Should().Be(9,
            "la cifra sale del REGISTRO, como el coste y la duración; el informe la escribe en su "
            + "anexo —«9 llamada(s) al modelo»— desde el mismo sitio");
        body.Should().NotBeEmpty();
    }

    /// <summary>
    /// <b>Sin cifra de pendientes no hay total.</b> Media división no es una cobertura: «1 unidad
    /// de 1» diría que está todo mirado, y lo que pasa es que no se sabe cuánto hay.
    /// </summary>
    [Fact]
    public void Sin_pendientes_en_el_inventario_la_frase_no_inventa_el_total()
    {
        ReportPage page = Page(pendingUnits: 0);

        page.Lead.Should().StartWith("Se auditaron 2 unidades · ");
        page.Lead.Should().NotContain(" de ");
    }

    /// <summary>
    /// La concordancia es de verdad: singular con uno, plural con más. Un informe que alguien va a
    /// leer entero no puede estar salpicado de «unidad(es)».
    /// </summary>
    [Fact]
    public void La_frase_concuerda_en_singular()
    {
        AuditSession session = Session();
        session.Units.RemoveAt(1);
        session.Counters.New = 1;

        var one = new[] { Find(Severity.Critica, "ERR-0001", "COR-001", FindingTag.Checklist, "src/Uno.cs") };
        string markdown = ReportBuilder.BuildSessionReport(
            App(), session, one, pendingUnits: 1, largeUnits: 0, "Org", TestRates.Table());
        (string body, _) = Atalaya.App.ViewModels.ReportsViewModel.SplitAnnex(markdown);

        var entry = new ReportEntry(
            "app", "App", session.Id.ToString(), "no-existe.md", "t", ReportKind.Sesion,
            session.StartedUtc, ReportDateSource.Session, session.By, "Lotes", 1, 1, 0,
            CreditCalculator.Calculate(session, TestRates.Table()).Credits,
            CostFormat.BillingUnit, HasSession: true, Session: session);

        ReportPage page = ReportPage.Compose(entry, session, body);

        page.Lead.Should().StartWith("Se auditó 1 unidad de 2 · 1 hallazgo nuevo · ");
        page.Lead.Should().EndWith("9 llamadas · 2 min 33 s", "las llamadas van entre el coste y el reloj");
    }

    /// <summary>
    /// <b>Una sesión que no registró llamadas no las nombra</b> (D-318). Las hay: seis de las
    /// setenta y tres de este hub llegaron sin consumo. Un «0 llamadas» diría que no se habló con
    /// el modelo, y lo que pasa es que no se apuntó.
    /// </summary>
    [Fact]
    public void Sin_llamadas_registradas_la_frase_no_las_nombra()
    {
        (ReportEntry entry, AuditSession session, string body) = Case();
        session.Usage.Calls = 0;

        ReportPage page = ReportPage.Compose(entry, session, body, Hub());

        page.Lead.Should().NotContain("llamada");
        page.Lead.Should().EndWith("58,0 AI credits · 2 min 33 s");
    }

    // ================================================================ sin registro

    /// <summary>
    /// <b>Sin registro no hay portada ni tarjetas</b> (D-318). Un informe importado de v4 no
    /// declara nada de sí mismo: se pinta su cuerpo como hasta F36. Inventar un cero para llenar la
    /// tarjeta sería decir una medida que nadie tomó.
    /// </summary>
    [Fact]
    public void Un_informe_sin_registro_se_queda_en_el_cuerpo()
    {
        (ReportEntry entry, _, string body) = Case();

        ReportPage page = ReportPage.Compose(entry with { Session = null }, null, body);

        page.Should().BeSameAs(ReportPage.Plain);
        page.HasCover.Should().BeFalse();
        page.HasStats.Should().BeFalse();
        page.HasFindings.Should().BeFalse();
        page.Sheet.Should().BeEmpty("la vista pinta el markdown entero, no el que compuso la página");
    }

    // ================================================================ las tarjetas de hallazgo

    /// <summary>
    /// Un hallazgo del cuerpo, una tarjeta: con su gravedad —reconocida por el mismo resolutor que
    /// pinta la pastilla de F27—, su regla, su línea y su texto dentro, tal cual.
    /// </summary>
    [Fact]
    public void Cada_hallazgo_del_cuerpo_es_una_tarjeta_con_su_gravedad_y_su_regla()
    {
        ReportPage page = Page();

        page.Findings.Should().HaveCount(3);
        // En el ORDEN DEL INFORME, que agrupa por unidad ignorando mayúsculas (F23 §5). La página
        // no reordena: leerlas en otro orden que el documento sería otro documento.
        page.Groups.Select(g => g.Unit).Should().Equal("src/sub/Dos.cs", "src/Uno.cs");

        ReportFinding alta = page.Findings.Single(f => f.Alias == "ERR-0001");
        alta.Severity.Should().Be("Alta");
        alta.RuleId.Should().Be("COR-001");
        alta.Line.Should().Be("línea 42");
        alta.Title.Should().Be("Título de ERR-0001");
        alta.Body.Should().Contain("Lo que pasa en ERR-0001.")
            .And.Contain("**Recomendación**: Lo que habría que hacer en ERR-0001.");
    }

    /// <summary>
    /// <b>«Abrir» solo cuando hay ficha que abrir.</b> El informe escribe el alias; la ficha se
    /// abre por ULID. Sin esa correspondencia el botón no aparece — un enlace que no lleva a ningún
    /// sitio es peor que no tenerlo (D-444, mismo principio).
    /// </summary>
    [Fact]
    public void Abrir_solo_aparece_cuando_el_alias_existe_en_el_hub()
    {
        ReportPage page = Page();

        page.Findings.Single(f => f.Alias == "ERR-0001").CanOpen.Should().BeTrue();
        page.Findings.Single(f => f.Alias == "ERR-0003").CanOpen.Should().BeTrue();
        page.Findings.Single(f => f.Alias == "ERR-0002").CanOpen.Should()
            .BeFalse("ese alias no corresponde a ningún hallazgo del hub");
    }

    /// <summary>
    /// El índice es la lista de lo que hay que decidir, <b>en el orden en que se decide</b>: por
    /// gravedad y no por unidad. Y es una entrada por hallazgo del cuerpo, ni una más ni una menos.
    /// </summary>
    [Fact]
    public void El_indice_lleva_un_hallazgo_por_entrada_y_va_por_gravedad()
    {
        ReportPage page = Page();

        page.Index.Should().HaveSameCount(page.Findings);
        page.Index.Select(f => f.Severity).Should().Equal("Alta", "Media", "Baja");
        page.Index.Should().OnlyContain(f => page.Findings.Contains(f),
            "el índice y las tarjetas son los MISMOS objetos: es lo que permite saltar de uno a otro");
    }

    // ================================================================ cobertura y origen

    /// <summary>
    /// La barra de pasadas sale del registro y el motivo de cierre del cuerpo: <b>la unidad que
    /// agotó el tope seguía encontrando</b>, y decir solo «una pasada» dejaría creer que se miró
    /// entera (D-887).
    /// </summary>
    [Fact]
    public void La_cobertura_lleva_las_pasadas_del_registro_y_el_motivo_que_el_informe_escribio()
    {
        (ReportEntry entry, AuditSession session, string body) = Case();
        ReportPage page = ReportPage.Compose(entry, session, body);

        page.Coverage.Should().HaveCount(2);

        ReportCoverage uno = page.Coverage[0];
        uno.Name.Should().Be("Uno.cs");
        uno.Where.Should().Be("src/");
        uno.Passes.Select(p => p.Found).Should().Equal(true, false, false);
        uno.Reason.Should().Contain("cerrada por dos pasadas secas");
        body.Should().Contain(uno.Reason, "el motivo no se vuelve a deducir: se lee del informe");
        uno.Reviewed.Should().Be("Uno, Dos");

        ReportCoverage dos = page.Coverage[1];
        dos.Reason.Should().Contain("seguía encontrando");
        body.Should().Contain(dos.Reason);
    }

    /// <summary>
    /// El origen dice de dónde salieron los hallazgos (D-887). Va en la página con los recuentos
    /// que el informe escribió, no con un recuento propio sobre los hallazgos del hub — que hoy
    /// pueden estar reclasificados.
    /// </summary>
    [Fact]
    public void El_origen_sale_de_la_linea_que_el_informe_escribe()
    {
        (ReportEntry entry, AuditSession session, string body) = Case();
        ReportPage page = ReportPage.Compose(entry, session, body);

        page.Origins.Select(o => (o.Name, o.Count)).Should().Equal(
            ("Catálogo de reglas", 2), ("Criterio del auditor", 1));
        body.Should().Contain("Origen: 2 del catálogo de reglas · 1 del criterio del auditor");
    }

    /// <summary>
    /// <b>El reparto por gravedad no nombra lo que no hay.</b> «0 Críticas» ocupa sitio para no
    /// decir nada, y un tramo de rosco a cero no es un tramo.
    /// </summary>
    [Fact]
    public void El_rosco_de_gravedad_solo_lleva_las_gravedades_que_existen()
    {
        ReportPage page = Page();

        page.Severities.Select(s => s.Name).Should().Equal("Alta", "Media", "Baja");
        page.Severities.Should().OnlyContain(s => s.Count > 0);
    }

    // ================================================================ copiar resumen

    /// <summary>
    /// <b>Copiar resumen</b> se lleva la frase y las tarjetas, EN ESE ORDEN: es el informe en cinco
    /// líneas, y en un correo la frase es la que sitúa a las cifras. Cada tarjeta lleva su título
    /// delante por lo mismo que en el panel (D-1038): un «58» pegado no dice de qué es.
    /// </summary>
    [Fact]
    public void Copiar_resumen_lleva_la_frase_y_luego_las_tarjetas()
    {
        ReportPage page = Page();
        var lines = page.Summary.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        lines[0].Should().Be(page.Lead);
        lines.Skip(1).Should().Equal(page.Stats.Select(s => s.CopyText));
        lines.Should().Contain(l => l.StartsWith("Coste: 58,0 AI credits", StringComparison.Ordinal));
    }

    // ================================================================ el cuerpo, entero

    /// <summary>
    /// <b>Ni una palabra del informe se pierde al partirlo.</b> La página se queda con cinco
    /// trozos —cabecera, cobertura, lo de en medio, hallazgos y firma— y entre todos suman el
    /// cuerpo: lo que cambia es cómo se pinta cada uno, no lo que dice (D-441).
    /// </summary>
    [Fact]
    public void El_partido_del_cuerpo_no_pierde_texto()
    {
        (_, _, string body) = Case();

        (string head, string cover, string middle, string? findings, string foot) =
            ReportPage.SplitBody(body);

        findings.Should().NotBeNull();
        head.Should().Contain("## Resumen").And.NotContain("## Cobertura");
        cover.Should().StartWith("## Cobertura").And.Contain("Uno.cs");
        findings!.Should().StartWith("## Hallazgos nuevos").And.Contain("ERR-0002");

        // LA FIRMA VIAJA CON EL ANEXO, y no es un descuido: `ReportBuilder` escribe el anexo
        // DESPUÉS de los hallazgos y firma al final, así que el corte de F27 ya se la lleva. El pie
        // de la página existe para los informes cuya última sección son los hallazgos, y en un
        // informe de sesión está vacío.
        foot.Should().BeEmpty();

        // Sin espacios en blanco, los cinco trozos son el cuerpo entero.
        string rebuilt = string.Concat(head, cover, middle, findings, foot);
        Squash(rebuilt).Should().Be(Squash(body));
    }

    // ================================================================ F36-1b · la segunda pasada

    /// <summary>
    /// <b>UNA fila y UNA rejilla</b> (F36-1b §1.1, D-990).
    /// <para>
    /// En el <c>dist</c> las cuatro cifras medían 130 px de alto y las dos gráficas, que iban FUERA
    /// de la rejilla, 180. Una rejilla que no incluye a la mitad de la fila no iguala nada: eso es
    /// exactamente lo que D-990 vino a arreglar y lo que se volvió a romper al poner las gráficas
    /// en un <c>StackPanel</c> al lado.
    /// </para>
    /// <para>
    /// Se comprueba sobre el panel de verdad y con contenidos de altos distintos —si se les fijara
    /// el alto a mano no habría nada que igualar— y sobre el marcado, que los seis salgan del mismo
    /// <c>ItemsControl</c>: dos colecciones distintas volverían a dar dos filas.
    /// </para>
    /// </summary>
    [Fact]
    public void Las_cuatro_cifras_y_las_dos_graficas_van_en_la_misma_rejilla_y_al_mismo_alto()
    {
        var anchos = new List<double>();
        var altos = new List<double>();

        ViewLayout.OnUiThread(() =>
        {
            var panel = new ColumnsPanel
            {
                MinColumnWidth = ReportLayout.TileMinWidth,
                MaxColumns = 6,
                Gap = ReportLayout.Gap,
            };

            // Cuatro cifras bajas y dos gráficas altas, como las de verdad.
            foreach (double height in new double[] { 90, 90, 90, 90, 150, 150 })
            {
                panel.Children.Add(new Border
                {
                    Background = Brushes.Gray,
                    Child = new Border { Height = height },
                });
            }

            ViewLayout.Layout(panel, 2538, 900);
            foreach (FrameworkElement child in panel.Children.OfType<FrameworkElement>())
            {
                anchos.Add(child.ActualWidth);
                altos.Add(child.ActualHeight);
            }
        });

        anchos.Should().HaveCount(6);
        anchos.Distinct().Should().ContainSingle("el ancho se reparte entre los seis");
        altos.Distinct().Should().ContainSingle("la rejilla iguala: los seis al alto del más alto (D-990)");

        // Y en la vista son los SEIS los que salen de la misma colección.
        string xaml = ViewLayout.Xaml("ReportsView.xaml");
        xaml.Should().Contain("ItemsSource=\"{Binding Tiles}\"",
            "las cifras y las gráficas van en UNA colección; dos darían dos filas");
        foreach (string tipo in new[] { "s:ReportStat", "vm:SeverityTile", "vm:OriginTile" })
        {
            xaml.Should().Contain($"DataType=\"{{x:Type {tipo}}}\"",
                "cada azulejo trae su plantilla por tipo, dentro de la misma rejilla");
        }
    }

    /// <summary>
    /// <b>Las dos barras de origen llevan su número y su parte, y suman cien.</b>
    /// <para>
    /// Redondear los dos porcentajes por separado da barras que suman 99 o 101, y una proporción
    /// que no suma cien no es una proporción. El del criterio es el que el informe ya escribe —la
    /// misma función, <c>PercentText</c>— y el del catálogo se obtiene restando.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(4, 6, "4 · 40 %", "6 · 60 %")]
    [InlineData(1, 2, "1 · 33 %", "2 · 67 %")]
    [InlineData(10, 0, "10 · 100 %", "0 · 0 %")]
    public void El_origen_lleva_numero_y_porcentaje_y_las_dos_barras_suman_cien(
        int catalogo, int criterio, string primera, string segunda)
    {
        string head = $"- Origen: {catalogo} del catálogo de reglas · {criterio} del criterio del "
            + $"auditor ({PercentText.Of(criterio, catalogo + criterio)})";

        var origins = ReportPage.ReadOrigins(head);

        origins.Select(o => o.Tally).Should().Equal(primera, segunda);
        (Percent(origins[0].Share) + Percent(origins[1].Share))
            .Should().Be(100, "dos barras que no suman cien no son una proporción");

        // Y el porcentaje del criterio es LITERALMENTE el que el informe escribió.
        head.Should().Contain($"({origins[1].Share})");
    }

    private static double Percent(string text)
        => double.Parse(text.Replace("%", string.Empty).Trim(), AppCulture.Display);

    /// <summary>
    /// <b>La ficha del documento se pliega; el cuerpo empieza por los hallazgos</b> (F36-1b §1.5 y
    /// §1.6).
    /// <para>
    /// La cabecera y el resumen decían con otras palabras lo que la portada acaba de decir: el H1,
    /// la lista de metadatos y el resumen se leían dos veces antes de llegar al primer hallazgo. Se
    /// pliegan, <b>no se borran</b> (D-441): el test comprueba las dos mitades de eso —que la ficha
    /// lleva exactamente la cabecera y el resumen, y que sumando todos los trozos sigue saliendo el
    /// cuerpo entero—.
    /// </para>
    /// <para>
    /// <b>El orden de PANTALLA no es el del documento</b>, y es a propósito: en pantalla lo primero
    /// es lo que hay que decidir. El <c>.md</c> no cambia — reordenar trozos ya partidos es lo que
    /// D-441 permite; reconstruir el markdown es lo que no.
    /// </para>
    /// </summary>
    [Fact]
    public void La_ficha_lleva_la_cabecera_y_el_resumen_y_el_cuerpo_empieza_por_los_hallazgos()
    {
        (ReportEntry entry, AuditSession session, string body) = Case();
        ReportPage page = ReportPage.Compose(entry, session, body, Hub());

        // La ficha: la cabecera y el resumen, y nada de lo que va después.
        page.HasSheet.Should().BeTrue();
        page.Sheet.Should().StartWith("# Informe de sesión")
            .And.Contain("**Autor**:")
            .And.Contain("## Resumen")
            .And.Contain("Nuevos: 3");
        page.Sheet.Should().NotContain("## Cobertura").And.NotContain("## Hallazgos nuevos");

        // Y en la vista va PLEGADA y antes del cuerpo, con los hallazgos por delante de la
        // cobertura: es el orden en pantalla, no el del documento.
        string xaml = ViewLayout.Xaml("ReportsView.xaml");
        int ficha = xaml.IndexOf("Header=\"Ficha del documento\"", StringComparison.Ordinal);
        int hallazgos = xaml.IndexOf("x:Name=\"FindingCards\"", StringComparison.Ordinal);
        int cobertura = xaml.IndexOf("Text=\"Cobertura\"", StringComparison.Ordinal);
        ficha.Should().BeGreaterThan(0);
        hallazgos.Should().BeGreaterThan(ficha, "la ficha va encima del cuerpo");
        cobertura.Should().BeGreaterThan(hallazgos, "en pantalla, primero lo que hay que decidir");

        // Y NADA SE PIERDE: los cinco trozos siguen siendo el cuerpo entero, en el orden del
        // documento — que es el que el `.md` conserva.
        (string head, string cover, string middle, string? findings, string foot) =
            ReportPage.SplitBody(body);
        head.Should().Be(page.Sheet, "la ficha es la primera parte del partido, sin tocar");
        Squash(string.Concat(head, cover, middle, findings, foot)).Should().Be(Squash(body));
    }

    /// <summary>
    /// <b>Las tarjetas de hallazgo no son prosa</b> (F36-1b §1.7): a partir de 1.600 px de ventana
    /// van en dos columnas, y por debajo en una. La prosa sigue en su medida de lectura (F27), que
    /// no vive aquí — la aplica cada documento sobre su propio texto.
    /// <para>
    /// El umbral y el ancho mínimo de una tarjeta son <b>el mismo número dicho dos veces</b>: el
    /// ancho se DERIVA del umbral, así que la rejilla no puede partir por un sitio distinto del que
    /// dice el manual. Este test lo comprueba contra la rejilla de verdad.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(2560, 2)]
    [InlineData(1600, 2)]
    [InlineData(1599, 1)]
    [InlineData(1000, 1)]
    public void Las_tarjetas_de_hallazgo_van_en_dos_columnas_cuando_la_ventana_da_de_si(
        double ventana, int columnas)
    {
        ReportLayout.CardColumns(ventana).Should().Be(columnas);

        // Y la rejilla de verdad parte por el mismo sitio, con el ancho que le queda al cuerpo.
        // Construir un `ColumnsPanel` toca la infraestructura de WPF, que exige STA.
        int rejilla = 0;
        ViewLayout.OnUiThread(() =>
        {
            var panel = new ColumnsPanel
            {
                MinColumnWidth = ReportLayout.CardMinWidth,
                MaxColumns = 2,
                Gap = ReportLayout.Gap,
            };

            rejilla = panel.ColumnsFor(ReportLayout.BodyWidth(ventana));
        });

        rejilla.Should().Be(
            columnas, "el umbral que dice el manual y el que aplica la rejilla son el mismo");
    }

    /// <summary>
    /// El carril enseña el <b>alias</b> y el título, y el título entero está en el tooltip. Un
    /// título recortado sin decirlo es lo que P-01 prohíbe; recortado con puntos suspensivos y con
    /// el entero a un palmo, no.
    /// </summary>
    [Fact]
    public void Cada_entrada_del_carril_lleva_su_alias_y_su_titulo()
    {
        ReportPage page = Page();

        ReportFinding con = page.Index.First(f => f.Alias.Length > 0);
        con.Alias.Should().Be("ERR-0001");
        con.Title.Should().Be("Título de ERR-0001");
        con.Label.Should().Be("ERR-0001 · Título de ERR-0001");

        // El título viaja ENTERO: la elipsis es del dibujo, no del modelo — recortar en el modelo
        // haría que el tooltip mintiera igual que la línea.
        var largo = new ReportFinding(
            "Alta", string.Empty, new string('a', 300), "u.cs", string.Empty, "R", string.Empty, string.Empty, null);
        largo.Title.Should().HaveLength(300);
        largo.Label.Should().Be(largo.Title, "sin alias, la etiqueta es el título y nada más");

        // Y el dibujo lo corta en DOS líneas: dos `LineHeight.Meta` y elipsis. WPF no tiene
        // `MaxLines`, así que el tope es de alto — y por eso está escrito aquí, para que no se
        // pierda el día que alguien lo quite.
        string xaml = ViewLayout.Xaml("ReportsView.xaml");
        xaml.Should().Contain("<sys:Double x:Key=\"Rail.TwoLines\">36</sys:Double>")
            .And.Contain("MaxHeight=\"{StaticResource Rail.TwoLines}\"");
    }

    /// <summary>
    /// <b>El reparto por gravedad de la tarjeta va en PASTILLAS</b>, las del sistema (retoque de
    /// F36-1b).
    /// <para>
    /// La gravedad se ve sin leer en Portafolio, en Hallazgos, en el informe (F27) y en el rosco
    /// que está justo al lado de esta tarjeta — y aquí se pintaba en texto plano: «1 alta · 5
    /// medias · 4 bajas». Una pastilla por gravedad PRESENTE, y ninguna por las que no hay: el
    /// color dice «hay algo de esta gravedad» y una pastilla a cero diría lo contrario de su propio
    /// número (UI-0051, D-318).
    /// </para>
    /// <para>
    /// <b>El texto no se pierde</b>: el subtítulo sigue diciendo lo mismo, porque es lo que se
    /// copia. Lo que cambia es cómo se dibuja.
    /// </para>
    /// </summary>
    [Fact]
    public void La_tarjeta_de_hallazgos_reparte_la_gravedad_en_pastillas()
    {
        ReportPage page = Page();
        ReportStat findings = page.Stats.Single(s => s.Key == "findings");

        // Una por gravedad presente, en orden de gravedad y con la concordancia de siempre.
        findings.HasChips.Should().BeTrue();
        findings.Chips.Select(c => c.Severity).Should().Equal("Alta", "Media", "Baja");
        findings.Chips.Select(c => c.Text).Should().Equal("1 alta", "1 media", "1 baja");
        findings.Chips.Should().HaveSameCount(page.Severities,
            "una pastilla por gravedad con datos, ni una más");

        // Y el texto sigue ahí: es lo que se copia.
        findings.Subtitle.Should().Be("1 alta · 1 media · 1 baja");
        findings.CopyText.Should().Contain(findings.Subtitle);

        // Las demás tarjetas NO llevan pastillas: «1 completa · 849 pendientes» no es una escala de
        // colores, y pintarla como si lo fuera sería inventarse un significado.
        page.Stats.Where(s => s.Key != "findings").Should().OnlyContain(s => !s.HasChips);

        // Y se DIBUJAN como pastillas, con los cuatro tonos reservados (D-316): el subtítulo en
        // texto solo aparece cuando no hay pastillas que poner.
        string xaml = ViewLayout.Xaml("ReportsView.xaml");
        xaml.Should().Contain("ItemsSource=\"{Binding Chips}\"")
            .And.Contain("Style=\"{StaticResource Report.SevPill}\"")
            .And.Contain("Style=\"{StaticResource Report.SevPill.Text}\"");
        foreach (string clave in new[] { "Brush.Sev.Crit.Soft", "Brush.Sev.High.Soft", "Brush.Sev.Med.Soft", "Brush.Sev.Low.Soft" })
        {
            xaml.Should().Contain(clave, "los cuatro rellenos son los reservados, no unos nuevos");
        }
    }

    /// <summary>
    /// <b>Las acciones son la última tarjeta de la fila, y ya no están en el carril</b> (F36-2b
    /// §1.2).
    /// <para>
    /// <b>Por qué se mudaron.</b> El carril solo existe cuando hay índice que aportar (§1.3), así
    /// que en un informe de un solo hallazgo —todos los de arreglo y casi todos los de
    /// verificación— desaparecía con él, y con él «Descargar .md». Descargar el informe hay que
    /// poder hacerlo siempre, mire lo que mire la página.
    /// </para>
    /// <para>
    /// Lo que este test protege es que <b>no vuelvan al carril</b> y que las tres estén en la
    /// tarjeta: los tres comandos se enlazan desde ahí, y el carril no enlaza ninguno. Se rompe
    /// con un copiar-pegar y no falla al compilar — el botón se queda donde no se ve.
    /// </para>
    /// </summary>
    [Fact]
    public void Las_acciones_van_en_la_fila_y_no_en_el_carril()
    {
        string xaml = ViewLayout.Xaml("ReportsView.xaml");

        int tile = xaml.IndexOf("DataType=\"{x:Type vm:ActionsTile}\"", StringComparison.Ordinal);
        tile.Should().BeGreaterThan(0, "las acciones son una tarjeta de la fila");
        int endTile = xaml.IndexOf("</DataTemplate>", tile, StringComparison.Ordinal);
        string acciones = xaml[tile..endTile];

        foreach (string comando in new[] { "DownloadCommand", "CopySummaryCommand", "OpenFindingCommand" })
        {
            acciones.Should().Contain(comando, "las tres acciones viven en la tarjeta");
        }

        // Y el carril NO enlaza ninguna: lo que queda en él es el índice y el enlace al anexo.
        int rail = xaml.IndexOf("<StackPanel x:Name=\"Rail\"", StringComparison.Ordinal);
        rail.Should().BeGreaterThan(0);
        string carril = xaml[rail..];
        foreach (string comando in new[] { "DownloadCommand", "CopySummaryCommand", "OpenFindingCommand" })
        {
            carril.Should().NotContain(comando,
                "una acción en el carril desaparece con él, y el carril es opcional");
        }
    }

    /// <summary>
    /// <b>El carril solo existe si tiene índice que aportar</b> (F36-2b §1.3): a partir de cuatro
    /// tarjetas de cuerpo. Con tres, el índice es la misma lista dos veces y se lleva 380 px del
    /// ancho del cuerpo — se vio en el <c>dist</c> con una verificación de un solo veredicto.
    /// <para>
    /// Se comprueba además <b>contra el panel de verdad</b>: un carril colapsado no le puede seguir
    /// quitando su ancho al cuerpo, que es lo que pasaría si el panel solo mirara cuántos hijos
    /// tiene.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(10, true)]
    public void El_carril_aparece_a_partir_de_cuatro_tarjetas(int cards, bool rail)
    {
        ReportLayout.NeedsRail(cards).Should().Be(rail);

        double bodyWidth = 0;
        ViewLayout.OnUiThread(() =>
        {
            var panel = new ReadingPanel
            {
                ReadWidth = ReportLayout.ReadWidth,
                RailWidth = ReportLayout.RailWidth,
                Gap = ReportLayout.Gap,
            };
            var body = new Border { Background = Brushes.Gray };
            var carril = new Border
            {
                Background = Brushes.Gray,
                Visibility = rail ? Visibility.Visible : Visibility.Collapsed,
            };
            panel.Children.Add(body);
            panel.Children.Add(carril);

            ViewLayout.Layout(panel, 2538, 900);
            bodyWidth = body.ActualWidth;
        });

        bodyWidth.Should().Be(
            rail ? 2538 - ReportLayout.Gap - ReportLayout.RailWidth : 2538,
            "sin carril el cuerpo se queda con el ancho entero");
    }

    /// <summary>
    /// <b>La fila reparte el ancho ENTERO</b> (F36-2b §1.1), y una tarjeta puede valer dos unidades.
    /// <para>
    /// Con <c>ColumnsPanel</c> la fila se partía en tantas columnas como cupieran y las tarjetas
    /// llenaban las primeras: con cinco tarjetas y seis columnas quedaba un canalón a la derecha,
    /// que es lo que se vio en el <c>dist</c>. Aquí se comprueba sobre el panel de verdad que la
    /// suma de las tarjetas y sus huecos es el ancho disponible, y que la doble mide exactamente el
    /// doble más un hueco.
    /// </para>
    /// </summary>
    [Fact]
    public void La_fila_reparte_el_ancho_entero_y_una_tarjeta_puede_valer_dos()
    {
        var anchos = new List<double>();

        ViewLayout.OnUiThread(() =>
        {
            var panel = new TilesPanel
            {
                MinUnitWidth = ReportLayout.TileMinWidth,
                Gap = ReportLayout.Gap,
            };

            // La fila de un arreglo: la doble de «Ficheros tocados» y cinco de una unidad.
            foreach (int span in new[] { 2, 1, 1, 1, 1, 1 })
            {
                var tile = new Border { Background = Brushes.Gray, Child = new Border { Height = 90 } };
                TilesPanel.SetSpan(tile, span);
                panel.Children.Add(tile);
            }

            ViewLayout.Layout(panel, 2538, 400);
            anchos.AddRange(panel.Children.OfType<FrameworkElement>().Select(c => c.ActualWidth));
        });

        anchos.Should().HaveCount(6);
        (anchos.Sum() + (5 * ReportLayout.Gap)).Should().BeApproximately(2538, 0.5,
            "la fila llega al borde: no hay canalón a la derecha");
        anchos.Skip(1).Distinct().Should().ContainSingle("las cinco sencillas miden lo mismo");
        anchos[0].Should().BeApproximately((anchos[1] * 2) + ReportLayout.Gap, 0.5,
            "la doble vale dos unidades y el hueco de en medio");
    }

    /// <summary>
    /// Y cuando no caben todas de ancho, las filas se <b>equilibran</b>: siete unidades en dos filas
    /// son cuatro y tres, no seis y una. Es una función pura, así que se comprueba sin montar nada.
    /// </summary>
    [Theory]
    [InlineData(7, new[] { 6 })]
    [InlineData(6, new[] { 3, 3 })]
    [InlineData(3, new[] { 2, 2, 2 })]
    [InlineData(1, new[] { 1, 1, 1, 1, 1, 1 })]
    public void Cuando_no_caben_todas_las_filas_se_equilibran(int perRow, int[] esperado)
    {
        var spans = new[] { 2, 1, 1, 1, 1, 1 };

        TilesPanel.Rows(spans, perRow).Should().Equal(esperado);

        // Y no se pierde ninguna tarjeta por el camino, quepan las que quepan.
        TilesPanel.Rows(spans, perRow).Sum().Should().Be(spans.Length);
    }

    private static string Squash(string text)
        => new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
}
