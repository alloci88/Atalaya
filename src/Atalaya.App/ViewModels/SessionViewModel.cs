using System.Collections.ObjectModel;
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
public sealed partial class SessionViewModel : ViewModelBase, IAppScoped
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
            _clock.Tick += (_, _) =>
            {
                OnPropertyChanged(nameof(ElapsedText));
                OnPropertyChanged(nameof(Footer));
            };
            _clock.Start();
        }
    }

    /// <summary>
    /// El título de la pantalla. <b>Con tildes</b> (F26-B revisión, D-983): el raíl, el MANUAL y
    /// la miga escriben «Última sesión» y aquí salía «Ultima sesion», así que la misma pantalla se
    /// llamaba de dos formas según dónde se leyera su nombre.
    /// </summary>
    public override string Title => Live.HasFailed && !Live.IsRunning
        ? "Sesión fallida"
        : Live.HasFinished && !Live.IsRunning ? "Última sesión" : "Sesión en vivo";

    /// <summary>El estado real, enlazado directamente por la vista.</summary>
    public LiveSessionService Live => _live;

    /// <summary>F26 §A.</summary>
    public override string RailKey => "session";

    public override bool BelongsToApp => true;

    /// <inheritdoc />
    public string AppSlug => _live.AppSlug;

    /// <inheritdoc />
    public string AppLabel => _live.AppSlug;

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

    /// <summary>
    /// El pie dice del coste EXACTAMENTE lo que dirá el informe de esta sesión (F16 §B). Decía
    /// «coste no informado por el SDK», que era una frase acuñada para Copilot —y que además nombra
    /// un SDK que con Claude Code no existe— mientras el informe de la misma sesión decía «tarifa
    /// no configurada». Un solo criterio, en <see cref="CreditText.OfSession"/>.
    /// </summary>
    public string CostText => CreditText.SessionFooter(
        _live.Calls, _live.InputTokens, _live.OutputTokens,
        _live.CacheReadTokens, _live.CacheWriteTokens, _live.CostResult, _live.Provider);

    public string PerUnitText => _live.CostPerUnit is { } c ? $"media {c:0.##}/unidad" : string.Empty;

    /// <summary>
    /// El pie entero, por segmentos (F17-RETOQUE): progreso y tiempo, que no ceden; y el consumo
    /// —llamadas, coste, tokens— por el criterio común, con la media por unidad la última en
    /// quedarse. Aquí ya no hay un segundo bloque de tokens: el que había («tokens X in / Y out»)
    /// era el resto de antes de F16-RETOQUE y repetía, peor formateado, lo que el criterio ya dice.
    /// </summary>
    public IReadOnlyList<FooterSegment> Footer
    {
        get
        {
            var segments = new List<FooterSegment>
            {
                FooterSegment.Of(ProgressText, bold: true),
            };

            // R2 §1 — la palabra va JUNTO A LA UNIDAD y no cede nunca el sitio: es lo que explica
            // que esta sesión cueste el triple y pueda traer duplicados. Una palabra ocupa poco y,
            // si se abreviara hasta desaparecer, desaparecería justo en la pantalla estrecha en la
            // que el coste se lee de reojo.
            if (_live.Exhaustive)
            {
                segments.Add(FooterSegment.Of(AuditModes.Exhaustive, opacity: 0.85));
            }

            segments.Add(FooterSegment.Of(ElapsedText, opacity: 0.85));
            segments.AddRange(CreditText.UsageSegments(
                _live.Calls, _live.InputTokens, _live.OutputTokens,
                _live.CacheReadTokens, _live.CacheWriteTokens, _live.CostResult, _live.Provider,
                _live.Budget, _live.Turns));
            if (PerUnitText.Length > 0)
            {
                segments.Add(FooterSegment.Of(PerUnitText, priority: 3, opacity: 0.7));
            }

            return segments;
        }
    }

    public int CriticalCount => Findings.Count(f => f.Severity == Severity.Critica);

    public int HighCount => Findings.Count(f => f.Severity == Severity.Alta);

    public int MediumCount => Findings.Count(f => f.Severity == Severity.Media);

    public int LowCount => Findings.Count(f => f.Severity == Severity.Baja);

    /// <summary>
    /// Los cuatro contadores del panel «Hallazgos», como pastillas del sistema (UI-0010).
    /// <para>
    /// Estaban escritos a mano en el XAML, cuatro <c>Border</c> con su color y su rótulo dentro
    /// —«Crítica 2», con la cifra detrás—, y por eso podían salirse del sistema sin que nadie lo
    /// notara: eran relleno vivo con una tinta que no resolvía. Como <see cref="SeverityChip"/>
    /// llevan su nivel y su recuento, la pastilla del sistema se encarga del color, del rótulo en
    /// plural y de apagarse cuando el recuento es cero.
    /// </para>
    /// <para>
    /// Los cuatro se pintan SIEMPRE, también a cero: son el marcador de la sesión y desaparecer
    /// haría que la fila cambiara de forma cada vez que aparece un hallazgo.
    /// </para>
    /// </summary>
    public IReadOnlyList<SeverityChip> SeverityChips => Enum.GetValues<Severity>()
        .Select(s => new SeverityChip(s, Findings.Count(f => f.Severity == s)))
        .ToList();

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

    /// <summary>El error del proveedor tal cual (BUGFIX-CUOTA). Copiable, y plegado por defecto.</summary>
    public string FailureDetail => _live.FailureDetail;

    /// <summary>Hay crudo que enseñar. Sin esto, «Ver detalle» aparecería para abrir un hueco vacío.</summary>
    public bool HasFailureDetail => _live.HasFailureDetail;

    /// <summary>
    /// El crudo empieza PLEGADO (BUGFIX-CUOTA). Un error del proveedor puede ocupar varias líneas, y
    /// desplegado por defecto empujaba —o tapaba— el resto de la pantalla: justo el defecto que este
    /// parte venía a arreglar. Se abre a un clic, y quien lo abre es porque va a copiarlo.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailToggleLabel))]
    private bool _isFailureDetailExpanded;

    public string DetailToggleLabel => IsFailureDetailExpanded ? "Ocultar detalle" : "Ver detalle";

    public bool IsRunning => _live.IsRunning;

    /// <summary>
    /// «Cerrar» solo existe en lo TERMINAL (BUGFIX-CIERRE). Mientras la sesión corre lo que hay es
    /// «Detener», que es otra cosa: archivar una sesión viva la dejaría corriendo sin ninguna
    /// pantalla que la enseñe, que es el zombi que F5.15 vino a matar.
    /// </summary>
    public bool CanClose => !_live.IsRunning && _live.HasSession;

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
    /// Archiva la pantalla y vuelve al Portafolio (BUGFIX-CIERRE). Una sesión fallida se quedaba
    /// fija en el rail, sin salida, ocupando sitio para siempre.
    /// <para>
    /// No borra nada: el registro de la sesión, sus hallazgos y su informe siguen en el hub, y el
    /// informe se lee donde se leen todos.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task Close()
    {
        if (!_live.Close())
        {
            return;
        }

        if (_navigation is not null)
        {
            await _navigation.NavigateToAsync<PortfolioViewModel>();
        }
    }

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
    private void ToggleFailureDetail() => IsFailureDetailExpanded = !IsFailureDetailExpanded;

    /// <summary>
    /// Al portapapeles, que es a donde va este texto: a un correo para quien administre la
    /// organización. Seleccionar a mano un bloque de varias líneas dentro de un banner es
    /// exactamente el gesto que la gente no hace, así que hay botón.
    /// </summary>
    [RelayCommand]
    private void CopyFailure()
    {
        string text = string.IsNullOrWhiteSpace(FailureDetail)
            ? FailureMessage
            : $"{FailureMessage}\n\n{FailureDetail}";

        try
        {
            System.Windows.Clipboard.SetText(text);
            _live.StatusMessage = "Error copiado al portapapeles.";
        }
        catch (Exception)
        {
            // El portapapeles lo puede tener tomado otro proceso. No es motivo para tumbar nada:
            // el texto sigue delante y se puede seleccionar a mano.
            _live.StatusMessage = "No se ha podido copiar: el portapapeles está ocupado.";
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

    /// <summary>
    /// Abre el informe de esta sesion en la vista Informes (F6.3).
    /// <para>
    /// Antes lo lanzaba al bloc de notas del sistema con un <c>Process.Start</c>. Eran dos formas
    /// distintas de leer lo mismo —una dentro y otra fuera— y la de fuera enseñaba markdown crudo.
    /// Ahora el informe se lee donde se leen todos, con sus tablas pintadas y con su boton de
    /// descarga para quien de verdad quiera el fichero.
    /// </para>
    /// </summary>
    [RelayCommand]
    private Task OpenReport()
    {
        if (string.IsNullOrWhiteSpace(_live.ReportPath) || !File.Exists(_live.ReportPath))
        {
            _live.StatusMessage = "El informe todavia no esta en disco.";
            return Task.CompletedTask;
        }

        if (_navigation is null)
        {
            return Task.CompletedTask;
        }

        return _navigation.NavigateToAsync<ReportsViewModel>(
            vm => vm.ShowReport(_live.AppSlug, _live.SessionId));
    }

    private void OnLiveChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(CostText));
        OnPropertyChanged(nameof(PerUnitText));
        OnPropertyChanged(nameof(Footer));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(ShowSummary));
        OnPropertyChanged(nameof(ShowFailure));
        OnPropertyChanged(nameof(FailureMessage));
        OnPropertyChanged(nameof(FailureOffersModelChange));
        OnPropertyChanged(nameof(FailureDetail));
        OnPropertyChanged(nameof(HasFailureDetail));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanClose));
        OnPropertyChanged(nameof(CriticalCount));
        OnPropertyChanged(nameof(HighCount));
        OnPropertyChanged(nameof(MediumCount));
        OnPropertyChanged(nameof(LowCount));
        OnPropertyChanged(nameof(SeverityChips));
    }
}
