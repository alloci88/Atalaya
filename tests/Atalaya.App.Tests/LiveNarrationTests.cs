using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.14 — la narración de V5 no puede contar una historia distinta de la sesión.
/// <para>
/// El parte: durante la primera pasada, la columna de actividad marcaba con ⚖ hallazgos que no
/// estaban disputados; al terminar, el resumen decía 0 disputados y los hallazgos persistidos
/// tampoco lo estaban. Dos relatos del mismo hecho, y el que el usuario ve primero era el falso.
/// </para>
/// <para>
/// Es la misma lección del bug de selección: dos cálculos paralelos de la misma verdad acaban
/// divergiendo. Aquí el invariante es que <b>lo que se narra en vivo y lo que se escribe en el hub
/// son el mismo suceso contado dos veces</b>, así que sus cuentas tienen que cuadrar.
/// </para>
/// </summary>
public sealed class LiveNarrationTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private readonly ServiceProvider _provider;
    private readonly MachineConfigStore _machines;
    private const string RepoUrl = "https://example.invalid/org/app.git";
    private const string UnitPath = "src/A.cs";

    public LiveNarrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-narration", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _settings = new SettingsService(_paths);
        _settings.Load();
        AppSettings s = _settings.Current;
        s.MaxPassesPerUnit = 1;
        _settings.Save(s);

        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "app", Name = "App", RepoUrl = RepoUrl, Stack = TechStack.DotNet, CurrentCycle = 1,
        });

        string clone = Path.Combine(_root, "clone");
        TestFactory.MakeClone(clone, RepoUrl);
        Directory.CreateDirectory(Path.Combine(clone, "src"));
        File.WriteAllText(Path.Combine(clone, "src", "A.cs"), "class A { void M() { } }");
        _machines.SetClonePath("app", clone);
        _hub.Store.WriteInventory("app", new InventoryCycle
        {
            CycleN = 1,
            Units = { new InventoryUnit { Path = UnitPath, Module = "M", State = UnitState.Pendiente } },
        });

        var services = new ServiceCollection();
        services.AddSingleton(_hub);
        services.AddSingleton(_paths);
        services.AddSingleton(_settings);
        services.AddSingleton(_machines);
        services.AddSingleton<IUlidFactory>(_ulids);
        services.AddSingleton<FindingIngestionService>();
        services.AddSingleton<ReconciliationService>();
        services.AddSingleton<OpenSessionStore>();
        services.AddTransient<SessionCoordinator>();
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    /// <summary>Un hallazgo ya existente en la unidad, visto en una sesión anterior.</summary>
    private Finding SeedFinding(string title)
    {
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow.AddDays(-3), AuditMode.Lotes, "viejo", "alvaro");
        var f = new Finding
        {
            Id = _ulids.NewUlid(),
            RuleId = "errores.null.desreferencia",
            Pillar = Pillar.Errores,
            Severity = Severity.Alta,
            Confidence = Confidence.Media,
            Title = title,
            Locations = { new Location(UnitPath, 1) },
            Origin = AuditMode.Lotes,
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
        _hub.Store.WriteFinding("app", f);
        return f;
    }

    /// <summary>
    /// Un hallazgo nuevo. <paramref name="symbol"/> y <paramref name="line"/> se separan a propósito
    /// entre llamadas: desde F24 dos hallazgos con la misma regla, el mismo miembro y la misma línea
    /// son una variante y la puerta rebota el segundo. Aquí lo que se narra son hallazgos
    /// DISTINTOS, así que se escriben distintos.
    /// </summary>
    private static SubmitFindingArgs NewFinding(string title, string symbol = "M", int line = 1)
        => new("errores.recursos.no-liberado", "errores", "media", title, "desc", "impacto", "reco",
            new[] { new SubmitLocation(UnitPath, line, null) }, symbol);

    /// <summary>
    /// Corre una sesión de verdad a través del servicio en vivo, de modo que la narración se puebla
    /// por el mismo camino que en producción, y devuelve el servicio ya cerrado.
    /// </summary>
    /// <param name="wire">
    /// Se ejecuta sobre el coordinador recién creado, para poder escuchar sus eventos. Es la única
    /// forma de comprobar el ORDEN en el que ocurren las cosas (F30 §1): desde fuera solo se ve el
    /// resultado, y el resultado es el mismo se narre cuando se narre.
    /// </param>
    private async Task<(LiveSessionService Live, SessionResult Result)> Run(
        Func<AuditUnitRequest, IEnumerable<SubmitFindingArgs>>? audit = null,
        Func<AuditUnitRequest, IEnumerable<VerdictArgs>>? reconcile = null,
        Action<SessionCoordinator>? wire = null)
    {
        var agent = new FakeCopilotAgent(auditScript: audit, reconcileScript: reconcile);
        SessionResult? captured = null;
        var live = new LiveSessionService(
            () =>
            {
                var coordinator = new SessionCoordinator(
                    _hub, _provider.GetRequiredService<FindingIngestionService>(),
                    _provider.GetRequiredService<ReconciliationService>(), _machines, _ulids, agent, _settings);
                wire?.Invoke(coordinator);
                return coordinator;
            },
            agent, _provider.GetRequiredService<OpenSessionStore>(), _hub);
        live.Completed += r => captured = r;

        await live.StartAsync(new SessionRequest("app", AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath });
        for (int i = 0; i < 400 && live.IsRunning; i++)
        {
            await Task.Delay(25);
        }

        live.IsRunning.Should().BeFalse("la sesión de prueba tiene que haber terminado");
        captured.Should().NotBeNull("sin resultado no hay con qué contrastar la narración");
        return (live, captured!);
    }

    /// <summary>
    /// <b>NI UN TOKEN MÁS</b> (F30, norma de la fase). Enseñar mejor lo que ya pasa no puede
    /// cambiar lo que pasa.
    /// <para>
    /// <b>Cómo se comprueba, y por qué así.</b> El hilo de actividad es un ESPEJO de la traza que
    /// la aplicación ya escribía: <c>SessionToolbox.ToolCallLog</c> lleva desde F3 apuntando cada
    /// llamada a herramienta, y el coordinador la vuelca en las notas de la sesión. Lo que F30
    /// cambia es cuándo se ve, no qué se hace. Así que lo que se exige es <b>uno a uno</b>: tantas
    /// líneas narradas en vivo como líneas <c>tool ·</c> tiene la sesión persistida, ni una más.
    /// Una narración que costara algo —una herramienta extra, un turno de cortesía— rompería esa
    /// igualdad por arriba; una que se perdiera eventos, por abajo.
    /// </para>
    /// <para>
    /// <b>Por qué no se comparan dos sesiones.</b> Era la primera idea y no vale: la segunda corre
    /// sobre el hub que dejó la primera, así que reconcilia los hallazgos que la otra creó y sus
    /// contadores son legítimamente distintos. Comparar eso habría medido el estado del hub, no el
    /// coste de narrar.
    /// </para>
    /// <para>
    /// Y protege el error concreto que esta fase podía cometer: pedirle al modelo que narre —una
    /// frase en el prompt, una herramienta más— para que la pantalla tuviera algo que enseñar. Eso
    /// es coste, y ésta es la red que lo cantaría.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Narrar_es_un_espejo_de_lo_que_ya_pasaba_y_no_cuesta_nada()
    {
        var narrado = new List<ActivityNote>();

        await Run(
            audit: _ => new[] { NewFinding("fuga de stream") },
            wire: c => c.ActivityNoted += narrado.Add);

        AuditSession persisted = _hub.Store.ListSessions("app").Should().ContainSingle().Subject;
        List<string> enLasNotas = persisted.Notes
            .Where(n => n.Contains(": tool · ", StringComparison.Ordinal))
            .ToList();

        enLasNotas.Should().NotBeEmpty("sin herramientas en las notas esto no compararía nada");

        narrado.Where(n => n.Kind == ActivityNoteKind.Tool).Should().HaveCount(enLasNotas.Count,
            "el hilo enseña las mismas llamadas que ya se apuntaban, ni una más ni una menos: "
            + "si narrar costara una herramienta de más, sobraría aquí");
    }

    /// <summary>
    /// <b>F30 §1b — el hueco entre pasadas tiene dueño, y se marca en el orden en que ocurre.</b>
    /// <para>
    /// <b>Lo medido.</b> Entre cerrar una pasada y el primer evento de la siguiente, Atalaya tarda
    /// <b>16 ms</b> —releer los 149 hallazgos del hub real del usuario y recomponer el prompt— y una
    /// pasada dura <b>16,9 s</b> de media (26 unidades, 92 pasadas, 1.559 s de sesiones reales). El
    /// <b>99,9 %</b> del silencio es el modelo. Por eso los dos tramos se marcan: el de Atalaya con
    /// su medida, ya terminado, y la entrega, a partir de la cual el pie cuenta.
    /// </para>
    /// <para>
    /// <b>Lo que se fija es el ORDEN</b>, que es lo único que puede romperse en silencio: preparar
    /// va antes de entregar, y entregar va antes de que llegue nada del modelo. Si algún día la
    /// entrega se emitiera después del primer evento del turno, el pie anclaría la espera en el
    /// sitio equivocado y diría que se espera a un modelo que ya está contestando.
    /// </para>
    /// </summary>
    [Fact]
    public async Task El_turno_se_marca_como_entregado_antes_de_que_llegue_nada_del_modelo()
    {
        var order = new List<ActivityNote>();

        await Run(
            audit: _ => new[] { NewFinding("fuga de stream") },
            wire: c => c.ActivityNoted += order.Add);

        int preparado = order.FindIndex(n => n.Text.StartsWith("Turno preparado", StringComparison.Ordinal));
        int entregado = order.FindIndex(n => n.Kind == ActivityNoteKind.Handover);
        int primeraHerramienta = order.FindIndex(n => n.Kind == ActivityNoteKind.Tool);

        preparado.Should().BeGreaterThanOrEqualTo(0, "el tramo de Atalaya se dice, con su medida");
        entregado.Should().BeGreaterThan(preparado,
            "primero Atalaya termina lo suyo y después entrega: al revés, el hueco no tendría dueño");
        primeraHerramienta.Should().BeGreaterThan(entregado,
            "nada del modelo puede llegar antes de que el turno se haya entregado");

        // Y la medida del tramo va en la línea: sin el número, «preparando» se lee como «Atalaya
        // está tardando», que es justo lo que la medición desmiente.
        order[preparado].Text.Should().MatchRegex(@"\d+ ms$");
    }

    /// <summary>
    /// <b>Y el pie no le echa al modelo un tiempo que no es suyo</b> (F30 §1b). «Esperando al
    /// modelo» solo existe con el turno EN EL AIRE: desde la entrega y hasta que llega la primera
    /// señal. Mientras Atalaya prepara el suyo no se está esperando a nadie, y al terminar la
    /// sesión tampoco.
    /// </summary>
    [Fact]
    public async Task Esperando_al_modelo_solo_cuenta_con_el_turno_en_el_aire()
    {
        (LiveSessionService live, _) = await Run(audit: _ => new[] { NewFinding("fuga de stream") });

        live.IsRunning.Should().BeFalse();
        live.IsWaiting.Should().BeFalse("terminada la sesión no se espera a nadie");
        live.SinceLastEvent.Should().Be(TimeSpan.Zero, "y el reloj de la espera no corre solo");
    }

    /// <summary>
    /// <b>UNA SESIÓN NUEVA NO HEREDA EL PIE DE LA ANTERIOR</b> (F30 §2e).
    /// <para>
    /// <b>El parte.</b> Al arrancar una sesión con Copilot, el pie enseñaba «incluido en tu
    /// suscripción de Claude» —de la sesión anterior, que había corrido con Claude Code— hasta que
    /// llegaba la primera muestra de consumo. Es un pie hablando de otra sesión, y en la única
    /// pantalla donde el usuario mira el coste mientras se gasta.
    /// </para>
    /// <para>
    /// <b>Por qué no lo veía nadie.</b> <c>Reset</c> limpia dieciocho campos y estos tres no
    /// estaban —el proveedor, el coste con su procedencia y su unidad—, así que el defecto solo
    /// aparecía en la <b>segunda</b> sesión de una ejecución de la aplicación y solo si la primera
    /// había sido de la otra casa. Un test que arranque una sola sesión no puede verlo; éste
    /// arranca la segunda con el rastro de la primera puesto a mano, que es la única forma de
    /// mirar el instante exacto en el que el defecto existía.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Una_sesion_nueva_no_hereda_el_pie_de_la_anterior()
    {
        (LiveSessionService live, _) = await Run(audit: _ => new[] { NewFinding("fuga de stream") });

        // Lo que la sesión anterior dejó escrito en el pie.
        live.Provider = "claude-code";
        live.CostResult = new CostResult(12.5m, Model: "opus");
        live.Calls = 7;
        live.Close().Should().BeTrue();

        // Y el instante EXACTO en el que arranca la siguiente: `Reset` es quien cierra con su
        // aviso, así que el primer repintado de la sesión nueva es el que enseña lo que se hereda.
        var first = new TaskCompletionSource<(string? Provider, CostResult Cost, int Calls)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Snapshot() => first.TrySetResult((live.Provider, live.CostResult, live.Calls));
        live.Changed += Snapshot;

        await live.StartAsync(
            new SessionRequest("app", AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath });
        (string? provider, CostResult cost, int calls) = await first.Task;
        live.Changed -= Snapshot;

        provider.Should().BeNull("el pie no puede etiquetar con una casa la sesión de la otra");
        cost.HasValue.Should().BeFalse("ni enseñar el coste de la sesión anterior como si fuera éste");
        calls.Should().Be(0);

        for (int i = 0; i < 400 && live.IsRunning; i++)
        {
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// <b>NINGÚN TRAMO DEL MODELO SE QUEDA SIN FRASE EN EL PIE</b> (F30 §2e).
    /// <para>
    /// <b>El parte.</b> Tras la prosa del modelo el pie se quedaba en «Unidad 1 de 1 · 00:28 ·
    /// 0 llamadas» y no volvía a decir nada, con el modelo escribiendo el reporte durante medio
    /// minuto. Dos causas, y ésta es la de fondo: la línea de «el modelo está escribiendo esto»
    /// tocaba el reloj de la espera <b>sin cambiar la frase</b>, así que el pie se quedaba con la
    /// del hito anterior — y en una unidad sin hallazgos vivos, donde el modelo no dice ni una
    /// palabra antes de reportar, esa frase era «esperando al modelo» a secas.
    /// </para>
    /// <para>
    /// <b>Lo que se fija es que la frase EXISTA SIEMPRE</b>, para todas las formas de
    /// <see cref="ToolStream"/>. Es lo único que puede romperse en silencio aquí: quien añada un
    /// tramo nuevo —como el razonamiento en esta misma tanda— y olvide su frase deja el pie mudo
    /// otra vez, y mudo es exactamente igual que colgado.
    /// </para>
    /// </summary>
    [Fact]
    public void Todo_tramo_que_el_modelo_escribe_tiene_frase_para_el_pie()
    {
        ToolStream[] tramos =
        [
            new ToolStream(ToolStreamPhase.Reasoning, string.Empty),
            new ToolStream(ToolStreamPhase.Started, "submit_findings"),
            new ToolStream(ToolStreamPhase.Input, "submit_findings", 3, "Credenciales embebidas"),
            new ToolStream(ToolStreamPhase.Started, "report_verdicts"),
            new ToolStream(ToolStreamPhase.Started, "add_locations"),
            new ToolStream(ToolStreamPhase.Started, "read_signatures"),
            new ToolStream(ToolStreamPhase.Started, "una_herramienta_que_todavia_no_existe"),
        ];

        foreach (ToolStream tramo in tramos)
        {
            ActivityWording.Writing(tramo).Should().NotBeNullOrWhiteSpace(
                "el hilo tiene que poder decir qué está pasando");
            ActivityWording.WaitingFor(tramo).Should().NotBeNullOrWhiteSpace(
                "y el pie también: sin frase se queda con la del hito anterior, que habla de otra cosa");

            // Y la frase del pie es COMPLETA, no un sufijo de «esperando al modelo»: el pie la
            // escribe tal cual y le pone el contador. Empezar por «esperando» daría «esperando al
            // modelo escribiendo el reporte», que es la frase que §2e viene a arreglar.
            ActivityWording.WaitingFor(tramo).Should().NotStartWith("esperando");
        }

        ActivityWording.WaitingFor(new ToolStream(ToolStreamPhase.Reasoning, string.Empty))
            .Should().Be("razonando",
                "el tramo que la traza midió en 22 s de los 62 de una pasada tiene nombre propio");
    }

    /// <summary>Todas las líneas de actividad narradas en la sesión, de todas las unidades y pasadas.</summary>
    private static List<ActivityEntry> Narration(LiveSessionService live)
        => live.Units.SelectMany(u => u.Passes).SelectMany(p => p.Entries).Where(e => e.IsEvent).ToList();

    private static int Glyphs(LiveSessionService live, string glyph)
        => Narration(live).Count(e => e.Glyph == glyph);

    private static SummaryLine? Line(LiveSessionService live, string label)
        => live.Summary.FirstOrDefault(l => l.Label == label);

    // ================================================================ el parte

    /// <summary>
    /// <b>El bug.</b> Una sesión que solo encuentra hallazgos nuevos no puede narrar ni una sola
    /// disputa. El ⚖ colgaba de <c>Disputes.Count</c> a través de un conversor que daba «visible»
    /// a cualquier valor no nulo — el cero incluido—, así que se pintaba en TODOS.
    /// </summary>
    [Fact]
    public async Task Sin_disputas_no_se_narra_ninguna_disputa()
    {
        (LiveSessionService live, SessionResult result) = await Run(
            audit: _ => new[]
            {
                NewFinding("fuga de stream"),
                NewFinding("handle sin cerrar", symbol: "N", line: 20),
            });

        result.Counters.Disputed.Should().Be(0, "nadie ha disputado nada");
        result.Counters.New.Should().Be(2);

        Glyphs(live, "⚖").Should().Be(0, "la narración no puede inventarse disputas");
        Glyphs(live, "＋").Should().Be(2);
        Line(live, "Disputados").Should().BeNull("una línea de resumen sin contenido no se pinta");

        live.Findings.Should().OnlyContain(f => f.Disputes.Count == 0);
        _hub.Store.ListFindings("app").Should().OnlyContain(f => f.Disputes.Count == 0);
    }

    /// <summary>
    /// Y la insignia ⚖ de la fila: con cero disputas se esconde. Es el conversor el que mentía, así
    /// que se interroga directamente con lo que la vista le enlaza.
    /// </summary>
    [Fact]
    public void La_insignia_de_disputa_se_esconde_con_cero_disputas()
    {
        IValueConverter converter = new Atalaya.App.NotEmptyToVisibilityConverter();

        object Convert(object value) => converter.Convert(value, typeof(Visibility), null!, CultureInfo.InvariantCulture);

        Convert(0).Should().Be(Visibility.Collapsed, "cero disputas es NINGUNA disputa");
        Convert(1).Should().Be(Visibility.Visible);
        Convert(new List<int>()).Should().Be(Visibility.Collapsed, "una colección vacía está vacía");
        Convert(new List<int> { 1 }).Should().Be(Visibility.Visible);
        Convert(false).Should().Be(Visibility.Collapsed);
        Convert(null!).Should().Be(Visibility.Collapsed);
        // Y lo de siempre sigue igual: es un conversor de cadenas en la mayoría de sus usos.
        Convert("  ").Should().Be(Visibility.Collapsed);
        Convert("algo").Should().Be(Visibility.Visible);
    }

    /// <summary>La vista enlaza la insignia al recuento de disputas del propio hallazgo, y a nada más.</summary>
    [Fact]
    public void La_insignia_se_enlaza_al_recuento_de_disputas()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        string xaml = File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "Atalaya.App", "Views", "SessionView.xaml"));

        xaml.Should().Contain("{Binding Disputes.Count, Converter={StaticResource NotEmptyToVisibility}}");
    }

    // ================================================================ el invariante

    /// <summary>
    /// <b>Una sola verdad.</b> Con una sesión que produce de todo —una reconfirmación, una disputa
    /// y un «arreglado» sin evidencia de cambio—, cada glifo de la narración en vivo tiene que
    /// aparecer tantas veces como dice el contador de la sesión.
    /// <para>
    /// Son DOS sesiones a propósito: para que un «arreglado» se degrade hace falta que el hallazgo
    /// se hubiera visto en este mismo commit y con esta misma unidad (F5.1b), y eso solo lo consigue
    /// una sesión anterior de verdad — sembrarlo a mano deja un sello viejo que sí acredita cambio.
    /// </para>
    /// </summary>
    [Fact]
    public async Task La_narracion_en_vivo_cuadra_con_los_contadores_de_la_sesion()
    {
        Finding presente = SeedFinding("sigue ahí");
        Finding disputado = SeedFinding("el auditor discrepa");

        // Primera sesión: nace un hallazgo con el sello de ESTE commit y ESTA unidad.
        (LiveSessionService first, SessionResult firstResult) = await Run(
            audit: _ => new[] { NewFinding("fuga de stream") },
            reconcile: _ => new[]
            {
                new VerdictArgs(presente.Id.ToString(), "presente", "sigue"),
                new VerdictArgs(disputado.Id.ToString(), "presente", "sigue"),
            });
        firstResult.Counters.New.Should().Be(1);
        Glyphs(first, "＋").Should().Be(1, "también en la primera: un ＋ por hallazgo nuevo");
        Glyphs(first, "⚖").Should().Be(0, "y ni una disputa donde no la hubo");

        Finding recien = _hub.Store.ListFindings("app").Single(f => f.Title == "fuga de stream");

        // Segunda sesión, sin tocar el código: el «arreglado» sobre el recién nacido no tiene
        // evidencia de cambio y se degrada; el otro se disputa.
        (LiveSessionService live, SessionResult result) = await Run(
            reconcile: _ => new[]
            {
                new VerdictArgs(presente.Id.ToString(), "presente", "sigue en el código"),
                new VerdictArgs(disputado.Id.ToString(), "no-es-defecto", "esto nunca fue un defecto"),
                new VerdictArgs(recien.Id.ToString(), "arreglado", "ya no está"),
            });

        SessionCounters c = result.Counters;
        c.New.Should().Be(0);
        c.Disputed.Should().Be(1);
        c.ResolutionsRefused.Should().Be(1, "la unidad no cambió: «arreglado» se degrada a presente");

        Glyphs(live, "＋").Should().Be(c.New, "un ＋ por hallazgo nuevo");
        Glyphs(live, "⚖").Should().Be(c.Disputed, "un ⚖ por disputa REAL");
        Glyphs(live, "✔").Should().Be(c.Resolved, "un ✔ por resolución");
        Glyphs(live, "⚠").Should().Be(c.ResolutionsRefused, "un ⚠ por «arreglado» degradado");
    }

    /// <summary>
    /// Y la pantalla de cierre cuadra con lo que se ESCRIBIÓ en el hub, no con lo que la narración
    /// creyó ver: es el mismo suceso contado dos veces y las dos cuentas tienen que dar lo mismo.
    /// </summary>
    [Fact]
    public async Task El_resumen_en_vivo_cuadra_con_la_sesion_persistida()
    {
        Finding presente = SeedFinding("sigue ahí");
        Finding disputado = SeedFinding("el auditor discrepa");

        (LiveSessionService live, SessionResult result) = await Run(
            audit: _ => new[] { NewFinding("fuga de stream") },
            reconcile: _ => new[]
            {
                new VerdictArgs(presente.Id.ToString(), "presente", "sigue en el código"),
                new VerdictArgs(disputado.Id.ToString(), "no-es-defecto", "nunca fue un defecto"),
            });

        AuditSession persisted = _hub.Store.ListSessions("app").Should().ContainSingle().Subject;
        SessionCounters c = persisted.Counters;

        Line(live, "Nuevos")!.Count.Should().Be(c.New);
        Line(live, "Confirmados")!.Count.Should().Be(c.Confirmed);
        Line(live, "Disputados")!.Count.Should().Be(c.Disputed);

        // Y el detalle de una línea nombra exactamente tantos casos como cuenta, allí donde el
        // contador y la lista narran lo mismo. «Confirmados» queda fuera a propósito: suma también
        // los «arreglado» degradados, que tienen su propia línea y su propio detalle.
        foreach (string label in new[] { "Nuevos", "Disputados" })
        {
            SummaryLine line = Line(live, label)!;
            line.Named.Should().HaveCount(line.Count, $"«{label}» dice {line.Count}: tiene que nombrar {line.Count}");
        }

        // El estado persistido manda: la disputa está en el hallazgo, y solo en ese.
        _hub.Store.TryReadFinding("app", disputado.Id.ToString())!.Disputes.Should().ContainSingle();
        _hub.Store.TryReadFinding("app", presente.Id.ToString())!.Disputes.Should().BeEmpty();
    }

    /// <summary>
    /// Las ubicaciones añadidas se narran con su propio glifo y no se confunden con un hallazgo
    /// nuevo: extender un defecto sistémico no crea nada.
    /// </summary>
    [Fact]
    public async Task Las_ubicaciones_anadidas_se_narran_como_tales()
    {
        Finding existente = SeedFinding("no valida argumentos nulos");

        var agent = new FakeCopilotAgent(
            reconcileScript: _ => new[] { new VerdictArgs(existente.Id.ToString(), "presente", "sigue") },
            extendScript: _ => new[]
            {
                new AddLocationsArgs(existente.Id.ToString(), new[] { new SubmitLocation(UnitPath, 1, null) }),
            });

        var live = new LiveSessionService(
            () => new SessionCoordinator(
                _hub, _provider.GetRequiredService<FindingIngestionService>(),
                _provider.GetRequiredService<ReconciliationService>(), _machines, _ulids, agent, _settings),
            agent, _provider.GetRequiredService<OpenSessionStore>(), _hub);
        SessionResult? result = null;
        live.Completed += r => result = r;

        await live.StartAsync(new SessionRequest("app", AuditMode.Lotes, new[] { UnitPath }), new[] { UnitPath });
        for (int i = 0; i < 400 && live.IsRunning; i++)
        {
            await Task.Delay(25);
        }

        Glyphs(live, "＋").Should().Be(0, "extender no crea un hallazgo");
        Glyphs(live, "⚖").Should().Be(0);
        result!.Counters.New.Should().Be(0);
        result.Counters.Disputed.Should().Be(0);
    }

    /// <summary>
    /// Todo glifo que la narración pinta pertenece al repertorio conocido: los de hallazgo (＋ ⊕ ⚖
    /// ⚠ ✔), los de cierre de pasada (✓ seca, ↻ con aportación) y, desde F30 §1, los del hilo de
    /// actividad — la herramienta (⚒), la lectura de un fichero (👁), el hito de Atalaya (◆) y la
    /// entrega del turno al modelo (→) —. Si
    /// algún día se añade un suceso y se olvida su caso —o se cuela una etiqueta de otro sitio—, se
    /// ve aquí en vez de en una insignia que miente.
    /// <para>
    /// <b>La familia de F30 es propia a propósito</b>: una llamada a <c>submit_findings</c> con
    /// cinco elementos es UN gesto del auditor, no cinco hallazgos, así que no puede llevar el ＋
    /// que este mismo fichero cuenta contra los contadores de la sesión.
    /// </para>
    /// </summary>
    [Fact]
    public async Task La_narracion_no_pinta_glifos_que_nadie_emite()
    {
        Finding presente = SeedFinding("sigue ahí");
        (LiveSessionService live, _) = await Run(
            audit: _ => new[] { NewFinding("fuga de stream") },
            reconcile: _ => new[] { new VerdictArgs(presente.Id.ToString(), "presente", "sigue") });

        Narration(live).Select(e => e.Glyph).Distinct()
            .Should().BeSubsetOf(new[] { "＋", "⊕", "⚖", "⚠", "✔", "✓", "↻", "⚒", "👁", "◆", "→" });
    }

    /// <summary>
    /// <b>F30 §1 — una herramienta llega al hilo ANTES de que la pasada termine.</b> Es la regla
    /// entera de esta fase.
    /// <para>
    /// <b>El defecto.</b> Todo esto ya se apuntaba —<c>SessionToolbox.ToolCallLog</c> lleva desde
    /// F3 registrando cada llamada: una sesión real de 86 llamadas dejó 136 notas de este tipo—,
    /// pero el coordinador las vuelca en <c>session.Notes</c> al CERRAR la pasada. Para cuando se
    /// pueden leer, ya han pasado los minutos en los que el usuario miraba una pantalla quieta:
    /// medido sobre las sesiones reales del hub, <b>11,8 s de media entre llamada y llamada, y
    /// hasta 48 s</b>. Lo único que podía aparecer en ese hueco era la prosa del modelo, y la prosa
    /// es opcional para él; las herramientas no.
    /// </para>
    /// <para>
    /// <b>Por qué se comprueba el ORDEN y no la presencia.</b> Que las líneas acaben estando no
    /// distingue nada: también estaban antes, al final. Lo que esta fase cambia es CUÁNDO, así que
    /// lo que se fija es que la primera herramienta se narre antes de que llegue el primer cierre
    /// de pasada. Si algún día la emisión volviera al volcado del final, esto se pone rojo.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Una_herramienta_llega_al_hilo_antes_de_que_termine_la_pasada()
    {
        var order = new List<string>();

        (LiveSessionService live, _) = await Run(
            audit: _ => new[] { NewFinding("fuga de stream") },
            wire: c =>
            {
                c.ActivityNoted += n => order.Add("herramienta · " + n.Text);
                c.PassFinished += (_, _) => order.Add("fin de pasada");
            });

        int primeraHerramienta = order.FindIndex(x => x.StartsWith("herramienta ·", StringComparison.Ordinal));
        int primerCierre = order.IndexOf("fin de pasada");

        primeraHerramienta.Should().BeGreaterThanOrEqualTo(0,
            "el auditor llamó a submit_findings y a unit_done: sin ninguna herramienta narrada, "
            + "esto pasaría por vacío y no probaría nada");
        primerCierre.Should().BeGreaterThan(primeraHerramienta,
            "la herramienta se narra cuando se ejecuta, no cuando la pasada acaba");

        // Y llega al hilo que se pinta, no solo al evento.
        Narration(live).Should().Contain(e => e.Glyph == "⚒");
    }

    /// <summary>
    /// «Presente» NO tiene glifo propio en la columna: se cuenta en el cierre de pasada
    /// («veredictos: N presente») y en la línea «Confirmados» del resumen. Queda fijado aquí porque
    /// es la pregunta natural al leer el resto de casos, y la respuesta —«se narra, pero agregado»—
    /// no se deduce mirando el switch.
    /// </summary>
    [Fact]
    public async Task Presente_se_narra_agregado_en_el_cierre_de_pasada()
    {
        Finding presente = SeedFinding("sigue ahí");
        (LiveSessionService live, SessionResult result) = await Run(
            reconcile: _ => new[] { new VerdictArgs(presente.Id.ToString(), "presente", "sigue") });

        result.Counters.Confirmed.Should().Be(1);
        Narration(live).Should().Contain(e => e.Text.Contains("1 presente"),
            "el cierre de pasada es donde se ven las reconfirmaciones");
        Line(live, "Confirmados")!.Count.Should().Be(result.Counters.Confirmed);
    }
}
