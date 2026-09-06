using System.Text.RegularExpressions;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Domain;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.9 §1 — «Importar v4» sale del menú y entra en el asistente de alta.
/// <para>
/// El motivo es de sitio, no de capacidad: importar el baseline de una app es una acción de
/// una-vez-por-app que se hace justo al darla de alta, no un destino permanente de la navegación.
/// Y va a seguir haciendo falta con cada aplicación de la empresa que quede por dar de alta, así
/// que tenía que quedar en el camino por el que se pasa, no en uno que hay que recordar.
/// </para>
/// </summary>
public sealed class OnboardingImportTests : IDisposable
{
    private const string RepoUrl = "https://example.invalid/org/heredada.git";

    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly ToastCenter _toasts = new();
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public OnboardingImportTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-f59-import", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "heredada");
        Directory.CreateDirectory(_clone);
        File.WriteAllText(Path.Combine(_clone, "Program.cs"), "class Program { static void Main() { } }\n");
        File.WriteAllText(Path.Combine(_clone, "heredada.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");

        _paths = new AppPaths(Path.Combine(_root, "local"));
        var settings = new SettingsService(_paths);
        settings.Load();
        // El alta exige un hub configurado. No se abre ninguna conexión: sin EnsureSync,
        // HubContext.Sync es null y el CommitAndPush del final no hace nada (N-1, sin red).
        settings.Current.HubUrlOverride = Path.Combine(_root, "remote");
        _hub = TestFactory.Hub(_paths, settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _machines = new MachineConfigStore(_paths.MachinesJson);
    }

    private OnboardingViewModel Wizard(IFolderPicker? picker = null)
        => TestFactory.Onboarding(
            _hub, _paths, _machines, _toasts, _ulids, new NavigationService(new EmptyServices()), picker);

    /// <summary>Una CodeAudit/ del sistema v4 dentro del clon, con lo mínimo importable.</summary>
    private string WriteCodeAudit(string? at = null)
    {
        string dir = at ?? Path.Combine(_clone, V4Baseline.FolderName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "BASELINE.md"), """
            # Baseline

            ## BUG-0007 [alta] El contador se desborda al pasar de 999

            - Pilar: errores
            - Severidad: alta
            - Confianza: alta
            - Ubicacion: Program.cs:12
            """);
        File.WriteAllText(Path.Combine(dir, "SILENCIADOS.md"), "# Silenciados\n");
        return dir;
    }

    // ============================================ Fuera del menú

    /// <summary>
    /// La mitad literal del §1: el item de navegación desaparece, y con él la página. Lo que se
    /// queda es el SERVICIO, que es lo que el asistente usa.
    /// </summary>
    [Fact]
    public void El_item_de_navegacion_y_su_pagina_ya_no_existen()
    {
        string shell = Source("src/Atalaya.App/MainWindow.xaml");
        shell.Should().NotContain("Importar v4");
        shell.Should().NotContain("ShowImportCommand");

        typeof(MainViewModel).GetProperty("ShowImportCommand")
            .Should().BeNull("un comando de navegación a una página que ya no existe");

        Repo("src/Atalaya.App/Views/ImportView.xaml").NotExist();
        Repo("src/Atalaya.App/ViewModels/ImportViewModel.cs").NotExist();
        Source("src/Atalaya.App/App.xaml").Should().NotContain("ImportViewModel");

        // Y el importador sigue entero —el servicio, y `Atalaya.ImportV4` en la solución—: lo que
        // ya no hay es desde dónde llamarlo. R4 §3 retiró de «Nueva aplicación» el bloque del
        // baseline v4, que era su ÚLTIMA entrada en la interfaz desde que F5.9 §1 lo sacó del menú.
        typeof(ImportService).Should().NotBeNull();
        Source("src/Atalaya.App/Views/OnboardingView.xaml").Should().NotContain("PickBaselineCommand");
    }

    // ============================================ Con CodeAudit/ presente

    [Fact]
    public void Elegir_un_clon_con_CodeAudit_lo_detecta_y_lo_propone_marcado()
    {
        WriteCodeAudit();
        OnboardingViewModel vm = Wizard();

        vm.ClonePath = _clone;

        vm.HasBaseline.Should().BeTrue();
        vm.CodeAuditPath.Should().Be(Path.Combine(_clone, V4Baseline.FolderName));
        vm.ImportBaseline.Should().BeTrue("quien tiene un baseline v4 casi siempre lo quiere");
        vm.BaselineNotice.Should().Contain("baseline del sistema v4");
    }

    [Fact]
    public async Task El_alta_con_baseline_trae_los_hallazgos_y_conserva_el_inventario_escaneado()
    {
        WriteCodeAudit();
        OnboardingViewModel vm = Wizard();
        vm.ClonePath = _clone;
        vm.Name = "Heredada";
        vm.RepoUrl = RepoUrl;

        await vm.CreateCommand.ExecuteAsync(null);

        AppConfig? app = _hub.Store.TryReadApp("heredada");
        app.Should().NotBeNull();

        _hub.Store.ListFindings("heredada")
            .Should().Contain(f => f.DisplayId == "BUG-0007", "el baseline v4 entró con el alta");

        // El inventario es el del CÓDIGO que hay en el clon: importar no puede dejar la app con
        // el inventario del sistema anterior, que no conoce los ficheros de hoy.
        InventoryCycle? inv = _hub.Store.TryReadInventory("heredada", app!.CurrentCycle);
        inv.Should().NotBeNull();
        inv!.Units.Should().Contain(u => u.Path.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase));

        _machines.Load().ClonePathFor("heredada").Should().Be(_clone);
        vm.ImportLog.Should().NotBeEmpty("el importador cuenta lo que hizo y lo que no pudo");
    }

    /// <summary>
    /// La casilla manda. Detectar la carpeta PROPONE; importar es una decisión, igual que
    /// re-escanear al detectar deriva (D-301) no es un efecto secundario de vincular.
    /// </summary>
    [Fact]
    public async Task Desmarcar_la_casilla_da_de_alta_la_app_sin_importar_nada()
    {
        WriteCodeAudit();
        OnboardingViewModel vm = Wizard();
        vm.ClonePath = _clone;
        vm.Name = "Heredada";
        vm.RepoUrl = RepoUrl;
        vm.ImportBaseline = false;

        await vm.CreateCommand.ExecuteAsync(null);

        _hub.Store.TryReadApp("heredada").Should().NotBeNull();
        _hub.Store.ListFindings("heredada")
            .Should().NotContain(f => f.DisplayId == "BUG-0007");
        vm.ImportLog.Should().BeEmpty();
    }

    // ============================================ Sin CodeAudit/

    [Fact]
    public async Task Sin_CodeAudit_el_asistente_da_de_alta_exactamente_igual_que_antes()
    {
        OnboardingViewModel vm = Wizard();
        vm.ClonePath = _clone;
        vm.Name = "Heredada";
        vm.RepoUrl = RepoUrl;

        vm.HasBaseline.Should().BeFalse();
        vm.ImportBaseline.Should().BeFalse();
        vm.BaselineNotice.Should().BeEmpty("no hay nada que decir donde no hay nada");

        await vm.CreateCommand.ExecuteAsync(null);

        AppConfig? app = _hub.Store.TryReadApp("heredada");
        app.Should().NotBeNull();
        app!.CurrentCycle.Should().Be(1);
        app.Stack.Should().Be(TechStack.DotNet);
        _hub.Store.TryReadInventory("heredada", 1)!.Units.Should().NotBeEmpty();
    }

    /// <summary>«O el usuario la señala»: el baseline puede vivir fuera del repo auditado.</summary>
    [Fact]
    public void Se_puede_senalar_una_carpeta_de_fuera_del_clon()
    {
        string fuera = WriteCodeAudit(Path.Combine(_root, "baseline-viejo"));
        OnboardingViewModel vm = Wizard(new TestFactory.FixedFolderPicker(fuera));
        vm.ClonePath = _clone;

        vm.HasBaseline.Should().BeFalse("dentro del clon no había ninguna");
        vm.PickBaselineCommand.Execute(null);

        vm.CodeAuditPath.Should().Be(fuera);
        vm.HasBaseline.Should().BeTrue();
        vm.BaselineNotice.Should().Contain("válida");
    }

    [Fact]
    public void Una_carpeta_que_no_es_v4_se_rechaza_diciendo_que_le_falta()
    {
        OnboardingViewModel vm = Wizard(new TestFactory.FixedFolderPicker(_clone));
        vm.PickBaselineCommand.Execute(null);

        vm.HasBaseline.Should().BeFalse();
        vm.ImportBaseline.Should().BeFalse("no se puede marcar lo que no se puede importar");
        vm.BaselineNotice.Should().Contain("BASELINE.md");
    }

    // ============================================ La detección, aislada

    [Fact]
    public void La_deteccion_acepta_la_raiz_del_repo_y_tambien_la_propia_CodeAudit()
    {
        string codeAudit = WriteCodeAudit();

        V4Baseline.Find(_clone).Should().Be(codeAudit);
        V4Baseline.Find(codeAudit).Should().Be(codeAudit, "señalar la carpeta directamente es el error fácil");
        V4Baseline.Find(Path.Combine(_root, "no-existe")).Should().BeNull();
        V4Baseline.Find(null).Should().BeNull();
        V4Baseline.Looks(_root).Should().BeFalse();
    }

    // ============================================ Utilidades

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // El árbol temporal puede quedar tomado; no es parte de lo probado.
        }
    }

    private static string Repo(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("los tests corren dentro del repositorio");
        return Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string Source(string relative)
    {
        string path = Repo(relative);
        File.Exists(path).Should().BeTrue($"se esperaba {path}");
        return Regex.Replace(File.ReadAllText(path), "<!--.*?-->", string.Empty, RegexOptions.Singleline);
    }
}

/// <summary>Un fichero que no debe existir, dicho con el nombre del fichero en el fallo.</summary>
internal static class PathAssertions
{
    public static void NotExist(this string path)
        => File.Exists(path).Should().BeFalse($"{Path.GetFileName(path)} salió del proyecto en F5.9 §1");
}
