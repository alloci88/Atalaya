using System.Text;
using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Ingestion;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
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

    public OnboardingViewModel(
        HubContext hub,
        InventoryScanner scanner,
        MachineConfigStore machines,
        NavigationService navigation,
        FindingIngestionService ingestion,
        ToastCenter toasts,
        CloneLinkService links,
        LinkCloneFlow linkFlow)
    {
        _hub = hub;
        _scanner = scanner;
        _machines = machines;
        _navigation = navigation;
        _ingestion = ingestion;
        _toasts = toasts;
        _links = links;
        _linkFlow = linkFlow;
    }

    public override string Title => "Nueva aplicación";

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _clonePath = string.Empty;
    [ObservableProperty] private TechStack _detectedStack = TechStack.Unknown;

    [ObservableProperty] private string _repoUrl = string.Empty;

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
        IsBusy = true;
        _toasts.Show("Escaneando y registrando…");
        try
        {
            await Task.Run(() =>
            {
                if (DetectedStack == TechStack.Unknown)
                {
                    DetectedStack = StackDetector.Detect(ClonePath);
                }

                var app = new AppConfig
                {
                    Slug = slug,
                    Name = Name.Trim(),
                    RepoUrl = RepoUrl.Trim(),
                    Stack = DetectedStack,
                    CurrentCycle = 1,
                };

                ScanOutput scan = _scanner.Scan(ClonePath, app, 1);
                app.Stack = scan.Stack;

                _hub.Store.WriteApp(app);
                _hub.Store.WriteInventory(slug, scan.Inventory);

                // Auto "unit too large" findings enter through the normal ingestion pipeline.
                string commit = GitInfo.HeadSha(ClonePath);
                var stamp = new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Lotes, commit, _hub.ResolveIdentity().Name);
                foreach (SubmittedFinding large in scan.LargeUnitFindings)
                {
                    _ingestion.Create(large, slug, AuditMode.Lotes, stamp);
                }

                _machines.SetClonePath(slug, ClonePath);
                _hub.Sync?.CommitAndPush($"app: onboard {slug} ({scan.Stack})");
            });

            _toasts.Show("Aplicación registrada.");
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
