using System.Collections.ObjectModel;
using System.Text;
using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Ingestion;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using Atalaya.Storage;
using Atalaya.Storage.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>Onboarding wizard (§4): register an app, detect its stack, build the first inventory.</summary>
public sealed partial class OnboardingViewModel : ViewModelBase
{
    private readonly HubContext _hub;
    private readonly InventoryScanner _scanner;
    private readonly MachineConfigStore _machines;
    private readonly NavigationService _navigation;
    private readonly FindingIngestionService _ingestion;
    private readonly MeasuredFindingService _measured;

    /// <summary>F5.7 §4: el resultado del alta se cuenta por el toast global.</summary>
    private readonly ToastCenter _toasts;

    /// <summary>
    /// F5.8 §2: «Nueva aplicación» es para dar de ALTA apps nuevas. Si el repo elegido ya está en
    /// el portafolio, lo que hace falta es vincular el clon, no crear un duplicado — que dejaría
    /// dos apps con los mismos hallazgos y ningún modo de decir cuál es la buena.
    /// </summary>
    private readonly CloneLinkService _links;

    private readonly LinkCloneFlow _linkFlow;

    /// <summary>
    /// F5.9 §1: el importador v4 dejo de ser un destino del menu y es un PASO OPCIONAL de esta
    /// alta. Dar de alta una app y traerse lo que el sistema anterior sabía de ella son el mismo
    /// gesto, y se hace una sola vez por aplicación — con las demas apps de la empresa todavia
    /// por dar de alta, tenía que estar aquí y no en un rincon permanente de la navegación.
    /// </summary>
    private readonly ImportService _import;

    private readonly IFolderPicker _picker;

    /// <summary>
    /// R3: los repositorios de la organización. El alta ya no pide escribir la URL — la elige de
    /// una lista, y de ella saca también el nombre de la aplicación.
    /// </summary>
    private readonly RepositoryCatalog _catalog;

    /// <summary>El diálogo del primer ciclo (F17 §4). Opcional: sin él, el ciclo 1 nace General.</summary>
    private readonly CycleConfigFlow? _configFlow;

    public OnboardingViewModel(
        HubContext hub,
        InventoryScanner scanner,
        MachineConfigStore machines,
        NavigationService navigation,
        FindingIngestionService ingestion,
        ToastCenter toasts,
        CloneLinkService links,
        LinkCloneFlow linkFlow,
        ImportService import,
        IFolderPicker picker,
        MeasuredFindingService measured,
        RepositoryCatalog catalog,
        CycleConfigFlow? configFlow = null)
    {
        _configFlow = configFlow;
        _measured = measured;
        _hub = hub;
        _scanner = scanner;
        _machines = machines;
        _navigation = navigation;
        _ingestion = ingestion;
        _toasts = toasts;
        _links = links;
        _linkFlow = linkFlow;
        _import = import;
        _picker = picker;
        _catalog = catalog;
    }

    public override string Title => "Nueva aplicación";

    /// <summary>
    /// F26 §A — el alta es una ACCIÓN del portafolio, no un lugar propio: por eso sale del raíl
    /// (ya era el botón primario de Portafolio) y, mientras dura, el raíl sigue señalando
    /// Portafolio, que es de donde has salido y adonde vuelves.
    /// </summary>
    public override string RailKey => "portfolio";

    /// <summary>
    /// El nombre de la aplicación. Desde R3 NO se escribe: se deriva del repositorio elegido, y la
    /// vista lo enseña como etiqueta. Un nombre distinto del repositorio no servía para nada y era
    /// una tercera cosa que se podía teclear mal.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasName))]
    private string _name = string.Empty;

    /// <summary>False mientras no haya repositorio: la etiqueta del nombre nace vacía.</summary>
    public bool HasName => Name.Length > 0;

    [ObservableProperty] private string _clonePath = string.Empty;
    [ObservableProperty] private TechStack _detectedStack = TechStack.Unknown;

    [ObservableProperty] private string _repoUrl = string.Empty;

    /// <summary>
    /// La carpeta <c>CodeAudit/</c> del sistema v4, si la hay. Se rellena sola al elegir el clon;
    /// también se puede señalar a mano cuando el baseline vive fuera del repo.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBaseline))]
    [NotifyPropertyChangedFor(nameof(BaselineBlockedReason))]
    [NotifyPropertyChangedFor(nameof(HasBaselineBlockedReason))]
    private string _codeAuditPath = string.Empty;

    /// <summary>
    /// Importar o no. Cuando la carpeta se detecta sola se marca por defecto —quien tiene un
    /// baseline v4 casi siempre lo quiere— pero es una casilla, no un automatismo: el alta no
    /// puede escribir en el hub cosas que el usuario no ha visto venir.
    /// </summary>
    [ObservableProperty] private bool _importBaseline;

    /// <summary>Lo que la ruta senalada tiene dentro, dicho antes de importar nada.</summary>
    [ObservableProperty] private string _baselineNotice = string.Empty;

    /// <summary>La ruta senalada es una CodeAudit/ de verdad.</summary>
    public bool HasBaseline => V4Baseline.Looks(CodeAuditPath);

    /// <summary>Lo que el importador registro. Se queda a la vista: es la salida, no un aviso.</summary>
    public ObservableCollection<string> ImportLog { get; } = new();

    partial void OnCodeAuditPathChanged(string value) => DescribeBaseline(auto: false);

    partial void OnClonePathChanged(string value)
    {
        DetectBaseline();
        ClonePathError = string.Empty;
    }

    /// <summary>
    /// <b>Los errores de validación van EN LÍNEA, bajo el campo que los produce</b> (F26 Parte C).
    /// <para>
    /// Hasta aquí, pulsar «Crear» sin repositorio o con una ruta que no existe soltaba un toast
    /// —«Elige el repositorio (o escribe su URL) y una ruta de clon válida.»— que decía las dos
    /// cosas a la vez, no decía cuál fallaba y caducaba a los ocho segundos. Es la misma regla que
    /// D-949 aplica a un botón bloqueado: la razón va pegada a lo que la produce.
    /// </para>
    /// <para>
    /// Aparecen al INTENTAR crear, no mientras se escribe: un formulario que se pone rojo antes de
    /// que lo hayas rellenado regaña por adelantado. Y se van solos en cuanto el campo cambia.
    /// </para>
    /// </summary>
    [ObservableProperty] private string _repoError = string.Empty;

    [ObservableProperty] private string _clonePathError = string.Empty;



    /// <summary>
    /// Busca la <c>CodeAudit/</c> en el clon elegido. Solo PROPONE: encontrarla no importa nada,
    /// igual que detectar deriva al vincular no re-escanea solo (D-301).
    /// </summary>
    private void DetectBaseline()
    {
        if (V4Baseline.Find(ClonePath) is not { } found)
        {
            return;
        }

        CodeAuditPath = found;
        ImportBaseline = true;
        DescribeBaseline(auto: true);
    }

    private void DescribeBaseline(bool auto)
    {
        OnPropertyChanged(nameof(HasBaseline));
        OnPropertyChanged(nameof(BaselineBlockedReason));
        OnPropertyChanged(nameof(HasBaselineBlockedReason));
        if (CodeAuditPath.Length == 0)
        {
            BaselineNotice = string.Empty;
            return;
        }

        if (!HasBaseline)
        {
            ImportBaseline = false;
            BaselineNotice = "En esa carpeta no hay ficheros del formato v4 "
                             + "(BASELINE.md, LOTES.md, SILENCIADOS.md o HISTORICO.md).";
            return;
        }

        BaselineNotice = auto
            ? "Se ha encontrado un baseline del sistema v4 dentro del clon. "
              + "Se importará con el alta: hallazgos, silencios, histórico e informes."
            : "Carpeta v4 válida. Se importará con el alta: hallazgos, silencios, histórico e informes.";
    }

    /// <summary>Señalar la carpeta a mano, cuando el baseline no vive dentro del repo.</summary>
    [RelayCommand]
    private void PickBaseline()
    {
        if (_picker.Pick("Elige la carpeta CodeAudit/ del sistema v4", ClonePath) is { } chosen)
        {
            CodeAuditPath = chosen;
        }
    }

    /// <summary>Señalar a mano la carpeta del clon, en vez de escribir la ruta (R3).</summary>
    [RelayCommand]
    private void PickClonePath()
    {
        if (_picker.Pick("Elige la carpeta del clon local", ClonePath) is { } chosen)
        {
            ClonePath = chosen;
        }
    }

    // ==================================================== R3 — El repositorio se elige, no se escribe

    /// <summary>Todos los repositorios cargados; <see cref="Repositories"/> es lo que pasa el filtro.</summary>
    private readonly List<RepoOption> _allRepositories = new();

    /// <summary>Lo que enseña el desplegable ahora mismo.</summary>
    public ObservableCollection<RepoOption> Repositories { get; } = new();

    /// <summary>
    /// El repositorio elegido. De él salen las dos cosas que antes se escribían: la URL, que se
    /// guarda como siempre, y el NOMBRE de la aplicación, que es el del repositorio.
    /// </summary>
    [ObservableProperty] private RepoOption? _selectedRepository;

    /// <summary>
    /// El texto del combo, que hace dos oficios porque el control es uno solo: filtra la lista
    /// mientras se escribe un nombre, y ES la URL cuando lo escrito es una URL. Ese segundo oficio
    /// es el respaldo: sin lista —sin red, sin permiso para listar— dar de alta sigue siendo posible.
    /// </summary>
    [ObservableProperty] private string _repoQuery = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoadingRepositories))]
    [NotifyPropertyChangedFor(nameof(RepositoriesFailed))]
    private RepoListState _repoListState = RepoListState.NotLoaded;

    /// <summary>Lo que le pasa a la lista, dicho en el sitio de la lista.</summary>
    [ObservableProperty] private string _repoListNotice = string.Empty;

    public bool IsLoadingRepositories => RepoListState == RepoListState.Loading;

    public bool RepositoriesFailed => RepoListState == RepoListState.Failed;

    /// <summary>De qué organización es la lista. Vacío cuando el despliegue no lo dice.</summary>
    public string RepoOwner => _catalog.Owner ?? string.Empty;

    public override Task LoadAsync() => LoadRepositoriesAsync(refresh: false);

    /// <summary>El botón de recargar, para cuando alguien acaba de crear el repositorio.</summary>
    [RelayCommand]
    private Task ReloadRepositories() => LoadRepositoriesAsync(refresh: true);

    private async Task LoadRepositoriesAsync(bool refresh)
    {
        RepoListState = RepoListState.Loading;
        RepoListNotice = string.Empty;
        try
        {
            IReadOnlyList<GitHubRepository> repos = await _catalog.ListAsync(refresh, CancellationToken.None);
            _allRepositories.Clear();
            foreach (GitHubRepository repo in repos.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                // Los que ya son una app del hub SALEN, y salen marcados: esconderlos dejaría al
                // usuario buscando un repositorio que está ahí, y enseñarlos sin marca lo mandaría
                // a intentar crear el duplicado que D-303 tiene que parar más adelante.
                _allRepositories.Add(new RepoOption(repo.Name, repo.CloneUrl, _links.FindByRepoUrl(repo.CloneUrl)));
            }

            ApplyRepoFilter(RepoQuery);
            RepoListState = RepoListState.Loaded;
            RepoListNotice = _allRepositories.Count == 0
                ? "Esta cuenta no ve ningún repositorio en la organización."
                : string.Empty;
        }
        catch (Exception ex)
        {
            _allRepositories.Clear();
            Repositories.Clear();
            RepoListState = RepoListState.Failed;
            RepoListNotice = $"No se pudo cargar la lista · reintentar. {ex.Message} "
                             + "Mientras tanto puedes escribir aquí mismo la URL del repositorio.";
        }
    }

    partial void OnSelectedRepositoryChanged(RepoOption? value)
    {
        if (value is null)
        {
            return;
        }

        // Una sola escritura: la URL. El nombre y la detección de duplicado cuelgan de ella, así
        // que elegir de la lista y escribir la URL a mano acaban exactamente en el mismo sitio.
        RepoUrl = value.Url;
    }

    partial void OnRepoQueryChanged(string value)
    {
        // Al elegir de la lista, WPF devuelve el NOMBRE del repositorio al mismo cuadro de texto.
        // Eso no es teclear: no filtra —el desplegable tiene que volver a abrirse entero, con el
        // elegido dentro— y no puede pisar la URL que se acaba de resolver.
        if (SelectedRepository is { } chosen
            && string.Equals(value.Trim(), chosen.Name, StringComparison.Ordinal))
        {
            ApplyRepoFilter(string.Empty);
            return;
        }

        ApplyRepoFilter(value);

        // Lo escrito solo se toma por URL cuando lo parece; si no, es el filtro de la lista.
        if (LooksLikeUrl(value))
        {
            RepoUrl = value.Trim();
        }
    }

    /// <summary>
    /// Deja en <see cref="Repositories"/> los que casan con lo escrito, EN SITIO.
    /// <para>
    /// Sin <c>Clear()</c> a propósito: vaciar la colección se lleva por delante el elemento
    /// seleccionado, y a un <c>ComboBox</c> al que le desaparece el seleccionado se le queda el
    /// cuadro en blanco. La primera versión hacía justo eso y elegir un repositorio parecía no
    /// haber elegido nada.
    /// </para>
    /// </summary>
    private void ApplyRepoFilter(string query)
    {
        string needle = query.Trim();

        for (int i = Repositories.Count - 1; i >= 0; i--)
        {
            if (!Matches(Repositories[i], needle))
            {
                Repositories.RemoveAt(i);
            }
        }

        int at = 0;
        foreach (RepoOption option in _allRepositories)
        {
            if (!Matches(option, needle))
            {
                continue;
            }

            if (at < Repositories.Count && ReferenceEquals(Repositories[at], option))
            {
                at++;
                continue;
            }

            Repositories.Insert(at++, option);
        }
    }

    private static bool Matches(RepoOption option, string needle)
        => needle.Length == 0 || option.Name.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeUrl(string text)
    {
        string t = text.Trim();
        return t.Contains("://", StringComparison.Ordinal)
               || t.Contains('@', StringComparison.Ordinal)
               || t.Contains('/', StringComparison.Ordinal)
               || t.Contains('\\', StringComparison.Ordinal);
    }

    /// <summary>
    /// El nombre de la aplicación es el del repositorio, siempre. Se saca de la URL con la MISMA
    /// normalización que decide si dos URLs son el mismo repo (<c>RemoteUrl</c>, D-295), para que
    /// el https y el ssh del mismo repositorio den el mismo nombre.
    /// </summary>
    internal static string NameFromRepoUrl(string? url)
    {
        string normalized = RemoteUrl.Normalize(url);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        int slash = normalized.LastIndexOf('/');
        return slash < 0 ? normalized : normalized[(slash + 1)..];
    }

    /// <summary>Escribir la URL ya basta para saber que la app existe: no hace falta llegar al final.</summary>
    partial void OnRepoUrlChanged(string value)
    {
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(CreateBlockedReason));
        OnPropertyChanged(nameof(HasCreateBlockedReason));
        Name = NameFromRepoUrl(value);
        DetectExistingApp();
        RepoError = string.Empty;
    }

    /// <summary>La app del hub que YA tiene este repo, si la hay.</summary>
    private AppConfig? _existing;

    /// <summary>
    /// El aviso de redirección (F5.8 §2). Vive como texto y no como toast porque no es el
    /// resultado de una acción: es una condición del formulario que dura mientras dure la URL.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDuplicate))]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    [NotifyPropertyChangedFor(nameof(CreateBlockedReason))]
    [NotifyPropertyChangedFor(nameof(HasCreateBlockedReason))]
    private string _duplicateNotice = string.Empty;

    public bool IsDuplicate => DuplicateNotice.Length > 0;

    /// <summary>
    /// POR QUÉ NO SE PUEDE CREAR AHORA MISMO. Vacío = se puede (P-27, UI-0038).
    /// <para>
    /// «Crear e inventariar» estaba encendido con el repositorio sin elegir: el primario de la
    /// vista invitaba a pulsarlo antes de que hubiera nada que crear. La regla es la misma que en
    /// Inventario y en Ajustes — se apaga cuando no puede hacer nada, y la razón va pegada a él.
    /// </para>
    /// </summary>
    public string CreateBlockedReason => RepoUrl.Trim().Length == 0
        ? "elige un repositorio"
        : IsDuplicate ? "ese repositorio ya tiene aplicación" : string.Empty;

    /// <summary>Dar de alta se apaga mientras falte el repositorio o ya exista su aplicación.</summary>
    public bool CanCreate => CreateBlockedReason.Length == 0;

    /// <summary>Y hay algo que decir al lado del botón apagado.</summary>
    public bool HasCreateBlockedReason => CreateBlockedReason.Length > 0;

    /// <summary>
    /// Por qué no se puede importar el baseline. Vacío = se puede (UI-0038): la casilla estaba
    /// deshabilitada y tampoco decía por qué.
    /// </summary>
    public string BaselineBlockedReason => HasBaseline
        ? string.Empty
        : "no se ha encontrado un CodeAudit en el clon";

    /// <inheritdoc cref="BaselineBlockedReason"/>
    public bool HasBaselineBlockedReason => !HasBaseline;

    /// <summary>
    /// Salir del formulario sin crear nada (UI-0041). No había ninguno: para salir hacía falta la
    /// flecha o la miga, mientras el raíl marcaba «Portafolio» como entrada activa — la entrada
    /// resaltada del menú era, literalmente, el botón que abandonaba el formulario sin avisar.
    /// </summary>
    [RelayCommand]
    private Task Cancel() => _navigation.NavigateToAsync<PortfolioViewModel>();

    /// <summary>El botón del aviso: «Vincular mi clon» con el nombre de la app que ya existe.</summary>
    public string LinkExistingLabel => _existing is null
        ? "Vincular mi clon"
        : $"Vincular mi clon de «{_existing.Name}»";

    [RelayCommand]
    private void Detect()
    {
        if (!Directory.Exists(ClonePath))
        {
            _toasts.Show("La ruta del clon no existe.");
            return;
        }

        // La carpeta elegida SABE de qué repo es. Si el usuario no escribió la URL, se toma de
        // ahí — y con ella se puede responder a la pregunta que importa: ¿esta app ya existe?
        if (string.IsNullOrWhiteSpace(RepoUrl) && GitInfo.OriginUrl(ClonePath) is { } origin)
        {
            RepoUrl = origin;
        }

        DetectedStack = StackDetector.Detect(ClonePath);
        DetectExistingApp();
        DetectBaseline();
        _toasts.Show($"Stack detectado: {DetectedStack}.");
    }

    /// <summary>
    /// ¿El repo elegido ya está en el portafolio? La comparación es la misma que usa el vínculo
    /// (<c>RemoteUrl</c>), así que https y ssh del mismo repo cuentan como el mismo repo.
    /// </summary>
    private void DetectExistingApp()
    {
        _existing = _links.FindByRepoUrl(RepoUrl);
        DuplicateNotice = _existing is null
            ? string.Empty
            : $"Esta aplicación ya existe en el portafolio como «{_existing.Name}». "
              + "No hace falta darla de alta otra vez: vincula tu clon y podrás auditarla.";
        OnPropertyChanged(nameof(LinkExistingLabel));
    }

    /// <summary>
    /// La redirección: abre el diálogo de vincular de la app que YA existe y, si queda vinculada,
    /// lleva a su inventario. Es el mismo diálogo del portafolio — no un segundo camino.
    /// </summary>
    [RelayCommand]
    private async Task LinkExisting()
    {
        if (_existing is null)
        {
            return;
        }

        string slug = _existing.Slug;
        _linkFlow.Run(slug);
        await _navigation.NavigateToAsync<InventoryViewModel>(vm => vm.SetApp(slug));
    }

    [RelayCommand]
    private async Task Create()
    {
        // La validación se dice EN LÍNEA, campo a campo (F26 §C): antes era un toast que juntaba
        // las dos condiciones y no decía cuál había fallado.
        RepoError = string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(RepoUrl)
            ? "Elige un repositorio de la lista, o escribe su URL."
            : string.Empty;
        ClonePathError = string.IsNullOrWhiteSpace(ClonePath)
            ? "Señala la carpeta donde tienes clonado el repositorio."
            : Directory.Exists(ClonePath) ? string.Empty : "Esa carpeta no existe en esta máquina.";

        if (RepoError.Length > 0 || ClonePathError.Length > 0)
        {
            return;
        }

        // Se vuelve a mirar la puerta aquí y no solo en el XAML: el botón gris es una cortesía de
        // la vista. Crear el duplicado es lo único que este asistente no puede hacer.
        DetectExistingApp();
        if (_existing is not null)
        {
            _toasts.Show(DuplicateNotice);
            return;
        }

        if (!_hub.IsConfigured)
        {
            _toasts.Show("Conecta primero el hub en Ajustes.");
            return;
        }

        string slug = Slugify(Name);
        bool importing = ImportBaseline && HasBaseline;
        IsBusy = true;
        ImportLog.Clear();
        _toasts.Show(importing ? "Importando el baseline v4, escaneando y registrando…" : "Escaneando y registrando…");
        try
        {
            // F17 §4: el escaneo primero, el diálogo después, y el ciclo 1 se escribe con lo
            // elegido. Los tres pasos se separan porque el diálogo vive en el hilo de la interfaz
            // y el escaneo no puede congelarla.
            (IReadOnlyList<string> log, AppConfig app, ScanOutput scan) = await Task.Run(() =>
            {
                if (DetectedStack == TechStack.Unknown)
                {
                    DetectedStack = StackDetector.Detect(ClonePath);
                }

                // 1) El baseline v4, ANTES del escaneo. Trae su propio app.json (con el ciclo en
                //    el que se quedó el sistema anterior) y su inventario con el estado auditado
                //    de cada unidad. Importar DESPUÉS lo pisaría con lo que acabamos de escanear
                //    y el alta perdería justo lo que se venía a rescatar.
                IReadOnlyList<string> importLog = importing
                    ? _import.Import(slug, Name.Trim(), RepoUrl.Trim(), CodeAuditPath, push: false)
                    : Array.Empty<string>();

                // 2) La app: la importada si la hay, con lo que el asistente sabe encima. El
                //    ciclo NO se toca — es del sistema anterior y lo dice el baseline.
                AppConfig app = _hub.Store.TryReadApp(slug) ?? new AppConfig
                {
                    Slug = slug,
                    Name = Name.Trim(),
                    RepoUrl = RepoUrl.Trim(),
                    Stack = DetectedStack,
                    CurrentCycle = 1,
                };
                app.Name = Name.Trim();
                app.RepoUrl = RepoUrl.Trim();
                app.Stack = DetectedStack;
                app.CurrentCycle = Math.Max(1, app.CurrentCycle);

                // El alta escanea con la política que la app estrena: la de fábrica, o la que
                // traiga su app.json si ya existía en el hub (F13).
                ScanOutput scan = _scanner.Scan(ClonePath, app, app.CurrentCycle);
                app.Stack = scan.Stack;
                _hub.Store.WriteApp(app);
                return (importLog, app, scan);
            });

            // F17 §4: tras el escaneo y ANTES de abrir el ciclo 1, la lupa. General preseleccionada
            // y marcada como recomendada; cancelar deja los valores por defecto y el alta sigue.
            InventoryCycle? previous = _hub.Store.TryReadInventory(slug, app.CurrentCycle);
            CycleConfig config = previous?.Config ?? CycleConfig.Default;
            if (_configFlow is not null)
            {
                var preview = new CycleConfigPreview(slug, app.Name, app.CurrentCycle, config, 0);
                config = await _configFlow.AskAsync(preview, CycleConfigReason.Alta) ?? config;
            }

            await Task.Run(() =>
            {
                // 3) El inventario del ciclo vigente sale del CÓDIGO que hay en el clon, pero
                //    arrastrando el estado de lo que el baseline daba por auditado: es la misma
                //    reconciliación del re-escaneo (D-302), no una segunda escrita aparte.
                InventoryCycle inventory = previous is null
                    ? scan.Inventory
                    : Rescanner.Reconcile(previous, scan.Inventory).Merged;
                inventory.Config = config;
                inventory.OpenedUtc ??= DateTimeOffset.UtcNow;
                inventory.OpenThemeHistory(config.Theme, inventory.OpenedUtc, _hub.ResolveIdentity().Name);
                _hub.Store.WriteInventory(slug, inventory);

                // Los hallazgos de «unidad demasiado grande» los pone al día el MISMO servicio que
                // los mantiene después (F5.16). Antes se creaban aquí con un bucle propio: dos
                // caminos para el mismo hecho, y el de aquí no sabía resolver los que sobraran al
                // re-vincular una app que ya existía.
                _measured.Reconcile(slug, inventory, ClonePath);

                _machines.SetClonePath(slug, ClonePath);
                _hub.Sync?.CommitAndPush(importing
                    ? $"app: onboard {slug} ({scan.Stack}) + import v4"
                    : $"app: onboard {slug} ({scan.Stack})");
            });

            foreach (string line in log)
            {
                ImportLog.Add(line);
            }

            _toasts.Show(importing ? "Aplicación registrada con el baseline v4." : "Aplicación registrada.");
            await _navigation.NavigateToAsync<InventoryViewModel>(vm => vm.SetApp(slug));
        }
        catch (Exception ex)
        {
            _toasts.Show($"Error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Slugify(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (c is ' ' or '-' or '_' && sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        string slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "app" : slug;
    }
}

/// <summary>En qué punto está la lista de repositorios (R3).</summary>
public enum RepoListState
{
    /// <summary>Todavía no se ha pedido.</summary>
    NotLoaded,

    Loading,

    Loaded,

    /// <summary>GitHub no contestó, o no dejó listar. El alta sigue siendo posible a mano.</summary>
    Failed,
}

/// <summary>
/// Un repositorio en el desplegable del alta (R3): su nombre corto, la URL que se guardará, y si
/// ya es una aplicación del hub.
/// </summary>
public sealed class RepoOption
{
    public RepoOption(string name, string url, AppConfig? existing = null)
    {
        Name = name;
        Url = url;
        Existing = existing;
    }

    public string Name { get; }

    /// <summary>La URL entera, que es lo que se guarda. En pantalla solo se enseña el nombre.</summary>
    public string Url { get; }

    /// <summary>La aplicación del hub que ya tiene este repositorio, si la hay.</summary>
    public AppConfig? Existing { get; }

    public bool AlreadyInHub => Existing is not null;

    /// <summary>Lo que se lee en la lista. La marca va escrita, no solo en un color.</summary>
    public string Label => AlreadyInHub ? $"{Name} · ya en el hub" : Name;

    public override string ToString() => Label;
}
