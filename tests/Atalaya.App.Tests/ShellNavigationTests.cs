using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F26 Parte A — <b>la navegación conserva el estado y el inventario está a un paso</b>
/// (principio 6, D-952/D-953/D-954).
/// <para>
/// Las dos quejas que se prueban aquí son literales de quien usa Atalaya:
/// «salir del inventario y volver son dos pasos (Portafolio → Inventario)» y «volver a Hallazgos
/// pierde el filtro y enseña todo, de todas las aplicaciones».
/// </para>
/// <para>
/// <b>Por qué son tests de regla.</b> Ninguno mira un control. El primero mira que exista un
/// CAMINO de un solo salto al inventario de la aplicación en la que estás; el segundo, que volver
/// devuelva la página que dejaste y no una recién hecha. Los dos se rompen en silencio: el raíl
/// sigue pintándose igual, y la vista sigue abriendo — con otros datos.
/// </para>
/// </summary>
public sealed class ShellNavigationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atalaya-shell", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;

    public ShellNavigationTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = TestFactory.Settings(_paths);
        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });
        _hub.Store.WriteApp(new AppConfig { Slug = "xblast", Name = "XBLAST", RepoUrl = "u", CurrentCycle = 1 });
        _hub.Store.WriteApp(new AppConfig { Slug = "otra", Name = "Otra", RepoUrl = "u", CurrentCycle = 1 });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Un temporal que Windows aún tiene abierto no es un fallo del test.
        }
    }

    private FindingsViewModel Findings(NavigationService navigation)
        => new(_hub, navigation, _settings, new GroupExpansionMemory());

    // ================================================================ el inventario, a un paso

    /// <summary>
    /// Estando dentro de una aplicación, el raíl ofrece SU inventario. Antes no lo ofrecía —el
    /// raíl no sabía en qué aplicación estabas—, y el único camino era volver al portafolio y
    /// entrar otra vez por la tarjeta: dos pasos para deshacer uno.
    /// </summary>
    [Fact]
    public async Task Dentro_de_una_aplicacion_el_rail_lleva_a_su_inventario_en_un_paso()
    {
        var provider = new PageProvider();
        var navigation = new NavigationService(provider);
        var findings = Findings(navigation);
        provider.Add(findings);
        MainViewModel shell = TestFactory.Shell(_paths, _hub, navigation: navigation);

        // Se entra en los hallazgos de XBLAST: a partir de aquí, «la aplicación» es XBLAST.
        findings.SetApp("xblast");
        await navigation.NavigateToAsync<FindingsViewModel>(_ => { });

        shell.ActiveApplication.Slug.Should().Be("xblast");

        NavItem[] entradas = shell.NavGroups.SelectMany(g => g.Items).ToArray();
        entradas.Should().Contain(
            i => i.Key == "inventory",
            "estando dentro de XBLAST, el raíl tiene que ofrecer su inventario sin pasar por el portafolio");

        shell.NavGroups.Should().Contain(
            g => g.Title == "XBLAST",
            "el grupo se rotula con el nombre de la aplicación, que es lo que dice de quién es ese inventario");
    }

    /// <summary>
    /// Y el salto es UNO: el comando del raíl deja el inventario delante, sin escala.
    /// </summary>
    [Fact]
    public async Task El_comando_del_rail_deja_el_inventario_delante_sin_escalas()
    {
        var provider = new PageProvider();
        var pages = new NavigationService(provider);
        var findings = Findings(pages);
        var inventory = TestFactory.Inventory(
            _hub, _paths, new MachineConfigStore(_paths.MachinesJson),
            new Atalaya.Domain.Ids.UlidFactory(Atalaya.Domain.Abstractions.SystemClock.Instance),
            _settings, new ToastCenter());
        provider.Add(findings, inventory);
        MainViewModel shell = TestFactory.Shell(_paths, _hub, navigation: pages);

        findings.SetApp("xblast");
        await pages.NavigateToAsync<FindingsViewModel>(_ => { });

        NavItem entrada = shell.NavGroups.SelectMany(g => g.Items).Single(i => i.Key == "inventory");
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)entrada.Command).ExecuteAsync(null);

        pages.Current.Should().BeSameAs(inventory);
        inventory.Slug.Should().Be("xblast", "el inventario al que lleva es el de la aplicación en la que estabas");
    }

    /// <summary>
    /// Fuera de toda aplicación —en el portafolio— no hay grupo de aplicación. Un «Inventario» que
    /// no sabe de qué es peor que ninguno: lleva a una pregunta en vez de a un sitio.
    /// </summary>
    [Fact]
    public async Task En_el_portafolio_no_hay_grupo_de_aplicacion()
    {
        var provider = new PageProvider();
        var navigation = new NavigationService(provider);
        var findings = Findings(navigation);
        var portfolio = TestFactory.Portfolio(_hub, _paths, navigation);
        provider.Add(findings, portfolio);
        MainViewModel shell = TestFactory.Shell(_paths, _hub, navigation: navigation);

        findings.SetApp("xblast");
        await navigation.NavigateToAsync<FindingsViewModel>(_ => { });
        shell.ActiveApplication.HasApp.Should().BeTrue();

        await shell.ShowPortfolioCommand.ExecuteAsync(null);

        shell.ActiveApplication.HasApp.Should().BeFalse();
        shell.NavGroups.SelectMany(g => g.Items).Should().NotContain(i => i.Key == "inventory");
    }

    // ================================================================ hallazgos vuelve como se dejó

    /// <summary>
    /// Se filtra por una aplicación, se va uno a otra página y vuelve por el raíl: los hallazgos
    /// están como se dejaron. Antes el comando del raíl construía una página NUEVA y le ponía
    /// «todas las aplicaciones» encima, así que volver era empezar de cero — con los hallazgos de
    /// todo el portafolio delante.
    /// </summary>
    [Fact]
    public async Task Volver_a_Hallazgos_devuelve_el_filtro_que_habia()
    {
        var provider = new PageProvider();
        var navigation = new NavigationService(provider);
        var findings = Findings(navigation);
        var metrics = TestFactory.Metrics(_hub, _paths, _settings, navigation);
        provider.Add(findings, metrics);
        MainViewModel shell = TestFactory.Shell(_paths, _hub, navigation: navigation);

        findings.SetApp("xblast");
        await navigation.NavigateToAsync<FindingsViewModel>(_ => { });
        findings.SelectedApp!.Slug.Should().Be("xblast");

        // Se va a otra parte y se vuelve por el raíl.
        await shell.ShowMetricsCommand.ExecuteAsync(null);
        await shell.ShowFindingsCommand.ExecuteAsync(null);

        navigation.Current.Should().BeSameAs(findings, "volver es volver a TU página, no a una nueva");
        findings.SelectedApp!.Slug.Should().Be("xblast", "y con el filtro que le habías puesto");
    }

    /// <summary>
    /// Y «volver» del navegador deshace UN paso, devolviendo la página anterior con su estado.
    /// </summary>
    [Fact]
    public async Task Volver_deshace_un_paso_y_devuelve_la_misma_pagina()
    {
        var provider = new PageProvider();
        var navigation = new NavigationService(provider);
        var findings = Findings(navigation);
        var metrics = TestFactory.Metrics(_hub, _paths, _settings, navigation);
        provider.Add(findings, metrics);
        MainViewModel shell = TestFactory.Shell(_paths, _hub, navigation: navigation);

        findings.SetApp("otra");
        await navigation.NavigateToAsync<FindingsViewModel>(_ => { });
        shell.CanGoBack.Should().BeFalse("desde la primera página no hay nada que deshacer");

        await shell.ShowMetricsCommand.ExecuteAsync(null);
        shell.CanGoBack.Should().BeTrue();

        await shell.GoBackCommand.ExecuteAsync(null);

        navigation.Current.Should().BeSameAs(findings);
        findings.SelectedApp!.Slug.Should().Be("otra");
    }

    /// <summary>
    /// Resuelve por tipo las páginas que se le den; lo demás, nada. Se le añaden DESPUÉS de
    /// construirlo porque una página recibe la navegación en su constructor: sin esto no hay forma
    /// de que la navegación conozca a la página que la conoce a ella.
    /// </summary>
    private sealed class PageProvider : IServiceProvider
    {
        private readonly List<object> _pages = new();

        public PageProvider Add(params object[] pages)
        {
            _pages.AddRange(pages);
            return this;
        }

        public object? GetService(Type serviceType) => _pages.FirstOrDefault(serviceType.IsInstanceOfType);
    }
}
