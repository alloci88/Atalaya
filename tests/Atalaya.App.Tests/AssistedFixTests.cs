using System.Text;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F6.9 — ARREGLO ASISTIDO. El circuito entero, sin asiento de Copilot.
/// <para>
/// Lo que se fija aquí es la seguridad del flujo, no la habilidad del modelo: que no arranque con
/// el árbol sucio, que un fichero fuera del hallazgo exija una autorización explícita, que el
/// descarte devuelva el clon <b>byte a byte</b>, que no puedan correr dos sesiones a la vez, que
/// la sesión quede registrada como <c>fix</c> en el hub — y que arreglar NO resuelva.
/// </para>
/// </summary>
public sealed class AssistedFixTests : IDisposable
{
    private const string Slug = "xblast";
    private const string RepoUrl = "https://github.com/org/xblast.git";
    private const string UnitPath = "Common/CommonStatics.cs";

    private static readonly string OriginalCode = string.Join("\r\n", new[]
    {
        "namespace Common;",
        "",
        "public static class CommonStatics",
        "{",
        "    public static byte[] HexStringToByteArray(string hex)",
        "    {",
        "        var bytes = new byte[hex.Length / 2];",
        "        for (int i = 0; i < bytes.Length; i++)",
        "        {",
        "            bytes[i] = System.Convert.ToByte(hex.Substring(i * 2, 2), 16);",
        "        }",
        "",
        "        return bytes;",
        "    }",
        "}",
        "",
    });

    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private readonly FixSnapshotStore _snapshots;
    private readonly AgentBusyGate _busy = new();
    private readonly ToastCenter _toasts = new();
    private Ulid _findingId;

    public AssistedFixTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-fix", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "clone");
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
        File.WriteAllText(Path.Combine(_clone, "Common", "Reader.cs"),
            "namespace Common;\r\n\r\npublic static class Reader\r\n{\r\n"
            + "    public static byte[] Read(string s) => CommonStatics.HexStringToByteArray(s);\r\n}\r\n");
        // Una solución de mentira, para que la delegación de compilar tenga algo que nombrar. Lo
        // que se ejecuta lo decide Atalaya, y en los tests el proceso está doblado (NoProcess).
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
            // best effort
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ================================================================= precondiciones

    /// <summary>
    /// <b>El árbol sucio no arranca. Sin excepciones.</b> Es lo que hace posible el descarte
    /// limpio: sin árbol limpio al empezar, «revertir lo que tocó el agente» no se puede
    /// distinguir de «pisar lo que estaba escribiendo el usuario».
    /// </summary>
    [Fact]
    public void Con_cambios_sin_commitear_el_arreglo_no_arranca_y_lo_dice()
    {
        File.AppendAllText(Path.Combine(_clone, UnitPath), "// tocado a mano\r\n");

        FixLaunchDecision decision = Launcher().Check(Slug, Finding());

        decision.CanStart.Should().BeFalse();
        decision.Block.Should().Be(FixBlock.ArbolSucio);
        decision.Message.Should().Contain("sin commitear");
        decision.Message.Should().Contain(UnitPath);
    }

    /// <summary>Un fichero NUEVO sin seguir también es trabajo del usuario, y también frena.</summary>
    [Fact]
    public void Un_fichero_nuevo_sin_seguir_tambien_cuenta_como_arbol_sucio()
    {
        File.WriteAllText(Path.Combine(_clone, "Common", "Borrador.cs"), "// a medias\r\n");

        Launcher().Check(Slug, Finding()).Block.Should().Be(FixBlock.ArbolSucio);
    }

    /// <summary>
    /// Lo que git IGNORA no cuenta: <c>bin/</c> y <c>obj/</c> están en el árbol de cualquiera y no
    /// son trabajo de nadie. Si contaran, el arreglo no arrancaría nunca en un repo compilado.
    /// </summary>
    [Fact]
    public void Lo_ignorado_por_git_no_ensucia_el_arbol()
    {
        File.WriteAllText(Path.Combine(_clone, ".gitignore"), "bin/\r\nobj/\r\n");
        Commit();
        Directory.CreateDirectory(Path.Combine(_clone, "bin"));
        File.WriteAllText(Path.Combine(_clone, "bin", "Common.dll"), "binario");

        WorkingTree.Inspect(_clone).Clean.Should().BeTrue();
        Launcher().Check(Slug, Finding()).CanStart.Should().BeTrue();
    }

    [Fact]
    public void Con_el_ajuste_apagado_el_arreglo_no_se_ofrece()
    {
        _settings.Current.EnableAssistedFix = false;

        FixLaunchDecision decision = Launcher().Check(Slug, Finding());

        decision.Block.Should().Be(FixBlock.Desactivado);
        decision.Message.Should().Contain("Ajustes");
        decision.Message.Should().Contain("prompt de arreglo",
            "el camino old school sigue disponible y hay que decirlo");
    }

    /// <summary>
    /// <b>D-563 — el botón que no aparecía.</b> Una máquina que ya usaba Atalaya antes de F6.9
    /// tiene <c>"enableAssistedFix": false</c> escrito en su <c>settings.json</c>, no la clave
    /// ausente: el nuevo valor por defecto no la alcanza y «Arreglar con agente» no se pinta.
    /// Tras la promoción del arranque, con el hallazgo activo y el clon vinculado, el botón se
    /// ofrece —que es exactamente lo que la ficha pregunta para pintarlo.
    /// </summary>
    [Fact]
    public void Un_settings_anterior_a_F6_9_vuelve_a_ofrecer_el_boton_tras_la_promocion()
    {
        File.WriteAllText(_paths.SettingsJson, """{"theme":"dark","enableAssistedFix":false}""");
        var stale = new SettingsService(_paths);
        stale.Load();

        Launcher(stale).Check(Slug, Finding()).Block
            .Should().Be(FixBlock.Desactivado, "así lo vivía el usuario");

        stale.MigrateAssistedFixDefault();

        FixLaunchDecision decision = Launcher(stale).Check(Slug, Finding());
        decision.Block.Should().NotBe(FixBlock.Desactivado);
        decision.CanStart.Should().BeTrue("hallazgo activo, clon vinculado y árbol limpio");
    }

    /// <summary>Y el settings que nunca tuvo la clave nace encendido sin ayuda de nadie.</summary>
    [Fact]
    public void Un_settings_sin_la_clave_ofrece_el_boton_desde_el_primer_arranque()
    {
        File.WriteAllText(_paths.SettingsJson, """{"theme":"dark"}""");
        var fresh = new SettingsService(_paths);
        fresh.Load();

        Launcher(fresh).Check(Slug, Finding()).CanStart.Should().BeTrue();
    }

    [Fact]
    public void Sin_clon_vinculado_el_remedio_es_vincular()
    {
        MachineConfig machine = _machines.Load();
        machine.ClonePaths.Remove(Slug);
        _machines.Save(machine);

        FixLaunchDecision decision = Launcher().Check(Slug, Finding());

        decision.Block.Should().Be(FixBlock.SinClon);
        decision.OffersLink.Should().BeTrue();
    }

    /// <summary>Una auditoría corriendo bloquea el arreglo, y el mensaje dice por qué.</summary>
    [Fact]
    public void Solo_una_sesion_de_Copilot_a_la_vez()
    {
        _busy.TryEnter(AgentWork.Auditoria).Should().BeTrue();

        FixLaunchDecision decision = Launcher().Check(Slug, Finding());

        decision.Block.Should().Be(FixBlock.Ocupado);
        decision.Message.Should().Contain("auditoría en curso");

        _busy.Exit(AgentWork.Auditoria);
        Launcher().Check(Slug, Finding()).CanStart.Should().BeTrue();
    }

    /// <summary>Y al revés: con un arreglo corriendo no se puede lanzar una auditoría.</summary>
    [Fact]
    public void Y_con_un_arreglo_corriendo_tampoco_se_puede_auditar()
    {
        _busy.TryEnter(AgentWork.Arreglo).Should().BeTrue();

        _busy.TryEnter(AgentWork.Auditoria).Should().BeFalse();
        _busy.BusyMessage.Should().Contain("arreglo asistido en curso");
    }

    // ================================================================= ámbito de apply_edit

    [Fact]
    public void Editar_un_fichero_del_hallazgo_no_pregunta_nada()
    {
        var approvals = new ScriptedApprovals(answer: false);
        FixToolbox toolbox = Toolbox(approvals);

        ApplyEditResult result = toolbox.ApplyEdit(
            UnitPath, "es donde está el defecto",
            new[] { new FixEdit("var bytes = new byte[hex.Length / 2];", "if (hex.Length % 2 != 0) throw new ArgumentException(nameof(hex));\r\n        var bytes = new byte[hex.Length / 2];") });

        result.Applied.Should().BeTrue();
        approvals.Asked.Should().BeEmpty("los ficheros del hallazgo se editan directamente");
        File.ReadAllText(Path.Combine(_clone, UnitPath)).Should().Contain("ArgumentException");
    }

    /// <summary>
    /// <b>Cualquier otro fichero exige autorización.</b> Y un «no» no es un error que reintentar:
    /// es una decisión, y la respuesta se lo dice al agente con esas palabras.
    /// </summary>
    [Fact]
    public void Editar_un_fichero_fuera_del_hallazgo_exige_aprobacion_del_usuario()
    {
        var approvals = new ScriptedApprovals(answer: false);
        FixToolbox toolbox = Toolbox(approvals);

        ApplyEditResult denied = toolbox.ApplyEdit(
            "Common/Reader.cs", "adapta el llamador", new[] { new FixEdit("Read", "ReadHex") });

        denied.Applied.Should().BeFalse();
        denied.Denied.Should().BeTrue();
        denied.Error.Should().Contain("NO autoriza");
        approvals.Asked.Should().ContainSingle().Which.Path.Should().Be("Common/Reader.cs");
        approvals.Asked[0].Reason.Should().Be("adapta el llamador");
        File.ReadAllText(Path.Combine(_clone, "Common/Reader.cs")).Should().NotContain("ReadHex");
    }

    [Fact]
    public void Autorizado_una_vez_no_se_vuelve_a_preguntar_por_el_mismo_fichero()
    {
        var approvals = new ScriptedApprovals(answer: true);
        FixToolbox toolbox = Toolbox(approvals);

        toolbox.ApplyEdit("Common/Reader.cs", "adapta el llamador",
            new[] { new FixEdit("public static byte[] Read", "public static byte[] ReadHex") })
            .Applied.Should().BeTrue();
        toolbox.ApplyEdit("Common/Reader.cs", "un retoque más",
            new[] { new FixEdit("ReadHex(string s)", "ReadHex(string hex)") })
            .Applied.Should().BeTrue();

        approvals.Asked.Should().ContainSingle("preguntar dos veces por el mismo fichero es un peaje");
    }

    /// <summary>El test del fichero del hallazgo entra sin preguntar: el encargo PIDE la prueba.</summary>
    [Fact]
    public void El_fichero_de_test_del_hallazgo_entra_en_el_ambito()
    {
        var approvals = new ScriptedApprovals(answer: false);
        FixToolbox toolbox = Toolbox(approvals);

        ApplyEditResult result = toolbox.ApplyEdit(
            "Tests/CommonStaticsTests.cs", "cubre el defecto",
            new[] { new FixEdit(string.Empty, "// test nuevo\r\n") });

        result.Applied.Should().BeTrue();
        approvals.Asked.Should().BeEmpty();
    }

    [Fact]
    public void Fuera_del_clon_no_se_puede_ni_leer_ni_escribir()
    {
        FixToolbox toolbox = Toolbox(new ScriptedApprovals(answer: true));

        toolbox.ReadFile("../fuera.txt").Error.Should().Contain("fuera del clon");
        toolbox.ApplyEdit(@"C:\Windows\System32\drivers\etc\hosts", "porque sí",
            new[] { new FixEdit("a", "b") }).Error.Should().Contain("fuera del clon");
    }

    [Fact]
    public void El_presupuesto_de_lecturas_se_agota_y_se_dice_cuanto_queda()
    {
        FixToolbox toolbox = Toolbox(new ScriptedApprovals(answer: true), readBudget: 2);

        toolbox.ReadFile(UnitPath).Remaining.Should().Be(1);
        toolbox.ReadFile("Common/Reader.cs").Remaining.Should().Be(0);

        ReadFileResult third = toolbox.ReadFile(UnitPath);
        third.Ok.Should().BeFalse();
        third.Error.Should().Contain("Presupuesto de lecturas agotado");
    }

    /// <summary>Un fragmento ambiguo se rechaza en vez de cambiar «la primera aparición».</summary>
    [Fact]
    public void Un_fragmento_que_aparece_varias_veces_se_rechaza_salvo_replaceAll()
    {
        File.WriteAllText(Path.Combine(_clone, UnitPath), "int a = 1;\r\nint a = 1;\r\n");
        Commit();
        FixToolbox toolbox = Toolbox(new ScriptedApprovals(answer: true));

        toolbox.ApplyEdit(UnitPath, "x", new[] { new FixEdit("int a = 1;", "int a = 2;") })
            .Error.Should().Contain("aparece 2 veces");

        toolbox.ApplyEdit(UnitPath, "x", new[] { new FixEdit("int a = 1;", "int a = 2;", ReplaceAll: true) })
            .Applied.Should().BeTrue();
    }

    /// <summary>
    /// Compilar es una PETICIÓN del agente que ejecuta la aplicación: no hay comando, ni
    /// argumentos, ni forma de apuntar a otro sitio. Y la salida vuelve recortada.
    /// </summary>
    [Fact]
    public void Compilar_lo_ejecuta_la_aplicacion_sobre_la_solucion_del_clon()
    {
        var runner = new RecordingProcess();
        var builds = new BuildRunner(runner);

        BuildAndTestResult result = builds.Run(_clone, CancellationToken.None);

        result.Ok.Should().BeTrue();
        runner.Calls.Should().HaveCount(2, "primero build, después test");
        runner.Calls.Should().OnlyContain(c => c.File == "dotnet");
        runner.Calls[0].Args.Should().StartWith("build").And.Contain("XBlast.sln");
        runner.Calls[1].Args.Should().StartWith("test").And.Contain("--no-build");
        runner.Calls.Should().OnlyContain(c => c.WorkingDirectory == _clone);
        result.Summary.Should().Contain("BUILD: OK").And.Contain("TESTS: OK");
    }

    /// <summary>Un clon sin solución no revienta la tool: devuelve un resultado que lo dice.</summary>
    [Fact]
    public void Un_clon_sin_solucion_no_revienta_la_tool_lo_declara()
    {
        string empty = Path.Combine(_root, "vacio");
        Directory.CreateDirectory(empty);

        BuildAndTestResult result = new BuildRunner(new NoProcess()).Run(empty, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Summary.Should().Contain("No se encontró ninguna solución");
        result.Summary.Should().Contain("NO se ha compilado", "el agente tiene que declararlo");
    }

    /// <summary>Si la compilación falla, los tests NO se ejecutan: el resumen ya dice lo que importa.</summary>
    [Fact]
    public void Si_el_build_falla_no_se_ejecutan_los_tests()
    {
        var runner = new RecordingProcess { ExitCode = 1 };

        BuildAndTestResult result = new BuildRunner(runner).Run(_clone, CancellationToken.None);

        result.Ok.Should().BeFalse();
        runner.Calls.Should().ContainSingle();
        result.Summary.Should().Contain("TESTS: no se ejecutaron");
    }

    // ================================================================= snapshot y descarte

    /// <summary>
    /// <b>El descarte restaura BYTE A BYTE.</b> Se compara el contenido binario, no el texto: el
    /// clon tiene que volver con su codificación, su BOM y sus finales de línea, no con unos
    /// equivalentes.
    /// </summary>
    [Fact]
    public void Descartar_restaura_los_ficheros_byte_a_byte()
    {
        byte[] before = File.ReadAllBytes(Path.Combine(_clone, UnitPath));
        byte[] callerBefore = File.ReadAllBytes(Path.Combine(_clone, "Common/Reader.cs"));

        FixSnapshotSet set = _snapshots.Begin("SESSION1", Slug, _clone, "BUG-0003", "t");
        var toolbox = new FixToolbox(
            _clone, new[] { UnitPath }, _snapshots, set, new ScriptedApprovals(answer: true),
            new FixPauseGate(), new BuildRunner(new NoProcess()), CancellationToken.None);

        toolbox.ApplyEdit(UnitPath, "arregla", new[] { new FixEdit("var bytes", "var octets") })
            .Applied.Should().BeTrue();
        toolbox.ApplyEdit("Common/Reader.cs", "adapta", new[] { new FixEdit("Read(string s)", "Read(string hex)") })
            .Applied.Should().BeTrue();
        toolbox.ApplyEdit("Common/Nuevo.cs", "hace falta", new[] { new FixEdit(string.Empty, "// nuevo\r\n") })
            .Applied.Should().BeTrue();

        File.ReadAllBytes(Path.Combine(_clone, UnitPath)).Should().NotEqual(before);

        FixRestoreReport report = _snapshots.Restore(set);

        report.Ok.Should().BeTrue(report.Message);
        report.Restored.Should().Be(2);
        report.Deleted.Should().Be(1, "el fichero que creó el agente se borra, no se vacía");
        File.ReadAllBytes(Path.Combine(_clone, UnitPath)).Should().Equal(before);
        File.ReadAllBytes(Path.Combine(_clone, "Common/Reader.cs")).Should().Equal(callerBefore);
        File.Exists(Path.Combine(_clone, "Common/Nuevo.cs")).Should().BeFalse();
        WorkingTree.Inspect(_clone).Clean.Should().BeTrue("el clon vuelve a estar como estaba");
    }

    /// <summary>La copia es la del ANTES DE LA SESIÓN, no la de la última edición.</summary>
    [Fact]
    public void La_copia_de_seguridad_se_toma_una_sola_vez_por_fichero()
    {
        byte[] original = File.ReadAllBytes(Path.Combine(_clone, UnitPath));
        FixSnapshotSet set = _snapshots.Begin("SESSION2", Slug, _clone, "BUG-0003", "t");
        FixToolbox toolbox = Toolbox(new ScriptedApprovals(answer: true), set: set);

        toolbox.ApplyEdit(UnitPath, "paso 1", new[] { new FixEdit("var bytes", "var octets") });
        toolbox.ApplyEdit(UnitPath, "paso 2", new[] { new FixEdit("var octets", "var buffer") });

        set.Entries.Should().ContainSingle();
        _snapshots.Restore(set);
        File.ReadAllBytes(Path.Combine(_clone, UnitPath)).Should().Equal(original);
    }

    /// <summary>
    /// El registro sobrevive al proceso: cerrar Atalaya con un arreglo a medias no puede llevarse
    /// el botón de descartar.
    /// </summary>
    [Fact]
    public void El_registro_de_lo_tocado_se_puede_recuperar_en_otra_ejecucion()
    {
        FixSnapshotSet set = _snapshots.Begin("SESSION3", Slug, _clone, "BUG-0003", "t");
        Toolbox(new ScriptedApprovals(answer: true), set: set)
            .ApplyEdit(UnitPath, "arregla", new[] { new FixEdit("var bytes", "var octets") });

        var reopened = new FixSnapshotStore(_paths);
        IReadOnlyList<FixSnapshotSet> pending = reopened.ListPending();

        pending.Should().ContainSingle();
        pending[0].Files.Should().Equal(UnitPath);
        reopened.Restore(pending[0]).Ok.Should().BeTrue();
        reopened.ListPending().Should().BeEmpty("un registro cerrado ya no se ofrece");
    }

    // ================================================================= la sesión completa

    /// <summary>
    /// El flujo entero contra un hallazgo real: precondiciones → narración → elicitación
    /// respondida → edición dentro de ámbito → build/tests por delegación → cierre con resumen y
    /// sugerencia de commit. Y al terminar: <b>el hallazgo sigue ACTIVO</b>.
    /// </summary>
    [Fact]
    public async Task Una_sesion_completa_narra_pregunta_edita_compila_y_cierra_sin_resolver()
    {
        var agent = new FakeCopilotAgent(fixScript: _ => new[]
        {
            new FixStep(Narration: "Voy a validar la longitud antes de recorrer el array.\n"),
            new FixStep(
                Question: "Validar rompe a los llamadores que dependían del truncado. "
                    + "(A) lanzar excepción y adaptar 1 llamador, (B) compatible + aviso, (C) abortar",
                Choices: new[] { "(A) excepción", "(B) compatible + aviso", "(C) abortar" },
                AllowFreeform: false),
            new FixStep(Read: UnitPath),
            new FixStep(Edit: new FixStepEdit(
                UnitPath, "es donde está el defecto",
                new[] { new FixEdit("var bytes = new byte[hex.Length / 2];",
                    "if (hex.Length % 2 != 0)\r\n        {\r\n            throw new ArgumentException(\"longitud impar\", nameof(hex));\r\n        }\r\n\r\n        var bytes = new byte[hex.Length / 2];") })),
            new FixStep(Build: true),
            new FixStep(Done: new FixDoneArgs(
                "Se valida que la cadena tenga longitud par antes de convertirla.",
                "Valida longitud par en HexStringToByteArray (BUG-0003)",
                "Antes recorría hex.Length / 2 bytes en silencio y perdía el último nibble.",
                "Ningún llamador dependía del truncado.")),
        });

        LiveFixService fix = Service(agent);
        var answered = new List<FixQuestion>();
        fix.Conversation.CollectionChanged += (_, e) =>
        {
            foreach (object? item in e.NewItems ?? Array.Empty<object>())
            {
                if (item is FixQuestion question)
                {
                    answered.Add(question);
                    fix.Answer(question, "(A) excepción");
                }
            }
        };

        await fix.StartAsync(new FixSessionRequest(Slug, _findingId));

        fix.HasFailed.Should().BeFalse(fix.FailureMessage);
        fix.HasFinished.Should().BeTrue();

        // La elicitación ocurrió y se contestó.
        answered.Should().ContainSingle();
        answered[0].Kind.Should().Be(FixAskKind.Decision);
        answered[0].Answer.Should().Be("(A) excepción");

        // La narración llegó, y el diff también.
        fix.Conversation.OfType<FixMessage>().Should()
            .Contain(m => m.Voice == FixVoice.Agente && m.Text.Contains("validar la longitud"));
        fix.Files.Should().ContainSingle();
        fix.Files[0].RelativePath.Should().Be(UnitPath);
        fix.Files[0].Added.Should().BeGreaterThan(0);
        fix.Files[0].Lines.Should().Contain(l => l.Kind == DiffKind.Anadida && l.Text.Contains("ArgumentException"));

        // Build/tests por delegación de la app.
        fix.HasBuildResult.Should().BeTrue();
        fix.LastBuild.Should().Contain("BUILD");

        // El cierre trae resumen y sugerencia de commit dentro del tope.
        fix.Summary.Should().Contain("longitud par");
        fix.Commit.Title.Should().Be("Valida longitud par en HexStringToByteArray (BUG-0003)");
        fix.Commit.Title.Length.Should().BeLessThanOrEqualTo(CommitSuggestion.MaxTitleLength);
        fix.Commit.Description.Should().Contain("nibble");
        fix.Risks.Should().Contain("Ningún llamador");
        fix.Commit.ToClipboard().Should().StartWith("Valida longitud par");

        // Y el CLON quedó modificado, sin commitear.
        File.ReadAllText(Path.Combine(_clone, UnitPath)).Should().Contain("longitud impar");
        WorkingTree.Inspect(_clone).Clean.Should().BeFalse();

        // ARREGLAR NO RESUELVE.
        Finding stored = _hub.Store.TryReadFinding(Slug, _findingId.ToString())!;
        stored.Status.Should().Be(FindingStatus.Activo);
        stored.Resolved.Should().BeNull();
        stored.History.Should().Contain(h => h.Event == FindingEvent.FixProposed);
        stored.History.Last(h => h.Event == FindingEvent.FixProposed).Detail
            .Should().Contain("arreglo asistido ejecutado");

        // La sesión queda registrada como fix, con su informe.
        AuditSession session = _hub.Store.ListSessions(Slug).Single();
        session.Mode.Should().Be(AuditMode.Fix);
        session.Notes.Should().Contain(n => n.Contains(UnitPath));
        File.Exists(_hub.HubPaths.ReportFile(Slug, session.Id.ToString())).Should().BeTrue();
        string report = File.ReadAllText(_hub.HubPaths.ReportFile(Slug, session.Id.ToString()));
        report.Should().Contain("Arreglo asistido");
        report.Should().Contain("NO están commiteados");
        report.Should().Contain("Valida longitud par");

        // Y el cerrojo queda libre para la siguiente sesión.
        _busy.IsBusy.Should().BeFalse();
    }

    /// <summary>
    /// El encargo hereda F6.7/F6.8 al completo: código actual del clon, «quién usa este código»
    /// con sus llamadores reales, y las reglas del modo interactivo — que dicen PREGUNTA, no
    /// decidas.
    /// </summary>
    [Fact]
    public async Task El_encargo_lleva_el_codigo_de_ahora_los_llamadores_y_las_reglas_del_modo_interactivo()
    {
        string? prompt = null;
        var agent = new FakeCopilotAgent(fixScript: request =>
        {
            prompt = request.Prompt;
            return Array.Empty<FixStep>();
        });

        await Service(agent).StartAsync(new FixSessionRequest(Slug, _findingId));

        prompt.Should().NotBeNull();
        prompt!.Should().Contain("BUG-0003");
        prompt.Should().Contain("## Quién usa este código", "hereda la sección de F6.8 tal cual");
        prompt.Should().Contain("Common/Reader.cs", "el llamador real del clon");
        prompt.Should().Contain("HexStringToByteArray", "el código de ahora, leído del clon");
        prompt.Should().Contain("NO decidas — PREGUNTA");
        prompt.Should().Contain("ask_user");
        prompt.Should().Contain("No tienes shell, ni git, ni red");
        prompt.Should().Contain("ANTES de darlo", "la narración es parte del producto");
    }

    /// <summary>
    /// Una autorización denegada a mitad de sesión: el agente lo sabe, el fichero no se toca y la
    /// tarjeta queda contestada en la conversación.
    /// </summary>
    [Fact]
    public async Task Una_autorizacion_denegada_deja_el_fichero_intacto_y_se_lo_dice_al_agente()
    {
        ApplyEditResult? outcome = null;
        var agent = new FakeCopilotAgent(fixScript: _ => new[]
        {
            new FixStep(
                Edit: new FixStepEdit("Common/Reader.cs", "adapta el llamador",
                    new[] { new FixEdit("Read(string s)", "Read(string hex)") }),
                OnEdit: r => outcome = r),
            new FixStep(Done: new FixDoneArgs("nada que hacer", "No toca nada (BUG-0003)", "", null)),
        });

        LiveFixService fix = Service(agent);
        fix.Conversation.CollectionChanged += (_, e) =>
        {
            foreach (object? item in e.NewItems ?? Array.Empty<object>())
            {
                if (item is FixQuestion { Kind: FixAskKind.Autorizacion } question)
                {
                    fix.Answer(question, LiveFixService.DenyLabel);
                }
            }
        };

        await fix.StartAsync(new FixSessionRequest(Slug, _findingId));

        outcome!.Denied.Should().BeTrue();
        fix.Files.Should().BeEmpty();
        File.ReadAllText(Path.Combine(_clone, "Common/Reader.cs")).Should().Contain("Read(string s)");
        fix.Conversation.OfType<FixQuestion>().Should()
            .ContainSingle().Which.Text.Should().Contain("Common/Reader.cs");
    }

    /// <summary>Una orden que el runtime no acepta a mitad de turno se ENCOLA y llega después.</summary>
    [Fact]
    public async Task Una_orden_del_usuario_que_no_cabe_en_el_turno_se_encola_para_el_siguiente()
    {
        var delivered = new List<string>();
        var agent = new FakeCopilotAgent(
            fixScript: _ => Array.Empty<FixStep>(),
            fixFollowUp: (message, _) => delivered.Add(message));

        LiveFixService fix = Service(agent);
        Task run = fix.StartAsync(new FixSessionRequest(Slug, _findingId));
        await fix.SendUserMessageAsync("no toques Reader.cs");
        await run;

        delivered.Should().ContainSingle().Which.Should().Contain("no toques Reader.cs");
        fix.Conversation.OfType<FixMessage>().Should()
            .Contain(m => m.Voice == FixVoice.Usuario && m.Text == "no toques Reader.cs");
        fix.Conversation.OfType<FixMessage>().Should()
            .Contain(m => m.IsSystem && m.Text.Contains("en cuanto lo termine"));
    }

    /// <summary>Descartar desde el view-model: pregunta antes, y cancelar no toca nada.</summary>
    [Fact]
    public async Task Descartar_pregunta_antes_y_cancelar_no_revierte_nada()
    {
        var agent = new FakeCopilotAgent(fixScript: _ => new[]
        {
            new FixStep(Edit: new FixStepEdit(UnitPath, "arregla",
                new[] { new FixEdit("var bytes", "var octets") })),
            new FixStep(Done: new FixDoneArgs("hecho", "Arregla (BUG-0003)", "", null)),
        });

        LiveFixService fix = Service(agent);
        await fix.StartAsync(new FixSessionRequest(Slug, _findingId));

        var confirmer = new ScriptedDiscard(answer: false);
        var vm = new AssistedFixViewModel(fix, _toasts, confirmer);
        vm.DiscardAllCommand.Execute(null);

        confirmer.Asked.Should().ContainSingle();
        File.ReadAllText(Path.Combine(_clone, UnitPath)).Should().Contain("var octets");

        confirmer.Answer = true;
        vm.DiscardAllCommand.Execute(null);
        File.ReadAllText(Path.Combine(_clone, UnitPath)).Should().Contain("var bytes");
        fix.Files.Should().BeEmpty();
    }

    // ================================================================= utilidades

    private Finding? Finding() => _hub.Store.TryReadFinding(Slug, _findingId.ToString());

    private AssistedFixLauncher Launcher() => Launcher(_settings);

    private AssistedFixLauncher Launcher(SettingsService settings)
        => new(settings, new CloneLinkService(_hub, _machines), _machines, _busy);

    private LiveFixService Service(ICopilotAgent agent)
        => new(
            _hub, agent, _machines, _ulids, _settings, new ReferenceCollector(), _snapshots,
            Launcher(), _busy, new BuildRunner(new NoProcess()));

    private FixToolbox Toolbox(
        IFixApprovals approvals, int readBudget = FixToolbox.DefaultReadBudget, FixSnapshotSet? set = null)
        => new(
            _clone,
            new[] { UnitPath },
            _snapshots,
            set ?? _snapshots.Begin(Guid.NewGuid().ToString("N"), Slug, _clone, "BUG-0003", "t"),
            approvals,
            new FixPauseGate(),
            new BuildRunner(new NoProcess()),
            CancellationToken.None,
            readBudget);

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

    /// <summary>Quién autoriza, guionizado: anota lo que le preguntaron y contesta lo pactado.</summary>
    private sealed class ScriptedApprovals : IFixApprovals
    {
        private readonly bool _answer;

        public ScriptedApprovals(bool answer) => _answer = answer;

        public List<(string Path, string Reason)> Asked { get; } = new();

        public Task<bool> ApproveFileAsync(string relativePath, string reason, CancellationToken ct)
        {
            Asked.Add((relativePath, reason));
            return Task.FromResult(_answer);
        }
    }

    private sealed class ScriptedDiscard : IFixDiscardConfirmer
    {
        public ScriptedDiscard(bool answer) => Answer = answer;

        public bool Answer { get; set; }

        public List<IReadOnlyList<string>> Asked { get; } = new();

        public bool Confirm(IReadOnlyList<string> files)
        {
            Asked.Add(files);
            return Answer;
        }
    }

    /// <summary>
    /// Compilar sin compilar: ningún test lanza <c>dotnet build</c> de verdad (tardaría minutos y
    /// dependería de lo que haya instalado). Lo que se prueba aquí es la DELEGACIÓN — que el
    /// agente lo pida y la aplicación lo ejecute—, no MSBuild.
    /// </summary>
    private sealed class NoProcess : IProcessRunner
    {
        public ProcessOutcome Run(
            string fileName, string arguments, string workingDirectory, TimeSpan timeout, CancellationToken ct)
            => new(0, $"{fileName} {arguments}\r\nCompilación correcta.\r\nAprobadas: 42", false);
    }

    /// <summary>El mismo doble, anotando exactamente qué se le pidió ejecutar.</summary>
    private sealed class RecordingProcess : IProcessRunner
    {
        public int ExitCode { get; set; }

        public List<(string File, string Args, string WorkingDirectory)> Calls { get; } = new();

        public ProcessOutcome Run(
            string fileName, string arguments, string workingDirectory, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add((fileName, arguments, workingDirectory));
            return new ProcessOutcome(ExitCode, "salida", false);
        }
    }
}
