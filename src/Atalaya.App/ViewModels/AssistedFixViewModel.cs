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
public sealed partial class AssistedFixViewModel : ViewModelBase, IAppScoped
{
    private readonly LiveFixService _fix;
    private readonly NavigationService? _navigation;
    private readonly IFixCloseConfirmer _closeConfirmer;
    private readonly ToastCenter _toasts;
    private readonly EditorLauncher? _editor;
    private readonly IFixDiscardConfirmer _confirmer;
    private readonly DispatcherTimer? _clock;

    /// <summary>
    /// El hilo al que hay que volver para avisar de un cambio (BUGFIX-F32). Se captura al
    /// construir, que es cuando se sabe: el view-model nace en el hilo de interfaz. Con
    /// <c>Application</c> es el suyo; sin ella —los tests— es el del hilo que lo creó, y ahí
    /// <c>CheckAccess</c> dice que sí y todo sigue ejecutándose en línea como siempre.
    /// </summary>
    private readonly Dispatcher _dispatcher =
        Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    /// <summary>
    /// F30 §4: «Verificar ahora» del arreglo terminado <b>verifica</b>. Opcional porque los tests
    /// que solo miran el estado del arreglo no montan un auditor; sin él, el botón sigue llevando a
    /// la ficha, que es lo que hacía antes.
    /// </summary>
    private readonly VerifyCoordinator? _verify;

    /// <summary>
    /// <b>Los pasos de esa verificación</b>, en la barra de acciones del arreglo terminado — que es
    /// desde donde se lanzó.
    /// </summary>
    public StepList VerifySteps { get; } = VerifyCoordinator.NewSteps();

    /// <summary>
    /// <b>Los pasos de quedarse los cambios</b> (BUGFIX-F32, que corrige a D-1033). Salen en la
    /// misma fila que los de «Verificar ahora» y por la misma razón: son cinco escrituras que
    /// duran segundos, y hasta aquí el botón se apagaba y no contaba ninguna.
    /// </summary>
    public StepList CommitSteps { get; } = LiveFixService.NewCommitSteps();

    public AssistedFixViewModel(
        LiveFixService fix,
        ToastCenter toasts,
        IFixDiscardConfirmer confirmer,
        NavigationService? navigation = null,
        EditorLauncher? editor = null,
        IFixCloseConfirmer? closeConfirmer = null,
        VerifyCoordinator? verify = null)
    {
        _verify = verify;
        _fix = fix;
        _toasts = toasts;
        _confirmer = confirmer;
        _closeConfirmer = closeConfirmer ?? new KeepOnClose();
        _navigation = navigation;
        _editor = editor;

        _fix.Changed += OnFixChanged;
        _fix.PropertyChanged += (_, _) => OnFixChanged();

        // El titulo se edita en la tarjeta y decide si el boton se puede pulsar (P-27): sin
        // escuchar a la sugerencia, borrarlo dejaba el boton encendido hasta el siguiente
        // cambio de cualquier otra cosa.
        _fix.Commit.PropertyChanged += (_, _) => OnFixChanged();

        if (Application.Current is not null)
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
    /// EL RAÍL, EL TÍTULO Y LA MIGA DICEN LO MISMO (UI-0029). Al acabar el arreglo, la entrada del
    /// raíl cambiaba de rótulo y de icono —«Arreglo asistido» + punto verde → «Último arreglo» +
    /// llave— y la página no: el título y la miga seguían diciendo «Arreglo asistido», así que en
    /// la misma pantalla los tres sitios que dicen dónde estás decían dos cosas. La sesión, en el
    /// mismo caso, sí cuadraba. Los tres estados son los mismos que `MainViewModel.FixNavLabel`
    /// pone en el raíl.
    /// </summary>
    public override string Title => !_fix.HasSession
        ? "Arreglo asistido"
        : _fix.IsRunning
            ? "Arreglo asistido"
            : _fix.HasFailed ? "Arreglo fallido" : "Último arreglo";

    /// <summary>F26 §A.</summary>
    public override string RailKey => "fix";

    public override bool BelongsToApp => true;

    /// <inheritdoc />
    public string AppSlug => _fix.Slug;

    /// <inheritdoc />
    public string AppLabel => _fix.AppName is { Length: > 0 } ? _fix.AppName : _fix.Slug;

    /// <summary>El estado real, enlazado directamente por la vista.</summary>
    public LiveFixService Fix => _fix;

    public ObservableCollection<ConversationEntry> Conversation => _fix.Conversation;

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

    /// <summary>
    /// El identificador del hallazgo, y <b>solo él</b> (F16-RETOQUE §2·2).
    /// <para>
    /// Antes esto era «{alias} · {app}» en una sola caja con recorte, así que lo primero que
    /// perdía era justamente el alias: se leía «BUG-0012…» junto al título y el alias entero solo
    /// aparecía dentro de «Volver al hallazgo (BUG-0012)». Dos apariciones y ninguna completa.
    /// Ahora el alias va aparte, en una columna que no encoge —es corto y cabe siempre— y el
    /// nombre de la aplicación, que sí puede ser largo, es lo que cede.
    /// </para>
    /// </summary>
    public string FindingAliasText => _fix.FindingAlias;

    /// <summary>La aplicación del arreglo. Es lo que se recorta cuando falta ancho.</summary>
    public string AppNameText => _fix.FindingAlias.Length == 0 ? string.Empty : _fix.AppName;

    public string SubHeaderText => _fix.FindingTitle;

    /// <summary>
    /// Con quién se está arreglando: «Claude Code · opus» (F16). Va en la cabecera, no escondido
    /// en el informe: la pantalla es la misma con los dos motores —ése es el punto— y justamente
    /// por eso tiene que decir cuál está detrás. Vacío antes de que haya sesión.
    /// <para>
    /// <b>Sin la palabra «modelo»</b> (F26-B revisión, D-983). La llevaba, y con ella la pastilla
    /// no cabía: en el dist se leía «GitHub Copilot · modelo claude-opu…», que es la peor mitad de
    /// las dos —el proveedor se entiende sin ayuda y el modelo es el dato que hay que mirar—.
    /// Ocho caracteres que no informaban de nada: lo que va detrás del punto ya se sabe que es un
    /// modelo. Sin ella entra entera, y una pastilla que entra entera no necesita recorte.
    /// </para>
    /// </summary>
    public string EngineText
    {
        get
        {
            if (_fix.ProviderName.Length == 0)
            {
                return string.Empty;
            }

            return _fix.Model is { Length: > 0 } model
                ? $"{_fix.ProviderName} · {model}"
                : $"{_fix.ProviderName} · modelo por defecto";
        }
    }

    /// <summary>Hay motor que nombrar. Sin esto la cabecera abriría un hueco vacío.</summary>
    public bool HasEngine => EngineText.Length > 0;

    /// <summary>
    /// «Volver al hallazgo» (H9.1 §1). Terminada una sesión —o descartada— el hallazgo que la
    /// originó no tenía camino de vuelta: había que ir a Hallazgos y buscarlo.
    /// <para>
    /// <b>Sin el alias entre paréntesis</b> (F16-RETOQUE §2·2). Lo llevaba porque «es lo que el
    /// usuario tiene en la cabeza», y es cierto — pero ya lo tiene delante, dos piezas más a la
    /// izquierda y entero. Repetirlo aquí no informaba de nada y era lo que hacía la cabecera
    /// redundante además de apretada. El enlace dice a dónde lleva; cuál es el hallazgo lo dice
    /// la identidad.
    /// </para>
    /// </summary>
    public string BackToFindingLabel => "Volver al hallazgo";

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

    /// <summary>Mismo criterio que el pie de la auditoría y que el informe (F16 §B).</summary>
    public string CostText => CostFormat.SessionFooter(
        _fix.Calls, _fix.InputTokens, _fix.OutputTokens,
        _fix.CacheReadTokens, _fix.CacheWriteTokens, _fix.CostResult, _fix.Provider);

    public string TouchedText => $"ficheros tocados: {Files.Count}";

    /// <summary>
    /// El pie del arreglo, por segmentos (F17-RETOQUE), con el mismo criterio de consumo que el de
    /// la auditoría. El título del hallazgo va el primero y cede el último de los que ceden: el
    /// resultado del build y las llamadas no ceden nunca.
    /// </summary>
    public IReadOnlyList<FooterSegment> Footer
    {
        get
        {
            var segments = new List<FooterSegment>
            {
                new(new[] { SubHeaderText, Shorten(SubHeaderText, 40) }.Distinct().ToList(), Priority: 4, Bold: true),
                FooterSegment.Of(ElapsedText, opacity: 0.85),
            };
            segments.AddRange(CostFormat.UsageSegments(
                _fix.Calls, _fix.InputTokens, _fix.OutputTokens,
                _fix.CacheReadTokens, _fix.CacheWriteTokens, _fix.CostResult, _fix.Provider));
            segments.Add(FooterSegment.Of(TouchedText, priority: 3, opacity: 0.8));
            segments.Add(FooterSegment.Of(BuildText, opacity: 0.8));
            return segments;
        }
    }

    private static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

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

    /// <summary>
    /// Se llegó a compilar (R11 §3). Sin esto, la pantalla de cierre abría una sección entera
    /// —titular, veredicto, ámbito y la caja de la salida— para decir «build/tests: no se ha
    /// pedido»: cuatro huecos donde no hay ningún dato. Cuando no se pidió, una línea y nada más.
    /// </summary>
    public bool HasBuildResult => _fix.HasBuildResult;

    /// <summary>El recordatorio que no puede faltar en la pantalla de cierre.</summary>
    public const string UncommittedReminder =
        "Los cambios están en tu clon sin commitear — revisa y commitea cuando estés conforme.";

    // ------------------------------------------------------------------ un arreglo sin cambios

    /// <summary>
    /// <b>El arreglo terminó sin tocar un solo fichero</b> (R10 §7): lo detuvieron, se descartó, o
    /// el agente nunca llegó a editar. Los tres acaban igual y la pantalla tiene que decir lo
    /// mismo en los tres.
    /// <para>
    /// <b>Lo que hacía antes.</b> Enseñaba la pantalla de cierre entera: el aviso ámbar «Los
    /// cambios están en tu clon sin commitear», la tarjeta «Sugerencia de commit» con un mensaje
    /// redactado para un cambio que no existe, «Verificar ahora» —verificar qué— y «Me quedo los
    /// cambios» —cuáles—. Cuatro piezas que no aplican, y la más peligrosa es la sugerencia de
    /// commit: describe un trabajo que nadie hizo, y quien la copie commitea una mentira.
    /// </para>
    /// </summary>
    public bool ClosedWithoutChanges => ShowClosing && Files.Count == 0;

    /// <summary>Lo contrario, que es lo que la vista necesita para ENSEÑAR lo que sí aplica.</summary>
    public bool ClosedWithChanges => ShowClosing && Files.Count > 0;

    // ------------------------------------------------------------------ commitear el arreglo

    /// <summary>
    /// El arreglo ya esta commiteado en el clon (F32, que revoca D-556). Es lo que parte la
    /// pantalla de cierre en dos: mientras no lo esta, el aviso ambar, la tarjeta de sugerencia
    /// y el boton; cuando lo esta, <b>ninguna de las tres</b> y una linea con el hash. Las
    /// piezas se van ENTERAS: media tarjeta de sugerencia sobre un commit ya hecho es peor que
    /// ninguna.
    /// </summary>
    public bool IsCommitted => _fix.CommittedSha is { Length: > 0 };

    /// <summary>Lo que queda por commitear: el aviso, la tarjeta y el boton cuelgan de esto.</summary>
    public bool ClosedUncommitted => ClosedWithChanges && !IsCommitted;

    /// <summary>
    /// La linea que sustituye a las tres piezas. Dice el hash, cuanto entro y -lo que Atalaya
    /// no hace y nunca hara- que publicar sigue siendo del usuario.
    /// </summary>
    public string CommittedLine
    {
        get
        {
            if (!IsCommitted)
            {
                return string.Empty;
            }

            // BUGFIX-F32-2 — EL AUTOR VA EN ESTA LÍNEA. El commit sale con la identidad de git
            // del clon (D-1033), y en xblast ésa era «Su Nombre»: un marcador que se publicó
            // sin que nadie lo viera. Se enseña aquí, donde el usuario está mirando justo
            // antes de pushear, y sin heurísticas de «esto parece un marcador» — Atalaya no
            // puede saberlo, y el que sí puede lo tiene delante.
            string autor = _fix.CommittedAuthor is { Length: > 0 } who
                ? $" · como {who}"
                : string.Empty;

            return $"Commiteado {_fix.CommittedSha} · {Files.Count} "
                + $"fichero{(Files.Count == 1 ? string.Empty : "s")}{autor} · pendiente de tu push";
        }
    }

    /// <summary>
    /// Se puede commitear. El titulo vacio lo apaga: un commit sin asunto no se hace, y
    /// averiguarlo despues de pulsar seria un boton encendido que al pulsarlo dice que no
    /// (P-27).
    /// </summary>
    public bool CanCommitChanges
        => ClosedUncommitted && !IsCommitting && _fix.Commit.Title.Trim().Length > 0;

    /// <summary>Un commit en marcha: puede haber un <c>pre-commit</c> largo detras.</summary>
    public bool IsCommitting => _fix.IsCommitting;

    /// <summary>El rotulo, que no cambia: el gesto es el mismo, lo que cambia es que hace.</summary>
    public const string CommitButtonLabel = "Me quedo los cambios";

    public string CommitButtonText => IsCommitting ? "Commiteando…" : CommitButtonLabel;

    /// <summary>La razon, para el chip pegado al boton. Vacia mientras se pueda pulsar.</summary>
    public string CommitBlockedReason
        => ClosedUncommitted && !IsCommitting && _fix.Commit.Title.Trim().Length == 0
            ? "escribe el título del commit"
            : string.Empty;

    public bool HasCommitBlockedReason => CommitBlockedReason.Length > 0;

    /// <summary>
    /// El titular de la pantalla de cierre. Sin ficheros tocados no hay nada que dar por
    /// terminado: lo que hay es una sesión que paró sin dejar rastro, y eso se dice.
    /// </summary>
    public string ClosingHeadline => Files.Count == 0
        ? "Arreglo detenido · no hay cambios en tu clon"
        : "Arreglo terminado";

    /// <summary>
    /// Hay algo que descartar. Antes esto se comprobaba DENTRO del comando y se contestaba con un
    /// aviso flotante —«No hay ningún cambio que descartar»—, o sea: un botón encendido que al
    /// pulsarlo dice que no. Apagado con la razón al lado es el patrón de la casa (P-27).
    /// </summary>
    /// <summary>
    /// <b>Y ya no se puede descartar lo que esta commiteado</b> (F32). Revertir un commit es
    /// otra operacion -con su propio commit, su propio mensaje y su propia decision- y esta
    /// pantalla no la ofrece: dejar "Descartar todo" encendido despues de commitear prometeria
    /// deshacer algo que ya esta en el historial.
    /// </summary>
    public bool CanDiscardAll => !IsCommitted && (Files.Count > 0 || _fix.HasPendingChanges);

    /// <summary>
    /// La razón, para el chip pegado al botón. Vacía mientras se pueda pulsar, y vacía también
    /// mientras la sesión corre: ahí un arreglo que todavía no ha escrito nada es lo normal y no
    /// hace falta explicárselo a nadie.
    /// </summary>
    public string DiscardBlockedReason => IsCommitted
        ? $"ya commiteado ({_fix.CommittedSha})"
        : !CanDiscardAll && HasSession && !IsRunning
            ? "no hay cambios"
            : string.Empty;

    public bool HasDiscardBlockedReason => DiscardBlockedReason.Length > 0;

    public override Task LoadAsync()
    {
        // F32 - volver por "Ultimo arreglo" es ESTE camino (D-572), asi que aqui es donde se
        // relee del hub si el arreglo ya se commiteo. Sin esto habria dos verdades -la de
        // memoria y la del `fixes/{ulid}.json`- y la pantalla se reconstruiria sin el hash.
        _fix.RefreshCommitState();
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
        // El botón ya está apagado en este caso (`CanDiscardAll`), con la razón al lado. Esto se
        // queda como red: un comando también se puede invocar desde el teclado.
        if (!CanDiscardAll)
        {
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

    /// <summary>
    /// <b>"Me quedo los cambios" COMMITEA</b> (F32, que revoca D-556). El clic es la decision
    /// que D-556 queria dejar en manos de una persona; lo que ha cambiado es que ahora esa
    /// decision hace algo. Se commitean exactamente los ficheros del arreglo, con el titulo y
    /// la descripcion TAL Y COMO ESTEN en la tarjeta en este momento -son editables, y lo que
    /// se commitea es lo que el usuario tiene delante, no lo que el agente sugirio-.
    /// <para>
    /// Va fuera del hilo de interfaz porque detras puede haber un <c>pre-commit</c> que tarde.
    /// Y si falla, lo unico que pasa es un toast con el motivo: la pantalla no cambia y el clon
    /// tampoco.
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCommitChanges))]
    private async Task CommitChanges()
    {
        if (!CanCommitChanges)
        {
            return;   // Red: el boton ya esta apagado, pero un comando entra tambien por teclado.
        }

        _fix.IsCommitting = true;
        CommitSteps.Start();
        OnFixChanged();
        try
        {
            FixCommitResult result = await Task.Run(() => _fix.CommitChanges(CommitSteps));
            if (!result.Ok)
            {
                // El paso está en rojo con su motivo, que es donde hay que leerlo (D-1029). El
                // toast lo repite para quien no estuviera mirando esa fila.
                _toasts.Show(result.Error ?? "No se pudo commitear.");
                return;
            }

            _toasts.Show($"Commiteado {result.Sha}. El push sigue siendo tuyo.");
        }
        catch (Exception ex)
        {
            // BUGFIX-F32 — LA REGLA: pulsar este botón NUNCA cierra la aplicación. El manejador
            // global (`UnhandledErrors`) es la red de todo lo demás; esto es el cinturón de
            // este camino, y deja el motivo donde se estaba mirando en vez de en un registro.
            CommitSteps.Fail(LiveFixService.PasoCommitear, ex.Message);
            _toasts.Show($"No se pudo commitear: {ex.Message}");
        }
        finally
        {
            CommitSteps.Finish();
            _fix.IsCommitting = false;
            RefreshPending();
            OnFixChanged();
        }
    }

    /// <inheritdoc cref="SessionViewModel.CanClose"/>
    public bool CanClose => !_fix.IsRunning && _fix.HasSession;

    /// <summary>
    /// Archiva la pantalla y vuelve a la ficha del hallazgo (BUGFIX-CIERRE) — que es de donde se
    /// salió y donde está lo siguiente que hacer con él: verificar.
    /// <para>
    /// <b>Cerrar no es descartar.</b> Si el agente dejó ficheros modificados se pregunta, y
    /// conservarlos es lo normal: son del usuario y su árbol es suyo. Sin cambios, cierra directo
    /// y sin preguntas.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task Close()
    {
        bool keep = false;
        if (_fix.HasPendingChanges)
        {
            FixCloseChoice choice = _closeConfirmer.Ask(_fix.Files.Select(f => f.RelativePath).ToList());
            switch (choice)
            {
                case FixCloseChoice.Cancelar:
                    return;

                case FixCloseChoice.DescartarYCerrar:
                    DiscardAll();
                    if (_fix.HasPendingChanges)
                    {
                        return;   // el descarte no se completó: la pantalla sigue haciendo falta
                    }

                    break;

                default:
                    keep = true;
                    break;
            }
        }

        if (!_fix.Close(keep))
        {
            return;
        }

        if (keep)
        {
            _toasts.Show("Cerrado. Los ficheros modificados siguen en tu clon: son tuyos, "
                + "y Atalaya ya no se ofrece a revertirlos.");
        }

        if (_navigation is null)
        {
            return;
        }

        if (CanGoBackToFinding)
        {
            await BackToFinding();
            return;
        }

        await _navigation.NavigateToAsync<PortfolioViewModel>();
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

        // El fichero tocado se abre por el principio: aquí no hay hallazgo ni línea que re-anclar,
        // y el toast lo cuenta igual que en la ficha — mismo camino, mismo mensaje (R13 §3).
        EditorOpenResult result = await _editor.OpenAsync(_fix.Slug, file.RelativePath, 1);
        _toasts.Show(EditorLauncher.Toast(result));
    }

    /// <summary>
    /// «Verificar ahora» al terminar: SUGERIDO, nunca automático. Arreglar no resuelve — la
    /// resolución llega por la vía de siempre, con evidencia, y la decide el usuario cuando dé el
    /// cambio por bueno.
    /// <para>
    /// <b>Y verifica AQUÍ</b> (F30 §4). Hasta aquí solo navegaba a la ficha, donde había que
    /// pulsar un segundo «Verificar ahora» con el mismo rótulo: dos botones iguales, uno de los
    /// cuales no hacía lo que decía. Ahora la verificación se lanza desde donde se pulsó, con sus
    /// pasos en la barra de acciones, y al acabar se sigue a la ficha — que es donde está el
    /// veredicto que se acaba de escribir.
    /// </para>
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

        if (_verify is not null)
        {
            IsBusy = true;
            VerifySteps.Start();
            try
            {
                VerifyOutcome outcome = await Task.Run(
                    () => _verify.RunAsync(slug, new[] { id }, CancellationToken.None, VerifySteps));
                _toasts.Show(outcome.Toast);
            }
            catch (Exception ex)
            {
                // El paso está en rojo con su motivo: NO se sigue a la ficha, porque lo que hay que
                // leer está aquí.
                _toasts.Show($"No se pudo verificar: {ex.Message}");
                return;
            }
            finally
            {
                VerifySteps.Finish();
                IsBusy = false;
            }
        }

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

    /// <summary>
    /// <b>Esto lo llaman hilos de fondo, y desde F32 toca algo que no se deja</b>
    /// (BUGFIX-F32).
    /// <para>
    /// El servicio avisa desde donde esté trabajando —el agente, una compilación, el commit—, y
    /// hasta F32 eso daba igual: <c>OnPropertyChanged</c> lo reparte WPF solo, marshalando cada
    /// enlace. <c>NotifyCanExecuteChanged</c> <b>no</b>: un <c>Button</c> enlazado se suscribe a
    /// <c>CanExecuteChanged</c>, y levantarlo desde un hilo de fondo termina en
    /// <c>Dispatcher.VerifyAccess</c> — «el subproceso que realiza la llamada no puede obtener
    /// acceso a este objeto»—. Ésa es la excepción que cerró la aplicación al pulsar «Me quedo
    /// los cambios», y no salía en los tests porque en un test <b>no hay ningún Button
    /// suscrito</b>: el aviso no llega a cruzar a nadie.
    /// </para>
    /// <para>
    /// Se cruza aquí, en un solo sitio, y no en cada llamante: el que se olvide de cruzar es el
    /// que rompe, y son treinta. Es la misma guarda que ya tienen <see cref="StepList"/> y la
    /// conversación (F30 §2d), con la misma salida sin <c>Application</c> para los tests.
    /// </para>
    /// </summary>
    private void OnFixChanged()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(OnFixChanged));
            return;
        }

        OnPropertyChanged(nameof(Title));
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
        OnPropertyChanged(nameof(CanClose));
        OnPropertyChanged(nameof(PauseLabel));
        OnPropertyChanged(nameof(FindingAliasText));
        OnPropertyChanged(nameof(AppNameText));
        OnPropertyChanged(nameof(SubHeaderText));
        OnPropertyChanged(nameof(EngineText));
        OnPropertyChanged(nameof(HasEngine));
        OnPropertyChanged(nameof(CanGoBackToFinding));
        OnPropertyChanged(nameof(BuildFullSolution));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(SelectedFile));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(CostText));
        OnPropertyChanged(nameof(Footer));
        OnPropertyChanged(nameof(TouchedText));
        OnPropertyChanged(nameof(BuildText));
        OnPropertyChanged(nameof(BuildScopeText));
        OnPropertyChanged(nameof(HasBuildResult));
        OnPropertyChanged(nameof(ClosedWithoutChanges));
        OnPropertyChanged(nameof(ClosedWithChanges));
        OnPropertyChanged(nameof(ClosingHeadline));
        OnPropertyChanged(nameof(CanDiscardAll));
        OnPropertyChanged(nameof(DiscardBlockedReason));
        OnPropertyChanged(nameof(HasDiscardBlockedReason));
        OnPropertyChanged(nameof(IsCommitted));
        OnPropertyChanged(nameof(ClosedUncommitted));
        OnPropertyChanged(nameof(CommittedLine));
        OnPropertyChanged(nameof(IsCommitting));
        OnPropertyChanged(nameof(CommitButtonText));
        OnPropertyChanged(nameof(CanCommitChanges));
        OnPropertyChanged(nameof(CommitBlockedReason));
        OnPropertyChanged(nameof(HasCommitBlockedReason));
        CommitChangesCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>
/// Quién confirma un descarte. Se inyecta —igual que el borrado de app y el reset de fábrica—
/// para que el flujo entero, incluido cancelar, se pruebe sin abrir una ventana.
/// </summary>
/// <summary>
/// Qué hacer con los ficheros que el agente dejó tocados cuando se cierra la pantalla
/// (BUGFIX-CIERRE). Son TRES respuestas y no dos: cerrar y descartar son gestos distintos, y
/// cancelar tiene que seguir siendo posible.
/// </summary>
public enum FixCloseChoice
{
    /// <summary>Se queda como está: la pantalla no se cierra.</summary>
    Cancelar,

    /// <summary>Los cambios se quedan en el clon. Es lo normal: son del usuario.</summary>
    ConservarYCerrar,

    /// <summary>Revierte lo del agente y luego cierra. Pasa por el mismo camino de «Descartar todo».</summary>
    DescartarYCerrar,
}

/// <summary>Quién hace esa pregunta. Inyectable para que el flujo entero se pueda probar.</summary>
public interface IFixCloseConfirmer
{
    FixCloseChoice Ask(IReadOnlyList<string> files);
}

/// <summary>
/// La respuesta por defecto cuando nadie pregunta (tests, construcciones a mano): conservar. Es el
/// lado seguro — cerrar una pantalla nunca puede tocar el árbol de trabajo de alguien por omisión.
/// </summary>
public sealed class KeepOnClose : IFixCloseConfirmer
{
    public FixCloseChoice Ask(IReadOnlyList<string> files) => FixCloseChoice.ConservarYCerrar;
}

public interface IFixDiscardConfirmer
{
    /// <summary>True si el usuario confirma revertir esos ficheros.</summary>
    bool Confirm(IReadOnlyList<string> files);
}
