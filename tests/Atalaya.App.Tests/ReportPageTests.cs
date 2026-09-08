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
        ReportPage page = Page();

        page.Lead.Should().Be("Se auditaron 2 unidades de 851 · 3 hallazgos nuevos, 1 alta · 58,0 AI credits en 2 min 33 s");
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

        page.Lead.Should().StartWith("Se auditó 1 unidad de 2 · 1 hallazgo nuevo, 1 crítica · ");
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
        page.Body.Should().BeEmpty("la vista pinta el markdown entero, no el que compuso la página");
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

    private static string Squash(string text)
        => new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
}
