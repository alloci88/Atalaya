using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.App.Views;
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
/// F5.8 — El piloto en la tarjeta del portafolio y la redirección de «Nueva aplicación».
/// <para>
/// Son los dos sitios donde el estado de vinculación deja de ser un dato y se convierte en algo
/// que el usuario ve y usa: la tarjeta que dice si puede auditar, y el asistente que, en vez de
/// dejarle crear un duplicado, le manda al gesto correcto.
/// </para>
/// </summary>
public sealed class LinkCloneFlowTests : IDisposable
{
    private const string RepoUrl = "https://example.invalid/org/xblast.git";

    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly CloneLinkService _links;
    private readonly ToastCenter _toasts = new();
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public LinkCloneFlowTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-f58-flow", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();

        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "xblast", Name = "XBlast", RepoUrl = RepoUrl, Stack = TechStack.DotNet, CurrentCycle = 1,
        });

        _machines = new MachineConfigStore(_paths.MachinesJson);
        _links = new CloneLinkService(_hub, _machines);
    }

    // ================================================================= §1 · la tarjeta

    [Fact]
    public async Task La_tarjeta_del_portafolio_lleva_el_piloto_de_esta_maquina()
    {
        PortfolioViewModel vm = Portfolio();
        await vm.LoadAsync();

        vm.Apps.Single().Link.State.Should().Be(CloneLinkState.SinVincular);

        TestFactory.MakeClone(Path.Combine(_root, "xblast"), RepoUrl);
        _machines.SetClonePath("xblast", Path.Combine(_root, "xblast"));

        // Recargar es lo que corre al arrancar, al sincronizar y al volver al primer plano.
        await vm.LoadAsync();

        vm.Apps.Single().Link.State.Should().Be(CloneLinkState.Vinculada);
    }

    /// <summary>
    /// Vincular desde la tarjeta la repinta EN EL ACTO. Esperar al siguiente sondeo dejaría el
    /// botón pidiendo lo que el usuario acaba de hacer.
    /// </summary>
    [Fact]
    public async Task Vincular_desde_la_tarjeta_apaga_el_piloto_sin_esperar_al_sondeo()
    {
        string clone = Path.Combine(_root, "xblast");
        TestFactory.MakeClone(clone, RepoUrl);

        var dialog = new LinkingDialog(() => _machines.SetClonePath("xblast", clone));
        PortfolioViewModel vm = Portfolio(dialog);
        await vm.LoadAsync();
        vm.Apps.Single().Link.NeedsAction.Should().BeTrue();

        vm.LinkCloneCommand.Execute(vm.Apps.Single());

        dialog.Shown.Should().ContainSingle().Which.Slug.Should().Be("xblast");
        vm.Apps.Single().Link.State.Should().Be(CloneLinkState.Vinculada);
    }

    /// <summary>Un diálogo de mentira que vincula sin ventana: hace lo que haría el de verdad.</summary>
    private sealed class LinkingDialog : ILinkCloneDialog
    {
        private readonly Action _link;

        public LinkingDialog(Action link) => _link = link;

        public List<LinkCloneViewModel> Shown { get; } = new();

        public LinkCloneViewModel Show(LinkCloneViewModel viewModel)
        {
            Shown.Add(viewModel);
            _link();
            return viewModel;
        }
    }

    /// <summary>
    /// Y la tarjeta lo enseña. Que el dato exista no vale si el XAML no lo pinta: el piloto, su
    /// tooltip en los tres casos, y el botón que solo aparece cuando hay algo que hacer.
    /// </summary>
    [Fact]
    public void La_tarjeta_pinta_el_piloto_su_tooltip_y_el_boton_de_vincular()
    {
        string xaml = ViewXaml("PortfolioView.xaml");

        xaml.Should().Contain("CloneLinkStateToBrush", "el piloto se ve, con los tres colores");
        xaml.Should().Contain("{Binding Link.Label}", "y se lee, para no depender solo del color");
        xaml.Should().Contain("{Binding Link.Tooltip}", "y se explica");
        xaml.Should().Contain("{Binding Link.ActionLabel}", "«Vincular…» y «Reparar…» no son lo mismo");
        xaml.Should().Contain("{Binding Link.NeedsAction,", "el botón no estorba cuando está en verde");
        xaml.Should().Contain("DataContext.LinkCloneCommand");
    }

    /// <summary>
    /// «Al volver la ventana al primer plano» (F5.8 §1). Lo que importa de esta regla es lo que
    /// NO hace: recargar cualquier página al enfocar tiraría el comentario a medio escribir de
    /// una ficha, o los ajustes sin guardar.
    /// </summary>
    [Fact]
    public void Solo_el_portafolio_y_el_inventario_se_recargan_al_enfocar()
    {
        ShellRefresh.ShouldReloadOnActivate(Portfolio(), busy: false).Should().BeTrue();
        ShellRefresh.ShouldReloadOnActivate(Portfolio(), busy: true)
            .Should().BeFalse("con una operación en curso no se recarga por debajo");
        ShellRefresh.ShouldReloadOnActivate(null, busy: false).Should().BeFalse();
        ShellRefresh.ShouldReloadOnActivate(new MetricsViewModel(new MetricsQuery(_hub)), busy: false)
            .Should().BeFalse("Métricas no depende del clon local");
    }

    // ================================================================= §2 · la redirección

    private OnboardingViewModel Onboarding()
        => new(
            _hub,
            new InventoryScanner(),
            _machines,
            new NavigationService(new EmptyServices()),
            new FindingIngestionService(_hub, _ulids),
            _toasts,
            _links,
            TestFactory.LinkFlow(_hub, _paths, _toasts));

    [Fact]
    public void Escribir_la_URL_de_una_app_que_ya_existe_redirige_a_vincular()
    {
        OnboardingViewModel vm = Onboarding();
        vm.IsDuplicate.Should().BeFalse("una URL vacía no es un duplicado");

        vm.RepoUrl = RepoUrl;

        vm.IsDuplicate.Should().BeTrue();
        vm.CanCreate.Should().BeFalse();
        vm.DuplicateNotice.Should().Contain("XBlast").And.Contain("vincula tu clon");
        vm.LinkExistingLabel.Should().Contain("XBlast");
    }

    /// <summary>La misma app clonada por SSH sigue siendo la misma app.</summary>
    [Fact]
    public void La_deteccion_no_se_escapa_por_la_forma_de_la_URL()
    {
        OnboardingViewModel vm = Onboarding();
        vm.RepoUrl = "git@example.invalid:org/xblast";

        vm.IsDuplicate.Should().BeTrue();
    }

    [Fact]
    public void Un_repo_nuevo_no_dispara_la_redireccion()
    {
        OnboardingViewModel vm = Onboarding();
        vm.RepoUrl = "https://example.invalid/org/otra-cosa.git";

        vm.IsDuplicate.Should().BeFalse();
        vm.CanCreate.Should().BeTrue();
    }

    /// <summary>
    /// Y la puerta se vuelve a mirar al crear: el asistente NO puede acabar con dos apps que son
    /// el mismo repo, ni aunque se le fuerce el comando.
    /// </summary>
    [Fact]
    public async Task Crear_con_un_repo_que_ya_existe_no_da_de_alta_nada()
    {
        string clone = Path.Combine(_root, "xblast");
        TestFactory.MakeClone(clone, RepoUrl);

        OnboardingViewModel vm = Onboarding();
        vm.Name = "XBlast otra vez";
        vm.RepoUrl = RepoUrl;
        vm.ClonePath = clone;

        await vm.CreateCommand.ExecuteAsync(null);

        _hub.Store.ListAppSlugs().Should().ContainSingle().Which.Should().Be("xblast");
        _toasts.Items.Should().Contain(t => t.Text.Contains("ya existe en el portafolio"));
    }

    /// <summary>
    /// «Detectar stack» sobre una carpeta ya clonada rellena la URL desde su <c>origin</c>: es
    /// como el asistente descubre solo que el repo elegido ya está en el portafolio.
    /// </summary>
    [Fact]
    public void Detectar_sobre_un_clon_existente_saca_la_URL_y_descubre_el_duplicado()
    {
        string clone = Path.Combine(_root, "xblast");
        TestFactory.MakeClone(clone, RepoUrl);

        OnboardingViewModel vm = Onboarding();
        vm.ClonePath = clone;
        vm.DetectCommand.Execute(null);

        vm.RepoUrl.Should().Be(RepoUrl);
        vm.IsDuplicate.Should().BeTrue();
    }

    [Fact]
    public void El_asistente_ofrece_la_salida_en_la_vista()
    {
        string xaml = ViewXaml("OnboardingView.xaml");

        xaml.Should().Contain("{Binding IsDuplicate,");
        xaml.Should().Contain("{Binding DuplicateNotice}");
        xaml.Should().Contain("{Binding LinkExistingCommand}");
        xaml.Should().Contain("IsEnabled=\"{Binding CanCreate}\"", "y no deja crear el duplicado");
    }

    // ================================================================= helpers

    private PortfolioViewModel Portfolio(ILinkCloneDialog? dialog = null)
        => new(
            new PortfolioQuery(_hub.Store),
            new NavigationService(new EmptyServices()),
            new AppDeletionService(_hub, _machines, new OpenSessionStore(_paths)),
            new NeverConfirms(),
            Live(),
            _hub,
            _toasts,
            _links,
            TestFactory.LinkFlow(_hub, _paths, _toasts, dialog: dialog));

    private LiveSessionService Live()
    {
        var agent = new Atalaya.Copilot.FakeCopilotAgent();
        return new LiveSessionService(
            () => new SessionCoordinator(
                _hub, new FindingIngestionService(_hub, _ulids), new ReconciliationService(_hub),
                _machines, _ulids, agent, _settings),
            agent,
            new OpenSessionStore(_paths));
    }

    private sealed class NeverConfirms : IDeleteAppConfirmer
    {
        public bool Confirm(DeleteAppConfirmation confirmation) => false;
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static string ViewXaml(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Atalaya.App", "Views", fileName));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // Handles de git en Windows; el temporal se lo lleva el sistema.
        }
    }
}
