using System.Collections.ObjectModel;
using System.Text;
using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Ingestion;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using Atalaya.Storage;
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
        IFolderPicker picker)
    {
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
    }

    public override string Title => "Nueva aplicación";

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _clonePath = string.Empty;
    [ObservableProperty] private TechStack _detectedStack = TechStack.Unknown;

    [ObservableProperty] private string _repoUrl = string.Empty;

    /// <summary>
    /// La carpeta <c>CodeAudit/</c> del sistema v4, si la hay. Se rellena sola al elegir el clon;
    /// también se puede señalar a mano cuando el baseline vive fuera del repo.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBaseline))]
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

    partial void OnClonePathChanged(string value) => DetectBaseline();

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

    /// <summary>Escribir la URL ya basta para saber que la app existe: no hace falta llegar al final.</summary>
    partial void OnRepoUrlChanged(string value) => DetectExistingApp();

    /// <summary>La app del hub que YA tiene este repo, si la hay.</summary>
    private AppConfig? _existing;

    /// <summary>
    /// El aviso de redirección (F5.8 §2). Vive como texto y no como toast porque no es el
    /// resultado de una acción: es una condición del formulario que dura mientras dure la URL.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDuplicate))]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    private string _duplicateNotice = string.Empty;

    public bool IsDuplicate => DuplicateNotice.Length > 0;

    /// <summary>Dar de alta se apaga mientras el repo elegido sea el de una app que ya existe.</summary>
    public bool CanCreate => !IsDuplicate;

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
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(RepoUrl) || !Directory.Exists(ClonePath))
        {
            _toasts.Show("Rellena nombre, URL del repo y una ruta de clon válida.");
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
            IReadOnlyList<string> log = await Task.Run(() =>
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

                ScanOutput scan = _scanner.Scan(ClonePath, app, app.CurrentCycle);
                app.Stack = scan.Stack;
                _hub.Store.WriteApp(app);

                // 3) El inventario del ciclo vigente sale del CÓDIGO que hay en el clon, pero
                //    arrastrando el estado de lo que el baseline daba por auditado: es la misma
                //    reconciliación del re-escaneo (D-302), no una segunda escrita aparte.
                InventoryCycle? previous = _hub.Store.TryReadInventory(slug, app.CurrentCycle);
                InventoryCycle inventory = previous is null
                    ? scan.Inventory
                    : Rescanner.Reconcile(previous, scan.Inventory).Merged;
                _hub.Store.WriteInventory(slug, inventory);

                // Auto "unit too large" findings enter through the normal ingestion pipeline.
                string commit = GitInfo.HeadSha(ClonePath);
                var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, commit, _hub.ResolveIdentity().Name);
                foreach (SubmittedFinding large in scan.LargeUnitFindings)
                {
                    _ingestion.Create(large, slug, AuditMode.Lotes, stamp);
                }

                _machines.SetClonePath(slug, ClonePath);
                _hub.Sync?.CommitAndPush(importing
                    ? $"app: onboard {slug} ({scan.Stack}) + import v4"
                    : $"app: onboard {slug} ({scan.Stack})");
                return importLog;
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
