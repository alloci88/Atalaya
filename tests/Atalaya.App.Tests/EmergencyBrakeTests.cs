using System.Text.RegularExpressions;
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
/// F5.13 — LOS DOS FRENOS DE EMERGENCIA, como invariantes.
/// <para>
/// Con una auditoría corriendo hay exactamente dos cosas que el usuario tiene que poder hacer
/// siempre: <b>volver a verla</b> y <b>pararla</b>. El item «Sesión en vivo» del rail y el botón
/// «Detener» de la cabecera de V5 son eso, y hasta el incidente del 2026-08-26 ninguno de los dos
/// tenía un solo test. Podían desaparecer en silencio —de la plantilla o del view-model— y nadie
/// se enteraba hasta estar delante de una sesión que no se puede frenar.
/// </para>
/// <para>
/// Se prueban en sus DOS capas a propósito: el estado observable (que el view-model lo ofrezca
/// cuando hay sesión) y la plantilla (que la vista lo pinte y lo enlace a ese estado). Un freno
/// puede caerse por cualquiera de las dos, y el episodio enseñó que la que se cae en silencio es
/// justamente la que no se compila.
/// </para>
/// </summary>
public sealed class EmergencyBrakeTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HubContext _hub;
    private readonly UlidFactory _ulids = new(SystemClock.Instance);
    private readonly ToastCenter _toasts = new();
    private readonly ServiceProvider _provider;

    public EmergencyBrakeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-brakes", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(Path.Combine(_root, "local"));
        _settings = new SettingsService(_paths);
        _settings.Load();
        _hub = TestFactory.Hub(_paths, _settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });

        var services = new ServiceCollection();
        services.AddSingleton(_hub);
        services.AddSingleton(_paths);
        services.AddSingleton(_settings);
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton<IUlidFactory>(_ulids);
        services.AddSingleton(new MachineConfigStore(_paths.MachinesJson));
        services.AddSingleton<FindingIngestionService>();
        services.AddSingleton<ReconciliationService>();
        var agent = new FakeCopilotAgent(_ => Array.Empty<SubmitFindingArgs>());
        services.AddSingleton<IAuditorProvider>(agent);
        services.AddSingleton<IAssistedFixProvider>(agent);
        services.AddSingleton<NavigationService>();
        services.AddSingleton<OpenSessionStore>();
        services.AddSingleton<AgentBusyGate>();
        services.AddSingleton(sp => new LiveSessionService(
            sp.GetRequiredService<SessionCoordinator>,
            sp.GetRequiredService<IAuditorProvider>(),
            sp.GetRequiredService<OpenSessionStore>(),
            sp.GetRequiredService<HubContext>(),
            busy: sp.GetRequiredService<AgentBusyGate>()));

        // F6.9: la carcasa tiene ahora un segundo item pulsante (el arreglo asistido), así que
        // MainViewModel necesita su servicio. Los frenos que estos tests vigilan son los de la
        // auditoría; el arreglo entra aquí solo para que la carcasa se pueda construir.
        services.AddSingleton<ReferenceCollector>();
        services.AddSingleton<FixSnapshotStore>();
        services.AddSingleton<CloneLinkService>();
        services.AddSingleton<GovernanceService>();
        services.AddSingleton<AssistedFixLauncher>();
        services.AddSingleton(sp => new LiveFixService(
            sp.GetRequiredService<HubContext>(),
            () => sp.GetRequiredService<IAssistedFixProvider>(),
            sp.GetRequiredService<MachineConfigStore>(),
            sp.GetRequiredService<IUlidFactory>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<ReferenceCollector>(),
            sp.GetRequiredService<FixSnapshotStore>(),
            sp.GetRequiredService<AssistedFixLauncher>(),
            sp.GetRequiredService<AgentBusyGate>()));
        services.AddSingleton(sp => new InterruptedSessionRecovery(
            sp.GetRequiredService<HubContext>(), sp.GetRequiredService<OpenSessionStore>()));
        services.AddSingleton<AccountStore>();
        services.AddSingleton<GitHubAccountService>();
        services.AddSingleton<DisplayIdService>();
        services.AddSingleton(_toasts);
        services.AddTransient<SessionCoordinator>();
        services.AddTransient<SessionViewModel>();
        services.AddSingleton<MainViewModel>();
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

    private LiveSessionService Live => _provider.GetRequiredService<LiveSessionService>();

    /// <summary>
    /// Pone el servicio en el ESTADO «hay una sesión viva» sin auditar nada: lo que estos tests
    /// vigilan es que los dos frenos estén ofrecidos mientras ese estado dure, no el motor.
    /// <para>
    /// Se avisa por el mismo evento que usa la sesión real (<c>Changed</c>), porque es de ahí de
    /// donde la carcasa se entera; un test que solo tocara la propiedad probaría una vía que en
    /// producción no es la que despierta al rail.
    /// </para>
    /// </summary>
    private LiveSessionService RunningSession()
    {
        LiveSessionService live = Live;
        live.IsRunning = true;
        RaiseChanged(live);
        return live;
    }

    private static void RaiseChanged(LiveSessionService live)
        => (typeof(LiveSessionService)
                .GetField("Changed", System.Reflection.BindingFlags.Instance
                                     | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(live) as Action)?.Invoke();

    private static string Repo(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("los tests corren dentro del repositorio");
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
    }

    /// <summary>Sin comentarios: lo que documenta una decisión no cuenta como interfaz.</summary>
    private static string Markup(string xaml)
        => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    // ==================================================== (a) volver a la sesión en vivo

    /// <summary>
    /// Con sesión activa, la carcasa ofrece el item y late. Sin sesión no está: es un acceso a algo
    /// que existe, no un hueco permanente.
    /// </summary>
    [Fact]
    public void Con_sesion_activa_el_rail_ofrece_el_item_de_sesion_en_vivo()
    {
        var main = _provider.GetRequiredService<MainViewModel>();

        main.HasSession.Should().BeFalse("sin sesión no hay nada que enseñar");

        RunningSession();

        main.HasSession.Should().BeTrue("con sesión viva el item TIENE que estar");
        main.IsSessionRunning.Should().BeTrue("y late, que es lo que dice que sigue corriendo");
        main.SessionNavLabel.Should().Be("Sesión en vivo");
    }

    /// <summary>Y pulsarlo lleva a V5. Un acceso que no navega no es un camino de vuelta.</summary>
    [Fact]
    public async Task El_item_de_sesion_en_vivo_navega_a_V5()
    {
        var main = _provider.GetRequiredService<MainViewModel>();
        RunningSession();

        await main.ShowSessionCommand.ExecuteAsync(null);

        _provider.GetRequiredService<NavigationService>().Current
            .Should().BeOfType<SessionViewModel>("es el único camino de vuelta a una sesión viva");
    }

    /// <summary>
    /// Terminada la sesión el acceso SIGUE, con otro nombre: el resumen de cierre no puede quedar
    /// inalcanzable por haber navegado fuera.
    /// </summary>
    [Fact]
    public void Terminada_la_sesion_el_acceso_sigue_como_ultima_sesion()
    {
        var main = _provider.GetRequiredService<MainViewModel>();
        LiveSessionService live = Live;
        live.HasFinished = true;
        RaiseChanged(live);

        main.HasSession.Should().BeTrue();
        main.IsSessionRunning.Should().BeFalse();
        main.SessionNavLabel.Should().Be("Última sesión");
    }

    /// <summary>
    /// Y el raíl lo OFRECE: la entrada existe exactamente cuando hay sesión que enseñar, lleva su
    /// rótulo y late mientras corre. Ésta es la mitad que puede caerse sin que el compilador diga
    /// nada — el camino de vuelta a una auditoría en marcha desaparecería y todo seguiría
    /// compilando.
    /// <para>
    /// Desde F26 §A el raíl se construye desde el view-model (<c>NavGroups</c>) en vez de estar
    /// escrito a mano en el XAML, así que la regla se mide donde ahora vive: en los datos. Es
    /// además más fuerte que leer la plantilla — comprueba el comportamiento con y sin sesión, no
    /// que exista una cadena de texto.
    /// </para>
    /// </summary>
    [Fact]
    public void El_rail_ofrece_la_sesion_exactamente_cuando_hay_sesion_que_ensenar()
    {
        var main = _provider.GetRequiredService<MainViewModel>();

        main.NavGroups.SelectMany(g => g.Items).Should().NotContain(
            i => i.Key == "session",
            "sin sesión, una entrada «Sesión en vivo» llevaría a una pantalla vacía");

        RunningSession();

        NavItem session = main.NavGroups.SelectMany(g => g.Items).Single(i => i.Key == "session");
        session.Label.Should().Be(main.SessionNavLabel);
        session.Pulsing.Should().BeTrue("el punto late mientras la auditoría corre");
        session.Command.Should().BeSameAs(main.ShowSessionCommand, "sin comando no hay camino de vuelta");
    }

    // ==================================================== (b) detener la sesión

    /// <summary>Con sesión activa, V5 ofrece Detener. Es el único freno y tiene que estar.</summary>
    [Fact]
    public void Con_sesion_activa_V5_ofrece_detener()
    {
        var vm = _provider.GetRequiredService<SessionViewModel>();

        vm.IsRunning.Should().BeFalse();

        RunningSession();

        vm.IsRunning.Should().BeTrue("es lo que hace visible el botón rojo de la cabecera");
        vm.StopCommand.Should().NotBeNull();
        vm.StopCommand.CanExecute(null).Should().BeTrue();
    }

    /// <summary>
    /// Y Detener DETIENE: cancela por el camino ordenado de F5.1b —la unidad en curso termina o se
    /// corta, y la sesión se cierra con informe parcial— y lo dice mientras tanto.
    /// </summary>
    [Fact]
    public void Detener_pide_la_parada_ordenada_y_lo_dice()
    {
        var vm = _provider.GetRequiredService<SessionViewModel>();
        LiveSessionService live = RunningSession();

        vm.StopCommand.Execute(null);

        live.StatusMessage.Should().Contain("Deteniendo",
            "una parada que no se anuncia parece que no ha hecho nada");
    }

    /// <summary>
    /// Y la cabecera de V5 lo PINTA, atado a <c>IsRunning</c>. La otra mitad que el compilador no
    /// vigila: el botón lleva ahí desde v1 y nada impedía que un rediseño se lo llevara.
    /// </summary>
    [Fact]
    public void La_cabecera_de_V5_pinta_el_boton_de_detener()
    {
        string view = Markup(Repo("src", "Atalaya.App", "Views", "SessionView.xaml"));

        view.Should().Contain("StopCommand", "sin el comando no hay freno");
        view.Should().Contain("Detener");
        view.Should().Contain("{Binding IsRunning, Converter={StaticResource BoolToVisibility}}",
            "visible exactamente mientras haya sesión activa");
    }
}
