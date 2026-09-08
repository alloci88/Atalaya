using System.Windows;
using Atalaya.App.Controls;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F36-2 — <b>la página de un informe de VERIFICACIÓN y la de uno de ARREGLO</b>.
/// <para>
/// <b>Qué se comprueba, y por qué es de regla</b> (N-5). Lo mismo que en F36 §1: que la página y el
/// documento sean dos lecturas del mismo hecho y no dos versiones de la verdad (D-591). Aquí eso
/// tiene un filo propio, porque el §0 midió que el registro de una verificación <b>no tiene ni un
/// veredicto</b> —los trece de este hub llevan <c>counters</c> a cero y <c>units</c> vacía— y que el
/// de un arreglo <b>no tiene el estado de su commit</b> salvo en <c>fixes/{ulid}.json</c>. Así que
/// cada cifra se comprueba contra el informe que <c>ReportBuilder</c> escribió para la MISMA sesión,
/// nunca contra una constante escrita a mano.
/// </para>
/// <para>
/// <b>Lo que NO se comprueba, a propósito</b>: el aspecto. Ciclo N-8.
/// </para>
/// </summary>
public sealed class ReportVerifyFixPageTests
{
    private static readonly UlidFactory Ulids = new(SystemClock.Instance);

    private static readonly DateTimeOffset Start = new(2026, 9, 8, 9, 15, 0, TimeSpan.Zero);

    private static AppConfig App() => new()
    {
        Slug = "app", Name = "App", RepoUrl = "u", Stack = TechStack.DotNet, CurrentCycle = 1,
    };

    // ============================================================ el caso de una VERIFICACIÓN

    /// <summary>
    /// Una sesión de verificación como las que escribe <c>VerifyCoordinator</c>: <b>sin contadores y
    /// sin unidades</b>, que es exactamente lo que el §0 midió en los trece de este hub. Si el
    /// registro tuviera los veredictos, esta fase no habría hecho falta.
    /// </summary>
    private static AuditSession VerifySession() => new()
    {
        Id = Ulids.NewUlid(),
        AppSlug = "app",
        Mode = AuditMode.Verify,
        By = "alguien",
        Machine = "maquina",
        Model = TestRates.Model,
        Provider = "copilot",
        CycleN = 1,
        StartedUtc = Start,
        EndedUtc = Start.AddSeconds(48),
        Usage = new UsageTotals { OutputTokens = TestRates.OutputFor(15m), Calls = 4 },
    };

    private static ReportBuilder.VerifyLine Line(
        string alias, Severity severity, string verdict, VerifyBasis basis = VerifyBasis.Anclado)
        => new(
            alias,
            $"Título de {alias}",
            severity,
            $"src/{alias}.cs",
            42,
            basis,
            basis == VerifyBasis.Simbolo ? "Clase.Metodo" : null,
            verdict,
            $"Lo que el juez dijo de {alias}.");

    /// <summary>Tres veredictos de tres clases: uno resuelto, uno que sigue activo y un no localizado.</summary>
    private static IReadOnlyList<ReportBuilder.VerifyLine> Lines() => new[]
    {
        Line("BUG-0001", Severity.Alta, "confirmado", VerifyBasis.Simbolo),
        Line("BUG-0002", Severity.Media, "resuelto"),
        Line("MEJ-0003", Severity.Baja, "no localizado"),
    };

    private static (ReportEntry Entry, AuditSession Session, string Body) VerifyCase(
        IReadOnlyList<ReportBuilder.VerifyLine>? lines = null)
    {
        AuditSession session = VerifySession();
        lines ??= Lines();
        string markdown = ReportBuilder.BuildVerifyReport(
            App(), session, lines, new[] { "BUG-0001: confirmado — el defecto sigue ahí." },
            "Org", TestRates.Table());
        (string body, _) = Atalaya.App.ViewModels.ReportsViewModel.SplitAnnex(markdown);

        var entry = new ReportEntry(
            "app", "App", session.Id.ToString(), "no-existe.md", "Verificación — App",
            ReportKind.Verificacion, session.StartedUtc, ReportDateSource.Session, session.By,
            "Verify", null, null, null,
            CreditCalculator.Calculate(session, TestRates.Table()).Credits,
            CostFormat.BillingUnit, HasSession: true, Session: session);

        return (entry, session, body);
    }

    private static ReportPage VerifyPage(ReportFindingIndex? hub = null)
    {
        (ReportEntry entry, AuditSession session, string body) = VerifyCase();
        return ReportPage.Compose(entry, session, body, hub);
    }

    // ============================================================ el caso de un ARREGLO

    private static AuditSession FixSession(string findingId, string alias) => new()
    {
        Id = Ulids.NewUlid(),
        AppSlug = "app",
        Mode = AuditMode.Fix,
        By = "alguien",
        Machine = "maquina",
        Model = TestRates.Model,
        Provider = "copilot",
        CycleN = 1,
        StartedUtc = Start,
        EndedUtc = Start.AddSeconds(54),
        Usage = new UsageTotals { OutputTokens = TestRates.OutputFor(27m), Calls = 10 },
        FixFindingId = findingId,
        FixFindingAlias = alias,
    };

    private static Finding Fixed(string alias) => new()
    {
        Id = Ulids.NewUlid(),
        DisplayId = alias,
        RuleId = "COR-001",
        Pillar = Pillar.Errores,
        Severity = Severity.Alta,
        Confidence = Confidence.Media,
        Title = $"Título de {alias}",
        Description = "Lo que pasa.",
        Recommendation = "Lo que habría que hacer.",
        Locations = { new Location("src/Uno.cs", 42) },
        FirstDetected = new DetectionStamp(Start, AuditMode.Lotes, "abc1234", "alguien"),
        LastConfirmed = new DetectionStamp(Start, AuditMode.Lotes, "abc1234", "alguien"),
    };

    /// <summary>Verde: sin errores nuevos y con los tests pasando.</summary>
    private static BuildVerdict Green() => new(
        Ok: true,
        Summary: "todo bien\nsalida larga de la compilación",
        TargetLabel: "el proyecto App/App.csproj",
        NewErrors: 0,
        HasBaseline: true,
        BaselineNote: "la de abc1234",
        TestsRun: true,
        TestsOk: true);

    /// <summary>Rojo: dos errores nuevos, con sus líneas.</summary>
    private static BuildVerdict Red() => new(
        Ok: false,
        Summary: "mal\nsalida larga de la compilación",
        TargetLabel: "el proyecto App/App.csproj",
        NewErrors: 2,
        NewErrorLines: new[] { "A.cs(1,1): error CS0001", "B.cs(2,2): error CS0002" },
        HasBaseline: true,
        BaselineNote: "la de abc1234",
        TestsRun: true,
        TestsOk: true);

    private static (ReportEntry Entry, AuditSession Session, string Body) FixCase(
        BuildVerdict? build = null,
        string? sha = null,
        Finding? finding = null,
        bool touched = true)
    {
        finding ??= Fixed("MEJ-0046");
        AuditSession session = FixSession(finding.Id.ToString(), finding.DisplayId!);
        var files = touched
            ? new List<(string, string, bool)>
            {
                ("src/Uno.cs", "+0 −38", true),
                ("src/Dos.cs", "+4 −1", false),
            }
            : new List<(string, string, bool)>();

        // LAS NOTAS QUE ESCRIBE EL ARREGLO (D-546): una por fichero, y las autorizadas lo dicen.
        // Es de ahí de donde la página lee el ámbito — del registro, no del texto.
        session.Notes.Add($"Arreglo asistido de {finding.DisplayId}: {finding.Title}");
        foreach ((string path, string tally, bool inScope) in files)
        {
            session.Notes.Add($"tocado: {path} ({tally})"
                + (inScope ? string.Empty : " — fuera del hallazgo, autorizado por el usuario"));
        }

        string markdown = ReportBuilder.BuildFixReport(
            App(), session, finding, files,
            "Se quitó el bloque muerto y se validó el parámetro.",
            risks: null,
            commitTitle: "Arregla lo que había que arreglar",
            commitDescription: string.Empty,
            build: build,
            organization: "Org",
            tests: new FixTestSituation("App/App.csproj", Array.Empty<string>(), AnyInClone: false),
            rates: TestRates.Table());

        if (sha is { Length: > 0 })
        {
            markdown = ReportBuilder.MarkFixCommitted(markdown, sha);
        }

        (string body, _) = Atalaya.App.ViewModels.ReportsViewModel.SplitAnnex(markdown);

        var entry = new ReportEntry(
            "app", "App", session.Id.ToString(), "no-existe.md", "Arreglo asistido — App",
            ReportKind.Sesion, session.StartedUtc, ReportDateSource.Session, session.By, "Fix",
            null, null, null,
            CreditCalculator.Calculate(session, TestRates.Table()).Credits,
            CostFormat.BillingUnit, HasSession: true,
            FindingId: finding.Id.ToString(), FindingAlias: finding.DisplayId, Session: session);

        return (entry, session, body);
    }

    private static FixRecord Record(AuditSession session, string? sha, string? author, int files = 2)
        => new()
        {
            Id = session.Id,
            AppSlug = "app",
            By = session.By,
            Utc = session.StartedUtc,
            CommitSha = sha,
            CommitAuthor = author,
            Files = Enumerable.Range(0, files)
                .Select(i => new FixFileStamp($"src/{i}.cs", $"sha256:{i}"))
                .ToList(),
        };

    // ================================================================ §2 · las dos fuentes

    /// <summary>
    /// <b>Cada cifra de una verificación está escrita en su informe</b>, con las mismas palabras.
    /// <para>
    /// El recuento y los veredictos salen del CUERPO —el registro no los tiene, y el §0 lo midió—;
    /// el coste y la duración, del registro, con el mismo <c>CreditCalculator</c> que escribió el
    /// informe. En los dos casos el resultado tiene que aparecer literalmente en el markdown: si no
    /// aparece, es que la página se lo ha inventado.
    /// </para>
    /// </summary>
    [Fact]
    public void Cada_cifra_de_una_verificacion_esta_escrita_en_su_informe()
    {
        (ReportEntry entry, AuditSession session, string body) = VerifyCase();
        ReportPage page = ReportPage.Compose(entry, session, body);

        // CON VARIOS VEREDICTOS, LA TARJETA LOS CUENTA. Con uno —el caso normal— dice cuál fue.
        ReportStat veredicto = page.Stats.Single(s => s.Key == "verdict");
        veredicto.Value.Should().Be("3");
        veredicto.Span.Should().Be(2, "lleva una enumeración dentro");
        veredicto.Subtitle.Should().Be("1 sigue activo · 1 no localizado · 1 resuelto");
        body.Should().Contain("**Hallazgos verificados**: 3",
            "la cifra de la tarjeta es la que el informe escribió");
        foreach (string escrito in new[] { "confirmado", "resuelto", "no localizado" })
        {
            body.Should().Contain($"**Veredicto**: {escrito}");
        }

        ReportStat coste = page.Stats.Single(s => s.Key == "cost");
        coste.Value.Should().Be("15,0");
        body.Should().Contain("15,0 AI credits", "el coste sale del mismo CreditCalculator");
        coste.Subtitle.Should().BeEmpty(
            "un arreglo arregla un hallazgo y una verificación verifica uno: no hay reparto");

        page.Stats.Single(s => s.Key == "duration").Value.Should().Be("48 s");
        page.Stats.Single(s => s.Key == "calls").Value.Should().Be("4");
        body.Should().Contain("**4 llamada(s) al modelo**", "las llamadas salen del mismo registro");

        // Y las tres tarjetas de recuento se han ido: verificar se lanza desde la ficha de UNO.
        page.Stats.Select(s => s.Key).Should().NotContain(new[] { "verified", "resolved", "active" });
        // Y el rosco se ha ido con ellas: verificar sale de la ficha de UNO, así que era un rosco
        // de un solo tramo — que no es un reparto, es un círculo (F36-2b §3).
        ViewLayout.Xaml("ReportsView.xaml").Should().NotContain("vm:VerdictTile");
    }

    /// <summary>
    /// <b>Con un solo veredicto, la tarjeta dice CUÁL fue</b>, en grande y en su color, y el
    /// subtítulo lleva lo que hay que hacer a continuación cuando lo hay. Los cuatro desenlaces.
    /// </summary>
    [Theory]
    [InlineData("resuelto", "Resuelto", "")]
    [InlineData("confirmado", "Sigue activo", "")]
    [InlineData("no localizado", "No localizado", "Verifica para re-anclarlo o cerrarlo.")]
    [InlineData("no concluyente", "No concluyente", "")]
    public void El_veredicto_de_un_solo_hallazgo_va_en_grande_con_su_subtitulo(
        string escrito, string enPantalla, string subtitulo)
    {
        (ReportEntry entry, AuditSession session, string body) = VerifyCase(new[]
        {
            Line("BUG-0001", Severity.Alta, escrito),
        });

        ReportPage page = ReportPage.Compose(entry, session, body);

        ReportStat veredicto = page.Stats.Single(s => s.Key == "verdict");
        veredicto.Value.Should().Be(enPantalla);
        veredicto.Subtitle.Should().Be(subtitulo);
        veredicto.Tone.Should().Be(ReportVerdicts.Tone(ReportVerdicts.Of(escrito)));

        // La gravedad va en su tarjeta, con la pastilla de siempre. Nunca como cifra.
        ReportStat gravedad = page.Stats.Single(s => s.Key == "severity");
        gravedad.Value.Should().BeEmpty("una gravedad no es un número");
        gravedad.Chips.Select(c => c.Severity).Should().Equal("Alta");
    }

    /// <summary>
    /// La frase de portada dice lo mismo que las tarjetas, concordada y <b>sin gravedad</b>: en una
    /// verificación lo que importa es el desenlace, y la gravedad de cada uno está en su tarjeta.
    /// </summary>
    [Fact]
    public void La_frase_de_una_verificacion_enumera_solo_los_veredictos_que_hay()
    {
        ReportPage page = VerifyPage();

        page.Lead.Should().Be(
            "Se verificaron 3 hallazgos · 1 sigue activo · 1 no localizado · 1 resuelto "
            + "· 15,0 AI credits · 4 llamadas · 48 s");
        page.Lead.Should().NotContain("no concluyente", "lo que no hay no se nombra (D-318)");
        page.Lead.Should().NotContain("Alta", "la gravedad no es el desenlace");
    }

    /// <summary>Con un solo veredicto, la frase concuerda en singular de verdad.</summary>
    [Fact]
    public void La_frase_de_una_verificacion_concuerda_en_singular()
    {
        AuditSession session = VerifySession();
        var one = new[] { Line("BUG-0001", Severity.Critica, "confirmado") };
        string markdown = ReportBuilder.BuildVerifyReport(
            App(), session, one, Array.Empty<string>(), "Org", TestRates.Table());
        (string body, _) = Atalaya.App.ViewModels.ReportsViewModel.SplitAnnex(markdown);

        var entry = new ReportEntry(
            "app", "App", session.Id.ToString(), "x.md", "t", ReportKind.Verificacion,
            session.StartedUtc, ReportDateSource.Session, session.By, "Verify", null, null, null,
            CreditCalculator.Calculate(session, TestRates.Table()).Credits,
            CostFormat.BillingUnit, HasSession: true, Session: session);

        ReportPage page = ReportPage.Compose(entry, session, body);

        page.Lead.Should().StartWith("Se verificó 1 hallazgo · 1 sigue activo · ");
        page.Lead.Should().EndWith("4 llamadas · 48 s");
    }

    /// <summary>
    /// <b>Cada veredicto es una tarjeta</b>, en el orden del informe y con lo que la decisión
    /// necesita: el desenlace, la gravedad, dónde estaba, qué código se le enseñó y el texto del
    /// juez. El borde es del VEREDICTO, no de la gravedad — eso se comprueba en el marcado.
    /// </summary>
    [Fact]
    public void Cada_veredicto_del_cuerpo_es_una_tarjeta_en_el_orden_del_informe()
    {
        ReportPage page = VerifyPage();

        page.Verdicts.Select(v => v.Alias).Should().Equal("BUG-0001", "BUG-0002", "MEJ-0003");
        page.Verdicts.Select(v => v.Kind).Should().Equal(
            ReportVerdictKind.Activo, ReportVerdictKind.Resuelto, ReportVerdictKind.NoLocalizado);

        ReportVerdict activo = page.Verdicts[0];
        activo.Label.Should().Be("sigue activo", "es la palabra de estado de la aplicación (D-504)");
        activo.Verdict.Should().Be("confirmado", "y la del informe se conserva tal cual");
        activo.Severity.Should().Be("Alta", "con el rotulado único de UI-0027");
        activo.Where.Should().Be("src/BUG-0001.cs:42");
        activo.Basis.Should().Contain("el símbolo del hallazgo, re-anclado a «Clase.Metodo»");
        activo.Body.Should().Contain("Lo que el juez dijo de BUG-0001.");
        activo.HasNextStep.Should().BeFalse();

        // Un «no localizado» dice qué hacer, y con las MISMAS palabras que la ficha.
        ReportVerdict perdido = page.Verdicts[2];
        perdido.NextStep.Should().Be(SnippetReader.NotLocatedNextStep);
    }

    /// <summary>
    /// <b>El índice del carril de una verificación va por VEREDICTO</b>, con los resueltos al final:
    /// lo que hay que decidir es lo que sigue abierto. Y son los MISMOS objetos que las tarjetas,
    /// para que pulsar una entrada pueda llevar a la suya sin una correspondencia aparte.
    /// </summary>
    [Fact]
    public void El_indice_de_una_verificacion_va_por_veredicto_y_deja_los_resueltos_al_final()
    {
        ReportPage page = VerifyPage();

        page.VerdictIndex.Select(v => v.Alias).Should().Equal("BUG-0001", "MEJ-0003", "BUG-0002");
        page.VerdictIndex.Should().OnlyContain(v => page.Verdicts.Contains(v));
        page.VerdictIndex.Should().HaveSameCount(page.Verdicts);
    }

    // ================================================================ §2 · el re-anclaje

    /// <summary>
    /// <b>El re-anclaje se ve</b> (BUGFIX-ANCLA, D-1037). Verificar también sirve para re-anclar, y
    /// hasta aquí eso no se veía en ninguna parte más que en el historial de la ficha.
    /// <para>
    /// <b>Se lee del EVENTO, no del texto</b>: el informe se escribe sin él. Con evento, la tarjeta
    /// lleva su pastilla y la frase lo cuenta; sin evento —un informe anterior a BUGFIX-ANCLA— no
    /// hay pastilla, porque no hubo re-anclaje que enseñar y no se inventa uno.
    /// </para>
    /// </summary>
    [Fact]
    public void El_reanclaje_sale_del_evento_de_la_verificacion_y_no_del_texto()
    {
        (ReportEntry entry, AuditSession session, string body) = VerifyCase();
        string findingId = Ulids.NewUlid().ToString();

        var conEvento = new ReportFindingIndex(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["BUG-0001"] = findingId },
            new Dictionary<string, string>())
        {
            Reanchors = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ReportFindingIndex.MoveKey(session.Id.ToString(), findingId)] = "re-anclado 507 → 497",
            },
        };

        ReportPage con = ReportPage.Compose(entry, session, body, conEvento);

        ReportVerdict activo = con.Verdicts.Single(v => v.Alias == "BUG-0001");
        activo.HasReanchor.Should().BeTrue();
        activo.Reanchor.Should().Be("re-anclado 507 → 497");
        con.Lead.Should().Contain("· 1 re-anclado", "la frase lo cuenta");
        con.Stats.Single(s => s.Key == "verdict").Subtitle.Should()
            .Contain("re-anclado", "y la tarjeta del veredicto también");
        body.Should().NotContain("re-anclado 507",
            "el informe NO lo escribe: por eso hay que leerlo del evento");

        // El MISMO informe sin el evento —o de antes de BUGFIX-ANCLA— no enseña nada.
        var sinEvento = new ReportFindingIndex(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["BUG-0001"] = findingId },
            new Dictionary<string, string>());

        ReportPage sin = ReportPage.Compose(entry, session, body, sinEvento);

        sin.Verdicts.Should().OnlyContain(v => !v.HasReanchor);
        sin.Lead.Should().NotContain("re-anclado");
        sin.Stats.Single(s => s.Key == "verdict").Subtitle.Should().NotContain("re-anclado");
    }

    // ================================================================ §3 · el estado del arreglo

    /// <summary>
    /// <b>Los tres estados de un arreglo, leídos del REGISTRO</b> (D-1033): sin commitear, commiteado
    /// —con su sha y con quién lo firmó (BUGFIX-F32-2)— y verificado cuando el hallazgo tiene un
    /// veredicto posterior. Es la única pregunta que un director hace sobre un arreglo, y estaba
    /// dentro de una cita a mitad del documento.
    /// </summary>
    [Fact]
    public void El_estado_del_arreglo_sale_del_registro_en_sus_tres_casos()
    {
        Finding finding = Fixed("MEJ-0046");

        // (1) Sin commitear: hay registro con ficheros y sin sha.
        (ReportEntry entry, AuditSession session, string body) = FixCase(Green(), finding: finding);
        ReportPage sin = ReportPage.Compose(entry, session, body, null, Record(session, null, null));
        sin.FixState!.Kind.Should().Be(ReportFixStateKind.SinCommitear);
        sin.FixState.Text.Should().Be("Sin commitear");
        sin.FixState.Tone.Should().Be(ReportTone.Warning, "queda algo por hacer");
        sin.FixState.FromRecord.Should().BeTrue();

        // (2) Commiteado: el sha y el autor van en la portada, no dentro de una cita.
        (entry, session, body) = FixCase(Green(), sha: "5249598", finding: finding);
        ReportPage con = ReportPage.Compose(
            entry, session, body, null, Record(session, "5249598", "Nombre <correo>"));
        con.FixState!.Kind.Should().Be(ReportFixStateKind.Commiteado);
        con.FixState.Text.Should().Be("Commiteado 5249598");
        con.FixState.Detail.Should().Be("como Nombre <correo>");
        con.FixState.Tone.Should().Be(ReportTone.Success);

        // (3) Verificado: hay un veredicto POSTERIOR sobre el hallazgo que se arregló.
        var hub = new ReportFindingIndex(
            new Dictionary<string, string>(), new Dictionary<string, string>())
        {
            LastVerdicts = new Dictionary<string, ReportVerdictEvent>(StringComparer.Ordinal)
            {
                [finding.Id.ToString()] =
                    new ReportVerdictEvent(session.StartedUtc.AddHours(1), FindingEvent.Resolved),
            },
        };

        ReportPage verificado = ReportPage.Compose(
            entry, session, body, hub, Record(session, "5249598", "Nombre <correo>"));
        verificado.FixState!.Kind.Should().Be(ReportFixStateKind.Verificado);
        verificado.FixState.Text.Should().Be("Verificado");
        verificado.FixState.Detail.Should()
            .Contain("commiteado en 5249598", "el commit no se pierde: baja al detalle")
            .And.Contain("el hallazgo quedó resuelto");

        // Un veredicto ANTERIOR al arreglo no lo verifica: verificar antes de arreglar no dice
        // nada de lo que el arreglo hizo.
        var antes = hub with
        {
            LastVerdicts = new Dictionary<string, ReportVerdictEvent>(StringComparer.Ordinal)
            {
                [finding.Id.ToString()] =
                    new ReportVerdictEvent(session.StartedUtc.AddHours(-1), FindingEvent.Resolved),
            },
        };
        ReportPage previo = ReportPage.Compose(
            entry, session, body, antes, Record(session, "5249598", "Nombre <correo>"));
        previo.FixState!.Kind.Should().Be(ReportFixStateKind.Commiteado);
    }

    /// <summary>
    /// <b>Y cuando las dos fuentes no dicen lo mismo, manda el registro.</b>
    /// <para>
    /// Puede pasar de verdad: el informe se escribe al cerrar el arreglo, ANTES de que el usuario
    /// decida quedarse los cambios, y la frase se sustituye después (D-1034). Un informe que no
    /// llegara a reescribirse seguiría diciendo «NO están commiteados» de un arreglo que sí lo está,
    /// y el registro es el que guarda el hecho.
    /// </para>
    /// </summary>
    [Fact]
    public void Con_registro_y_cuerpo_en_desacuerdo_la_pantalla_dice_lo_que_dice_el_registro()
    {
        // El cuerpo sigue diciendo «NO están commiteados» —no se le pasa el sha— y el registro sí.
        (ReportEntry entry, AuditSession session, string body) = FixCase(Green());
        body.Should().Contain("NO están commiteados");

        ReportPage page = ReportPage.Compose(
            entry, session, body, null, Record(session, "5249598", null));

        page.FixState!.Kind.Should().Be(ReportFixStateKind.Commiteado);
        page.FixState.FromRecord.Should().BeTrue();
        page.FixState.Source.Should().Contain("registro", "de dónde sale se dice (N-2)");

        // Y la otra lectura, la del cuerpo, dice lo contrario: es la discrepancia que este test
        // fija, y la que la pantalla resuelve a favor del registro.
        ReportPage delCuerpo = ReportPage.Compose(entry, session, body, null, fix: null);
        delCuerpo.FixState!.Kind.Should().Be(ReportFixStateKind.SinCommitear);
        delCuerpo.FixState.FromRecord.Should().BeFalse();
    }

    /// <summary>
    /// <b>Un arreglo que no tocó nada no dice «sin commitear»</b>: no hay nada que commitear. No
    /// deja registro —por eso el §0 encontró cuatro sesiones <c>fix</c> sin uno—, así que el estado
    /// se lee del cuerpo, y ahí <b>manda la lista de ficheros sobre la cita</b>: un informe anterior
    /// a R10 §7 escribía el aviso igualmente, advirtiendo de unos cambios que no existían.
    /// </summary>
    [Fact]
    public void Un_arreglo_que_no_toco_nada_no_pide_commitear_nada()
    {
        (ReportEntry entry, AuditSession session, string body) = FixCase(build: null, touched: false);

        ReportPage page = ReportPage.Compose(entry, session, body, null, fix: null);

        page.FixState!.Kind.Should().Be(ReportFixStateKind.SinCambios);
        page.FixState.Tone.Should().Be(ReportTone.Neutral, "ni bien ni mal: no pasó nada");
        page.Files.Should().BeEmpty();
        page.Lead.Should().Contain("sin cambios en el clon");
    }

    // ================================================================ §3 · cifras y barra

    /// <summary>
    /// <b>Cada cifra de un arreglo está escrita en su informe.</b> Los ficheros y sus recuentos se
    /// leen del cuerpo —no hay registro que los tenga: <c>fixes/</c> guarda rutas y huellas, no
    /// líneas—, y el coste del registro con la misma función de siempre.
    /// </summary>
    [Fact]
    public void Cada_cifra_de_un_arreglo_esta_escrita_en_su_informe()
    {
        (ReportEntry entry, AuditSession session, string body) = FixCase(Green());
        ReportPage page = ReportPage.Compose(entry, session, body);

        // «Ficheros» y «Cambios» se han ido: repetían lo que la barra de al lado ya dice.
        page.Stats.Select(s => s.Key).Should().NotContain(new[] { "files", "lines" });
        ReportPage.FilesSubtitle(page.Files).Should().Be("2 ficheros · 1 fuera del hallazgo, autorizado");
        body.Should().Contain("(+0 −38)").And.Contain("(+4 −1)",
            "los recuentos de la barra son los que el informe escribió");

        page.Stats.Single(s => s.Key == "duration").Value.Should().Be("54 s");
        page.Stats.Single(s => s.Key == "calls").Value.Should().Be("10");

        ReportStat build = page.Stats.Single(s => s.Key == "build");
        build.Value.Should().Be("Verde");
        build.Tone.Should().Be(ReportTone.Success);
        build.Subtitle.Should().Be("sin tests", "es un hecho del repositorio, no un resultado (H9.1 §3)");

        ReportStat coste = page.Stats.Single(s => s.Key == "cost");
        coste.Value.Should().Be("27,0");
        coste.Subtitle.Should().BeEmpty("un arreglo arregla un hallazgo: no hay reparto que hacer");
        body.Should().Contain("27,0 AI credits");

        page.Lead.Should().Be(
            "MEJ-0046 · 2 ficheros · +4 −39 · build verde · 27,0 AI credits · 10 llamadas · 54 s");
    }

    /// <summary>
    /// <b>El build en rojo lleva su razón corta</b>, y es la línea de veredicto que el informe
    /// escribió — no una frase nueva. Sin compilación no se pinta ni verde ni rojo (D-318).
    /// </summary>
    [Fact]
    public void El_build_en_rojo_dice_por_que_y_sin_compilacion_no_dice_nada()
    {
        (ReportEntry entry, AuditSession session, string body) = FixCase(Red());
        ReportPage rojo = ReportPage.Compose(entry, session, body);

        ReportStat build = rojo.Stats.Single(s => s.Key == "build");
        build.Value.Should().Be("Rojo");
        build.Tone.Should().Be(ReportTone.Danger);
        build.Subtitle.Should().StartWith("2 error(es) nuevo(s)");
        body.Should().Contain($"**Veredicto**: {rojo.Build!.Verdict}",
            "la razón corta es la línea del informe, letra por letra");
        rojo.Lead.Should().Contain("build rojo");

        // Y los errores y la salida se pliegan: son cientos de líneas de compilador.
        rojo.Build!.Detail.Should().Contain("Errores nuevos (2)").And.Contain("Salida completa");
        rojo.Build.Lead.Should().NotContain("Errores nuevos (2)");

        (entry, session, body) = FixCase(build: null);
        ReportPage sin = ReportPage.Compose(entry, session, body);
        sin.Stats.Single(s => s.Key == "build").Value.Should().Be(ReportPage.ReportsUnknown);
        sin.Lead.Should().NotContain("build");
    }

    /// <summary>
    /// <b>Una barra por fichero tocado, proporcional al que más cambió.</b> Repartir cada barra
    /// contra su propio total las dejaría todas llenas y no habría nada que comparar; y la lista de
    /// ficheros no se repite en el cuerpo, porque la barra ya la dice.
    /// </summary>
    [Fact]
    public void La_barra_lleva_una_fila_por_fichero_y_es_proporcional()
    {
        (ReportEntry entry, AuditSession session, string body) = FixCase(Green());
        ReportPage page = ReportPage.Compose(entry, session, body);

        page.Files.Select(f => f.Path).Should().Equal("src/Uno.cs", "src/Dos.cs");
        page.Files.Select(f => f.Tally).Should().Equal("+0 −38", "+4 −1");
        // EL ÁMBITO SALE DE LAS NOTAS DE LA SESIÓN (D-546), no del texto del informe.
        page.Files.Select(f => f.Scope).Should()
            .Equal(ReportFileScope.Hallazgo, ReportFileScope.Autorizado);
        page.Files.Select(f => f.Mark).Should().Equal("hallazgo", "fuera del hallazgo");

        // Y sin notas, sin marca: no se deduce del informe una decisión que se tomó y se anotó.
        session.Notes.Clear();
        ReportPage sinNotas = ReportPage.Compose(entry, session, body);
        sinNotas.Files.Should().OnlyContain(f => !f.HasMark);
        ReportPage.FilesSubtitle(sinNotas.Files).Should().Be("2 ficheros",
            "sin registro no se cuenta lo que salió del ámbito");

        // Proporcional: el fichero de 38 cambios llena la barra y el de 5 ocupa 5/38 de ella.
        int mayor = page.Files.Max(f => f.Total);
        mayor.Should().Be(38);
        (page.Files[1].Total / (double)mayor).Should().BeApproximately(5 / 38.0, 0.0001);

        // Y el cuerpo que se pinta ya no lleva la sección de ficheros: la dice la barra.
        page.Story.Should().NotContain("Ficheros tocados");
        page.Middle.Should().NotContain("Ficheros tocados");
    }

    // ================================================================ el cuerpo, entero

    /// <summary>
    /// <b>Ni una palabra se pierde</b> (D-441). El cuerpo se parte por sus secciones y cada trozo se
    /// pinta de una forma, pero la suma de los trozos vuelve a ser el documento — que es lo único
    /// que hace que reordenar y plegar no sea reescribir.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void El_partido_del_cuerpo_no_pierde_texto(bool verificacion)
    {
        string body = verificacion ? VerifyCase().Body : FixCase(Green()).Body;

        ReportSections cut = ReportSections.Split(body);

        string junto = string.Join(
            "\n", new[] { cut.Head }.Concat(cut.Parts).Append(cut.Foot).Where(p => p.Length > 0));
        Squash(junto).Should().Be(Squash(body));
        cut.Foot.Should().StartWith("---", "la firma cierra el documento, no la última sección");
    }

    /// <summary>
    /// <b>La ficha del documento lleva exactamente lo que la portada ya dice</b>, y nada más.
    /// <para>
    /// En una verificación: la cabecera, la cita de qué es verificar y las «Notas de la sesión»,
    /// que repiten los veredictos con otras palabras. En un arreglo: la cabecera y la cita del
    /// commit — el estado ya está en la portada y en grande. Lo que se decide con esta página —los
    /// veredictos, la prosa del arreglo, su compilación— <b>no</b> está ahí dentro.
    /// </para>
    /// </summary>
    [Fact]
    public void La_ficha_del_documento_lleva_la_cabecera_y_lo_que_repite_la_portada()
    {
        ReportPage verify = VerifyPage();

        verify.Sheet.Should().StartWith("# Verificación — App");
        verify.Sheet.Should().Contain("Verificar **juzga el código que hay ahora**");
        verify.Sheet.Should().Contain("## Notas de la sesión");
        verify.Sheet.Should().NotContain("## Veredictos",
            "los veredictos son el cuerpo, no la ficha");

        (ReportEntry entry, AuditSession session, string body) = FixCase(Green());
        ReportPage fix = ReportPage.Compose(entry, session, body);

        fix.Sheet.Should().StartWith("# Arreglo asistido — App");
        fix.Sheet.Should().Contain("NO están commiteados",
            "la cita no se borra ni se reescribe: se pliega (D-441)");
        foreach (string fuera in new[]
                 {
                     "## Qué cambió y por qué", "## Ficheros tocados", "## Compilación y tests",
                     "## Sugerencia de commit",
                 })
        {
            fix.Sheet.Should().NotContain(fuera, "eso es el cuerpo");
        }

        fix.Story.Should().Be("Se quitó el bloque muerto y se validó el parámetro.");
        fix.Commit.Should().Contain("Arregla lo que había que arreglar",
            "la sugerencia sale a su propio pliegue, sin reescribirse");
    }

    /// <summary>
    /// <b>Sin registro no hay portada</b>, tampoco en estos dos tipos: se pinta el cuerpo como hasta
    /// F36. No se inventa un cero para llenar la tarjeta (D-318).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sin_registro_los_dos_tipos_se_quedan_en_el_cuerpo(bool verificacion)
    {
        (ReportEntry entry, _, string body) = verificacion ? VerifyCase() : FixCase(Green());

        ReportPage page = ReportPage.Compose(entry with { Session = null }, null, body);

        page.Should().BeSameAs(ReportPage.Plain);
        page.HasCover.Should().BeFalse();
        page.HasVerdicts.Should().BeFalse();
        page.HasFixState.Should().BeFalse();
        page.Sheet.Should().BeEmpty("la vista pinta el markdown entero");
    }

    /// <summary>«Copiar resumen» sigue llevando la frase y luego las tarjetas, en los dos tipos.</summary>
    [Fact]
    public void Copiar_resumen_lleva_la_frase_y_luego_las_tarjetas()
    {
        foreach (ReportPage page in new[] { VerifyPage(), FixPage() })
        {
            string[] lines = page.Summary
                .Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            page.HasSummary.Should().BeTrue();
            lines[0].Should().Be(page.Lead);
            lines.Should().HaveCount(page.Stats.Count + 1);
        }
    }

    private static ReportPage FixPage()
    {
        (ReportEntry entry, AuditSession session, string body) = FixCase(Green());
        return ReportPage.Compose(entry, session, body);
    }


    // ================================================================ F36-2b · la segunda pasada

    /// <summary>
    /// <b>El resolutor por texto devuelve TEXTO, no markdown</b> (F36-2b §1.4).
    /// <para>
    /// «Código que se le enseñó» se pinta como un dato suelto, fuera del renderizador de markdown,
    /// y ahí un asterisco es un asterisco: la línea real de un veredicto sobre la unidad entera
    /// salía con ellos. Se comprueba con esa línea, la que <c>ReportBuilder</c> escribe.
    /// </para>
    /// </summary>
    [Fact]
    public void Lo_que_se_lee_del_cuerpo_sale_sin_asteriscos()
    {
        (ReportEntry entry, AuditSession session, string body) = VerifyCase(new[]
        {
            Line("BUG-0001", Severity.Alta, "no concluyente", VerifyBasis.Unidad),
        });

        // La línea del informe SÍ lleva la negrita: el `.md` no se toca (D-441).
        body.Should().Contain("**la unidad entera**");

        ReportPage page = ReportPage.Compose(entry, session, body);

        ReportVerdict verdict = page.Verdicts.Single();
        verdict.Basis.Should().StartWith("la unidad entera, porque");
        verdict.Basis.Should().NotContain("*", "un asterisco fuera del markdown es un asterisco");
        verdict.Where.Should().NotContain("`", "y una comilla invertida, una comilla invertida");
    }

    /// <summary>
    /// <b>La firma va al pie, en una línea</b> (F36-2b §1.5). Como párrafo de markdown quedaba
    /// suelta a media pantalla; la raya que la precede es del dibujo, no del texto — el <c>.md</c>
    /// sigue teniéndola.
    /// </summary>
    [Fact]
    public void La_firma_sale_en_una_linea_y_sin_la_raya()
    {
        foreach (ReportPage page in new[] { VerifyPage(), FixPage() })
        {
            page.HasFoot.Should().BeTrue();
            page.Foot.Should().StartWith("---", "el trozo del documento la lleva");
            page.FootLine.Should().Be("Atalaya · Org");
            page.FootLine.Should().NotContain("-", "la raya es del dibujo, no de la firma");
        }
    }

    /// <summary>
    /// <b>La sugerencia de commit se pliega cuando ya es historia</b> (F36-2b §2): con el arreglo
    /// commiteado el mensaje está en el commit, y esto es el borrador de algo que ya se hizo. Con
    /// «sin commitear» sigue abierta, porque entonces es lo que hay que usar.
    /// </summary>
    [Fact]
    public void La_sugerencia_de_commit_se_pliega_cuando_el_arreglo_ya_esta_commiteado()
    {
        Finding finding = Fixed("MEJ-0046");

        (ReportEntry entry, AuditSession session, string body) = FixCase(Green(), finding: finding);
        ReportPage abierta = ReportPage.Compose(entry, session, body, null, Record(session, null, null));
        abierta.HasCommit.Should().BeTrue();
        abierta.CommitDone.Should().BeFalse("sin commitear, es lo que hay que usar");
        abierta.CommitTitle.Should().Be("Sugerencia de commit");

        (entry, session, body) = FixCase(Green(), sha: "e660243", finding: finding);
        ReportPage plegada = ReportPage.Compose(
            entry, session, body, null, Record(session, "e660243", null));
        plegada.CommitDone.Should().BeTrue();
        plegada.CommitTitle.Should().Be("Sugerencia de commit · usada en e660243");
        plegada.Commit.Should().Contain("Arregla lo que había que arreglar",
            "se pliega, no se borra (D-441)");

        // Y verificado también: el commit sigue estando, así que el borrador sigue siendo historia.
        var hub = new ReportFindingIndex(
            new Dictionary<string, string>(), new Dictionary<string, string>())
        {
            LastVerdicts = new Dictionary<string, ReportVerdictEvent>(StringComparer.Ordinal)
            {
                [finding.Id.ToString()] =
                    new ReportVerdictEvent(session.StartedUtc.AddHours(1), FindingEvent.Resolved),
            },
        };
        ReportPage verificado = ReportPage.Compose(
            entry, session, body, hub, Record(session, "e660243", null));
        verificado.FixState!.Kind.Should().Be(ReportFixStateKind.Verificado);
        verificado.CommitDone.Should().BeTrue();
    }

    /// <summary>
    /// <b>El correo del autor no empuja la portada</b> (F36-2b §2). Se recorta con puntos
    /// suspensivos y el entero queda en el tooltip: recortar sin decirlo es lo que P-01 prohíbe.
    /// </summary>
    [Fact]
    public void El_autor_del_commit_se_recorta_cuando_no_cabe()
    {
        (ReportEntry entry, AuditSession session, string body) = FixCase(Green(), sha: "5249598");

        ReportPage corto = ReportPage.Compose(
            entry, session, body, null, Record(session, "5249598", "Ana <a@x.io>"));
        corto.FixState!.DetailElided.Should().BeFalse();
        corto.FixState.DetailShort.Should().Be(corto.FixState.Detail);

        ReportPage largo = ReportPage.Compose(
            entry, session, body, null,
            Record(session, "5249598", "Álvaro López Ciller <alvaro.lopez.ciller@empresa.com>"));
        largo.FixState!.DetailElided.Should().BeTrue();
        largo.FixState.DetailShort.Should().EndWith("…").And.HaveLength(ReportFixState.Fit + 1);
        largo.FixState.Detail.Should().Contain("alvaro.lopez.ciller@empresa.com",
            "el entero sigue estando: es lo que va al tooltip");
    }

    /// <summary>
    /// <b>El carril no existe si no tiene índice que aportar</b> (F36-2b §1.3), y una verificación
    /// de un veredicto es justo ese caso: la tarjeta del índice repetía la única del cuerpo.
    /// </summary>
    [Fact]
    public void Una_verificacion_de_un_veredicto_no_tiene_carril()
    {
        VerifyPage().HasRail.Should().BeFalse("un veredicto no necesita índice");
        FixPage().HasRail.Should().BeFalse("un arreglo no tiene tarjetas de cuerpo que indexar");

        // Con cuatro, sí. Se monta con cuatro veredictos de verdad, no con un contador a mano.
        (ReportEntry entry, AuditSession session, string body) = VerifyCase(new[]
        {
            Line("BUG-0001", Severity.Alta, "confirmado"),
            Line("BUG-0002", Severity.Media, "resuelto"),
            Line("BUG-0003", Severity.Baja, "resuelto"),
            Line("BUG-0004", Severity.Baja, "resuelto"),
        });

        ReportPage cuatro = ReportPage.Compose(entry, session, body);
        cuatro.Verdicts.Should().HaveCount(4);
        cuatro.HasRail.Should().BeTrue();
    }

    /// <summary>
    /// <b>La tarjeta de acciones lleva las acciones que la página puede ofrecer</b> (F36-2b §1.2):
    /// las dos de siempre, y en un arreglo la tercera — que aquí es la única vía a la ficha.
    /// </summary>
    [Fact]
    public void La_tarjeta_de_acciones_lleva_las_dos_o_las_tres()
    {
        var dos = new ActionsTile(CanCopy: true, CanOpenFinding: false, "Ver el hallazgo");
        dos.Span.Should().Be(1);
        dos.CanOpenFinding.Should().BeFalse();

        var tres = new ActionsTile(CanCopy: true, CanOpenFinding: true, "Ver el hallazgo (MEJ-0046)");
        tres.OpenFindingLabel.Should().Contain("MEJ-0046",
            "el identificador va en el rótulo, no en un tooltip");

        // Y el enlace al anexo no se va con el carril: sin carril baja a la ficha del documento.
        string xaml = ViewLayout.Xaml("ReportsView.xaml");
        xaml.Split("Content=\"Anexo técnico ↓\"").Should().HaveCount(3,
            "hay un enlace en el carril y otro en la ficha, uno visible cada vez");
        xaml.Should().Contain("Binding HasAnnexOutsideRail");
    }

    /// <summary>
    /// <b>Cuatro barras a la vista y el resto en un «+N más»</b> (F36-2b §2). La fila no puede
    /// crecer con el número de ficheros: con doce, la portada se iría de la pantalla.
    /// </summary>
    [Theory]
    [InlineData(3, 0, "")]
    [InlineData(4, 0, "")]
    [InlineData(5, 1, "+1 más")]
    [InlineData(9, 5, "+5 más")]
    public void Las_barras_de_mas_de_cuatro_ficheros_se_despliegan(int files, int hidden, string label)
    {
        var bars = Enumerable.Range(0, files)
            .Select(i => new FileBar(
                $"src/{i}.cs", "+1 −1", "hallazgo", false, "t",
                new GridLength(1, GridUnitType.Star),
                new GridLength(1, GridUnitType.Star),
                new GridLength(1, GridUnitType.Star)))
            .ToList();

        var tile = new FilesTile("x", bars, Math.Max(0, files - ReportsViewModel.FilesAtAGlance));

        tile.Hidden.Should().Be(hidden);
        tile.HasHidden.Should().Be(hidden > 0);
        if (hidden > 0)
        {
            tile.MoreLabel.Should().Be(label);
        }

        tile.Bars.Should().HaveCount(files, "todas están: lo que cambia es cuántas se ven de golpe");
        tile.Span.Should().Be(2);
    }

    /// <summary>
    /// <b>El build dice «sin tests» o lo que el informe diga de ellos</b>, y en rojo la razón corta.
    /// No se cuenta cuántos tests hay: ni el registro ni el informe lo escriben, y sacarlo de la
    /// salida del compilador sería inventarse una medida (D-318).
    /// </summary>
    [Fact]
    public void El_subtitulo_del_build_sale_de_lo_que_el_informe_dice_de_los_tests()
    {
        (ReportEntry entry, AuditSession session, string body) = FixCase(Green());
        ReportPage sinTests = ReportPage.Compose(entry, session, body);
        sinTests.Stats.Single(s => s.Key == "build").Subtitle.Should().Be("sin tests");
        body.Should().Contain("no tiene proyectos de tests", "es un hecho del repositorio (H9.1 §3)");

        // Con proyecto de tests, lo que el informe escribió en su línea de «Tests».
        AuditSession conTests = FixSession(Ulids.NewUlid().ToString(), "MEJ-0046");
        Finding finding = Fixed("MEJ-0046");
        string markdown = ReportBuilder.BuildFixReport(
            App(), conTests, finding,
            new List<(string, string, bool)> { ("src/Uno.cs", "+1 −1", true) },
            "resumen", null, "título", string.Empty, Green(), "Org",
            new FixTestSituation("App/App.csproj", new[] { "App.Tests/App.Tests.csproj" }, true),
            TestRates.Table());
        (string conBody, _) = Atalaya.App.ViewModels.ReportsViewModel.SplitAnnex(markdown);

        var conEntry = new ReportEntry(
            "app", "App", conTests.Id.ToString(), "x.md", "t", ReportKind.Sesion,
            conTests.StartedUtc, ReportDateSource.Session, conTests.By, "Fix", null, null, null,
            CreditCalculator.Calculate(conTests, TestRates.Table()).Credits,
            CostFormat.BillingUnit, HasSession: true, Session: conTests);

        ReportPage page = ReportPage.Compose(conEntry, conTests, conBody);
        page.Stats.Single(s => s.Key == "build").Subtitle.Should().Be("pasan");
        conBody.Should().Contain("**Tests**: pasan");
    }

    // ================================================================ el dibujo

    /// <summary>
    /// <b>El borde de una tarjeta de veredicto es el VEREDICTO, no la gravedad</b>, y las tarjetas
    /// se parten en dos columnas por el mismo umbral que las de hallazgo — es el mismo tipo de
    /// bloque, y dos umbrales distintos para lo mismo se separan al primer cambio.
    /// <para>
    /// Va sobre el marcado porque es donde vive la regla: el color lo eligen unos disparadores sobre
    /// <c>Kind</c>, y si alguien los pusiera sobre <c>Severity</c> nada fallaría — se pintaría otra
    /// cosa, en silencio.
    /// </para>
    /// </summary>
    [Fact]
    public void La_tarjeta_de_un_veredicto_se_pinta_por_el_veredicto_y_no_por_la_gravedad()
    {
        string xaml = ViewLayout.Xaml("ReportsView.xaml");

        int at = xaml.IndexOf("<Style x:Key=\"Verdict.Card\"", StringComparison.Ordinal);
        at.Should().BeGreaterThan(0);
        string style = xaml[at..xaml.IndexOf("</Style>", at, StringComparison.Ordinal)];

        style.Should().NotContain("Binding Severity",
            "aquí lo que importa es la resolución, no la gravedad");
        foreach (string kind in new[] { "Resuelto", "Activo", "NoLocalizado" })
        {
            style.Should().Contain($"Binding Kind}}\" Value=\"{kind}\"");
        }

        // Y los tres tonos son los RESERVADOS de la casa: verde de éxito, el azul de «Activo» de la
        // pastilla de estado de un hallazgo y el ámbar de aviso. Ninguno nuevo (D-316).
        foreach (string brush in new[] { "Brush.Success.Ink", "Brush.Primary.Ink", "Brush.Warning.Ink" })
        {
            style.Should().Contain(brush);
        }

        // Dos columnas con el MISMO umbral que las tarjetas de hallazgo.
        int cards = xaml.IndexOf("x:Name=\"VerdictCards\"", StringComparison.Ordinal);
        cards.Should().BeGreaterThan(0);
        xaml[cards..(cards + 900)].Should()
            .Contain("MinColumnWidth=\"{x:Static c:ReportLayout.CardMinWidth}\"")
            .And.Contain("MaxColumns=\"2\"");
    }

    /// <summary>
    /// Y los cuatro colores del rosco de veredictos salen de <b>claves del tema</b>, no de una
    /// paleta nueva: son los mismos «resuelto», «activo» y «aviso» que la aplicación pinta desde
    /// siempre, así que no hay una segunda copia que se pueda desviar (D-316, D-317).
    /// </summary>
    [Fact]
    public void Los_colores_del_rosco_de_veredictos_son_los_reservados_de_la_casa()
    {
        ReportVerdicts.BrushKey(ReportVerdicts.Tone(ReportVerdictKind.Resuelto))
            .Should().Be("Brush.Success.Ink");
        ReportVerdicts.BrushKey(ReportVerdicts.Tone(ReportVerdictKind.Activo))
            .Should().Be("Brush.Primary.Ink");
        ReportVerdicts.BrushKey(ReportVerdicts.Tone(ReportVerdictKind.NoLocalizado))
            .Should().Be("Brush.Warning.Ink");
        ReportVerdicts.BrushKey(ReportVerdicts.Tone(ReportVerdictKind.NoConcluyente))
            .Should().Be("Brush.TextMuted");

        // Las claves existen en los dos temas. Una clave inventada no falla al compilar: se queda
        // sin pincel y el tramo desaparece.
        foreach (string tema in new[] { "Palette.Light.xaml", "Palette.Dark.xaml" })
        {
            string palette = Theme(tema);
            foreach (string clave in new[]
                     {
                         "Brush.Success.Ink", "Brush.Primary.Ink", "Brush.Warning.Ink", "Brush.TextMuted",
                     })
            {
                palette.Should().Contain($"x:Key=\"{clave}\"");
            }
        }
    }

    /// <summary>Un diccionario de tema, leído del repositorio como <c>ViewLayout</c> lee una vista.</summary>
    private static string Theme(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Atalaya.App", "Themes", file));
    }

    private static string Squash(string text)
        => new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
}
