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

    public OnboardingViewModel(
        HubContext hub,
        InventoryScanner scanner,
        MachineConfigStore machines,
        NavigationService navigation,
        FindingIngestionService ingestion,
        ToastCenter toasts)
    {
        _hub = hub;
        _scanner = scanner;
        _machines = machines;
        _navigation = navigation;
        _ingestion = ingestion;
        _toasts = toasts;
    }

    public override string Title => "Nueva aplicación";

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _repoUrl = string.Empty;
    [ObservableProperty] private string _clonePath = string.Empty;
    [ObservableProperty] private TechStack _detectedStack = TechStack.Unknown;

    [RelayCommand]
    private void Detect()
    {
        if (!Directory.Exists(ClonePath))
        {
            _toasts.Show("La ruta del clon no existe.");
            return;
        }

        DetectedStack = StackDetector.Detect(ClonePath);
        _toasts.Show($"Stack detectado: {DetectedStack}.");
    }

    [RelayCommand]
    private async Task Create()
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(RepoUrl) || !Directory.Exists(ClonePath))
        {
            _toasts.Show("Rellena nombre, URL del repo y una ruta de clon válida.");
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
