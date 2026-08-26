using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using Atalaya.App.Services;
using Atalaya.Domain;
using Atalaya.Domain.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>
/// V5 Sesion en vivo (§8), rediseñada en F5.2.
/// <para>
/// Es una VISTA sobre <see cref="LiveSessionService"/>, no la dueña del estado. Antes el estado
/// vivia aqui y este view-model es <c>Transient</c>: navegar fuera y volver lo perdia todo aunque
/// la auditoria siguiera corriendo. Y peor, <c>LoadAsync</c> LANZABA la sesion, asi que navegar
/// ejecutaba trabajo (D-085). Ahora <c>LoadAsync</c> no ejecuta nada: la vista se reconstruye sola
/// porque el estado esta en el servicio.
/// </para>
/// </summary>
public sealed partial class SessionViewModel : ViewModelBase
{
    private readonly LiveSessionService _live;
    private readonly DispatcherTimer? _clock;

    /// <summary>
    /// Para el atajo «Elegir modelo» del panel de fallo (F5.15). Opcional: los tests que solo miran
    /// el estado de V5 no montan la navegacion.
    /// </summary>
    private readonly NavigationService? _navigation;

    public SessionViewModel(LiveSessionService live, NavigationService? navigation = null)
    {
        _live = live;
        _navigation = navigation;
        _live.Changed += OnLiveChanged;
        _live.PropertyChanged += (_, _) => OnLiveChanged();

        if (System.Windows.Application.Current is not null)
        {
            _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clock.Tick += (_, _) => OnPropertyChanged(nameof(ElapsedText));
            _clock.Start();
        }
    }

    public override string Title => Live.HasFailed && !Live.IsRunning
        ? "Sesion fallida"
        : Live.HasFinished && !Live.IsRunning ? "Ultima sesion" : "Sesion en vivo";

    /// <summary>El estado real, enlazado directamente por la vista.</summary>
    public LiveSessionService Live => _live;

    public ObservableCollection<UnitProgress> Units => _live.Units;

    public ObservableCollection<Finding> Findings => _live.Findings;

    public ObservableCollection<SummaryLine> Summary => _live.Summary;

    /// <summary>Autoscroll activo. Se apaga solo si el usuario sube a leer.</summary>
    [ObservableProperty]
    private bool _autoScroll = true;

    public string ElapsedText
    {
        get
        {
            TimeSpan e = _live.Elapsed;
            return e.TotalHours >= 1
                ? $"{(int)e.TotalHours}h {e.Minutes:00}m {e.Seconds:00}s"
                : $"{e.Minutes:00}:{e.Seconds:00}";
        }
    }

    public string ProgressText => _live.UnitCount == 0
        ? "Sin unidades"
        : $"Unidad {Math.Max(1, _live.UnitIndex)} de {_live.UnitCount}";

    public string CostText => _live.Cost is { } c
        ? $"{_live.Calls} llamadas · coste {c:0.##} {_live.CostUnit}"
        : $"{_live.Calls} llamadas · coste no informado por el SDK";

    public string TokensText =>
        $"tokens {_live.InputTokens:N0} in / {_live.OutputTokens:N0} out"
        + (_live.CacheReadTokens > 0 ? $" · cache {_live.CacheReadTokens:N0}" : "");

    public string PerUnitText => _live.CostPerUnit is { } c ? $"media {c:0.##}/unidad" : string.Empty;

    public int CriticalCount => Findings.Count(f => f.Severity == Severity.Critica);

    public int HighCount => Findings.Count(f => f.Severity == Severity.Alta);

    public int MediumCount => Findings.Count(f => f.Severity == Severity.Media);

    public int LowCount => Findings.Count(f => f.Severity == Severity.Baja);

    /// <summary>La pantalla de cierre sustituye a la linea fugaz de estado cuando termina.</summary>
    public bool ShowSummary => !_live.IsRunning && _live.HasFinished && Summary.Count > 0;

    /// <summary>
    /// El panel de fallo (F5.15). Es el hueco por el que se coló el zombi del 2026-08-26: la unica
    /// superficie que enseñaba <c>StatusMessage</c> colgaba de <see cref="ShowSummary"/>, o sea de
    /// <c>HasFinished</c>, que en un fallo de arranque es false. El mensaje existia y no se pintaba
    /// en ninguna parte.
    /// </summary>
    public bool ShowFailure => !_live.IsRunning && _live.HasFailed;

    public string FailureMessage => _live.FailureMessage;

    /// <summary>El fallo se cura eligiendo otro modelo: la vista ofrece el atajo.</summary>
    public bool FailureOffersModelChange => _live.FailureOffersModelChange;

    public bool IsRunning => _live.IsRunning;

    /// <summary>
    /// Navegar NO ejecuta trabajo. La vista se repinta desde el estado del servicio, que es lo que
    /// hace que volver a V5 a mitad de sesion enseñe la sesion al dia.
    /// </summary>
    public override Task LoadAsync()
    {
        OnLiveChanged();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Stop() => _live.Stop();

    /// <summary>
    /// Lleva a Ajustes, que es donde se elige el modelo. Es la mitad accionable del mensaje de
    /// fallo: decir «elige otro en Ajustes» sin ofrecer el camino es dejar el trabajo a medias.
    /// </summary>
    [RelayCommand]
    private async Task FixModel()
    {
        if (_navigation is not null)
        {
            await _navigation.NavigateToAsync<SettingsViewModel>();
        }
    }

    [RelayCommand]
    private void BackToBottom() => AutoScroll = true;

    [RelayCommand]
    private void ToggleLine(SummaryLine? line)
    {
        if (line is { HasDetails: true })
        {
            line.IsExpanded = !line.IsExpanded;
        }
    }

    /// <summary>Abre el informe markdown de la sesion con la aplicacion asociada.</summary>
    [RelayCommand]
    private void OpenReport()
    {
        if (string.IsNullOrWhiteSpace(_live.ReportPath) || !File.Exists(_live.ReportPath))
        {
            _live.StatusMessage = "El informe todavia no esta en disco.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_live.ReportPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _live.StatusMessage = $"No se pudo abrir el informe: {ex.Message}";
        }
    }

    private void OnLiveChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(CostText));
        OnPropertyChanged(nameof(TokensText));
        OnPropertyChanged(nameof(PerUnitText));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(ShowSummary));
        OnPropertyChanged(nameof(ShowFailure));
        OnPropertyChanged(nameof(FailureMessage));
        OnPropertyChanged(nameof(FailureOffersModelChange));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CriticalCount));
        OnPropertyChanged(nameof(HighCount));
        OnPropertyChanged(nameof(MediumCount));
        OnPropertyChanged(nameof(LowCount));
    }
}
