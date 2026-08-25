using System.Collections.ObjectModel;
using System.Windows;
using Atalaya.App.Services;
using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>A unit row in the live session queue (V5).</summary>
public sealed partial class UnitRow : ObservableObject
{
    public required string Path { get; init; }

    [ObservableProperty]
    private string _phase = "en cola";
}

/// <summary>V5 Sesión en vivo (§8): queue, streamed agent text, findings entering, tokens/cost.</summary>
public sealed partial class SessionViewModel : ViewModelBase
{
    private readonly SessionCoordinator _coordinator;
    private readonly ICopilotAgent _agent;
    private CancellationTokenSource? _cts;
    private SessionRequest? _request;

    /// <summary>
    /// Ya se lanzó la auditoría para el <see cref="_request"/> actual. Se rearma en
    /// <see cref="Configure"/>, es decir, una sesión por configuración explícita.
    /// </summary>
    private bool _startedForRequest;

    public SessionViewModel(SessionCoordinator coordinator, ICopilotAgent agent)
    {
        _coordinator = coordinator;
        _agent = agent;
        _coordinator.UnitPhaseChanged += OnUnitPhase;
        _coordinator.FindingReported += OnFinding;
        _coordinator.TextStreamed += OnText;
        _coordinator.UsageUpdated += OnUsage;
    }

    public override string Title => "Sesión en vivo";

    public ObservableCollection<UnitRow> Queue { get; } = new();
    public ObservableCollection<string> Findings { get; } = new();

    [ObservableProperty] private string _streamed = string.Empty;
    [ObservableProperty] private long _inputTokens;
    [ObservableProperty] private long _outputTokens;
    [ObservableProperty] private decimal? _cost;
    [ObservableProperty] private string _costUnit = "(unidad SDK)";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _headerText = string.Empty;

    public void Configure(SessionRequest request, IReadOnlyList<string> displayPaths)
    {
        _request = request;
        _startedForRequest = false;
        HeaderText = $"{request.Mode} · {request.Slug}";
        Queue.Clear();
        foreach (string p in displayPaths)
        {
            Queue.Add(new UnitRow { Path = p });
        }
    }

    /// <summary>
    /// Arranca la sesión al entrar en la página — pero UNA SOLA VEZ por configuración.
    /// <para>
    /// <b>Por qué el guardia.</b> <c>LoadAsync</c> es "recarga la vista" para todas las páginas,
    /// pero en ésta <i>ejecuta trabajo</i>. El tick de polling (§3) llama a
    /// <c>Navigation.Current.LoadAsync()</c> cada vez que un pull trae cambios, y una sesión
    /// termina haciendo commit+push: al minuto siguiente el poll se traía sus PROPIOS cambios y
    /// relanzaba una auditoría entera, en bucle indefinido mientras la página siguiera abierta
    /// (2026-08-25: 7 sesiones sobre CommonStatics.cs a intervalos de 60 s, baseline contaminado).
    /// </para>
    /// </summary>
    public override async Task LoadAsync()
    {
        if (_request is not null && !IsRunning && !_startedForRequest)
        {
            await Start();
        }
    }

    [RelayCommand]
    private async Task Start()
    {
        if (_request is null || IsRunning)
        {
            return;
        }

        // El cerrojo se echa ANTES del primer await. Con el guardia después de
        // CheckAsync, dos disparos casi simultáneos (poll + navegación) pasaban los dos y
        // arrancaban dos sesiones en el mismo segundo.
        IsRunning = true;
        _startedForRequest = true;
        StatusMessage = "Comprobando Copilot…";

        try
        {
            AgentReadiness readiness = await _agent.CheckAsync(CancellationToken.None);
            if (!readiness.Ready)
            {
                StatusMessage = readiness.Message;
                return;
            }

            StatusMessage = "Auditando…";
            _cts = new CancellationTokenSource();
            SessionResult result = await Task.Run(() => _coordinator.RunAsync(_request, _cts.Token));
            // F4: el resumen gana "no verificables" e "incompletas" — pero solo si los hay, y con
            // la causa implícita en el propio texto. Sin números sin causa.
            // F5.1b: una parada también reporta lo que SÍ se guardó — antes decía solo "detenida"
            // y el trabajo hecho parecía perdido, cuando estaba en el hub.
            StatusMessage = (result.Interrupted ? "Sesión detenida; lo auditado queda guardado." : "Sesión completada.")
                + $" Nuevos {result.Counters.New}, confirmados {result.Counters.Confirmed}, "
                + $"resueltos {result.Counters.Resolved}, silenciados respetados {result.Counters.SilencedRespected}."
                + (result.Counters.NoVerificables > 0
                    ? $" {result.Counters.NoVerificables} no verificables (marcados para revisión)."
                    : "")
                // F5.1b: lo que la app NO aplicó tal cual tiene que verse AQUÍ, no solo en el
                // informe. La primera sesión con disputas dijo «confirmados 20» y se calló que
                // una era una discrepancia de criterio: un número sin causa, otra vez.
                + (result.Counters.Disputed > 0
                    ? $" ⚖ {result.Counters.Disputed} disputado(s): el auditor sostiene que nunca fueron defecto; "
                      + "no se han resuelto, los decides tú en Hallazgos."
                    : "")
                + (result.Counters.ResolutionsRefused > 0
                    ? $" ⚠ {result.Counters.ResolutionsRefused} «arreglado» sin evidencia de cambio, "
                      + "degradado(s) a presente."
                    : "")
                + (result.IncompleteUnits > 0
                    ? $" ⚠ {result.IncompleteUnits} unidad(es) incompleta(s): el auditor dejó hallazgos sin veredicto y no se han modificado."
                    : "")
                + (result.ReachedZeroPending ? " Ciclo sin pendientes." : "");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Sesión detenida.";
        }
        catch (CopilotAuthenticationException authEx)
        {
            StatusMessage = authEx.Message; // §6.1 help text instead of a raw SDK error
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _cts?.Cancel();
        StatusMessage = "Deteniendo tras la unidad actual…";
    }

    private void OnUnitPhase(string path, string phase) => OnUi(() =>
    {
        UnitRow? row = Queue.FirstOrDefault(r => r.Path == path);
        if (row is not null)
        {
            row.Phase = phase;
        }
    });

    private void OnFinding(Finding f, string kind) => OnUi(() =>
        Findings.Add($"[{f.Severity}] {f.Title}  ({kind})"));

    private void OnText(string t) => OnUi(() =>
    {
        Streamed += t;
        if (Streamed.Length > 8000)
        {
            Streamed = Streamed[^8000..];
        }
    });

    private void OnUsage(long input, long output, decimal? cost, string? costUnit) => OnUi(() =>
    {
        InputTokens = input;
        OutputTokens = output;
        Cost = cost;
        CostUnit = string.IsNullOrWhiteSpace(costUnit) ? "(unidad SDK)" : costUnit!;
    });

    private static void OnUi(Action action)
    {
        Application? app = Application.Current;
        if (app is null)
        {
            action();
        }
        else
        {
            app.Dispatcher.Invoke(action);
        }
    }
}
