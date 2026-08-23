using System.Diagnostics;
using System.Windows;
using Atalaya.App.Services;
using Atalaya.Storage.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>What the Cuenta page is showing right now.</summary>
public enum AccountMode
{
    /// <summary>No account: the welcome / connect screen (D2, D4).</summary>
    Disconnected,

    /// <summary>Device flow running: the big user code and the countdown.</summary>
    Authorizing,

    /// <summary>Connected: profile, the four checks, last sync, disconnect.</summary>
    Connected,
}

/// <summary>
/// Cuenta (D2): the single place where connection lives, split out of Ajustes. One button starts
/// the GitHub device flow; the resulting token feeds git, Copilot and the commit identity, and the
/// chained verification reports each of the four steps live with an actionable diagnosis.
/// </summary>
public sealed partial class AccountViewModel : ViewModelBase
{
    private readonly GitHubAccountService _account;
    private readonly GitHubDeviceFlow _deviceFlow;
    private readonly GitHubApiClient _api;
    private readonly DeployConfig _deploy;
    private readonly HubContext _hub;
    private readonly NavigationService _navigation;
    private CancellationTokenSource? _cts;

    public AccountViewModel(
        GitHubAccountService account,
        GitHubDeviceFlow deviceFlow,
        GitHubApiClient api,
        DeployConfig deploy,
        HubContext hub,
        ConnectionChecker checker,
        NavigationService navigation)
    {
        _account = account;
        _deviceFlow = deviceFlow;
        _api = api;
        _deploy = deploy;
        _hub = hub;
        _navigation = navigation;
        Checker = checker;
        Sync();
    }

    public override string Title => "Cuenta";

    /// <summary>The four live check rows, bound directly.</summary>
    public ConnectionChecker Checker { get; }

    [ObservableProperty] private AccountMode _mode = AccountMode.Disconnected;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _userCode = string.Empty;
    [ObservableProperty] private string _verificationUri = GitHubDeviceFlow.DefaultVerificationUri;
    [ObservableProperty] private string _countdown = string.Empty;
    [ObservableProperty] private string _login = string.Empty;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string? _avatarUrl;
    [ObservableProperty] private string _lastSync = "nunca";
    [ObservableProperty] private bool _needsReconnect;
    [ObservableProperty] private string _syncState = string.Empty;
    [ObservableProperty] private string _syncError = string.Empty;

    /// <summary>Where the hub clone lives on this machine — the first thing to check when sync misbehaves.</summary>
    public string HubClonePath => _hub.HubPaths.Root;

    public bool IsDisconnected => Mode == AccountMode.Disconnected;

    public bool IsAuthorizing => Mode == AccountMode.Authorizing;

    public bool IsConnected => Mode == AccountMode.Connected;

    /// <summary>Empty when the deployment names no organization, so the UI can hide the row.</summary>
    public string OrganizationLogin => _deploy.OrganizationLogin;

    public bool ShowOrganization => _deploy.ChecksOrgMembership;

    /// <summary>False when the administrator has not registered the OAuth App yet.</summary>
    public bool CanConnect => _deploy.HasClientId;

    public string ConnectBlockedMessage => ConnectionHelp.NoClientId;

    partial void OnModeChanged(AccountMode value)
    {
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(IsAuthorizing));
        OnPropertyChanged(nameof(IsConnected));
    }

    public override async Task LoadAsync()
    {
        Sync();
        if (_account.IsConnected && Checker.Steps.All(s => s.State == CheckState.Pending))
        {
            await CheckConnection();
        }
    }

    /// <summary>Mirrors the account service into the bound properties.</summary>
    public void Sync()
    {
        GitHubAccount? account = _account.Current;
        if (account is null)
        {
            Mode = AccountMode.Disconnected;
            Login = string.Empty;
            DisplayName = string.Empty;
            AvatarUrl = null;
        }
        else
        {
            if (Mode != AccountMode.Authorizing)
            {
                Mode = AccountMode.Connected;
            }

            Login = account.Login;
            DisplayName = account.DisplayName;
            AvatarUrl = account.AvatarUrl;
        }

        NeedsReconnect = _account.NeedsReconnect;
        LastSync = _hub.LastSync is { } t ? t.ToLocalTime().ToString("g") : "nunca";
        SyncState = _hub.Health switch
        {
            SyncHealth.Green => "sincronizado",
            SyncHealth.Amber => _hub.IsCloned ? "pendiente de sincronizar" : "sin clonar",
            _ => "con errores",
        };
        SyncError = _hub.LastSyncError ?? string.Empty;
    }

    [RelayCommand]
    private async Task Connect()
    {
        if (!CanConnect)
        {
            StatusMessage = ConnectionHelp.NoClientId;
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        Mode = AccountMode.Authorizing;
        StatusMessage = "Pidiendo un código a GitHub…";
        UserCode = string.Empty;
        Countdown = string.Empty;

        try
        {
            DeviceCodeGrant grant = await _deviceFlow.RequestCodeAsync(_deploy.GitHubClientId, ct);
            UserCode = grant.UserCode;
            VerificationUri = grant.VerificationUri;
            StatusMessage = "Introduce el código en github.com y autoriza «Atalaya».";

            var progress = new Progress<DeviceFlowProgress>(p =>
                Countdown = p.Remaining > TimeSpan.Zero
                    ? $"Caduca en {p.Remaining:mm\\:ss}"
                    : "Caducado");

            string token = await _deviceFlow.WaitForTokenAsync(_deploy.GitHubClientId, grant, progress, ct);

            StatusMessage = "Autorizado. Leyendo tu perfil…";
            GitHubUser user = await _api.GetCurrentUserAsync(token, ct);
            _account.Connect(token, user);

            Mode = AccountMode.Connected;
            Sync();
            StatusMessage = string.Empty;
            await CheckConnection();

            // First run (D4): connect → chained verification → hub cloned → land on V1 Portfolio.
            // Zero further questions. If something failed, stay here showing which step and why.
            if (Checker.Steps.All(s => s.State is CheckState.Ok or CheckState.Skipped))
            {
                await _navigation.NavigateToAsync<PortfolioViewModel>();
            }
        }
        catch (OperationCanceledException)
        {
            Mode = _account.IsConnected ? AccountMode.Connected : AccountMode.Disconnected;
            StatusMessage = "Conexión cancelada.";
        }
        catch (DeviceFlowException ex)
        {
            Mode = _account.IsConnected ? AccountMode.Connected : AccountMode.Disconnected;
            StatusMessage = ex.Message;
        }
        catch (GitHubApiException ex)
        {
            Mode = _account.IsConnected ? AccountMode.Connected : AccountMode.Disconnected;
            StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            Mode = _account.IsConnected ? AccountMode.Connected : AccountMode.Disconnected;
            StatusMessage = $"No se pudo completar la conexión: {ex.Message}";
        }
        finally
        {
            Countdown = string.Empty;
        }
    }

    [RelayCommand]
    private void CancelConnect() => _cts?.Cancel();

    [RelayCommand]
    private void CopyCode()
    {
        if (TryCopy(UserCode))
        {
            StatusMessage = "Código copiado al portapapeles.";
        }
    }

    /// <summary>Opens github.com/login/device AND copies the code, so there is nothing to retype.</summary>
    [RelayCommand]
    private void OpenVerificationPage()
    {
        TryCopy(UserCode);
        try
        {
            Process.Start(new ProcessStartInfo(VerificationUri) { UseShellExecute = true });
            StatusMessage = "Código copiado. Pégalo en la página que se ha abierto.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Abre {VerificationUri} manualmente ({ex.Message}).";
        }
    }

    [RelayCommand]
    private void OpenHelp(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            StatusMessage = $"Abre {url} manualmente.";
        }
    }

    /// <summary>Re-runs the four checks. Absorbs the old "Comprobar Copilot" button (D2).</summary>
    [RelayCommand]
    private async Task CheckConnection()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Comprobando conexión…";
        try
        {
            ConnectionCheckResult result = await Checker.RunAsync(CancellationToken.None);
            StatusMessage = result.AllOk ? "Todo listo." : result.FirstProblem ?? "Revisa los pasos marcados.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error comprobando la conexión: {ex.Message}";
        }
        finally
        {
            Sync();
            IsBusy = false;
        }
    }

    /// <summary>
    /// Pulls the hub right now, cloning it first if needed. Makes the sync state diagnosable from
    /// the UI instead of from the log.
    /// </summary>
    [RelayCommand]
    private async Task SyncNow()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Sincronizando con el hub…";
        try
        {
            await Task.Run(_hub.EnsureHub);
            StatusMessage = _hub.Health == SyncHealth.Green
                ? $"Hub sincronizado ({_hub.LastSync?.ToLocalTime():g})."
                : _hub.LastSyncError is { } error
                    ? $"No se pudo sincronizar: {error}"
                    : "No se pudo sincronizar con el hub.";
        }
        catch (Exception ex)
        {
            _account.NoteFailure(ex);
            StatusMessage = ConnectionChecker.DescribeHubFailure(ex, Login).Detail;
        }
        finally
        {
            Sync();
            IsBusy = false;
        }
    }

    /// <summary>Opens the hub clone in the file explorer.</summary>
    [RelayCommand]
    private void OpenHubFolder()
    {
        try
        {
            Directory.CreateDirectory(HubClonePath);
            Process.Start(new ProcessStartInfo(HubClonePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"No se pudo abrir {HubClonePath} ({ex.Message}).";
        }
    }

    /// <summary>
    /// Forgets the account: deletes <c>auth.dat</c> and drops the token. The Copilot runtime and
    /// the git credentials are keyed on that token, so both are torn down and rebuilt on their
    /// next use. Deliberately does NOT delete the hub clone: reconnecting with another account
    /// must not re-clone or corrupt it.
    /// </summary>
    [RelayCommand]
    private void Disconnect()
    {
        _cts?.Cancel();
        _account.Disconnect();
        foreach (ConnectionStep step in Checker.Steps)
        {
            step.Reset(step.Title);
        }

        Sync();
        StatusMessage = "Cuenta desconectada. El clon local del hub se conserva.";
    }

    private static bool TryCopy(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
