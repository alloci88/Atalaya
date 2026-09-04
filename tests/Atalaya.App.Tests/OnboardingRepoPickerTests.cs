using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Domain.Abstractions;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// R3 — «Nueva aplicación» ELIGE el repositorio, no lo escribe.
/// <para>
/// Cinco reglas, y solo cinco (norma <b>N-5</b>): el nombre sale del repositorio elegido; elegirlo
/// sobrevive a que el combo devuelva su nombre al cuadro de texto; un repositorio que ya es una app
/// del hub lleva a vincular y no a crear; sin lista cargada, escribir la URL a mano sigue dando de
/// alta; y la lista se recuerda en la sesión pero el botón de recargar vuelve a preguntar de verdad.
/// Ninguna toca la red: la API se sirve por <see cref="HttpStub"/>.
/// </para>
/// </summary>
public sealed class OnboardingRepoPickerTests : IDisposable
{
    private const string Org = "Applied-Advanced-Solutions-AAS";

    private readonly string _root;
    private readonly string _clone;
    private readonly AppPaths _paths;
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly ToastCenter _toasts = new();
    private readonly UlidFactory _ulids = new(SystemClock.Instance);

    public OnboardingRepoPickerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atalaya-r3-repos", Guid.NewGuid().ToString("N"));
        _clone = Path.Combine(_root, "xblast");
        Directory.CreateDirectory(_clone);
        File.WriteAllText(Path.Combine(_clone, "Program.cs"), "class Program { static void Main() { } }\n");
        File.WriteAllText(Path.Combine(_clone, "xblast.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");

        _paths = new AppPaths(Path.Combine(_root, "local"));
        SettingsService settings = TestFactory.Settings(_paths);
        // El alta exige un hub configurado. Sin EnsureSync, HubContext.Sync es null y el
        // CommitAndPush del final no hace nada: cero red (N-1).
        settings.Current.HubUrlOverride = Path.Combine(_root, "remote");
        _hub = TestFactory.Hub(_paths, settings);
        _hub.Store.WriteHub(new HubInfo { OrganizationName = "AAS" });
        _machines = new MachineConfigStore(_paths.MachinesJson);
    }

    /// <summary>La lista que contesta la API, con dos repositorios de la organización.</summary>
    private const string TwoRepos = """
        [
          {"name":"XBLAST","clone_url":"https://github.com/Applied-Advanced-Solutions-AAS/XBLAST.git"},
          {"name":"Atalaya","clone_url":"https://github.com/Applied-Advanced-Solutions-AAS/Atalaya.git"}
        ]
        """;

    /// <summary>
    /// El catálogo con una cuenta conectada y la API respondiendo lo que se le diga. El token es
    /// el de la cuenta, igual que en la aplicación: no hay una segunda credencial para esto.
    /// </summary>
    private RepositoryCatalog Catalog(HttpStub stub)
    {
        GitHubAccountService account = TestFactory.Account(_paths);
        account.Connect("gho_test", new GitHubUser(1, "yo", "Yo", null, null));
        return new RepositoryCatalog(
            account,
            new GitHubApiClient(stub.Client()),
            new DeployConfig { OrganizationLogin = Org },
            _hub);
    }

    private OnboardingViewModel Wizard(RepositoryCatalog catalog)
        => TestFactory.Onboarding(
            _hub, _paths, _machines, _toasts, _ulids,
            new NavigationService(new NoServices()), catalog: catalog);

    // ============================================================ El nombre sale del repositorio

    /// <summary>
    /// La regla del §1: el nombre de la app deja de ser una decisión aparte. Si se rompiera en
    /// silencio, el alta registraría el slug de un texto vacío —o del último que quedara escrito—
    /// y la app entraría en el hub con un nombre que no es el de su repositorio.
    /// </summary>
    [Fact]
    public async Task El_nombre_de_la_app_sale_del_repositorio_elegido()
    {
        OnboardingViewModel vm = Wizard(Catalog(new HttpStub().Json(TwoRepos)));
        await vm.LoadAsync();

        vm.Name.Should().BeEmpty("la etiqueta nace vacía: todavía no hay repositorio");
        vm.HasName.Should().BeFalse();

        vm.SelectedRepository = vm.Repositories.Single(r => r.Name == "XBLAST");

        vm.Name.Should().Be("XBLAST");
        vm.HasName.Should().BeTrue();
        vm.RepoUrl.Should().Be("https://github.com/Applied-Advanced-Solutions-AAS/XBLAST.git");
    }

    /// <summary>
    /// La costura con WPF, que es donde esto se rompió de verdad: un <c>ComboBox</c> editable
    /// devuelve el NOMBRE del repositorio elegido al mismo cuadro de texto que filtra la lista. Si
    /// ese eco se toma por una búsqueda, el filtro deja la colección con un solo elemento —y al
    /// control le desaparece el que tenía seleccionado, así que se queda en blanco—. No hay error
    /// en ninguna parte: el desplegable simplemente pasa a enseñar un repositorio de los veinte.
    /// </summary>
    [Fact]
    public async Task El_repositorio_elegido_sobrevive_a_que_el_combo_devuelva_su_nombre()
    {
        OnboardingViewModel vm = Wizard(Catalog(new HttpStub().Json(TwoRepos)));
        await vm.LoadAsync();

        RepoOption xblast = vm.Repositories.Single(r => r.Name == "XBLAST");
        vm.SelectedRepository = xblast;

        // Lo que hace WPF al elegir de la lista.
        vm.RepoQuery = "XBLAST";

        vm.Repositories.Should().HaveCount(2, "el eco no es una búsqueda: la lista no se recorta");
        vm.Repositories.Should().Contain(xblast);
        vm.SelectedRepository.Should().BeSameAs(xblast);
        vm.RepoUrl.Should().Be("https://github.com/Applied-Advanced-Solutions-AAS/XBLAST.git");

        // Y escribir de verdad sí filtra, sin perder por ello lo ya elegido.
        vm.RepoQuery = "Ata";
        vm.Repositories.Should().ContainSingle().Which.Name.Should().Be("Atalaya");
        vm.RepoUrl.Should().Be("https://github.com/Applied-Advanced-Solutions-AAS/XBLAST.git");
    }

    // =========================================================== Lo que ya está en el hub, marcado

    /// <summary>
    /// D-303 desde la lista: el repositorio de una app que ya existe se enseña, sale marcado, y
    /// elegirlo lleva al diálogo de vincular en vez de crear un duplicado. Sin esto la lista sería
    /// un catálogo de cosas creables y el usuario se estrellaría contra la puerta de
    /// <c>Create</c> — que sigue ahí, y este test la vuelve a forzar a mano.
    /// </summary>
    [Fact]
    public async Task Elegir_un_repo_que_ya_esta_en_el_hub_lleva_a_vincular_y_no_a_crear()
    {
        _hub.Store.WriteApp(new AppConfig
        {
            Slug = "xblast",
            Name = "XBLAST",
            RepoUrl = "https://github.com/Applied-Advanced-Solutions-AAS/XBLAST",
            CurrentCycle = 1,
        });

        OnboardingViewModel vm = Wizard(Catalog(new HttpStub().Json(TwoRepos)));
        await vm.LoadAsync();

        RepoOption xblast = vm.Repositories.Single(r => r.Name == "XBLAST");
        xblast.AlreadyInHub.Should().BeTrue();
        xblast.Label.Should().Contain("ya en el hub");
        vm.Repositories.Single(r => r.Name == "Atalaya").AlreadyInHub.Should().BeFalse();

        vm.SelectedRepository = xblast;

        vm.IsDuplicate.Should().BeTrue();
        vm.CanCreate.Should().BeFalse();
        vm.LinkExistingLabel.Should().Contain("XBLAST");

        // Y la puerta del modelo, no la del XAML: crear a la fuerza no escribe una segunda app.
        vm.ClonePath = _clone;
        await vm.CreateCommand.ExecuteAsync(null);
        _hub.Store.ListAppSlugs().Should().ContainSingle().Which.Should().Be("xblast");
    }

    // ================================================================ El camino manual no se cierra

    /// <summary>
    /// El anti-objetivo escrito como test: la lista es la comodidad, no la única puerta. Sin red
    /// —o sin permiso para listar— el combo lo dice y escribir la URL a mano sigue dando de alta.
    /// Si esto se rompiera, un fallo de GitHub dejaría al equipo sin poder registrar aplicaciones.
    /// </summary>
    [Fact]
    public async Task Sin_lista_cargada_escribir_la_url_a_mano_sigue_dando_de_alta()
    {
        var stub = new HttpStub().Throws(new System.Net.Http.HttpRequestException("sin red"));
        OnboardingViewModel vm = Wizard(Catalog(stub));

        await vm.LoadAsync();

        vm.RepoListState.Should().Be(RepoListState.Failed);
        vm.RepositoriesFailed.Should().BeTrue();
        vm.RepoListNotice.Should().Contain("No se pudo cargar la lista · reintentar");
        vm.Repositories.Should().BeEmpty();

        // El mismo control, escrito a mano: es una URL, así que vale como URL — y de ella sale el
        // nombre igual que si se hubiera elegido de la lista.
        vm.RepoQuery = "https://github.com/Applied-Advanced-Solutions-AAS/XBLAST.git";
        vm.Name.Should().Be("XBLAST");

        vm.ClonePath = _clone;
        vm.CanCreate.Should().BeTrue();
        await vm.CreateCommand.ExecuteAsync(null);

        AppConfig? app = _hub.Store.TryReadApp("xblast");
        app.Should().NotBeNull();
        app!.RepoUrl.Should().Be("https://github.com/Applied-Advanced-Solutions-AAS/XBLAST.git");
        _machines.Load().ClonePathFor("xblast").Should().Be(_clone);
    }

    // ============================================================== La caché, y el botón que la rompe

    /// <summary>
    /// La lista se cachea en la sesión —volver a la pantalla no vuelve a pagar la llamada— y el
    /// botón de recargar SÍ vuelve a preguntar. Es la mitad silenciosa del §1: con la caché sin
    /// invalidar, un repositorio recién creado no aparecería hasta reiniciar la aplicación y no
    /// habría ningún síntoma que lo explicara.
    /// </summary>
    [Fact]
    public async Task La_lista_se_cachea_en_la_sesion_y_recargar_vuelve_a_preguntar()
    {
        var stub = new HttpStub().Json(TwoRepos).Json(TwoRepos);
        RepositoryCatalog catalog = Catalog(stub);
        OnboardingViewModel vm = Wizard(catalog);

        await vm.LoadAsync();
        await vm.LoadAsync();

        stub.Requests.Should().HaveCount(1, "la segunda visita a la pantalla se sirve de la caché");

        await vm.ReloadRepositoriesCommand.ExecuteAsync(null);

        stub.Requests.Should().HaveCount(2);
        stub.Requests[1].RequestUri!.AbsolutePath.Should().Be($"/orgs/{Org}/repos");
        stub.Requests[1].Headers.Authorization!.ToString().Should().Be("Bearer gho_test");
    }

    /// <summary>Un contenedor que no resuelve nada: la navegación no se ejercita aquí.</summary>
    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Un temporal que Windows todavía tiene abierto no es un fallo del test.
        }
    }
}
