using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Atalaya.App.Services;
using Atalaya.Domain.Ids;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>Un cambio pendiente de una sesión anterior, listo para descartar o dar por bueno.</summary>
public sealed record PendingFixRow(FixSnapshotSet Set)
{
    public string Headline =>
        $"{Set.FindingAlias ?? "arreglo"} · {Set.Slug} · {Set.StartedUtc.ToLocalTime():dd/MM/yyyy HH:mm}";

    public string Detail =>
        $"{Set.Entries.Count} fichero(s) sin commitear en {Set.CloneRoot}: "
        + string.Join(", ", Set.Files.Take(4))
        + (Set.Entries.Count > 4 ? $" …y {Set.Entries.Count - 4} más" : string.Empty);
}

/// <summary>
/// V8 «Arreglo asistido» (F6.9 §4): la conversación con el agente y el diff de lo que va tocando.
/// <para>
/// Es una VISTA sobre <see cref="LiveFixService"/>, igual que V5 lo es sobre la sesión de
/// auditoría y por la misma razón: este view-model es <c>Transient</c>, así que si el estado
/// viviera aquí, salir a mirar la ficha del hallazgo y volver dejaría la pantalla vacía con el
/// agente todavía escribiendo. <see cref="LoadAsync"/> NO ejecuta trabajo: navegar nunca lanza
/// nada (D-085).
/// </para>
/// </summary>
public sealed partial class AssistedFixViewModel : ViewModelBase
{
    private readonly LiveFixService _fix;
    private readonly NavigationService? _navigation;
    private readonly ToastCenter _toasts;
    private readonly EditorLauncher? _editor;
    private readonly IFixDiscardConfirmer _confirmer;
    private readonly DispatcherTimer? _clock;

    public AssistedFixViewModel(
        LiveFixService fix,
        ToastCenter toasts,
        IFixDiscardConfirmer confirmer,
        NavigationService? navigation = null,
        EditorLauncher? editor = null)
    {
        _fix = fix;
        _toasts = toasts;
        _confirmer = confirmer;
        _navigation = navigation;
        _editor = editor;

        _fix.Changed += OnFixChanged;
        _fix.PropertyChanged += (_, _) => OnFixChanged();

        if (Application.Current is not null)
        {
            _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clock.Tick += (_, _) => OnPropertyChanged(nameof(ElapsedText));
            _clock.Start();
        }
    }

    public override string Title => "Arreglo asistido";

    /// <summary>El estado real, enlazado directamente por la vista.</summary>
    public LiveFixService Fix => _fix;

    public ObservableCollection<FixEntry> Conversation => _fix.Conversation;

    public ObservableCollection<FixFileChange> Files => _fix.Files;

    public CommitSuggestion Commit => _fix.Commit;

    /// <summary>Autoscroll de la conversación; se apaga solo si el usuario sube a leer.</summary>
    [ObservableProperty]
    private bool _autoScroll = true;

    /// <summary>Lo que el usuario está escribiendo para dirigir al agente.</summary>
    [ObservableProperty]
    private string _draft = string.Empty;

    /// <summary>Cambios de sesiones anteriores que siguen en el clon sin cerrar.</summary>
    public ObservableCollection<PendingFixRow> Pending { get; } = new();

    // ------------------------------------------------------------------ lo que la vista lee

    public bool IsRunning => _fix.IsRunning;

    public bool IsPaused => _fix.IsPaused;

    public bool HasSession => _fix.HasSession;

    public bool ShowFailure => !_fix.IsRunning && _fix.HasFailed;

    public bool ShowClosing => !_fix.IsRunning && _fix.HasFinished;

    public bool ShowEmpty => !_fix.HasSession;

    public string FailureMessage => _fix.FailureMessage;

    public bool FailureOffersModelChange => _fix.FailureOffersModelChange;

    /// <summary>El error del proveedor tal cual (BUGFIX-CUOTA). Copiable, y plegado por defecto.</summary>
    public string FailureDetail => _fix.FailureDetail;

    public bool HasFailureDetail => _fix.HasFailureDetail;

    /// <summary>Plegado de salida: un error largo del proveedor no puede empujar la vista.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailToggleLabel))]
    private bool _isFailureDetailExpanded;

    public string DetailToggleLabel => IsFailureDetailExpanded ? "Ocultar detalle" : "Ver detalle";

    /// <summary>
    /// Lo que se le dice al usuario sobre su clon cuando el arreglo falla. NO es una frase fija:
    /// si el agente ya había escrito antes del corte, decir «tu clon no se ha tocado» sería mentir
    /// justo cuando importa saberlo.
    /// </summary>
    public string FailureCloneNote => Files.Count == 0
        ? "Tu clon no se ha tocado. El generador de prompt de arreglo de la ficha sigue disponible."
        : $"El agente ya había modificado {Files.Count} fichero(s) antes del corte: revísalos en "
          + "«Cambios en tu clon» y usa «Descartar todo» si quieres dejarlo como estaba.";

    public string PauseLabel => _fix.IsPaused ? "Continuar" : "Pausar";

    public string HeaderText => _fix.FindingAlias.Length == 0
        ? string.Empty
        : $"{_fix.FindingAlias} · {_fix.AppName}";

    public string SubHeaderText => _fix.FindingTitle;

    /// <summary>
    /// «Volver al hallazgo (OPT-0002)» (H9.1 §1). Terminada una sesión —o descartada— el hallazgo
    /// que la originó no tenía camino de vuelta: había que ir a Hallazgos y buscarlo. El alias va
    /// en el rótulo porque es lo que el usuario tiene en la cabeza.
    /// </summary>
    public string BackToFindingLabel => _fix.FindingAlias.Length == 0
        ? "Volver al hallazgo"
        : $"Volver al hallazgo ({_fix.FindingAlias})";

    /// <summary>Hay hallazgo al que volver: hace falta la app y el identificador.</summary>
    public bool CanGoBackToFinding => _fix.Slug.Length > 0 && _fix.FindingId != default;

    /// <summary>
    /// Compilar la solución entera en vez del proyecto de lo tocado (H9.1 §2). Vive en el servicio
    /// —no aquí— porque la vista es transitoria y el interruptor tiene que sobrevivir a navegar.
    /// </summary>
    public bool BuildFullSolution
    {
        get => _fix.BuildFullSolution;
        set
        {
            if (_fix.BuildFullSolution != value)
            {
                _fix.BuildFullSolution = value;
                OnPropertyChanged();
            }
        }
    }

    public bool HasFiles => Files.Count > 0;

    public FixFileChange? SelectedFile => Files.FirstOrDefault(f => f.IsSelected) ?? Files.FirstOrDefault();

    public string ElapsedText
    {
        get
        {
            TimeSpan e = _fix.Elapsed;
            return e.TotalHours >= 1
                ? $"{(int)e.TotalHours}h {e.Minutes:00}m {e.Seconds:00}s"
                : $"{e.Minutes:00}:{e.Seconds:00}";
        }
    }

    public string CostText => _fix.Cost is { } c
        ? $"{_fix.Calls} llamadas · coste {c:0.##} {_fix.CostUnit}"
        : $"{_fix.Calls} llamadas · coste no informado por el SDK";

    public string TouchedText => $"ficheros tocados: {Files.Count}";

    /// <summary>
    /// El resultado de compilar, en la barra inferior. Desde H9.1 lleva el DELTA: «✓ verde · 0
    /// error(es) nuevo(s) · 18 preexistente(s)». Un rojo sin causa atribuible ya no existe, y un
    /// verde con 18 errores heredados tampoco se calla que están.
    /// </summary>
    public string BuildText
    {
        get
        {
            if (!_fix.HasBuildResult)
            {
                return "build/tests: no se ha pedido";
            }

            string verdict = _fix.LastBuildOk ? "✓ verde" : "✗ rojo";
            return _fix.LastVerdict is { } v
                ? $"build/tests: {verdict} · {v.Headline}"
                : $"build/tests: {verdict}";
        }
    }

    /// <summary>Qué se compiló la última vez: «el proyecto Common/Common.csproj».</summary>
    public string BuildScopeText => _fix.LastVerdict is { TargetLabel.Length: > 0 } v
        ? $"Se compiló {v.TargetLabel}."
        : string.Empty;

    /// <summary>El recordatorio que no puede faltar en la pantalla de cierre.</summary>
    public const string UncommittedReminder =
        "Los cambios están en tu clon sin commitear — revisa y commitea cuando estés conforme.";

    public override Task LoadAsync()
    {
        RefreshPending();
        OnFixChanged();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ conversación

    /// <summary>Responde una tarjeta con una de sus opciones.</summary>
    [RelayCommand]
    private void Choose(object? parameter)
    {
        if (parameter is object[] { Length: 2 } pair
            && pair[0] is FixQuestion question && pair[1] is FixChoice choice)
        {
            _fix.Answer(question, choice.Label);
        }
    }

    /// <summary>Responde una tarjeta con el texto libre que el usuario escribió en ella.</summary>
    [RelayCommand]
    private void AnswerFreeform(FixQuestion? question)
    {
        if (question is null)
        {
            return;
        }

        if (question.Draft.Trim().Length == 0)
        {
            _toasts.Show("Escribe la respuesta antes de enviarla.");
            return;
        }

        _fix.Answer(question, question.Draft);
        question.Draft = string.Empty;
    }

    /// <summary>Envía una orden al agente a mitad de sesión.</summary>
    [RelayCommand]
    private async Task Send()
    {
        string text = Draft;
        Draft = string.Empty;
        await _fix.SendUserMessageAsync(text);
    }

    // ------------------------------------------------------------------ barra de acciones

    [RelayCommand]
    private void TogglePause() => _fix.TogglePause();

    [RelayCommand]
    private void Stop() => _fix.Stop();

    /// <summary>
    /// Descartar todo, con confirmación. Es destructivo sobre el trabajo del agente —no sobre el
    /// del usuario, que no podía haber ninguno: el árbol estaba limpio— y aun así se pregunta,
    /// porque deshacer diez minutos de sesión con un clic accidental es exactamente el accidente
    /// que la confirmación existe para evitar.
    /// </summary>
    [RelayCommand]
    private void DiscardAll()
    {
        if (Files.Count == 0 && !_fix.HasPendingChanges)
        {
            _toasts.Show("No hay ningún cambio que descartar.");
            return;
        }

        if (!_confirmer.Confirm(Files.Select(f => f.RelativePath).ToList()))
        {
            return;
        }

        FixRestoreReport report = _fix.DiscardAll();
        _toasts.Show(report.Message);
        RefreshPending();
        OnFixChanged();
    }

    /// <summary>Cierra el registro: el usuario da los cambios por buenos y se queda con ellos.</summary>
    [RelayCommand]
    private void Finish()
    {
        _fix.AcceptChanges();
        _toasts.Show(UncommittedReminder);
        RefreshPending();
        OnFixChanged();
    }

    [RelayCommand]
    private void ToggleFailureDetail() => IsFailureDetailExpanded = !IsFailureDetailExpanded;

    /// <summary>
    /// El error al portapapeles, que es a donde va: a un correo para quien administre la
    /// organización. Seleccionar a mano varias líneas dentro de un banner es el gesto que nadie
    /// hace, así que hay botón — el mismo que en la sesión de auditoría.
    /// </summary>
    [RelayCommand]
    private void CopyFailure()
    {
        string text = string.IsNullOrWhiteSpace(FailureDetail)
            ? FailureMessage
            : FailureMessage + Environment.NewLine + Environment.NewLine + FailureDetail;

        try
        {
            Clipboard.SetText(text);
            _toasts.Show("Error copiado al portapapeles.");
        }
        catch
        {
            _toasts.Show("El portapapeles no estaba disponible. El texto sigue aquí para copiarlo a mano.");
        }
    }

    [RelayCommand]
    private void CopyCommit()
    {
        try
        {
            Clipboard.SetText(Commit.ToClipboard());
            _toasts.Show("Título y descripción copiados: pégalos en tu commit.");
        }
        catch
        {
            _toasts.Show("El portapapeles no estaba disponible. El texto sigue aquí para copiarlo a mano.");
        }
    }

    /// <summary>Abre el fichero tocado en el editor configurado, para revisarlo de verdad.</summary>
    [RelayCommand]
    private async Task OpenInEditor()
    {
        FixFileChange? file = SelectedFile;
        if (file is null || _editor is null)
        {
            _toasts.Show("No hay ningún fichero tocado que abrir.");
            return;
        }

        if (!await _editor.OpenAsync(_fix.Slug, file.RelativePath, 1))
        {
            _toasts.Show("No se pudo abrir el editor. Revisa el editor configurado en Ajustes.");
        }
    }

    /// <summary>
    /// «Verificar ahora» al terminar: SUGERIDO, nunca automático. Arreglar no resuelve — la
    /// resolución llega por la vía de siempre, con evidencia, y la decide el usuario cuando dé el
    /// cambio por bueno.
    /// </summary>
    [RelayCommand]
    private async Task VerifyNow()
    {
        if (_navigation is null || _fix.Slug.Length == 0)
        {
            return;
        }

        Ulid id = _fix.FindingId;
        string slug = _fix.Slug;
        await _navigation.NavigateToAsync<FindingDetailViewModel>(vm => vm.Load(slug, id));
    }

    /// <summary>
    /// A la ficha del hallazgo que originó este arreglo. Es NAVEGAR, no verificar: lo mismo que
    /// hace «Verificar ahora» al cerrar, pero disponible también cuando la sesión falló o cuando
    /// se vuelve al último arreglo desde el rail.
    /// </summary>
    [RelayCommand]
    private async Task BackToFinding()
    {
        if (_navigation is null || !CanGoBackToFinding)
        {
            return;
        }

        Ulid id = _fix.FindingId;
        string slug = _fix.Slug;
        await _navigation.NavigateToAsync<FindingDetailViewModel>(vm => vm.Load(slug, id));
    }

    [RelayCommand]
    private async Task FixModel()
    {
        if (_navigation is not null)
        {
            await _navigation.NavigateToAsync<SettingsViewModel>();
        }
    }

    [RelayCommand]
    private Task OpenReport()
    {
        if (_navigation is null || _fix.SessionId.Length == 0 || !File.Exists(_fix.ReportPath))
        {
            _toasts.Show("El informe todavía no está en disco.");
            return Task.CompletedTask;
        }

        return _navigation.NavigateToAsync<ReportsViewModel>(
            vm => vm.ShowReport(_fix.Slug, _fix.SessionId));
    }

    [RelayCommand]
    private void SelectFile(FixFileChange? file)
    {
        if (file is null)
        {
            return;
        }

        foreach (FixFileChange other in Files)
        {
            other.IsSelected = ReferenceEquals(other, file);
        }

        OnPropertyChanged(nameof(SelectedFile));
    }

    [RelayCommand]
    private void BackToBottom() => AutoScroll = true;

    // ------------------------------------------------------------------ pendientes de antes

    [RelayCommand]
    private void DiscardPending(PendingFixRow? row)
    {
        if (row is null || !_confirmer.Confirm(row.Set.Files))
        {
            return;
        }

        _toasts.Show(_fix.DiscardPrevious(row.Set).Message);
        RefreshPending();
    }

    [RelayCommand]
    private void KeepPending(PendingFixRow? row)
    {
        if (row is null)
        {
            return;
        }

        _fix.ClosePrevious(row.Set);
        _toasts.Show("Anotado: esos cambios se quedan. Ya no se ofrecerá descartarlos.");
        RefreshPending();
    }

    private void RefreshPending()
    {
        Pending.Clear();
        foreach (FixSnapshotSet set in _fix.PendingFromPreviousSessions())
        {
            Pending.Add(new PendingFixRow(set));
        }

        OnPropertyChanged(nameof(HasPending));
    }

    public bool HasPending => Pending.Count > 0;

    private void OnFixChanged()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(ShowFailure));
        OnPropertyChanged(nameof(ShowClosing));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(FailureMessage));
        OnPropertyChanged(nameof(FailureOffersModelChange));
        OnPropertyChanged(nameof(FailureDetail));
        OnPropertyChanged(nameof(HasFailureDetail));
        OnPropertyChanged(nameof(FailureCloneNote));
        OnPropertyChanged(nameof(PauseLabel));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(SubHeaderText));
        OnPropertyChanged(nameof(BackToFindingLabel));
        OnPropertyChanged(nameof(CanGoBackToFinding));
        OnPropertyChanged(nameof(BuildFullSolution));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(SelectedFile));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(CostText));
        OnPropertyChanged(nameof(TouchedText));
        OnPropertyChanged(nameof(BuildText));
        OnPropertyChanged(nameof(BuildScopeText));
    }
}

/// <summary>
/// Quién confirma un descarte. Se inyecta —igual que el borrado de app y el reset de fábrica—
/// para que el flujo entero, incluido cancelar, se pruebe sin abrir una ventana.
/// </summary>
public interface IFixDiscardConfirmer
{
    /// <summary>True si el usuario confirma revertir esos ficheros.</summary>
    bool Confirm(IReadOnlyList<string> files);
}
