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

    // ============================================ BUGFIX-LECTURA: leer por trozos, sin el usuario

    /// <summary>
    /// <b>Un fichero de tres veces el tope se lee ENTERO, por trozos, y vuelve a ser el fichero.</b>
    /// Es la promesa entera de BUGFIX-LECTURA en un test: mientras la respuesta diga cuánto falta,
    /// el agente encadena rangos hasta el final y lo que junta es el original —el mismo criterio
    /// byte a byte con el que se restauran los snapshots (D-560)—. Sin el rango, la primera lectura
    /// devolvía 120.000 caracteres cortados a mitad de línea y el resto no había forma de pedirlo.
    /// </summary>
    [Fact]
    public void Un_fichero_de_tres_veces_el_tope_se_lee_entero_por_trozos_y_concatena_byte_a_byte()
    {
        string original = Grande(FixToolbox.MaxFileChars * 3);
        string ruta = Path.Combine(_clone, "Grande.cs");
        File.WriteAllText(ruta, original, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        FixToolbox toolbox = Toolbox(new ScriptedApprovals(answer: true));
        var juntado = new StringBuilder();
        int desde = 1;
        int vueltas = 0;
        int total;

        do
        {
            ReadFileResult trozo = toolbox.ReadFile("Grande.cs", desde);
            trozo.Ok.Should().BeTrue(trozo.Error);
            trozo.FirstLine.Should().Be(desde);
            juntado.Append(trozo.Content);
            total = trozo.TotalLines;
            desde = trozo.LastLine + 1;
            vueltas++;
        }
        while (desde <= total && vueltas < 10);

        vueltas.Should().Be(4, "tres trozos de tope más el rabo; y ninguno cuesta más de una lectura");
        juntado.ToString().Should().Be(original);
        new UTF8Encoding(false).GetBytes(juntado.ToString()).Should().Equal(File.ReadAllBytes(ruta));
    }

    /// <summary>
    /// <b>La lectura SIN rango de un fichero grande no corta en silencio.</b> Dice cuántas líneas
    /// tiene el fichero, cuáles van y por dónde sigue — y corta por línea entera, que es lo que
    /// permite volver a pegarlas—. Es la diferencia entre un agente que pide el trozo siguiente y
    /// uno que se inventa un límite y le pide al usuario que le pegue el resto.
    /// </summary>
    [Fact]
    public void La_lectura_sin_rango_de_un_fichero_grande_dice_cuantas_lineas_tiene_y_cuales_devuelve()
    {
        string original = Grande(FixToolbox.MaxFileChars * 3);
        File.WriteAllText(Path.Combine(_clone, "Grande.cs"), original);
        int lineas = original.Split('\n').Length - 1;

        ReadFileResult primero = Toolbox(new ScriptedApprovals(answer: true)).ReadFile("Grande.cs");

        primero.Ok.Should().BeTrue();
        primero.TotalLines.Should().Be(lineas);
        primero.FirstLine.Should().Be(1);
        primero.LastLine.Should().BeLessThan(lineas);
        primero.Notice.Should().NotBeNull()
            .And.Contain($"fichero de {lineas} líneas")
            .And.Contain($"devueltas 1–{primero.LastLine}")
            .And.Contain("startLine")
            .And.Contain($"startLine {primero.LastLine + 1}");
        primero.Content.Should().EndWith("\r\n", "se corta por línea entera, nunca a mitad");
        primero.Content!.Length.Should().BeLessOrEqualTo(FixToolbox.MaxFileChars);
    }

    /// <summary>
    /// El rango es para VER, no para editar: <c>apply_edit</c> sigue siendo fragmento literal
    /// (D-545). Y un rango imposible se contesta con el número de líneas que sí tiene, que es lo
    /// que el agente necesita para corregirse.
    /// </summary>
    [Fact]
    public void Un_rango_fuera_del_fichero_dice_cuantas_lineas_tiene_de_verdad()
    {
        FixToolbox toolbox = Toolbox(new ScriptedApprovals(answer: true));

        int lineas = OriginalCode.Split('\n').Length - 1;   // el fichero acaba en salto
        ReadFileResult fuera = toolbox.ReadFile(UnitPath, lineas + 100);

        fuera.Ok.Should().BeFalse();
        fuera.Error.Should().Contain($"tiene {lineas} líneas");
        fuera.TotalLines.Should().Be(lineas);
    }

    /// <summary>
    /// <b>El encargo dice cómo se lee un fichero que no cabe, y que al usuario no se le pide que
    /// pegue código.</b> Es el test de texto de «Cómo trabajas aquí» (D-543): la herramienta sin la
    /// instrucción no arregla nada, porque un agente que no sabe que existe el rango no lo usa.
    /// </summary>
    [Fact]
    public async Task El_encargo_dice_como_leer_un_fichero_que_no_cabe_y_que_no_se_pide_pegar_codigo()
    {
        string? prompt = null;
        var agent = new FakeCopilotAgent(fixScript: request =>
        {
            prompt = request.Prompt;
            return Array.Empty<FixStep>();
        });

        await Service(agent).StartAsync(new FixSessionRequest(Slug, _findingId));

        prompt.Should().NotBeNull();
        prompt!.Should().Contain("read_file(path, startLine, endLine)");
        prompt.Should().Contain("pide el resto por rango");
        prompt.Should().Contain("Jamás le pidas al usuario que te pegue código o líneas");
    }

    /// <summary>Líneas numeradas, para que un trozo se reconozca por su contenido.</summary>
    private static string Grande(int minChars)
    {
        var sb = new StringBuilder(minChars + 128);
        for (int n = 1; sb.Length < minChars; n++)
        {
            sb.Append("// linea ").Append(n).Append(' ')
              .Append('x', 60).Append("\r\n");
        }

        return sb.ToString();
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
    /// <para>
    /// Sin nada tocado todavía —y sin proyectos en este clon de prueba— el ámbito es la solución,
    /// que es el que había antes de H9.1. El ámbito por proyecto se prueba en
    /// <see cref="BuildScopeTests"/>, con un clon que sí tiene proyectos.
    /// </para>
    /// </summary>
    [Fact]
    public void Compilar_lo_ejecuta_la_aplicacion_sobre_la_solucion_del_clon()
    {
        var runner = new RecordingProcess();
        var builds = new BuildRunner(runner);

        BuildVerdict result = builds.Run(_clone, CancellationToken.None);

        result.Ok.Should().BeTrue();
        runner.Calls.Should().HaveCount(2, "primero build, después test");
        runner.Calls.Should().OnlyContain(c => c.File == "dotnet");
        runner.Calls[0].Args.Should().StartWith("build").And.Contain("XBlast.sln");
        runner.Calls[1].Args.Should().StartWith("test").And.Contain("--no-build");
        runner.Calls.Should().OnlyContain(c => c.WorkingDirectory == _clone);
        result.Summary.Should().Contain("BUILD: OK").And.Contain("TESTS (XBlast.sln): OK");
        result.TargetLabel.Should().Contain("solución");
    }

    /// <summary>Un clon sin solución no revienta la tool: devuelve un resultado que lo dice.</summary>
    [Fact]
    public void Un_clon_sin_solucion_no_revienta_la_tool_lo_declara()
    {
        string empty = Path.Combine(_root, "vacio");
        Directory.CreateDirectory(empty);

        BuildVerdict result = new BuildRunner(new NoProcess()).Run(empty, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Summary.Should().Contain("No hay nada que Atalaya pueda compilar");
        result.Summary.Should().Contain("NO se ha compilado", "el agente tiene que declararlo");
    }

    /// <summary>Si la compilación falla, los tests NO se ejecutan: el resumen ya dice lo que importa.</summary>
    [Fact]
    public void Si_el_build_falla_no_se_ejecutan_los_tests()
    {
        var runner = new RecordingProcess { ExitCode = 1 };

        BuildVerdict result = new BuildRunner(runner).Run(_clone, CancellationToken.None);

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
        answered[0].Ask.Should().Be(FixAskKind.Decision);
        answered[0].Answer.Should().Be("(A) excepción");

        // La narración llegó, y el diff también.
        fix.Conversation.OfType<FixMessage>().Should()
            .Contain(m => m.Voice == ConversationVoice.Agente && m.Text.Contains("validar la longitud"));
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
                if (item is FixQuestion { Ask: FixAskKind.Autorizacion } question)
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
            .Contain(m => m.Voice == ConversationVoice.Usuario && m.Text == "no toques Reader.cs");
        fix.Conversation.OfType<FixMessage>().Should()
            .Contain(m => m.IsAtalaya && m.Text.Contains("en cuanto lo termine"));
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

    // ================================================================= BUGFIX-CIERRE · cerrar

    /// <summary>
    /// Cerrar NO es descartar. Con ficheros tocados se pregunta, y conservar es lo normal: son del
    /// usuario y su árbol es suyo. La pantalla se va; el clon se queda como está.
    /// </summary>
    [Fact]
    public async Task Cerrar_con_cambios_pregunta_y_conservar_deja_el_clon_intacto()
    {
        LiveFixService fix = await FinishedFix();
        var closer = new ScriptedClose(FixCloseChoice.ConservarYCerrar);
        var vm = new AssistedFixViewModel(fix, _toasts, new ScriptedDiscard(answer: false),
            closeConfirmer: closer);

        fix.HasPendingChanges.Should().BeTrue();
        vm.CanClose.Should().BeTrue();

        vm.CloseCommand.Execute(null);

        closer.Asked.Should().ContainSingle("hay cambios: hay que preguntar");
        File.ReadAllText(Path.Combine(_clone, UnitPath)).Should().Contain("var octets",
            "conservar significa conservar: cerrar una pantalla no toca el árbol de nadie");
        fix.HasSession.Should().BeFalse("y la entrada del rail se va");
        fix.HasPendingChanges.Should().BeFalse("Atalaya deja de ofrecerse a revertir lo que ya es suyo");
    }

    /// <summary>Y descartar-y-cerrar pasa por el MISMO camino de «Descartar todo».</summary>
    [Fact]
    public async Task Cerrar_descartando_revierte_y_luego_cierra()
    {
        LiveFixService fix = await FinishedFix();
        var vm = new AssistedFixViewModel(fix, _toasts, new ScriptedDiscard(answer: true),
            closeConfirmer: new ScriptedClose(FixCloseChoice.DescartarYCerrar));

        vm.CloseCommand.Execute(null);

        File.ReadAllText(Path.Combine(_clone, UnitPath)).Should().Contain("var bytes");
        fix.HasSession.Should().BeFalse();
    }

    /// <summary>Cancelar no cierra nada, que es lo que significa cancelar.</summary>
    [Fact]
    public async Task Cancelar_al_cerrar_deja_la_pantalla_donde_estaba()
    {
        LiveFixService fix = await FinishedFix();
        var vm = new AssistedFixViewModel(fix, _toasts, new ScriptedDiscard(answer: false),
            closeConfirmer: new ScriptedClose(FixCloseChoice.Cancelar));

        vm.CloseCommand.Execute(null);

        fix.HasSession.Should().BeTrue();
        File.ReadAllText(Path.Combine(_clone, UnitPath)).Should().Contain("var octets");
    }

    /// <summary>
    /// Sin cambios que conservar, cerrar es directo y sin preguntas — que es exactamente el caso
    /// del parte: el arreglo fallido por cuota no había tocado un solo fichero, y la propia
    /// pantalla lo decía.
    /// </summary>
    [Fact]
    public async Task Un_arreglo_fallido_sin_tocar_nada_se_cierra_sin_preguntar()
    {
        LiveFixService fix = Service(new FakeCopilotAgent(fixScript: _ =>
            throw new AuditorProviderException(
                CopilotHelp.QuotaExhausted("mensual"), AgentProblem.QuotaExhausted, "crudo")));

        await fix.StartAsync(new FixSessionRequest(Slug, _findingId));

        var closer = new ScriptedClose(FixCloseChoice.Cancelar);
        var vm = new AssistedFixViewModel(fix, _toasts, new ScriptedDiscard(answer: false),
            closeConfirmer: closer);

        fix.HasFailed.Should().BeTrue();
        fix.HasPendingChanges.Should().BeFalse("el agente no llegó a escribir nada");
        vm.CanClose.Should().BeTrue();

        vm.CloseCommand.Execute(null);

        closer.Asked.Should().BeEmpty("no hay nada que preguntar: no se tocó ningún fichero");
        fix.HasSession.Should().BeFalse("y la pantalla deja de ocupar una entrada del rail");
    }

    /// <summary>Un arreglo terminado con éxito y sin cambios vivos también se archiva.</summary>
    [Fact]
    public async Task Cerrar_no_borra_el_informe_del_arreglo()
    {
        LiveFixService fix = await FinishedFix();
        string sessionId = fix.SessionId;

        fix.Close(keepChanges: true).Should().BeTrue();

        _hub.Store.ListSessions(Slug).Should().Contain(s => s.Id.ToString() == sessionId,
            "cerrar quita la pantalla de en medio, no borra historia");
    }

    /// <summary>Un arreglo con cambios NO se cierra sin que alguien lo haya decidido.</summary>
    [Fact]
    public async Task Con_cambios_vivos_no_se_cierra_por_las_buenas()
        => (await FinishedFix()).Close(keepChanges: false).Should().BeFalse(
            "sería quitar de la vista el único camino al descarte");

    /// <summary>
    /// <b>UN ARREGLO QUE NO TOCÓ NADA NO OFRECE NADA QUE HACER CON LO QUE NO HAY</b> (R10 §7).
    /// <para>
    /// Detenido, descartado, o el agente no llegó a editar: los tres acaban con «Ficheros tocados:
    /// ninguno», y con los tres la pantalla de cierre seguía enseñando el aviso ámbar «los cambios
    /// están en tu clon sin commitear», la tarjeta «Sugerencia de commit», «Verificar ahora» y «Me
    /// quedo los cambios». Cuatro piezas que hablan de un cambio inexistente, y la peor es la
    /// sugerencia de commit: redacta el mensaje de un trabajo que nadie hizo, listo para copiar.
    /// </para>
    /// <para>
    /// <b>La vista Y el informe</b>, porque el informe es el que se lee después, cuando ya nadie
    /// recuerda que no hubo cambios. Y es una regla que se rompe en silencio: nada falla, la
    /// pantalla se pinta entera y lo único que pasa es que miente.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Un_arreglo_sin_ficheros_tocados_no_ofrece_verificar_ni_quedarse_los_cambios()
    {
        // El agente cierra sin editar: es lo que deja una sesión detenida a tiempo.
        var agent = new FakeCopilotAgent(fixScript: _ => new[]
        {
            new FixStep(Done: new FixDoneArgs(
                "Se revisó la unidad y no se llegó a tocar nada.", "Arregla (BUG-0003)", "", null)),
        });

        LiveFixService fix = Service(agent);
        await fix.StartAsync(new FixSessionRequest(Slug, _findingId));

        fix.HasFinished.Should().BeTrue();
        fix.Files.Should().BeEmpty("el agente no editó");

        // ---- la vista
        var vm = new AssistedFixViewModel(fix, _toasts, new ScriptedDiscard(answer: false));

        vm.ClosedWithoutChanges.Should().BeTrue();
        vm.ClosedWithChanges.Should().BeFalse(
            "de esto cuelgan el aviso ámbar, la sugerencia de commit, «Verificar ahora» y «Me quedo los cambios»");
        vm.ClosingHeadline.Should().Be("Arreglo detenido · no hay cambios en tu clon");
        vm.CanDiscardAll.Should().BeFalse("no hay nada que descartar");
        vm.DiscardBlockedReason.Should().Be("no hay cambios", "y el botón apagado dice por qué (P-27)");

        // «Qué cambió y por qué» sí se enseña, con lo que haya: es lo único que esta pantalla
        // tiene que contar.
        fix.Summary.Should().NotBeEmpty();

        // ---- el informe
        string report = File.ReadAllText(fix.ReportPath!);

        report.Should().Contain("No hay cambios en el clon");
        report.Should().NotContain("Estos cambios NO están commiteados");
        report.Should().NotContain("## Sugerencia de commit");
        report.Should().Contain("## Qué cambió y por qué");
    }

    /// <summary>Una sesión de arreglo TERMINADA con su edición aplicada, lista para cerrar.</summary>
    private async Task<LiveFixService> FinishedFix()
    {
        var agent = new FakeCopilotAgent(fixScript: _ => new[]
        {
            new FixStep(Edit: new FixStepEdit(UnitPath, "arregla",
                new[] { new FixEdit("var bytes", "var octets") })),
            new FixStep(Done: new FixDoneArgs("hecho", "Arregla (BUG-0003)", "", null)),
        });

        LiveFixService fix = Service(agent);
        await fix.StartAsync(new FixSessionRequest(Slug, _findingId));
        return fix;
    }

    private sealed class ScriptedClose : IFixCloseConfirmer
    {
        private readonly FixCloseChoice _choice;

        public ScriptedClose(FixCloseChoice choice) => _choice = choice;

        public List<IReadOnlyList<string>> Asked { get; } = new();

        public FixCloseChoice Ask(IReadOnlyList<string> files)
        {
            Asked.Add(files);
            return _choice;
        }
    }

    // ================================================================= F9 §2 · la huella del arreglo

    /// <summary>
    /// <b>El arreglo deja su huella para no morderse la cola (F9 §2).</b> Sin esto, el commit con el
    /// que el usuario publique este arreglo haría que la unidad apareciera «cambiada desde su
    /// auditoría» —por culpa de la propia auditoría—, y cada arreglo realimentaría la lista de
    /// candidatas para siempre.
    /// <para>
    /// Se guarda el CONTENIDO que quedó escrito, no un hash de commit: Atalaya no commitea (D-556),
    /// así que al cerrar el arreglo el commit que lo recogerá todavía no existe.
    /// </para>
    /// </summary>
    [Fact]
    public async Task El_arreglo_registra_que_ficheros_dejo_escritos_y_con_que_contenido()
    {
        var agent = new FakeCopilotAgent(fixScript: _ => new[]
        {
            new FixStep(Edit: new FixStepEdit(UnitPath, "arregla",
                new[] { new FixEdit("var bytes", "var octets") })),
            new FixStep(Done: new FixDoneArgs("hecho", "Arregla (BUG-0003)", "", null)),
        });

        LiveFixService fix = Service(agent);
        await fix.StartAsync(new FixSessionRequest(Slug, _findingId));

        FixRecord record = _hub.Store.ListFixes(Slug).Should().ContainSingle().Subject;

        record.Id.ToString().Should().Be(fix.SessionId, "un arreglo, un registro, con el ULID de su sesión");
        record.FindingId.Should().Be(_findingId.ToString());
        record.FindingAlias.Should().Be("BUG-0003");
        record.By.Should().NotBeNullOrWhiteSpace("la huella lleva quién la dejó");

        FixFileStamp stamp = record.Files.Should().ContainSingle().Subject;
        stamp.Path.Should().Be(UnitPath);
        stamp.ContentHash.Should().Be(
            Atalaya.Domain.Hashing.HashUtil.NormalizedContentHash(
                File.ReadAllBytes(Path.Combine(_clone, UnitPath))),
            "es la huella de lo que el agente dejó en el clon, tal cual");
    }

    /// <summary>Sin ficheros tocados no hay nada que reconocer, y no se escribe un registro vacío.</summary>
    [Fact]
    public async Task Un_arreglo_que_no_toca_nada_no_deja_huella()
    {
        var agent = new FakeCopilotAgent(fixScript: _ => new[]
        {
            new FixStep(Done: new FixDoneArgs("no había nada que tocar", "Nada (BUG-0003)", "", null)),
        });

        await Service(agent).StartAsync(new FixSessionRequest(Slug, _findingId));

        _hub.Store.ListFixes(Slug).Should().BeEmpty();
    }

    // ================================================================= H9.1 §1 · volver al hallazgo

    /// <summary>
    /// <b>El círculo se cierra en las dos direcciones (H9.1 §1).</b> Terminada una sesión, del
    /// hallazgo se llega a su arreglo y del arreglo se vuelve a su hallazgo. Antes no había ninguna
    /// de las dos: el informe nombraba «OPT-0002» y quien lo leía tenía que ir a buscarlo a mano.
    /// </summary>
    [Fact]
    public async Task El_arreglo_deja_camino_de_vuelta_al_hallazgo_en_las_dos_direcciones()
    {
        var agent = new FakeCopilotAgent(fixScript: _ => new[]
        {
            new FixStep(Edit: new FixStepEdit(UnitPath, "arregla",
                new[] { new FixEdit("var bytes", "var octets") })),
            new FixStep(Done: new FixDoneArgs("hecho", "Arregla (BUG-0003)", "", null)),
        });

        LiveFixService fix = Service(agent);
        await fix.StartAsync(new FixSessionRequest(Slug, _findingId));

        // Del informe al hallazgo: la sesión registra de QUÉ hallazgo era.
        AuditSession session = _hub.Store.ListSessions(Slug).Single();
        session.FixFindingId.Should().Be(_findingId.ToString());
        session.FixFindingAlias.Should().Be("BUG-0003");

        // Y la fila de Informes lo lleva, que es lo que enciende el enlace.
        ReportEntry entry = new ReportsQuery(_hub).Find(Slug, session.Id.ToString())!;
        entry.HasFinding.Should().BeTrue();
        entry.FindingId.Should().Be(_findingId.ToString());
        entry.FindingAlias.Should().Be("BUG-0003");

        // Del hallazgo al informe: el evento del historial apunta a SU sesión.
        Finding stored = _hub.Store.TryReadFinding(Slug, _findingId.ToString())!;
        stored.History.Last(h => h.Event == FindingEvent.FixProposed).SessionId
            .Should().Be(session.Id.ToString());

        // Y la vista ofrece la vuelta. El identificador NO va en el rótulo (F16-RETOQUE §2·2):
        // ya está en la cabecera, entero y dos piezas más a la izquierda. Repetirlo era lo que
        // hacía la cabecera redundante además de apretada.
        var vm = new AssistedFixViewModel(fix, _toasts, new ScriptedDiscard(answer: false));
        vm.CanGoBackToFinding.Should().BeTrue();
        vm.BackToFindingLabel.Should().Be("Volver al hallazgo");
        vm.FindingAliasText.Should().Be("BUG-0003", "el alias vive en la identidad, y entero");
    }

    /// <summary>
    /// Un informe de arreglo anterior a H9.1 no trae el hallazgo, y eso no puede romper nada: el
    /// enlace simplemente no aparece. Lo viejo del hub sigue leyéndose.
    /// </summary>
    [Fact]
    public void Un_informe_de_arreglo_sin_hallazgo_registrado_no_ofrece_el_enlace()
    {
        var session = new AuditSession
        {
            Id = _ulids.NewUlid(),
            AppSlug = Slug,
            Mode = AuditMode.Fix,
            By = "alguien",
            Machine = "PC",
            StartedUtc = DateTimeOffset.UtcNow.AddDays(-1),
            EndedUtc = DateTimeOffset.UtcNow.AddDays(-1),
        };
        _hub.Store.WriteSession(session);
        _hub.Store.WriteReport(Slug, session.Id.ToString(), "# Arreglo asistido — X-BLAST\n\nCuerpo.");

        ReportEntry entry = new ReportsQuery(_hub).Find(Slug, session.Id.ToString())!;

        entry.HasFinding.Should().BeFalse();
        entry.FindingAlias.Should().BeNull();
    }

    // ================================================================= H9.1 §2 · el ámbito en la sesión

    /// <summary>
    /// El interruptor de «solución completa» es del usuario y vive en el SERVICIO, no en la vista:
    /// la vista es transitoria y navegar fuera no puede cambiar en silencio lo que se va a
    /// compilar. Apagado de serie, que es el ámbito acotado.
    /// </summary>
    [Fact]
    public void El_ambito_de_compilacion_lo_manda_el_usuario_y_sobrevive_a_la_vista()
    {
        LiveFixService fix = Service(new FakeCopilotAgent(fixScript: _ => Array.Empty<FixStep>()));

        fix.BuildFullSolution.Should().BeFalse("por defecto se compila solo el proyecto de lo tocado");

        var vm = new AssistedFixViewModel(fix, _toasts, new ScriptedDiscard(answer: false));
        vm.BuildFullSolution = true;

        fix.BuildFullSolution.Should().BeTrue();
        new AssistedFixViewModel(fix, _toasts, new ScriptedDiscard(answer: false))
            .BuildFullSolution.Should().BeTrue("el estado no vive en la vista");
    }

    /// <summary>
    /// El informe de una sesión fix separa lo que trajo el cambio de lo que ya estaba (H9.1 §2).
    /// Presentar 18 errores heredados como resultado de un arreglo de dos líneas es lo que
    /// convierte cada sesión en un susto.
    /// </summary>
    [Fact]
    public void El_informe_separa_los_errores_nuevos_de_los_preexistentes()
    {
        var verdict = new BuildVerdict(
            true,
            "salida completa de dotnet",
            TargetLabel: "la solución XBlast.sln",
            NewErrors: 0,
            PreexistingErrors: 2,
            NewErrorLines: Array.Empty<string>(),
            PreexistingErrorLines: new[] { "Viejo1.cs(1,5): error CS0246", "Viejo2.cs(2,5): error CS0246" },
            ExcludedProjects: new[] { "Native/CCCoreWrapper.vcxproj" },
            BaselineNote: "línea base medida el 28/08/2026 10:00 sobre el commit abc1234",
            HasBaseline: true,
            TestsRun: true,
            TestsOk: true);

        var sb = new System.Text.StringBuilder();
        ReportBuilder.AppendBuildSection(sb, verdict);
        string report = sb.ToString();

        report.Should().Contain("**Ámbito**: la solución XBlast.sln");
        report.Should().Contain("0 error(es) nuevo(s)");
        report.Should().Contain("Preexistentes (2) — ya fallaban antes del arreglo");
        report.Should().Contain("No son del cambio y no cuentan en el veredicto");
        report.Should().Contain("CCCoreWrapper.vcxproj");
        report.Should().Contain("toolset C++ de Visual Studio");
        report.Should().Contain("línea base medida");
    }

    // ================================================================= H9.1 §3 · los tests

    /// <summary>
    /// <b>El encargo declara la situación de tests, y el clon de prueba no tiene ninguno</b> —igual
    /// que XBLAST—. En el primer uso real el agente gastó turnos buscando un proyecto de tests
    /// inexistente y acabó declarando la búsqueda infructuosa como riesgo. Ahora se lo dicen antes
    /// de empezar, en imperativo, y la regla de «añade un test» desaparece del encargo: pedirle que
    /// pruebe donde no hay dónde es lo que le hacía salir a buscar.
    /// </summary>
    [Fact]
    public async Task El_encargo_dice_que_no_hay_tests_y_prohibe_buscarlos()
    {
        string? prompt = null;
        var agent = new FakeCopilotAgent(fixScript: request =>
        {
            prompt = request.Prompt;
            return Array.Empty<FixStep>();
        });

        LiveFixService fix = Service(agent);
        await fix.StartAsync(new FixSessionRequest(Slug, _findingId));

        prompt.Should().NotBeNull();
        prompt!.Should().Contain("no tiene proyectos de tests");
        prompt.Should().Contain("No los busques ni los crees");
        prompt.Should().NotContain("Añade o ajusta un test",
            "sin tests, pedirle que escriba uno es lo que le manda a explorar");
        prompt.Should().Contain("no lo declares como riesgo",
            "que no haya tests es un hecho del proyecto, no una carencia del arreglo");

        // Y se dice en la conversación, una vez y sin drama.
        fix.TestSituation.HasTests.Should().BeFalse();
        fix.Conversation.OfType<FixMessage>().Should()
            .Contain(m => m.IsAtalaya && m.Text.Contains("no tiene proyectos de tests"));
    }

    /// <summary>
    /// Con tests, el encargo los NOMBRA y vuelve a pedir el test que cubra el defecto. La regla no
    /// se ha perdido: se ha condicionado a que exista dónde ponerlo.
    /// </summary>
    [Fact]
    public async Task Con_un_proyecto_de_tests_el_encargo_lo_nombra_y_vuelve_a_pedir_el_test()
    {
        Directory.CreateDirectory(Path.Combine(_clone, "Common"));
        File.WriteAllText(Path.Combine(_clone, "Common", "Common.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Directory.CreateDirectory(Path.Combine(_clone, "Common.Tests"));
        File.WriteAllText(Path.Combine(_clone, "Common.Tests", "Common.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>"
            + "<PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.11.1\" />"
            + "<ProjectReference Include=\"..\\Common\\Common.csproj\" /></ItemGroup></Project>");
        Commit();

        string? prompt = null;
        var agent = new FakeCopilotAgent(fixScript: request =>
        {
            prompt = request.Prompt;
            return Array.Empty<FixStep>();
        });

        await Service(agent).StartAsync(new FixSessionRequest(Slug, _findingId));

        prompt.Should().NotBeNull();
        prompt!.Should().Contain("Common.Tests/Common.Tests.csproj");
        prompt.Should().Contain("los ejecutará");
        prompt.Should().Contain("Añade o ajusta un test");
        prompt.Should().NotContain("No los busques ni los crees");
    }

    /// <summary>
    /// El informe dice que no hay tests como HECHO del repositorio, una vez. No como resultado de
    /// una búsqueda, que es lo que acabó escrito en el informe del primer uso real.
    /// </summary>
    [Fact]
    public async Task El_informe_dice_que_no_hay_tests_como_hecho_del_repositorio()
    {
        var agent = new FakeCopilotAgent(fixScript: _ => new[]
        {
            new FixStep(Edit: new FixStepEdit(UnitPath, "arregla",
                new[] { new FixEdit("var bytes", "var octets") })),
            new FixStep(Build: true),
            new FixStep(Done: new FixDoneArgs("hecho", "Arregla (BUG-0003)", "", null)),
        });

        await Service(agent).StartAsync(new FixSessionRequest(Slug, _findingId));

        AuditSession session = _hub.Store.ListSessions(Slug).Single();
        string report = File.ReadAllText(_hub.HubPaths.ReportFile(Slug, session.Id.ToString()));

        report.Should().Contain("no tiene proyectos de tests");
        report.Should().Contain("hecho del repositorio, conocido antes de empezar");
        report.Should().Contain("**Ámbito**", "un veredicto sin ámbito no se puede interpretar");
    }

    /// <summary>
    /// Y en la línea de tests del veredicto, «no hay» no es «no se ejecutaron»: lo primero es un
    /// hecho del proyecto y lo segundo, una duda sobre lo que pasó.
    /// </summary>
    [Fact]
    public void La_linea_de_tests_distingue_no_haber_de_no_haberse_ejecutado()
    {
        var build = new BuildVerdict(true, "…", TargetLabel: "el proyecto Common/Common.csproj");

        var sinTests = new System.Text.StringBuilder();
        ReportBuilder.AppendBuildSection(
            sinTests, build, new FixTestSituation("Common/Common.csproj", Array.Empty<string>(), AnyInClone: true));
        sinTests.ToString().Should().Contain("**Tests**: no hay en este proyecto");

        var desconocido = new System.Text.StringBuilder();
        ReportBuilder.AppendBuildSection(desconocido, build, tests: null);
        desconocido.ToString().Should().Contain("**Tests**: no se ejecutaron");
    }

    // ================================================================= utilidades

    // ============================================ F32: «Me quedo los cambios» commitea

    /// <summary>
    /// <b>Se commitean EXACTAMENTE los ficheros del arreglo, y ninguno más</b> (F32).
    /// <para>
    /// Con dos cebos que son el caso real y no un supuesto: un fichero ajeno modificado en el
    /// árbol y otro ajeno ya preparado en el índice — el usuario puede tener cosas suyas a medias
    /// (D-684) y su árbol es suyo—. Los dos tienen que quedar <b>fuera del commit y como
    /// estaban</b>: uno modificado sin commitear, el otro todavía en el índice. Un `git add`
    /// seguido de `git commit` se los habría llevado a los dos.
    /// </para>
    /// </summary>
    [Fact]
    public async Task El_commit_lleva_solo_los_ficheros_del_arreglo_y_deja_lo_del_usuario_como_estaba()
    {
        LiveFixService fix = await FixedSession();

        // Cebo 1: un fichero ajeno modificado a mano DESPUÉS del arreglo.
        string ajeno = Path.Combine(_clone, "Common", "Reader.cs");
        File.AppendAllText(ajeno, "// mío, a medias\r\n");
        string ajenoAntes = File.ReadAllText(ajeno);

        // Cebo 2: otro ajeno, nuevo y ya PREPARADO en el índice.
        string preparado = Path.Combine(_clone, "Common", "Mio.cs");
        File.WriteAllText(preparado, "// esto lo iba a commitear yo\r\n");
        using (var repo = new LibGit2Sharp.Repository(_clone))
        {
            LibGit2Sharp.Commands.Stage(repo, "Common/Mio.cs");
        }

        FixCommitResult result = fix.CommitChanges();

        result.Ok.Should().BeTrue(result.Error);
        result.Sha.Should().NotBeNullOrWhiteSpace();

        using (var repo = new LibGit2Sharp.Repository(_clone))
        {
            // Lo que ENTRÓ: solo el fichero del arreglo.
            LibGit2Sharp.Commit head = repo.Head.Tip!;
            LibGit2Sharp.TreeChanges diff = repo.Diff.Compare<LibGit2Sharp.TreeChanges>(
                head.Parents.First().Tree, head.Tree);
            diff.Select(c => c.Path).Should().BeEquivalentTo(new[] { UnitPath });

            // Lo del usuario, intacto: uno sin commitear y el otro todavía preparado.
            LibGit2Sharp.RepositoryStatus status = repo.RetrieveStatus(
                new LibGit2Sharp.StatusOptions { IncludeUntracked = true });
            status.Modified.Select(e => e.FilePath).Should().Contain("Common/Reader.cs");
            status.Added.Select(e => e.FilePath).Should().Contain("Common/Mio.cs",
                "lo que el usuario tenía en el índice sigue en el índice (D-684)");
        }

        File.ReadAllText(ajeno).Should().Be(ajenoAntes);
    }

    /// <summary>
    /// <b>El mensaje es el que el usuario tiene delante, no el que sugirió el agente.</b> La
    /// tarjeta es editable (D-556 lo era y sigue siéndolo): lo que se commitea es el título y la
    /// descripción tal y como están al pulsar, con una línea en blanco entre los dos.
    /// </summary>
    [Fact]
    public async Task El_mensaje_del_commit_es_el_editado_no_el_sugerido()
    {
        LiveFixService fix = await FixedSession();
        string sugerido = fix.Commit.Title;

        fix.Commit.Title = "Valida la longitud antes de convertir (BUG-0003)";
        fix.Commit.Description = "Lo he reescrito yo.\r\n\r\n# Y esta línea empieza por almohadilla.";

        fix.CommitChanges().Ok.Should().BeTrue();

        using var repo = new LibGit2Sharp.Repository(_clone);
        string message = repo.Head.Tip!.Message;
        message.Should().StartWith("Valida la longitud antes de convertir (BUG-0003)\n\n");
        message.Should().Contain("Lo he reescrito yo.");
        message.Should().Contain("# Y esta línea empieza por almohadilla.",
            "una línea que empieza por # es texto del usuario, no un comentario que git se coma");
        repo.Head.Tip.MessageShort.Should().NotBe(sugerido);
    }

    /// <summary>
    /// <b>Un commit que falla no toca NADA.</b> Dos causas y un solo criterio: el árbol de trabajo
    /// queda byte a byte igual (el de D-560), la pantalla sigue sin commit y el motivo llega.
    /// <para>
    /// El primer caso es el que de verdad pasa en un repositorio corporativo: un <c>pre-commit</c>
    /// que devuelve 1. Y su salida vuelve con el motivo, porque es lo único con lo que el usuario
    /// puede hacer algo.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Un_commit_que_falla_deja_el_arbol_igual_y_dice_por_que()
    {
        LiveFixService fix = await FixedSession(
            new RejectingGit("pre-commit: falta la cabecera de licencia en CommonStatics.cs"));

        byte[] antes = File.ReadAllBytes(Path.Combine(_clone, UnitPath));
        string headAntes = GitInfo.HeadSha(_clone);

        FixCommitResult result = fix.CommitChanges();

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("git rechazó el commit")
            .And.Contain("siguen en el clon")
            .And.Contain("falta la cabecera de licencia", "la cola de lo que dijo el hook");
        File.ReadAllBytes(Path.Combine(_clone, UnitPath)).Should().Equal(antes);
        GitInfo.HeadSha(_clone).Should().Be(headAntes);
        fix.CommittedSha.Should().BeNull();

        var vm = new AssistedFixViewModel(fix, _toasts, new ScriptedDiscard(answer: true));
        vm.IsCommitted.Should().BeFalse();
        vm.ClosedUncommitted.Should().BeTrue("la pantalla no cambia si el commit no se hizo");
        vm.CanDiscardAll.Should().BeTrue("y se puede seguir descartando");
    }

    /// <summary>Sin identidad de git no se inventa un autor: se falla con el motivo (F32 §1).</summary>
    [Fact]
    public async Task Sin_identidad_de_git_no_se_commitea_y_se_dice_que_falta()
    {
        LiveFixService fix = await FixedSession();
        using (var repo = new LibGit2Sharp.Repository(_clone))
        {
            repo.Config.Unset("user.name", LibGit2Sharp.ConfigurationLevel.Local);
            repo.Config.Set("user.name", string.Empty, LibGit2Sharp.ConfigurationLevel.Local);
        }

        FixCommitResult result = fix.CommitChanges();

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("identidad de git").And.Contain("no se inventa un autor");
        fix.CommittedSha.Should().BeNull();
    }

    /// <summary>
    /// <b>El estado commiteado PERSISTE en <c>fixes/{ulid}.json</c></b> (F32 §1), y la pantalla se
    /// reconstruye desde ahí: volver por «Último arreglo» es el mismo camino (D-572), así que es la
    /// misma pantalla — sin aviso ámbar, sin tarjeta de sugerencia y sin botón—.
    /// <para>
    /// Y la huella de contenido de D-685 <b>se conserva</b>: sigue siendo la prueba de atribución;
    /// el hash es un atajo.
    /// </para>
    /// </summary>
    [Fact]
    public async Task El_estado_commiteado_persiste_y_la_pantalla_se_reconstruye_desde_el_hub()
    {
        LiveFixService fix = await FixedSession();
        FixCommitResult result = fix.CommitChanges();
        result.Ok.Should().BeTrue(result.Error);

        FixRecord record = _hub.Store.ListFixes(Slug).Single();
        record.CommitSha.Should().Be(result.Sha);
        record.Files.Should().ContainSingle()
            .Which.ContentHash.Should().StartWith("sha256:", "la huella de D-685 se conserva");

        // El evento del historial, JUNTO al FixProposed que ya estaba.
        Finding stored = Finding()!;
        stored.History.Select(h => h.Event).Should()
            .Contain(FindingEvent.FixProposed).And.Contain(FindingEvent.FixCommitted);
        stored.History.Last(h => h.Event == FindingEvent.FixCommitted).Detail
            .Should().Contain(result.Sha!);
        stored.Status.Should().Be(FindingStatus.Activo, "arreglar no resuelve (D-557)");

        // El informe del hub deja de afirmar lo contrario de lo que pasó.
        AuditSession session = _hub.Store.ListSessions(Slug).Single();
        string report = File.ReadAllText(_hub.HubPaths.ReportFile(Slug, session.Id.ToString()));
        report.Should().Contain($"Commiteados en `{result.Sha}`");
        report.Should().NotContain("Estos cambios NO están commiteados");

        // Y LA PANTALLA: se reconstruye del hub, no de la memoria.
        fix.CommittedSha = null;
        var vm = new AssistedFixViewModel(fix, _toasts, new ScriptedDiscard(answer: true));
        vm.IsCommitted.Should().BeFalse("todavía no ha leído el hub");

        await vm.LoadAsync();

        vm.IsCommitted.Should().BeTrue();
        vm.ClosedUncommitted.Should().BeFalse("ni aviso ámbar, ni tarjeta, ni botón");
        vm.CommittedLine.Should().Be($"Commiteado {result.Sha} · 1 fichero · pendiente de tu push");
        vm.CanDiscardAll.Should().BeFalse();
        vm.DiscardBlockedReason.Should().Be($"ya commiteado ({result.Sha})");
        vm.CanCommitChanges.Should().BeFalse("no se commitea dos veces");
    }

    /// <summary>Título vacío: el botón se apaga con la razón al lado (P-27), no falla al pulsar.</summary>
    [Fact]
    public async Task Sin_titulo_el_boton_de_commitear_se_apaga_con_su_razon()
    {
        LiveFixService fix = await FixedSession();
        var vm = new AssistedFixViewModel(fix, _toasts, new ScriptedDiscard(answer: true));

        vm.CanCommitChanges.Should().BeTrue();
        vm.CommitBlockedReason.Should().BeEmpty();

        fix.Commit.Title = "   ";

        vm.CanCommitChanges.Should().BeFalse();
        vm.CommitBlockedReason.Should().Be("escribe el título del commit");
        vm.CommitButtonText.Should().Be(AssistedFixViewModel.CommitButtonLabel);
    }

    /// <summary>Una sesión terminada con un fichero tocado, lista para commitear.</summary>
    private async Task<LiveFixService> FixedSession(IProcessRunner? git = null)
    {
        var agent = new FakeCopilotAgent(fixScript: _ => new[]
        {
            new FixStep(Edit: new FixStepEdit(UnitPath, "es donde está el defecto",
                new[] { new FixEdit("var bytes = new byte[hex.Length / 2];",
                    "if (hex.Length % 2 != 0) throw new ArgumentException(nameof(hex));\r\n"
                    + "        var bytes = new byte[hex.Length / 2];") })),
            new FixStep(Done: new FixDoneArgs(
                "Valida la longitud.", "Arregla BUG-0003", "Antes truncaba en silencio.", null)),
        });

        LiveFixService fix = new(
            _hub, () => agent, _machines, _ulids, _settings, new ReferenceCollector(), _snapshots,
            Launcher(), _busy, new BuildRunner(new NoProcess()),
            committer: new FixCommitter(git));

        await fix.StartAsync(new FixSessionRequest(Slug, _findingId));
        fix.HasFinished.Should().BeTrue(fix.FailureMessage);
        fix.Files.Should().ContainSingle();
        return fix;
    }

    /// <summary>Un git que rechaza el commit, como haría un <c>pre-commit</c> con política.</summary>
    private sealed class RejectingGit : IProcessRunner
    {
        private readonly string _output;

        public RejectingGit(string output) => _output = output;

        public ProcessOutcome Run(
            string fileName, string arguments, string workingDirectory, TimeSpan timeout, CancellationToken ct)
            => new(1, _output, false);
    }

    private Finding? Finding() => _hub.Store.TryReadFinding(Slug, _findingId.ToString());

    private AssistedFixLauncher Launcher() => Launcher(_settings);

    private AssistedFixLauncher Launcher(SettingsService settings)
        => new(settings, new CloneLinkService(_hub, _machines), _machines, _busy);

    private LiveFixService Service(IAssistedFixProvider agent)
        => new(
            _hub, () => agent, _machines, _ulids, _settings, new ReferenceCollector(), _snapshots,
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
