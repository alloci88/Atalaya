using System.Text;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.ClaudeCode;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Hashing;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F16 — EL ARREGLO ASISTIDO CON CLAUDE CODE, con la cadena de producción entera.
/// <para>
/// <b>Qué se ejercita, y por qué no basta con un doble.</b> Aquí corren de verdad: un proceso que
/// hace de <c>claude</c>, el puente <c>Atalaya.Mcp</c> que él lanza como servidor MCP, una tubería
/// con nombre, el servidor MCP de Atalaya, el <see cref="FixToolbox"/> de siempre sobre un clon de
/// git de verdad, y <see cref="LiveFixService"/> por encima. Lo único de mentira es quién decide
/// qué herramienta llamar: en lugar de un modelo, un guion.
/// </para>
/// <para>
/// <b>Y lo que se afirma es el CONTRATO, no el driver.</b> Las mismas cuatro herramientas, el mismo
/// régimen de permisos —los ficheros del hallazgo se editan solos, cualquier otro pide autorización
/// y un «no» se devuelve como decisión—, los mismos frenos, la misma huella del arreglo y el mismo
/// hallazgo que sigue ACTIVO al terminar. Son las mismas propiedades que
/// <see cref="AssistedFixTests"/> fija con Copilot: si alguna se cumpliera solo con uno de los dos
/// motores, la pantalla común sería una mentira.
/// </para>
/// </summary>
public sealed class AssistedFixClaudeTests : IDisposable
{
    private const string Slug = "xblast";
    private const string RepoUrl = "https://github.com/org/xblast.git";
    private const string UnitPath = "Common/CommonStatics.cs";
    private const string CallerPath = "Common/Reader.cs";

    private static readonly string OriginalCode = string.Join("\r\n", new[]
    {
        "namespace Common;",
        "",
        "public static class CommonStatics",
        "{",
        "    public static byte[] HexStringToByteArray(string hex)",
        "    {",
        "        var bytes = new byte[hex.Length / 2];",
        "        return bytes;",
        "    }",
        "}",
        "",
    });

    private static readonly string CallerCode = string.Join("\r\n", new[]
    {
        "namespace Common;",
        "",
        "public static class Reader",
        "{",
        "    public static byte[] Read(string s) => CommonStatics.HexStringToByteArray(s);",
        "}",
        "",
    });

    private readonly string _root;
    private readonly string _clone;
    private readonly string _work;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private readonly FixSnapshotStore _snapshots;
    private readonly AgentBusyGate _busy = new();
    private readonly Ulid _findingId;

    public AssistedFixClaudeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-fix-claude", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
        _work = Path.Combine(_root, "claude");
        Directory.CreateDirectory(_work);
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _snapshots = new FixSnapshotStore(_paths);

        TestFactory.MakeClone(_clone, RepoUrl);
        Directory.CreateDirectory(Path.Combine(_clone, "Common"));
        File.WriteAllText(Path.Combine(_clone, UnitPath), OriginalCode);
        File.WriteAllText(Path.Combine(_clone, CallerPath), CallerCode);
        File.WriteAllText(Path.Combine(_clone, "XBlast.sln"), "Microsoft Visual Studio Solution File\r\n");
        Commit();

        MachineConfig machine = _machines.Load();
        machine.ClonePaths[Slug] = _clone;
        _machines.Save(machine);
        _hub.Store.WriteApp(new AppConfig { Slug = Slug, Name = "X-BLAST", RepoUrl = RepoUrl, CurrentCycle = 3 });
        _findingId = WriteFinding();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ================================================================= la sesión completa

    /// <summary>
    /// El guion del encargo, de punta a punta: leer, preguntar, intentar salirse del hallazgo y
    /// que se lo nieguen, replantear dentro, compilar y cerrar.
    /// <para>
    /// Las afirmaciones son las de F6.9 con Copilot, una por una. La que más importa es la del
    /// medio: <c>Reader.cs</c> queda <b>intacto</b> aunque el agente lo pidiera, y el «no» le llega
    /// como una decisión —no como un error— para que replantee.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Una_sesion_completa_con_Claude_lee_pregunta_respeta_el_permiso_compila_y_cierra()
    {
        Script(
            "text Voy a mirar el fichero del hallazgo.",
            $$"""call read_file {"path":"{{Json(UnitPath)}}"}""",
            """call ask_user {"question":"Validar rompe al llamador. (A) excepcion o (B) compatible?","choices":["(A) excepcion","(B) compatible"]}""",
            $$"""call apply_edit {"path":"{{Json(CallerPath)}}","reason":"habria que adaptar al llamador","edits":[{"oldText":"CommonStatics.HexStringToByteArray(s)","newText":"CommonStatics.HexStringToByteArray(s ?? string.Empty)"}]}""",
            "text No se autoriza tocar el llamador: replanteo sin salir del hallazgo.",
            $$"""call apply_edit {"path":"{{Json(UnitPath)}}","reason":"es donde esta el defecto","edits":[{"oldText":"var bytes = new byte[hex.Length / 2];","newText":"if (hex.Length % 2 != 0)\r\n        {\r\n            throw new ArgumentException(\"longitud impar\", nameof(hex));\r\n        }\r\n\r\n        var bytes = new byte[hex.Length / 2];"}]}""",
            """call run_build_and_tests {}""",
            """call fix_done {"summary":"Se valida que la cadena tenga longitud par antes de convertirla.","commitTitle":"Valida longitud par en HexStringToByteArray (BUG-0003)","commitDescription":"Antes recorria hex.Length / 2 bytes en silencio.","risks":"Ningun llamador dependia del truncado."}""");

        LiveFixService fix = Service();
        var asked = new List<FixQuestion>();
        AnswerCards(fix, asked, decision: "(A) excepcion", authorize: false);

        await fix.StartAsync(new FixSessionRequest(Slug, _findingId));

        fix.HasFailed.Should().BeFalse(fix.FailureMessage);
        fix.HasFinished.Should().BeTrue();

        // --- la pregunta del agente llegó como TARJETA y se contestó ---
        asked.Should().HaveCount(2);
        asked[0].Kind.Should().Be(FixAskKind.Decision);
        asked[0].Answer.Should().Be("(A) excepcion");

        // --- el permiso fuera del hallazgo lo gobierna Atalaya, no el CLI ---
        asked[1].Kind.Should().Be(FixAskKind.Autorizacion);
        asked[1].Context.Should().Be(CallerPath);
        File.ReadAllText(Path.Combine(_clone, CallerPath)).Should().Be(
            CallerCode, "un «no» deja el fichero exactamente como estaba");
        Transcript().Should().Contain(
            l => l.StartsWith("apply_edit ->", StringComparison.Ordinal) && l.Contains("\"Denied\":true"),
            "el «no» se le devuelve al agente como decisión, no como error");

        // --- el fichero del hallazgo se edita SIN preguntar ---
        fix.Files.Should().ContainSingle();
        fix.Files[0].RelativePath.Should().Be(UnitPath);
        fix.Files[0].Lines.Should().Contain(l => l.Kind == DiffKind.Anadida && l.Text.Contains("ArgumentException"));
        File.ReadAllText(Path.Combine(_clone, UnitPath)).Should().Contain("longitud impar");

        // --- narración, build por delegación y cierre ---
        fix.Conversation.OfType<FixMessage>().Should()
            .Contain(m => m.Voice == FixVoice.Agente && m.Text.Contains("replanteo"));
        fix.HasBuildResult.Should().BeTrue();
        fix.Summary.Should().Contain("longitud par");
        fix.Commit.Title.Should().Be("Valida longitud par en HexStringToByteArray (BUG-0003)");

        // --- la sesión queda escrita CON SU CASA ---
        AuditSession session = _hub.Store.ListSessions(Slug).Single();
        session.Mode.Should().Be(AuditMode.Fix);
        session.Provider.Should().Be(ClaudeCodeProvider.Id);
        session.Model.Should().Be("opus");
        // El consumo de las OCHO llamadas del guion, ya cuadrado contra el agregado que declara el
        // CLI al cerrar el turno (900 de encargo del sistema + 10 por llamada). Las llamadas se
        // cuentan una vez cada una: el ajuste del final no es una llamada nueva.
        session.Usage.Calls.Should().Be(8);
        session.Usage.InputTokens.Should().Be(980);
        session.Usage.OutputTokens.Should().Be(40);
        session.Usage.CacheWriteTokens.Should().Be(160,
            "la caché escrita también se guarda: con esta casa es el sumando más grande");

        string report = File.ReadAllText(_hub.HubPaths.ReportFile(Slug, session.Id.ToString()));
        report.Should().Contain("Claude Code");
        report.Should().Contain("NO están commiteados");

        // --- ARREGLAR NO RESUELVE, aquí tampoco ---
        Finding stored = _hub.Store.TryReadFinding(Slug, _findingId.ToString())!;
        stored.Status.Should().Be(FindingStatus.Activo);
        stored.History.Should().Contain(h => h.Event == FindingEvent.FixProposed);

        _busy.IsBusy.Should().BeFalse("el cerrojo se suelta pase lo que pase");
    }

    /// <summary>
    /// <b>La huella del arreglo se registra igual.</b> La escribe la aplicación con lo que quedó en
    /// disco, así que no puede depender del motor — pero eso hay que comprobarlo, no suponerlo: es
    /// la pieza que impide que el commit del usuario haga aparecer su propia unidad como «cambiada
    /// desde su auditoría» y realimente la lista de candidatas para siempre (F9 §2).
    /// </summary>
    [Fact]
    public async Task La_huella_del_arreglo_queda_escrita_tambien_con_Claude()
    {
        Script(
            $$"""call apply_edit {"path":"{{Json(UnitPath)}}","reason":"es donde esta el defecto","edits":[{"oldText":"var bytes","newText":"// arreglado\r\n        var bytes"}]}""",
            """call fix_done {"summary":"hecho","commitTitle":"Arregla BUG-0003","commitDescription":"d"}""");

        LiveFixService fix = Service();
        AnswerCards(fix, new List<FixQuestion>(), decision: "sí", authorize: true);
        await fix.StartAsync(new FixSessionRequest(Slug, _findingId));

        fix.HasFinished.Should().BeTrue(fix.FailureMessage);

        FixRecord record = _hub.Store.ListFixes(Slug).Should().ContainSingle().Subject;
        record.FindingId.Should().Be(_findingId.ToString());
        record.Files.Should().ContainSingle(f => f.Path == UnitPath);

        // La huella es la del contenido QUE QUEDÓ EN DISCO, calculada por la aplicación. Es lo que
        // más tarde permite reconocer el commit del usuario como «esto lo escribió Atalaya» — y por
        // eso tiene que salir idéntica venga el arreglo de la casa que venga.
        record.Files[0].ContentHash.Should().Be(
            HashUtil.NormalizedContentHash(File.ReadAllBytes(Path.Combine(_clone, UnitPath))));

        // Y el circuito se cierra: el usuario commitea y VERIFICA con el mismo proveedor.
        Commit();
        ScriptVerify();
        var verify = new VerifyCoordinator(_hub, _machines, _ulids, Provider());
        VerifyOutcome outcome = await verify.RunAsync(
            Slug, new[] { _findingId }, CancellationToken.None);

        outcome.Applied.Should().Be(1);
        Finding closed = _hub.Store.TryReadFinding(Slug, _findingId.ToString())!;
        closed.Status.Should().Be(FindingStatus.Resuelto);
        _hub.Store.ListSessions(Slug).Should().Contain(x => x.Mode == AuditMode.Verify
            && x.Provider == ClaudeCodeProvider.Id);
    }

    /// <summary>
    /// Los tres frenos, con este motor. <b>Los respaldos los hace la aplicación</b> —el agente solo
    /// pide ediciones—, así que descartar tiene que devolver el clon byte a byte venga el arreglo
    /// de donde venga.
    /// </summary>
    [Fact]
    public async Task Descartar_todo_devuelve_el_clon_byte_a_byte_tambien_con_Claude()
    {
        byte[] before = File.ReadAllBytes(Path.Combine(_clone, UnitPath));

        Script(
            $$"""call apply_edit {"path":"{{Json(UnitPath)}}","reason":"es donde esta el defecto","edits":[{"oldText":"var bytes","newText":"// tocado\r\n        var bytes"}]}""",
            """call fix_done {"summary":"hecho","commitTitle":"Arregla BUG-0003","commitDescription":"d"}""");

        LiveFixService fix = Service();
        AnswerCards(fix, new List<FixQuestion>(), decision: "sí", authorize: true);
        await fix.StartAsync(new FixSessionRequest(Slug, _findingId));

        File.ReadAllBytes(Path.Combine(_clone, UnitPath)).Should().NotEqual(before);

        FixRestoreReport report = fix.DiscardAll();

        report.Restored.Should().Be(1);
        File.ReadAllBytes(Path.Combine(_clone, UnitPath)).Should().Equal(before);
        WorkingTree.Inspect(_clone).Clean.Should().BeTrue();
    }

    /// <summary>
    /// <b>Pausar detiene lo único que se puede detener</b> (D-550): que no caiga ni un cambio más
    /// en el clon. El agente puede seguir; su siguiente <c>apply_edit</c> espera en la puerta.
    /// </summary>
    [Fact]
    public async Task En_pausa_no_cae_ni_un_cambio_mas_en_el_clon()
    {
        Script(
            $$"""call apply_edit {"path":"{{Json(UnitPath)}}","reason":"primera","edits":[{"oldText":"namespace Common;","newText":"// primera\r\nnamespace Common;"}]}""",
            $$"""call apply_edit {"path":"{{Json(UnitPath)}}","reason":"segunda","edits":[{"oldText":"var bytes","newText":"// segunda\r\n        var bytes"}]}""",
            """call fix_done {"summary":"hecho","commitTitle":"Arregla BUG-0003","commitDescription":"d"}""");

        LiveFixService fix = Service();
        AnswerCards(fix, new List<FixQuestion>(), decision: "sí", authorize: true);

        // En cuanto la PRIMERA edición aterriza se pausa, y se comprueba que la segunda no entra.
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fix.Files.CollectionChanged += (_, _) =>
        {
            if (fix.Files.Count == 1 && !fix.IsPaused)
            {
                fix.TogglePause();
                paused.TrySetResult();
            }
        };

        Task running = fix.StartAsync(new FixSessionRequest(Slug, _findingId));
        await paused.Task;

        // Con la puerta cerrada, la segunda edición no llega al disco por mucho que se espere.
        await Task.Delay(400);
        File.ReadAllText(Path.Combine(_clone, UnitPath)).Should().NotContain(
            "// segunda", "en pausa no se escribe nada en el clon");
        fix.IsPaused.Should().BeTrue();

        fix.TogglePause();
        await running;

        File.ReadAllText(Path.Combine(_clone, UnitPath)).Should().Contain(
            "// segunda", "al continuar, la edición que esperaba se aplica");
        fix.HasFinished.Should().BeTrue(fix.FailureMessage);
    }

    /// <summary>
    /// <b>Detener</b> corta la sesión y no deja el CLI vivo. Lo ya aplicado se queda —para eso está
    /// «Descartar todo»— y la sesión se registra como interrumpida en vez de morir muda.
    /// </summary>
    [Fact]
    public async Task Detener_cierra_la_sesion_y_no_deja_el_CLI_vivo()
    {
        Script(
            $$"""call apply_edit {"path":"{{Json(UnitPath)}}","reason":"primera","edits":[{"oldText":"namespace Common;","newText":"// primera\r\nnamespace Common;"}]}""",
            "text Ahora esperaría al usuario…",
            """call ask_user {"question":"¿Sigo?","choices":["sí"]}""",
            """call fix_done {"summary":"hecho","commitTitle":"Arregla BUG-0003","commitDescription":"d"}""");

        LiveFixService fix = Service();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fix.Conversation.CollectionChanged += (_, e) =>
        {
            foreach (object? item in e.NewItems ?? Array.Empty<object>())
            {
                if (item is FixQuestion)
                {
                    stopped.TrySetResult();
                }
            }
        };

        Task running = fix.StartAsync(new FixSessionRequest(Slug, _findingId));
        await stopped.Task;
        fix.Stop();

        await running;

        fix.IsRunning.Should().BeFalse();
        _busy.IsBusy.Should().BeFalse("el cerrojo se suelta también al detener");
        File.ReadAllText(Path.Combine(_clone, UnitPath)).Should().Contain(
            "// primera", "lo aplicado antes de parar se queda: para deshacerlo está el descarte");
        _hub.Store.ListSessions(Slug).Should().ContainSingle().Which.Interrupted.Should().BeTrue();
    }

    /// <summary>
    /// El campo de instrucciones: lo que el usuario escribe mientras el agente trabaja se le
    /// entrega en el turno siguiente, y la interfaz lo dice con esas palabras (D-535).
    /// <para>
    /// Con este motor el mensaje SIEMPRE se encola —el CLI acepta líneas a mitad de turno pero no
    /// dice cuándo las entrega—, así que lo que se fija aquí es que la cola llega de verdad: el
    /// segundo turno del CLI recibe el texto del usuario.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Lo_que_el_usuario_escribe_llega_en_el_turno_siguiente()
    {
        Script(
            "text Primer turno: he mirado el código.",
            "wait sigue.txt",
            "---",
            "text Recibido, uso TryParse.",
            $$"""call apply_edit {"path":"{{Json(UnitPath)}}","reason":"es donde esta el defecto","edits":[{"oldText":"var bytes","newText":"// TryParse\r\n        var bytes"}]}""",
            """call fix_done {"summary":"hecho","commitTitle":"Arregla BUG-0003","commitDescription":"d"}""");

        LiveFixService fix = Service();
        AnswerCards(fix, new List<FixQuestion>(), decision: "sí", authorize: true);

        var spoke = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fix.Conversation.CollectionChanged += (_, e) =>
        {
            foreach (object? item in e.NewItems ?? Array.Empty<object>())
            {
                if (item is FixMessage { Voice: FixVoice.Agente } m && m.Text.Contains("Primer turno"))
                {
                    spoke.TrySetResult();
                }
            }
        };

        Task running = fix.StartAsync(new FixSessionRequest(Slug, _findingId));
        await spoke.Task;
        await fix.SendUserMessageAsync("prefiero TryParse");

        // El primer turno no se cierra hasta que el mensaje está encolado: sin esta señal, un turno
        // que solo dice una frase acabaría antes de que el usuario llegara a escribir, y el test
        // mediría una carrera en vez de la cola.
        File.WriteAllText(Path.Combine(_work, "sigue.txt"), "ya");
        await running;

        fix.HasFinished.Should().BeTrue(fix.FailureMessage);
        Transcript().Should().Contain(
            l => l.StartsWith("turn 2 in:", StringComparison.Ordinal) && l.Contains("prefiero TryParse"));
        fix.Conversation.OfType<FixMessage>().Should().Contain(
            m => m.Text.Contains("ocupado con este turno"),
            "no se promete una inmediatez que el CLI no garantiza");

        // Y el consumo de LOS DOS turnos, sin contar el primero dos veces: el CLI informa en
        // acumulado —tanto el coste como el agregado de tokens— y el lector reporta diferencias.
        // Cuatro llamadas en total: una en el primer turno y tres en el segundo.
        AuditSession session = _hub.Store.ListSessions(Slug).Single();
        session.Usage.Calls.Should().Be(4);
        session.Usage.InputTokens.Should().Be(940);
        session.Usage.OutputTokens.Should().Be(20);
    }

    /// <summary>
    /// La pantalla y el botón dicen con QUIÉN se arregla. Es el mismo criterio que el diálogo de
    /// lanzar una auditoría (D-781): el juez de una sesión no puede descubrirse leyendo el informe.
    /// </summary>
    [Fact]
    public async Task La_pantalla_anuncia_proveedor_y_modelo()
    {
        Script("""call fix_done {"summary":"hecho","commitTitle":"Arregla BUG-0003","commitDescription":"d"}""");

        LiveFixService fix = Service();
        var view = new AssistedFixViewModel(fix, new ToastCenter(), new AlwaysDiscard());

        await fix.StartAsync(new FixSessionRequest(Slug, _findingId));

        fix.HasFinished.Should().BeTrue(fix.FailureMessage);
        view.HasEngine.Should().BeTrue();
        // Sin la palabra «modelo» desde D-983: con ella la pastilla no cabia en la cabecera
        // y se recortaba justo por el modelo, que es el dato que hay que mirar.
        view.EngineText.Should().Be("Claude Code · opus");
        fix.Conversation.OfType<FixMessage>().Should()
            .Contain(m => m.Text.Contains("Claude Code") && m.Text.Contains("modelo opus"));

        Launcher().EngineLabel.Should().Be("Claude Code, modelo opus");
    }

    /// <summary>
    /// <b>El pie se mueve MIENTRAS el agente trabaja.</b> Era el parte: con un turno ya avanzado
    /// —pregunta contestada y edición aplicada— el pie seguía en «0 llamadas · coste no calculable
    /// (sin tokens registrados)». Y era mentira dos veces: el proveedor sí informa consumo, y lo
    /// informa por llamada.
    /// <para>
    /// La causa no era que el arreglo no reenviara nada: es que el consumo solo se leía del evento
    /// que cierra el turno, y con Claude un arreglo entero —leer, preguntar, editar, compilar—
    /// cabe en UN turno. El pie no se movía hasta el final de la sesión.
    /// </para>
    /// </summary>
    [Fact]
    public async Task El_pie_cuenta_llamadas_y_tokens_mientras_la_sesion_corre()
    {
        Script(
            "text Voy a mirar el fichero del hallazgo.",
            $$"""call read_file {"path":"{{Json(UnitPath)}}"}""",
            "text Ya lo tengo.",
            "wait sigue.txt",
            $$"""call apply_edit {"path":"{{Json(UnitPath)}}","reason":"es donde esta el defecto","edits":[{"oldText":"var bytes","newText":"// arreglado\r\n        var bytes"}]}""",
            """call fix_done {"summary":"hecho","commitTitle":"Arregla BUG-0003","commitDescription":"d"}""");

        LiveFixService fix = Service();
        var view = new AssistedFixViewModel(fix, new ToastCenter(), new AlwaysDiscard());
        AnswerCards(fix, new List<FixQuestion>(), decision: "sí", authorize: true);

        // Se espera a que el pie tenga algo que contar, SIN que el turno haya terminado: el guion
        // está parado en `wait`, así que si el pie solo se moviera al cerrar, esto no llegaría.
        var counting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fix.PropertyChanged += (_, _) =>
        {
            if (fix.Calls > 0 && fix.InputTokens > 0)
            {
                counting.TrySetResult();
            }
        };

        Task running = fix.StartAsync(new FixSessionRequest(Slug, _findingId));
        await counting.Task;

        fix.IsRunning.Should().BeTrue("el agente sigue trabajando: esto es el pie A MITAD de sesión");
        fix.Calls.Should().BeGreaterThanOrEqualTo(1);
        fix.InputTokens.Should().BeGreaterThan(0);
        fix.CacheWriteTokens.Should().BeGreaterThan(0, "la caché escrita también se cuenta");

        view.CostText.Should().NotContain("sin tokens registrados",
            "eso solo es verdad cuando de verdad no hay dato, y aquí lo hay");
        view.CostText.Should().NotStartWith("0 llamadas");

        File.WriteAllText(Path.Combine(_work, "sigue.txt"), "ya");
        await running;

        fix.HasFinished.Should().BeTrue(fix.FailureMessage);
    }

    /// <summary>
    /// <b>El pie de un arreglo con Claude Code: llamadas, tokens y quién paga</b> (F16-RETOQUE §1).
    /// Ni credits, ni «equivalente API», ni «tarifa no configurada» — ese consumo va contra la
    /// suscripción de quien lanzó la sesión y no factura a nadie, así que no se tarifa. Lo que se
    /// enseña son las magnitudes que SÍ son hechos medidos.
    /// </summary>
    [Fact]
    public async Task El_pie_ensena_llamadas_tokens_y_que_va_contra_la_suscripcion()
    {
        SeedRates();
        Script("""call fix_done {"summary":"hecho","commitTitle":"Arregla BUG-0003","commitDescription":"d"}""");

        LiveFixService fix = Service();
        var view = new AssistedFixViewModel(fix, new ToastCenter(), new AlwaysDiscard());
        await fix.StartAsync(new FixSessionRequest(Slug, _findingId));

        fix.HasFinished.Should().BeTrue(fix.FailureMessage);
        fix.InputTokens.Should().BeGreaterThan(0, "los tokens se siguen registrando: son dato primario");
        fix.CostResult.Why.Should().Be(CostUnavailable.NotBilled);

        view.CostText.Should().Contain("llamadas").And.Contain("entrada").And.Contain("salida");
        // F17-RETOQUE: el orden es llamadas → coste → tokens; el coste ya no va el último.
        view.CostText.Should().Contain("coste: incluido en tu suscripción de Claude");
        view.CostText.IndexOf("coste:", StringComparison.Ordinal).Should()
            .BeLessThan(view.CostText.IndexOf("entrada", StringComparison.Ordinal));
        view.CostText.Should().NotContain("credits")
            .And.NotContain("equivalente API")
            .And.NotContain("tarifa")
            .And.NotContain("no calculable");
    }

    /// <summary>
    /// Y aunque la organización tenga una tarifa escrita para ese modelo, tampoco se usa: el freno
    /// está en el cálculo, no en que falte el dato. Es el test que se pondría rojo si alguien
    /// volviera a colar a esta casa por el camino de las tarifas.
    /// </summary>
    [Fact]
    public async Task Aunque_haya_tarifa_escrita_para_su_modelo_no_se_tarifa()
    {
        _hub.Store.WriteModelRates(new ModelRateTable
        {
            Source = "Una tarifa heredada de la siembra vieja, atada a claude-code.",
            Rates = { new ModelRate("opus", 1m, 5m, 0.1m, 2m, ClaudeCodeProvider.Id) },
        });
        Script("""call fix_done {"summary":"hecho","commitTitle":"Arregla BUG-0003","commitDescription":"d"}""");

        LiveFixService fix = Service();
        var view = new AssistedFixViewModel(fix, new ToastCenter(), new AlwaysDiscard());
        await fix.StartAsync(new FixSessionRequest(Slug, _findingId));

        fix.HasFinished.Should().BeTrue(fix.FailureMessage);
        fix.CostResult.Credits.Should().BeNull();
        view.CostText.Should().NotContain("credits");
    }

    /// <summary>
    /// Y sin consumo informado por el proveedor, el pie no se inventa unos tokens: dice las
    /// llamadas y la frase del coste, y nada más. «Sin tokens registrados» queda para las casas que
    /// SÍ facturan — a ésta no le puede salir, porque su motivo se decide antes de mirar los tokens.
    /// </summary>
    [Fact]
    public async Task Sin_consumo_informado_el_pie_no_se_inventa_tokens()
    {
        SeedRates();
        Script("""call fix_done {"summary":"hecho","commitTitle":"Arregla BUG-0003","commitDescription":"d"}""");
        File.WriteAllText(Path.Combine(_work, "fake-silent.txt"), "sin usage");

        LiveFixService fix = Service();
        var view = new AssistedFixViewModel(fix, new ToastCenter(), new AlwaysDiscard());
        await fix.StartAsync(new FixSessionRequest(Slug, _findingId));

        fix.HasFinished.Should().BeTrue(fix.FailureMessage);
        fix.InputTokens.Should().Be(0);
        view.CostText.Should().EndWith("coste: incluido en tu suscripción de Claude");
        view.CostText.Should().NotContain("entrada", "sin tokens no se escribe un desglose de ceros");
        view.CostText.Should().NotContain("sin tokens registrados");
    }

    /// <summary>
    /// El informe del arreglo cuenta lo mismo que el de auditoría: tokens POR TIPO, llamadas al
    /// modelo y el coste con su etiqueta. Los tokens van enteros porque son el hecho; el coste va
    /// detrás porque es un derivado que mañana se recalcula (D-788).
    /// </summary>
    [Fact]
    public async Task El_informe_del_arreglo_trae_tokens_llamadas_y_coste()
    {
        SeedRates();
        Script(
            "text Miro el fichero.",
            $$"""call apply_edit {"path":"{{Json(UnitPath)}}","reason":"es donde esta el defecto","edits":[{"oldText":"var bytes","newText":"// arreglado\r\n        var bytes"}]}""",
            """call fix_done {"summary":"hecho","commitTitle":"Arregla BUG-0003","commitDescription":"d"}""");

        LiveFixService fix = Service();
        AnswerCards(fix, new List<FixQuestion>(), decision: "sí", authorize: true);
        await fix.StartAsync(new FixSessionRequest(Slug, _findingId));

        fix.HasFinished.Should().BeTrue(fix.FailureMessage);
        AuditSession session = _hub.Store.ListSessions(Slug).Single();
        string report = File.ReadAllText(_hub.HubPaths.ReportFile(Slug, session.Id.ToString()));

        report.Should().Contain("- **Tokens**: entrada 930, salida 15, caché lectura 300, escritura 60");
        report.Should().Contain("3 llamada(s) al modelo");

        // F16-RETOQUE §1 — el coste, dicho entero y sin insinuar que falte nada por configurar.
        report.Should().Contain("- **Coste**: incluido en tu suscripción de Claude");
        report.Should().NotContain("equivalente API").And.NotContain("tarifa no configurada");

        // Y lo que el CLI declaró, como línea informativa del proveedor: viene gratis, es un dato
        // medido y NO es el coste de la sesión. Se dice qué es en la misma línea.
        report.Should().Contain("- **Lo que declaró el CLI**:")
            .And.Contain("USD (tarifa de lista)")
            .And.Contain("no el coste de esta sesión y no entra en ninguna métrica");
    }

    // ================================================================= ayudas

    /// <summary>El proveedor de PRODUCCIÓN, apuntando al CLI falso y al puente de verdad.</summary>
    private ClaudeCodeProvider Provider()
        => new(
            bridgeExecutable: Path.Combine(AppContext.BaseDirectory, "Atalaya.Mcp.exe"),
            modelProvider: () => "opus",
            workDirectory: () => _work,
            locator: () => Path.Combine(AppContext.BaseDirectory, "Atalaya.FakeCli.exe"));

    private LiveFixService Service()
    {
        ClaudeCodeProvider provider = Provider();
        return new LiveFixService(
            _hub, () => provider, _machines, _ulids, _settings, new ReferenceCollector(), _snapshots,
            Launcher(provider), _busy, new BuildRunner(new NoProcess()));
    }

    private AssistedFixLauncher Launcher() => Launcher(Provider());

    private AssistedFixLauncher Launcher(ClaudeCodeProvider provider)
        => new(
            _settings, new CloneLinkService(_hub, _machines), _machines, _busy,
            AuditorProviderRegistry.Of(provider));

    /// <summary>
    /// El guion del CLI falso. Va JUNTO al fichero de configuración MCP de esta sesión, que es un
    /// directorio de un solo uso: sin variables de entorno, que son del proceso entero y dos tests
    /// a la vez se las pisarían.
    /// </summary>
    private void Script(params string[] lines)
        => File.WriteAllLines(
            Path.Combine(_work, "fake-script.txt"), lines, new UTF8Encoding(false));

    /// <summary>
    /// Una tarifa para el modelo de estas sesiones, atada a su casa. Sin ella el coste sale «no
    /// calculable (tarifa no configurada)», que es correcto pero no es lo que estos tests miran.
    /// </summary>
    private void SeedRates()
        => _hub.Store.WriteModelRates(new ModelRateTable
        {
            Source = "Tarifa de test para el arreglo con Claude Code.",
            Rates = { new ModelRate("opus", 1m, 5m, 0.1m, 2m, ClaudeCodeProvider.Id) },
        });

    /// <summary>El guion de la verificación que cierra el ciclo: un veredicto y nada más.</summary>
    private void ScriptVerify()
        => Script($$"""call submit_verdict {"findingUlid":"{{_findingId}}","verdict":"resuelto","evidence":"El metodo valida la longitud antes de recorrer el array."}""");

    private string[] Transcript()
    {
        string path = Path.Combine(_work, "fake-transcript.txt");
        return File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
    }

    /// <summary>Contesta las tarjetas según llegan, como haría una persona delante.</summary>
    private static void AnswerCards(
        LiveFixService fix, List<FixQuestion> asked, string decision, bool authorize)
        => fix.Conversation.CollectionChanged += (_, e) =>
        {
            foreach (object? item in e.NewItems ?? Array.Empty<object>())
            {
                if (item is not FixQuestion question)
                {
                    continue;
                }

                asked.Add(question);
                fix.Answer(question, question.Kind == FixAskKind.Autorizacion
                    ? (authorize ? LiveFixService.ApproveLabel : LiveFixService.DenyLabel)
                    : decision);
            }
        };

    /// <summary>Una ruta dentro de un JSON escrito a mano en el guion.</summary>
    private static string Json(string path) => path.Replace("\\", "\\\\");

    private Ulid WriteFinding()
    {
        Ulid id = _ulids.NewUlid();
        var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, "abc1234", "auditor");
        var finding = new Finding
        {
            Id = id,
            DisplayId = "BUG-0003",
            RuleId = "err.validacion",
            Pillar = Pillar.Errores,
            Tag = FindingTag.Checklist,
            Severity = Severity.Alta,
            Confidence = Confidence.Media,
            Title = "HexStringToByteArray no valida longitud impar",
            Description = "Trunca en silencio el último nibble.",
            Impact = "Datos corruptos aguas abajo.",
            Recommendation = "Validar que la longitud sea par y lanzar si no lo es.",
            Symbol = "CommonStatics.HexStringToByteArray",
            Locations = { new Location { Path = UnitPath, Line = 5 } },
            FirstDetected = stamp,
            LastConfirmed = stamp,
        };
        _hub.Store.WriteFinding(Slug, finding);
        return id;
    }

    private void Commit()
    {
        using var repo = new LibGit2Sharp.Repository(_clone);
        LibGit2Sharp.Commands.Stage(repo, "*");
        var who = new LibGit2Sharp.Signature("t", "t@t", DateTimeOffset.UtcNow);
        repo.Commit("estado inicial", who, who, new LibGit2Sharp.CommitOptions { AllowEmptyCommit = true });
    }

    private sealed class AlwaysDiscard : IFixDiscardConfirmer
    {
        public bool Confirm(IReadOnlyList<string> files) => true;
    }

    /// <summary>Compilar está DOBLADO: lo que se prueba es la delegación, no MSBuild (D-561).</summary>
    private sealed class NoProcess : IProcessRunner
    {
        public ProcessOutcome Run(
            string fileName, string arguments, string workingDirectory, TimeSpan timeout, CancellationToken ct)
            => new(0, "Compilación correcta." + Environment.NewLine + "Aprobadas: 42", false);
    }
}
