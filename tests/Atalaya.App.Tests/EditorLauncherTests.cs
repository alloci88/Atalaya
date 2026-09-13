using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.App.Views;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// R13 §0(a) y §2 — el lanzador lee el ajuste en CADA apertura, no ofrece lo que no está, y no se
/// cae a otro editor en silencio.
/// <para>
/// La detección va detrás de <see cref="IEditorProbe"/> por lo mismo que el tope de D-208 va detrás
/// de una función con la llamada al sistema inyectada: si hubiera que instalar ocho editores para
/// probarlo, no se probaría.
/// </para>
/// </summary>
public sealed class EditorLauncherTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly MachineConfigStore _machines;
    private readonly string _clone;
    private readonly FakeProbe _probe = new();

    public EditorLauncherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-editor", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _machines = new MachineConfigStore(_paths.MachinesJson);
        _settings = new SettingsService(_paths);
        _settings.Load();

        _clone = Path.Combine(_root, "clone");
        Directory.CreateDirectory(Path.Combine(_clone, "src"));
        File.WriteAllText(Path.Combine(_clone, "src", "Motor.cs"), "// uno\n// dos\n");
        _machines.SetClonePath("app", _clone);
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
    }

    /// <summary>
    /// <b>La regla del parte (a).</b> Con el ajuste en X se construye el comando de X. Se prueba
    /// para cada editor del registro que se pueda instalar, con TODOS instalados a la vez: si el
    /// lanzador ignorase el ajuste, elegiría siempre el mismo y el test lo vería.
    /// </summary>
    [Fact]
    public void El_lanzador_construye_el_comando_del_editor_que_dice_el_ajuste()
    {
        foreach (EditorDefinition editor in EditorRegistry.All.Where(e => e.Kind == EditorKind.Program))
        {
            _probe.Install(editor.Executables[0], $@"C:\progs\{editor.Id}\{editor.Executables[0]}");
        }

        foreach (EditorDefinition editor in EditorRegistry.All.Where(e => e.Kind == EditorKind.Program))
        {
            Configure(editor.Id);
            EditorCommand command = Command(editor, line: 142);

            command.FileName.Should().Be($@"C:\progs\{editor.Id}\{editor.Executables[0]}",
                $"con el ajuste en «{editor.Name}» se lanza {editor.Executables[0]}, no otro");
        }
    }

    /// <summary>
    /// El ajuste se relee EN CADA APERTURA (BUGFIX-AJUSTES §3, R5): cambiarlo entre dos aperturas
    /// cambia el editor sin reiniciar nada.
    /// </summary>
    [Fact]
    public void Cambiar_el_ajuste_entre_dos_aperturas_cambia_el_editor()
    {
        _probe.Install("devenv.exe", @"C:\vs\devenv.exe");
        _probe.Install("code.cmd", @"C:\code\code.cmd");

        Configure(EditorRegistry.VisualStudioId);
        Command(EditorRegistry.Find(EditorRegistry.VisualStudioId)!, 10).FileName.Should().Be(@"C:\vs\devenv.exe");

        Configure("vscode");
        Command(EditorRegistry.Find("vscode")!, 10).FileName.Should().Be(@"C:\code\code.cmd");
    }

    /// <summary>
    /// <b>No se cae a otro editor en silencio</b> (§3). Con el editor elegido desinstalado, falla y
    /// dice por qué — que es exactamente lo que no hacía el <c>catch</c> que abría con el manejador
    /// del sistema y devolvía éxito.
    /// </summary>
    [Fact]
    public void El_editor_que_no_esta_falla_con_motivo_y_no_abre_otro()
    {
        Configure(EditorRegistry.VisualStudioId);

        EditorOpenResult result = Launcher().Open("app", "src/Motor.cs", 142);

        result.Opened.Should().BeFalse();
        result.Failure.Should().Contain("no está en esta máquina").And.Contain("Ajustes");
        EditorLauncher.Toast(result).Should().Contain("Visual Studio").And.NotContain("Manejador del sistema");
    }

    /// <summary>El desplegable ofrece lo detectado, más los dos que están siempre.</summary>
    [Fact]
    public void Solo_se_ofrecen_los_editores_detectados()
    {
        _probe.Install("notepad++.exe", @"C:\Program Files\Notepad++\notepad++.exe");

        IReadOnlyList<string> offered = new EditorDetector(_probe).Offer(configured: "notepadpp")
            .Select(d => d.Editor.Id).ToList();

        offered.Should().Contain("notepadpp");
        offered.Should().Contain(EditorRegistry.SystemId);
        offered.Should().NotContain(EditorRegistry.VisualStudioId, "Visual Studio no está en esta máquina");
    }

    /// <summary>
    /// Pero el editor que el ajuste YA nombra no desaparece de la lista aunque no se encuentre:
    /// quitarlo cambiaría el ajuste del usuario por la espalda, que es el mismo defecto con otra
    /// cara. Se queda marcado.
    /// </summary>
    [Fact]
    public void El_editor_configurado_que_ya_no_esta_sigue_en_la_lista_marcado()
    {
        DetectedEditor missing = new EditorDetector(_probe).Offer(EditorRegistry.VisualStudioId)
            .First(d => d.Editor.Id == EditorRegistry.VisualStudioId);

        missing.Detected.Should().BeFalse();
        missing.Label.Should().Contain("no encontrado");
    }

    /// <summary>El PATH y el registro de Windows valen los dos, y el registro va primero.</summary>
    [Fact]
    public void El_ejecutable_se_busca_en_el_registro_y_en_el_PATH()
    {
        _probe.OnPath(@"C:\bin", "notepad++.exe");
        new EditorDetector(_probe).Locate(EditorRegistry.Find("notepadpp")!)
            .Should().Be(@"C:\bin\notepad++.exe");

        var conRegistro = new FakeProbe();
        conRegistro.OnPath(@"C:\bin", "notepad++.exe");
        conRegistro.Install("notepad++.exe", @"C:\Program Files\Notepad++\notepad++.exe");
        new EditorDetector(conRegistro).Locate(EditorRegistry.Find("notepadpp")!)
            .Should().Be(@"C:\Program Files\Notepad++\notepad++.exe");
    }

    // ------------------------------------------------------------------ los toasts (§3)

    /// <summary>El caso normal: qué editor y qué línea.</summary>
    [Fact]
    public void El_toast_dice_editor_y_linea()
        => EditorLauncher.Toast(Result(line: 142, delivered: true))
            .Should().Be("Abierto en VS Code · línea 142");

    /// <summary>El editor que no admite línea lo dice, con su motivo, en vez de callarlo.</summary>
    [Fact]
    public void El_toast_dice_cuando_el_editor_no_admite_linea()
        => EditorLauncher.Toast(Result(line: 142, delivered: false, name: "Visual Studio",
                noLineReason: "no lo permite desde la línea de comandos"))
            .Should().Be("Abierto en Visual Studio · sin ir a la línea 142 "
                + "(no lo permite desde la línea de comandos)");

    /// <summary>La línea re-anclada dice de dónde venía (D-021).</summary>
    [Fact]
    public void El_toast_de_la_linea_reanclada_dice_de_donde_venia()
        => EditorLauncher.Toast(Result(line: 149, delivered: true,
                origin: LineOrigin.Reanclada, originalLine: 142))
            .Should().Be("Abierto en VS Code · línea 149 (antes 142)");

    /// <summary>Y la que no se pudo anclar se abre en la original diciéndolo.</summary>
    [Fact]
    public void El_toast_de_la_linea_sin_anclar_lo_dice()
        => EditorLauncher.Toast(Result(line: 142, delivered: true, origin: LineOrigin.SinAnclar))
            .Should().Be("Abierto en VS Code · línea 142 (original; la unidad ha cambiado)");

    // ------------------------------------------------------------------ Ajustes (§2)

    /// <summary>
    /// El desplegable de Ajustes enseña <b>lo que hay</b>: los detectados y el manejador del
    /// sistema. Ofrecer Visual Studio en una máquina sin Visual Studio es ofrecer el fallo de los
    /// 10 s de D-208.
    /// </summary>
    [Fact]
    public void Ajustes_solo_ofrece_los_editores_de_esta_maquina()
    {
        _probe.Install("notepad++.exe", @"C:\Program Files\Notepad++\notepad++.exe");
        SettingsViewModel vm = Settings();

        vm.EditorOptions.Select(o => o.Id).Should().Contain(["notepadpp", EditorRegistry.SystemId]);
        vm.EditorOptions.Select(o => o.Id).Should().NotContain("vscode");
    }

    /// <summary>
    /// <b>R13-2 — el ajuste no puede apuntar a una opción que ya no existe.</b> «Otro (comando
    /// personalizado)» se retiró; una máquina que lo tuviera elegido pasa al manejador del sistema
    /// al arrancar, y <b>se le dice</b>. Un ajuste que cambia solo y sin avisar se vive igual que
    /// un ajuste que no ajusta (D-765).
    /// </summary>
    [Fact]
    public void El_ajuste_que_apuntaba_a_Otro_se_muda_al_manejador_del_sistema_y_se_dice()
    {
        Configure(SettingsService.RetiredCustomEditorId);

        string? aviso = _settings.MigrateRetiredEditor();

        aviso.Should().NotBeNullOrWhiteSpace("una mudanza silenciosa no se distingue de un fallo");
        _settings.Current.Editor.Should().Be(EditorRegistry.SystemId);

        var releido = new SettingsService(_paths);
        releido.Load();
        releido.Current.Editor.Should().Be(EditorRegistry.SystemId, "y queda escrito en el fichero");

        // Y no vuelve a hablar: en cuanto está mudado no hay nada que mudar.
        _settings.MigrateRetiredEditor().Should().BeNull();
    }

    /// <summary>Al que tiene un editor de verdad elegido no se le toca ni se le dice nada.</summary>
    [Fact]
    public void La_mudanza_no_toca_al_que_ya_tiene_un_editor_del_registro()
    {
        Configure("notepadpp");

        _settings.MigrateRetiredEditor().Should().BeNull();
        _settings.Current.Editor.Should().Be("notepadpp");
    }

    /// <summary>
    /// «Probar» abre de verdad y cuenta lo que ha pasado. Es la única forma de que el usuario sepa
    /// que su editor funciona antes de necesitarlo.
    /// </summary>
    [Fact]
    public async Task Probar_cuenta_lo_que_ha_pasado()
    {
        var toasts = new ToastCenter();
        SettingsViewModel vm = Settings(toasts);
        vm.Editor = EditorRegistry.VisualStudioId;

        await vm.TestEditorCommand.ExecuteAsync(null);

        // Visual Studio no está en esta máquina de mentira, y por eso el toast dice el motivo en
        // vez de callarse — ni de abrir con otro.
        toasts.Items.Last().Text.Should().Contain("No se pudo abrir").And.Contain("Visual Studio");
    }

    // ------------------------------------------------------------------ el arnés

    private SettingsViewModel Settings(ToastCenter? toasts = null)
    {
        HubContext hub = TestFactory.Hub(_paths, _settings);
        return new SettingsViewModel(
            _settings,
            new Atalaya.Copilot.FakeCopilotAgent(),
            toasts ?? new ToastCenter(),
            new FactoryResetService(
                hub, _paths, _settings, TestFactory.Account(_paths), new OpenSessionStore(_paths),
                new ProviderSecretStore(_paths)),
            new NoReset(),
            hub,
            new NavigationService(new NoServices()),
            editors: new EditorDetector(_probe),
            launcher: Launcher(),
            paths: _paths);
    }

    private void Configure(string editorId)
    {
        Atalaya.App.Services.AppSettings s = _settings.Current;
        s.Editor = editorId;
        _settings.Save(s);
    }

    private EditorLauncher Launcher() => new(_settings, _machines, new EditorDetector(_probe));

    private EditorCommand Command(EditorDefinition editor, int line)
        => EditorRegistry.Build(
            editor, new EditorDetector(_probe).Locate(editor),
            Path.Combine(_clone, "src", "Motor.cs"), line);

    private static EditorOpenResult Result(
        int line, bool delivered, string name = "VS Code", string? noLineReason = null,
        LineOrigin origin = LineOrigin.Anclada, int originalLine = 0)
        => new(true, name, "cmd", line, delivered, noLineReason, null, origin, originalLine);

    /// <summary>El reset de fábrica no se dispara desde aquí.</summary>
    private sealed class NoReset : IFactoryResetConfirmer
    {
        public bool Confirm(FactoryResetConfirmation confirmation) => false;
    }

    /// <summary>La navegación no se ejercita en estos casos; basta con que exista.</summary>
    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>Una máquina de mentira: los editores que YO diga, y ni uno más.</summary>
    private sealed class FakeProbe : IEditorProbe
    {
        private readonly Dictionary<string, string> _registered = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _path = [];

        /// <summary>Instalado y registrado en <c>App Paths</c>, como hace casi todo instalador.</summary>
        public void Install(string executable, string path)
        {
            _registered[executable] = path;
            _files.Add(path);
        }

        /// <summary>Instalado y alcanzable por el PATH, sin entrada en el registro.</summary>
        public void OnPath(string directory, string executable)
        {
            _path.Add(directory);
            _files.Add(Path.Combine(directory, executable));
        }

        public bool FileExists(string path) => _files.Contains(path);

        public IEnumerable<string> Directories(string parent) => [];

        public IEnumerable<string> PathDirectories() => _path;

        public string? AppPath(string executable) => _registered.GetValueOrDefault(executable);

        // Sin expansión: en el arnés no hay %ProgramFiles% que valga, y las carpetas conocidas no
        // existen — que es justo lo que se quiere para probar «este editor no está».
        public string Expand(string path) => path;
    }
}
