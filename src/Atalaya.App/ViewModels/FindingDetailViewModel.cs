using System.Collections.ObjectModel;
using System.Windows;
using Atalaya.App.Services;
using Atalaya.App.Views;
using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>
/// V4 Detalle de hallazgo (§8): ficha completa, snippet, historial, comentarios, gobernanza y la
/// acción "Generar prompt de arreglo" (§5.7).
/// <para>
/// <b>F5.4.</b> Aquí vive el juego completo de acciones de escritura sobre un hallazgo. V3 se quedó
/// sin ninguna: <b>la lista encuentra, el detalle actúa</b>. Lo que bajó de V3 fue
/// <see cref="VerifyCommand"/>, las dos salidas de disputa
/// (<see cref="AcceptDisputeCommand"/> / <see cref="DismissDisputeCommand"/>) y
/// <see cref="OpenInEditorCommand"/>. Silenciar y asignar ya estaban.
/// </para>
/// <para>
/// <b>F5.5.</b> La ficha se reordena en dos columnas y deja de hablar por el pie. Tres cambios de
/// fondo en este view-model: (1) <b>no hay texto de estado</b> — cada acción avisa por el
/// <see cref="ToastCenter"/> de F5.3, porque un mensaje incrustado al final de una columna
/// kilométrica no lo ve nadie y encima se quedaba pegado; (2) la <b>visibilidad condicional</b>
/// de cada control es una propiedad de aquí (<see cref="ShowDispute"/>, <see cref="CanReopen"/>,
/// <see cref="CanUnsilence"/>) en vez de botones siempre presentes que fallan al pulsarlos;
/// (3) el historial y los metadatos se sirven ya <b>traducidos y compuestos</b>, que es la única
/// forma de que la vista no acabe volcando identificadores de C# en castellano.
/// </para>
/// <para>
/// <b>La asignación sigue aquí a propósito.</b> <see cref="ApplyAssignCommand"/> y
/// <see cref="Assignee"/> no se han borrado: F5.5 retira los controles de la <i>vista</i> porque
/// nadie usa la asignación, pero el campo se conserva en el modelo y la acción sigue viva, de modo
/// que recuperarla es volver a poner dos controles y no reescribir la gobernanza.
/// </para>
/// </summary>
public sealed partial class FindingDetailViewModel : ViewModelBase, IAppScoped
{
    private readonly HubContext _hub;
    private readonly GovernanceService _governance;
    private readonly MachineConfigStore _machines;
    private readonly VerifyCoordinator _verify;

    /// <summary>
    /// <b>Los pasos de la verificación</b> (F30 §4), bajo el botón que la lanza. En vertical:
    /// aquí hay sitio, y un fallo tiene que caber con su motivo en la línea.
    /// </summary>
    public StepList VerifySteps { get; } = VerifyCoordinator.NewSteps();

    private readonly EditorLauncher _editor;
    private readonly ToastCenter _toasts;
    private readonly AnchorRepair? _anchors;

    /// <summary>
    /// F5.8 §3: la GOBERNANZA no depende del clon —silenciar, cambiar severidad, disputar y
    /// resolver a mano siguen enteros sin él, y el snippet ya enseña la copia anclada con su
    /// aviso—. Verificar sí: le pregunta al agente sobre código que tiene que estar delante.
    /// </summary>
    private readonly CloneLinkService _links;

    private readonly LinkCloneFlow _linkFlow;

    /// <summary>
    /// F6.8: quién usa el código del hallazgo. Se recolecta al generar el prompt de arreglo —no al
    /// abrir la ficha—: es un barrido del clon y abrir un hallazgo tiene que seguir siendo
    /// instantáneo.
    /// </summary>
    private readonly ReferenceCollector _references;

    /// <summary>
    /// F6.9 · «Arreglar con agente». Opcionales los dos: la ficha se abre y se gobierna igual sin
    /// ellos —los tests que solo miran gobernanza no montan una sesión de arreglo— y sin ellos el
    /// botón simplemente no está. El generador de prompt no depende de esto para nada.
    /// </summary>
    private readonly AssistedFixLauncher? _fixLauncher;

    private readonly LiveFixService? _fix;

    /// <summary>
    /// F7: las convenciones del proyecto que el prompt de arreglo tiene que llevar. Opcional como
    /// el resto: sin ella el prompt sale igual, sin la sección.
    /// </summary>
    private readonly DirectiveService? _directives;

    /// <summary>
    /// La última recolección, y de qué hallazgo era. La fila «Usado desde» de los metadatos sale de
    /// aquí: si ya se ha mirado, decirlo es gratis; lo que no se hace nunca es mirar por si acaso.
    /// </summary>
    private (Ulid Finding, ReferenceReport Report)? _lastReferences;

    /// <summary>
    /// La gestión de patrones (F12 §F). Opcional: sin ella la ficha sigue diciendo POR QUÉ está
    /// silenciado, y lo único que no ofrece es el atajo para retirar el patrón.
    /// </summary>
    private readonly IPatternSilencesDialog? _patternsDialog;

    public FindingDetailViewModel(
        HubContext hub,
        GovernanceService governance,
        MachineConfigStore machines,
        VerifyCoordinator verify,
        EditorLauncher editor,
        ToastCenter toasts,
        CloneLinkService links,
        LinkCloneFlow linkFlow,
        AnchorRepair? anchors = null,
        ReferenceCollector? references = null,
        AssistedFixLauncher? fixLauncher = null,
        LiveFixService? fix = null,
        NavigationService? navigation = null,
        DirectiveService? directives = null,
        IPatternSilencesDialog? patternsDialog = null)
    {
        _hub = hub;
        ScopeOptions = new[]
        {
            new SilenceScopeOption(SilenceScope.Hallazgo, scope => SilenceScope = scope) { IsSelected = true },
            new SilenceScopeOption(SilenceScope.Patron, scope => SilenceScope = scope) { HasExemplar = true },
        };
        RefreshScopeOptions();
        _governance = governance;
        _machines = machines;
        _verify = verify;
        _editor = editor;
        _toasts = toasts;
        _links = links;
        _linkFlow = linkFlow;
        _anchors = anchors;

        // El recolector no tiene estado propio ni dependencias: si nadie lo inyecta, se construye.
        // Así el prompt de arreglo lleva sus referencias también en los caminos que no pasan por DI.
        _references = references ?? new ReferenceCollector();
        _fixLauncher = fixLauncher;
        _fix = fix;
        _patternsDialog = patternsDialog;
        _navigation = navigation;
        _directives = directives;
    }

    private readonly NavigationService? _navigation;

    /// <inheritdoc cref="CloneLink.CanAudit"/>
    public bool CanAudit => Link.CanAudit;

    /// <inheritdoc cref="CloneLink.DisabledActionTooltip"/>
    public string AuditDisabledTooltip => Link.DisabledActionTooltip;

    /// <summary>El estado de vinculación de la app del hallazgo (F5.8 §1).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAudit))]
    [NotifyPropertyChangedFor(nameof(AuditDisabledTooltip))]
    private CloneLink _link = CloneLink.Unknown(string.Empty);

    /// <summary>El acceso directo a vincular que acompaña a «Verificar ahora» deshabilitado.</summary>
    [RelayCommand]
    private void LinkClone()
    {
        if (Slug.Length == 0)
        {
            return;
        }

        Link = _linkFlow.Run(Slug);
        RefreshAssistedFix();
    }

    public override string Title => Finding is null ? "Hallazgo" : $"{Finding.DisplayId ?? Finding.Id.ToString()}";

    /// <summary>F26 §A — la ficha no tiene entrada propia: pertenece a Hallazgos.</summary>
    public override string RailKey => "findings";

    /// <summary>
    /// LA MIGA DE UNA FICHA PASA POR SU LISTA (UI-0058). Era «Portafolio › XBLAST › BUG-0008», sin
    /// «Hallazgos» —que es de donde vienes— y con el eslabón intermedio llevando al INVENTARIO, así
    /// que la miga no servía para volver a los hallazgos filtrados y solo quedaba la flecha. Ahora
    /// la página es «Hallazgos» y el hallazgo cuelga de ella, que es lo que es.
    /// </summary>
    public override string CrumbLabel => "Hallazgos";

    /// <inheritdoc />
    public override string SubCrumbLabel => Finding is null
        ? string.Empty
        : Finding.DisplayId ?? Finding.Id.ToString();

    /// <inheritdoc />
    public override System.Windows.Input.ICommand? SubCrumbParentCommand => BackToFindingsCommand;

    /// <summary>
    /// Vuelve a la lista de hallazgos TAL COMO LA DEJASTE: la del historial si está, con su filtro
    /// y su desplazamiento. Es el eslabón «Hallazgos» de la miga, y no hay un segundo botón de
    /// volver dentro del contenido (UI-0058).
    /// </summary>
    [RelayCommand]
    private Task BackToFindings()
        => _navigation is null
            ? Task.CompletedTask
            : _navigation.NavigateOrResumeAsync<FindingsViewModel>();

    public override bool BelongsToApp => true;

    /// <inheritdoc />
    public string AppSlug => Slug;

    /// <inheritdoc />
    public string AppLabel => AppName;

    [ObservableProperty] private string _slug = string.Empty;
    [ObservableProperty] private Finding? _finding;
    [ObservableProperty] private string _ruleText = string.Empty;

    // Governance inputs
    /// <summary>
    /// El alcance del silencio (F5.12). Arranca siempre en «solo este hallazgo»: es el gesto de
    /// todos los días, y el que no puede equivocarse por inercia. Callar un tipo entero de problema
    /// se elige a propósito.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SilenceActionLabel))]
    [NotifyPropertyChangedFor(nameof(CanApplySilence))]
    private SilenceScope _silenceScope = SilenceScope.Hallazgo;

    /// <summary>El radio sigue al modelo, no solo al revés: fijar el alcance desde código lo marca.</summary>
    partial void OnSilenceScopeChanged(SilenceScope value)
    {
        foreach (SilenceScopeOption option in ScopeOptions)
        {
            option.IsSelected = option.Value == value;
        }
    }

    [ObservableProperty] private SilenceReason _silenceReason = SilenceReason.FalsoPositivo;
    [ObservableProperty] private int _silenceExpiryDays;
    [ObservableProperty] private string _silenceNotes = string.Empty;
    [ObservableProperty] private string _assignee = string.Empty;
    [ObservableProperty] private Severity _severity = Severity.Media;
    [ObservableProperty] private string _justification = string.Empty;
    [ObservableProperty] private string _newComment = string.Empty;

    /// <summary>
    /// La resolución manual llega plegada (F5.5 §4): es la acción excepcional de la tarjeta —cierra
    /// un hallazgo sin auditoría— y desplegada compite visualmente con silenciar y verificar, que
    /// son las del día a día.
    /// </summary>
    [ObservableProperty] private bool _manualResolutionExpanded;

    // ------------------------------------------------------------------ snippet

    [ObservableProperty] private string _snippet = string.Empty;
    [ObservableProperty] private int _snippetFirstLine = 1;
    [ObservableProperty] private int _snippetHighlightLine;
    [ObservableProperty] private string _snippetCaption = string.Empty;

    /// <summary>
    /// La cabecera del bloque de código, en DOS piezas (F26-B revisión, D-983).
    /// <para>
    /// Escrita de una sola vez —«Clase.Miembro · ruta:línea»— no cabía, y lo que el recorte se
    /// llevaba era siempre el final: la ruta. Pero el orden importa al revés. El nombre de la
    /// clase y del miembro es lo que dice QUÉ se está mirando y es corto; la ruta es larga y
    /// repetitiva —los primeros segmentos son los mismos para media aplicación— y admite
    /// acortarse por el medio sin perder lo que la identifica, que son sus extremos.
    /// </para>
    /// </summary>
    [ObservableProperty] private string _snippetMember = string.Empty;

    /// <inheritdoc cref="SnippetMember"/>
    [ObservableProperty] private string _snippetWhere = string.Empty;
    [ObservableProperty] private string _snippetNotice = string.Empty;
    [ObservableProperty] private string _snippetPath = string.Empty;
    [ObservableProperty] private SnippetState _snippetState = SnippetState.SinUbicacion;

    /// <summary>
    /// Cómo se pinta la franja de encima del código (F6.7): ámbar cuando avisa de algo que hay que
    /// atender, neutra cuando solo informa. Lo decide el ESTADO del hallazgo, no el anclaje.
    /// </summary>
    [ObservableProperty] private SnippetTone _snippetTone = SnippetTone.Aviso;

    /// <summary>
    /// El aviso lleva «Verificar ahora» solo cuando verificar arregla lo que avisa. Viene del
    /// panel y ya no se recalcula aquí: sobre un hallazgo resuelto o silenciado no hay nada que
    /// verificar desde esta franja, y esa decisión es del lector del snippet (F6.7).
    /// </summary>
    [ObservableProperty] private bool _snippetNoticeOffersVerify;

    /// <summary>Hay código que pintar. Si no, el panel se retira y queda solo el aviso.</summary>
    public bool HasSnippet => Snippet.Length > 0;

    /// <summary>Hay algo que advertir sobre el código antes de que se lea.</summary>
    public bool HasSnippetNotice => SnippetNotice.Length > 0;

    // ------------------------------------------------------------------ cabecera

    /// <summary>Hay una discrepancia abierta: la ficha ofrece las dos salidas (F5.1b).</summary>
    public bool IsDisputed => Finding is { Disputes.Count: > 0 };

    /// <summary>La sección de disputa solo existe cuando hay disputa (F5.5 §4).</summary>
    public bool ShowDispute => IsDisputed;

    /// <summary>Quién discrepa y por qué, para decidir con la razón delante y no a ciegas.</summary>
    public string DisputeSummary => Finding is null || Finding.Disputes.Count == 0
        ? string.Empty
        : string.Join(" · ", Finding.Disputes.Select(d => $"{d.Model ?? "auditor"}: {d.Justification}"));

    /// <summary>Lo que se lee en el chip de disputa. Misma marca que la lista de V3.</summary>
    public string DisputeBadge
    {
        get
        {
            if (Finding is null || Finding.Disputes.Count == 0)
            {
                return string.Empty;
            }

            int models = Finding.Disputes.Select(d => d.Model ?? "auditor").Distinct().Count();
            return models > 1 ? $"⚖︎ Disputado ×{models}" : "⚖︎ Disputado";
        }
    }

    public Confidence Confidence => Finding?.Confidence ?? Confidence.Media;

    public string ConfidenceLabel => ConfidenceNames.Display(Confidence);

    public string ConfidenceHelp => ConfidenceNames.Help(Confidence);

    public FindingStatus Status => Finding?.Status ?? FindingStatus.Activo;

    public string StatusLabel => FindingStatusNames.Display(Status);

    public string StatusHelp => FindingStatusNames.Help(Status);

    public bool NeedsReview => Finding?.NeedsReview ?? false;

    // ------------------------------------------------------------------ gobernanza

    /// <summary>
    /// Solo se des-silencia lo silenciado <b>a mano</b> (F12 §F). Lo que tapa un patrón no se
    /// levanta desde aquí: el patrón seguiría puesto y la auditoría siguiente volvería a callarlo,
    /// así que el botón habría hecho un gesto que se deshace solo. La palanca que sirve es retirar
    /// el patrón, y a eso lleva <see cref="ManageSilencingPatternCommand"/>.
    /// </summary>
    public bool CanUnsilence => Status == FindingStatus.Silenciado && !SilencedByPattern;

    /// <summary>Silenciar algo ya silenciado no hace nada: el botón se retira.</summary>
    public bool CanSilence => Status != FindingStatus.Silenciado;

    /// <summary>
    /// Si el botón de la sección hace algo (F5.12). Los dos alcances tienen puertas distintas:
    /// silenciar un hallazgo ya silenciado no hace nada, pero <b>silenciar su tipo sí</b> — es el
    /// camino natural, de hecho. Alguien silencia un falso positivo, ve que se repite por toda la
    /// aplicación y vuelve a esa misma ficha a callar el tipo entero; hasta aquí se encontraba con
    /// el selector de alcance pintado y ningún botón que pulsar.
    /// </summary>
    public bool CanApplySilence => SilenceScope == SilenceScope.Patron || CanSilence;

    /// <summary>«Reabrir» aparece SOLO si está resuelto (F5.5 §4).</summary>
    public bool CanReopen => Status == FindingStatus.Resuelto;

    /// <summary>Un hallazgo ya resuelto no se vuelve a resolver a mano.</summary>
    public bool CanResolveManually => Status != FindingStatus.Resuelto;

    /// <summary>El silencio que hay escrito sobre este hallazgo, o null.</summary>
    private Silence? CurrentSilence
        => Finding is null || Slug.Length == 0 ? null : _hub.Store.TryReadSilence(Slug, Finding.Id);

    /// <summary>
    /// Este silencio lo puso un PATRÓN, no una persona sobre este hallazgo (F5.12, F12 §F). Cambia
    /// qué se lee y qué se ofrece: la palanca que lo deshace es el patrón.
    /// </summary>
    public bool SilencedByPattern
    {
        get
        {
            Silence? s = CurrentSilence;
            return s is not null
                   && (s.ByPatternId is not null || !string.IsNullOrWhiteSpace(s.ByPatternExemplar));
        }
    }

    /// <summary>
    /// El silencio vigente, escrito entero (F12 §F): <b>quién, cuándo, por qué</b> — o el patrón que
    /// lo tapa. Le faltaba la fecha, que es justo el dato que convierte «alguien decidió esto» en
    /// «alguien decidió esto entonces», y sin ella no hay forma de saber si la decisión es de este
    /// sprint o de hace un año.
    /// </summary>
    public string SilenceSummary
    {
        get
        {
            Silence? silence = CurrentSilence;
            if (silence is null)
            {
                return string.Empty;
            }

            string when = silence.Utc.ToLocalTime().ToString("dd/MM/yyyy");
            string expiry = silence.ExpiresUtc is null
                ? "permanente"
                : $"caduca el {silence.ExpiresUtc.Value.ToLocalTime():dd/MM/yyyy}";
            string notes = string.IsNullOrWhiteSpace(silence.Notes) ? string.Empty : $" — {silence.Notes!.Trim()}";

            // F5.12: un silencio nacido de un patrón lo DICE. Sin esto, la ficha afirmaba que
            // alguien había mirado este caso concreto y decidido sobre él, cuando lo que hubo fue
            // una decisión sobre un tipo de problema entero — y de ahí salen las dos preguntas que
            // nadie podría contestar: por qué está silenciado y a quién preguntarle.
            if (!string.IsNullOrWhiteSpace(silence.ByPatternExemplar))
            {
                return $"Silenciado por el patrón «{silence.ByPatternExemplar!.Trim()}», puesto por "
                    + $"{silence.By} el {when} · {SilenceReasonNames.Display(silence.Reason)} · {expiry}{notes}";
            }

            return $"Silenciado por {silence.By} el {when} · "
                + $"{SilenceReasonNames.Display(silence.Reason)} · {expiry}{notes}";
        }
    }

    public bool HasSilence => SilenceSummary.Length > 0;

    /// <summary>
    /// Cómo se deshace, dicho en la misma tarjeta que lo afirma (F12 §F). Un hallazgo que dice
    /// «silenciado» y no dice cómo dejar de estarlo obliga a buscar la palanca por la aplicación.
    /// </summary>
    public string SilenceUndoHint
    {
        get
        {
            if (!HasSilence)
            {
                return string.Empty;
            }

            return SilencedByPattern
                ? "Lo tapa un patrón, no una decisión sobre este caso: se deshace retirando el "
                  + "patrón, y entonces vuelve a activo al instante — sin re-auditar."
                : "Se deshace con «Des-silenciar»: vuelve a aparecer en informes y auditorías.";
        }
    }

    /// <summary>El botón que lleva a la palanca de verdad cuando lo que tapa es un patrón.</summary>
    public bool CanManageSilencingPattern => SilencedByPattern && _patternsDialog is not null;

    /// <summary>
    /// Abre la gestión de patrones de esta aplicación, que es donde se retira el que tapa este
    /// hallazgo. No lo retira por su cuenta: retirar un patrón afecta a toda la aplicación y esa
    /// decisión se toma viendo la lista, no desde una ficha.
    /// </summary>
    [RelayCommand]
    private void ManageSilencingPattern()
    {
        if (_patternsDialog is null || Slug.Length == 0)
        {
            return;
        }

        var vm = new PatternSilencesViewModel(_hub, _governance, _toasts);
        vm.Load(Slug);
        _patternsDialog.Show(vm);

        // Al volver, el hallazgo puede haber dejado de estar silenciado: el patrón que lo tapaba
        // ya no está y el silencio por patrón es derivado (F12 §F).
        if (Finding is not null)
        {
            Reload(Finding.Id);
        }
    }

    // ------------------------------------------------------------------ F5.12 · alcance

    /// <summary>El nombre de la app del hallazgo: lo que se lee en el texto de consecuencia.</summary>
    public string AppName
    {
        get
        {
            string? name = Slug.Length == 0 ? null : _hub.Store.TryReadApp(Slug)?.Name;
            return string.IsNullOrWhiteSpace(name) ? Slug : name!;
        }
    }

    /// <summary>La regla del hallazgo abierto. Metadato informativo: ya no es gobernanza (F5.12).</summary>
    public string RuleId => Finding?.RuleId ?? string.Empty;

    /// <summary>
    /// Lo MIDE la aplicación (F5.16), así que «Verificar ahora» lo vuelve a medir en vez de
    /// preguntarle al auditor. Cambia el texto de los botones y del aviso: quien pulsa tiene que
    /// saber que va a leer un número, no a gastar tokens.
    /// </summary>
    public bool IsMeasured => UnitMeasure.IsMeasured(RuleId);

    /// <summary>El botón dice lo que hace: medir no es lo mismo que preguntar.</summary>
    public string VerifyActionLabel => IsMeasured ? "Medir ahora" : "Verificar ahora";

    /// <summary>Y la línea de ayuda lo explica sin que haya que pulsarlo para averiguarlo.</summary>
    public string VerifyHelp => IsMeasured
        ? "Vuelve a medir la unidad en tu clon y aplica el resultado. No consulta al auditor ni gasta tokens."
        : "Le pide al auditor un veredicto sobre este hallazgo, anclado en el código de tu clon.";

    /// <summary>
    /// La frase que definirá el alcance del patrón (F5.12). Se propone desde el título del hallazgo
    /// —sin los nombres propios del caso— y es EDITABLE antes de confirmar: es lo único que el
    /// auditor va a leer, así que quien silencia tiene que ver y poder pulir exactamente lo que se
    /// va a dejar de reportar.
    /// </summary>
    [ObservableProperty] private string _patternExemplar = string.Empty;

    /// <summary>
    /// El patrón que nació de ESTE hallazgo, si lo hay (F5.12). Escrito en la ficha para que un
    /// hallazgo silenciado por su propio patrón no parezca silenciado porque sí.
    /// </summary>
    public string PatternOriginSummary
    {
        get
        {
            if (Finding is null || Slug.Length == 0)
            {
                return string.Empty;
            }

            PatternSilence? pattern = _governance.PatternOriginatedBy(Slug, Finding.Id);
            if (pattern is null)
            {
                return string.Empty;
            }

            string state = pattern.IsExpiredAt(DateTimeOffset.UtcNow) ? " — caducado, ya no suprime" : string.Empty;
            return $"Origen del patrón silenciado «{pattern.Exemplar}» ({pattern.ShortId}, por {pattern.By}){state}";
        }
    }

    public bool HasPatternOrigin => PatternOriginSummary.Length > 0;

    /// <summary>
    /// Las dos opciones de alcance, cada una con la frase que dice QUÉ PASA si se elige. La
    /// consecuencia no es un tooltip: es lo que separa «silenciar esto» de «dejar de mirar esto en
    /// toda la aplicación», y quien las confunde no se entera hasta la auditoría siguiente.
    /// </summary>
    public IReadOnlyList<SilenceScopeOption> ScopeOptions { get; }

    /// <summary>
    /// Re-escribe los textos de las opciones con la app del hallazgo abierto. Se re-escriben en vez
    /// de reconstruirse para no perder el radio marcado en cada recarga.
    /// </summary>
    private void RefreshScopeOptions()
    {
        SilenceScopeOption solo = ScopeOptions[0];
        solo.Label = "Solo este hallazgo";
        solo.Consequence = "Este caso concreto deja de contar. Otras auditorías pueden volver a "
            + "reportar problemas parecidos en otros sitios.";

        SilenceScopeOption patron = ScopeOptions[1];
        patron.Label = "Este tipo de problema en toda la aplicación";
        patron.Consequence = $"Las auditorías de {AppName} dejarán de reportar problemas de este tipo. "
            + "El juicio de similitud lo hace el auditor.";
    }

    /// <summary>
    /// El borrador de la frase, generalizado desde el título del hallazgo. Vive aquí y no en el
    /// XAML porque es lo que hay que poder comprobar sin abrir una ventana.
    /// </summary>
    private string ProposeExemplar()
        => Finding is null ? string.Empty : ExemplarDraft.Propose(Finding.Title, Finding.Symbol);

    /// <summary>El botón cambia de nombre con el alcance: no hace lo mismo en los dos.</summary>
    public string SilenceActionLabel
        => SilenceScope == SilenceScope.Patron ? "Silenciar este tipo" : "Silenciar";

    public ObservableCollection<HistoryRow> History { get; } = new();
    public ObservableCollection<CommentRow> Comments { get; } = new();
    public ObservableCollection<MetaRow> Meta { get; } = new();

    /// <summary>Los motivos de silencio con su texto legible: «Falso positivo», no «FalsoPositivo».</summary>
    public IReadOnlyList<Labeled<SilenceReason>> ReasonOptions { get; } = Enum.GetValues<SilenceReason>()
        .Select(r => new Labeled<SilenceReason>(r, SilenceReasonNames.Display(r)))
        .ToList();

    public IReadOnlyList<Severity> Severities { get; } = Enum.GetValues<Severity>();

    partial void OnFindingChanged(Finding? value) => RaiseDerived();

    partial void OnSnippetChanged(string value) => OnPropertyChanged(nameof(HasSnippet));

    partial void OnSnippetNoticeChanged(string value) => OnPropertyChanged(nameof(HasSnippetNotice));

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(IsDisputed));
        OnPropertyChanged(nameof(ShowDispute));
        OnPropertyChanged(nameof(DisputeSummary));
        OnPropertyChanged(nameof(DisputeBadge));
        OnPropertyChanged(nameof(Confidence));
        OnPropertyChanged(nameof(ConfidenceLabel));
        OnPropertyChanged(nameof(ConfidenceHelp));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusHelp));
        OnPropertyChanged(nameof(NeedsReview));
        OnPropertyChanged(nameof(CanSilence));
        OnPropertyChanged(nameof(CanApplySilence));
        OnPropertyChanged(nameof(CanUnsilence));
        OnPropertyChanged(nameof(CanReopen));
        OnPropertyChanged(nameof(CanResolveManually));
        OnPropertyChanged(nameof(SilenceSummary));
        OnPropertyChanged(nameof(HasSilence));
        OnPropertyChanged(nameof(SilencedByPattern));
        OnPropertyChanged(nameof(SilenceUndoHint));
        OnPropertyChanged(nameof(CanManageSilencingPattern));
        OnPropertyChanged(nameof(PatternOriginSummary));
        OnPropertyChanged(nameof(HasPatternOrigin));
        OnPropertyChanged(nameof(AppName));
        OnPropertyChanged(nameof(RuleId));
        OnPropertyChanged(nameof(IsMeasured));
        OnPropertyChanged(nameof(VerifyActionLabel));
        OnPropertyChanged(nameof(VerifyHelp));
        RefreshScopeOptions();
        // El borrador del ejemplar se re-propone en cada carga: pertenece al hallazgo abierto, y
        // arrastrar el de la ficha anterior sería peor que una caja vacía.
        PatternExemplar = ProposeExemplar();
        OnPropertyChanged(nameof(Title));
    }

    public void Load(string slug, Ulid id)
    {
        Slug = slug;
        Reload(id);
    }

    private void Reload(Ulid id)
    {
        // El estado del clon se relee en cada recarga de la ficha (F5.8 §1): entre abrirla y
        // volver a ella la carpeta ha podido moverse, y «Verificar ahora» no puede quedar
        // habilitado sobre un clon que ya no está.
        Link = _links.For(Slug);
        Finding = _hub.Store.TryReadFinding(Slug, id.ToString());
        History.Clear();
        Comments.Clear();
        Meta.Clear();
        if (Finding is null)
        {
            return;
        }

        Severity = Finding.Severity;
        RuleText = RuleCatalog.Find(Finding.RuleId)?.Look ?? string.Empty;

        // F33 — arreglar no resuelve (D-557): un arreglo sin veredicto después sigue pidiendo una
        // verificación aunque el ancla esté intacta, así que el botón tiene que enterarse.
        HasUnverifiedFix = UnverifiedFix(Finding);

        // Antes de pintar nada: dejar las ubicaciones apuntando a donde está el código (D-226).
        // Sin esto, la ficha acertaba con la línea pero lo anunciaba en 24 de 25 hallazgos, y un
        // aviso que sale siempre es el banner que había que quitar. Es idempotente: en cuanto la
        // ubicación está bien no escribe, así que abrir la ficha dos veces no toca el hub.
        RepairAnchors(Finding);

        // El historial se lee de lo más reciente a lo más antiguo: lo último que le pasó a este
        // hallazgo es lo que explica en qué estado está ahora.
        foreach (HistoryEntry h in Finding.History.OrderByDescending(h => h.Utc))
        {
            History.Add(new HistoryRow
            {
                Event = h.Event,
                Utc = h.Utc,
                By = h.By,
                Detail = h.Detail ?? string.Empty,
                SessionId = h.SessionId,
            });
        }

        foreach (Comment c in _hub.Store.ListComments(Slug, Finding.Id.ToString()).OrderByDescending(c => c.Utc))
        {
            Comments.Add(new CommentRow
            {
                By = c.By,
                Utc = c.Utc,
                Kind = c.Kind,
                Detail = c.Body,
            });
        }

        BuildMeta(Finding);
        LoadSnippet(Finding);
        // F6.9: las precondiciones del arreglo asistido se releen aquí por la misma razón que el
        // vínculo del clon — el árbol de trabajo y la sesión en curso cambian por debajo.
        RefreshAssistedFix();
        RaiseDerived();
    }

    /// <summary>
    /// La tarjeta de metadatos (F5.5 §2). El <b>ruleId</b> aterriza aquí: es la regla del checklist
    /// que motivó el hallazgo y desde V3 se puede buscar por ella, así que no se elimina — deja de
    /// flotar como texto suelto bajo el título y pasa a ser un campo con nombre.
    /// </summary>
    private void BuildMeta(Finding f)
    {
        Location? loc = f.Locations.FirstOrDefault();
        string app = _hub.Store.TryReadApp(Slug)?.Name ?? Slug;

        Meta.Add(new MetaRow(
            "Regla", f.RuleId,
            "Regla del checklist que motivó este hallazgo.", Mono: true));

        if (RuleText.Length > 0)
        {
            Meta.Add(new MetaRow("Qué busca", RuleText, "El criterio con el que el auditor la aplica."));
        }

        // UI-0026: un alias es un literal de máquina que se copia y se pega, igual que el
        // commit anclado. O van las dos en monoespaciada o ninguna.
        Meta.Add(new MetaRow(
            "Identificador", f.DisplayId ?? "(sin alias todavía)",
            "El alias es de presentación; la identidad es el ULID.", Mono: true));
        Meta.Add(new MetaRow("Aplicación", app));

        if (loc is not null)
        {
            string extra = f.Locations.Count > 1 ? $"  (+{f.Locations.Count - 1} ubicaciones más)" : string.Empty;
            Meta.Add(new MetaRow("Unidad", $"{loc.Path}:{loc.Line}{extra}", Mono: true));
        }

        Meta.Add(new MetaRow(
            "Origen", AuditModeNames.Display(f.Origin),
            "La clase de sesión en la que se detectó."));
        Meta.Add(new MetaRow(
            "Temática", ThemeCatalog.Display(f.Theme),
            "La lupa del ciclo que lo detectó. Solo un ciclo General o uno de esta misma temática "
            + "lo reconcilia; durante un ciclo de otra temática envejece sin que nadie lo mire."));
        Meta.Add(new MetaRow("Primera detección", Stamp(f.FirstDetected)));
        Meta.Add(new MetaRow("Última confirmación", Stamp(f.LastConfirmed)));
        Meta.Add(new MetaRow(
            "Detectado con", Judge(f),
            "Con qué casa y con qué modelo se vio por última vez. El modelo solo no basta: no dice "
            + "si detrás hubo un CLI local o el asiento de la organización, y es lo que hace legible "
            + "una discrepancia — dos casas distintas coincidiendo es una segunda opinión."));
        Meta.Add(new MetaRow(
            "Veces confirmado", f.TimesConfirmed.ToString(),
            "Cuántas auditorías han vuelto a verlo. Es lo que sostiene la confianza."));
        Meta.Add(new MetaRow(
            "Commit anclado", Short(f.LastConfirmed.Commit),
            $"El commit en el que se confirmó por última vez: {f.LastConfirmed.Commit}", Mono: true));

        if (f.Resolved is { } resolved)
        {
            Meta.Add(new MetaRow(
                "Resuelto", $"{resolved.Utc.ToLocalTime():dd/MM/yyyy} · {resolved.By} · vía {resolved.Via}",
                resolved.Justification));
        }

        AddUsageRow(f);
    }

    /// <summary>
    /// «Usado desde: N sitios» (F6.8 §4). Solo aparece cuando la recolección YA se hizo —al generar
    /// el prompt de arreglo—, porque el dato es gratis a partir de ahí y le da al humano el radio de
    /// impacto sin abrir el prompt. Nunca dispara una recolección: abrir una ficha no puede costar
    /// un barrido del clon.
    /// </summary>
    private void AddUsageRow(Finding f)
    {
        if (_lastReferences is not { } memo || memo.Finding != f.Id || !memo.Report.Collected)
        {
            return;
        }

        ReferenceReport refs = memo.Report;
        string value = refs.Total switch
        {
            0 => "sin llamadores en el clon",
            1 => "1 sitio",
            _ => $"{refs.Total} sitios",
        };

        var tip = new System.Text.StringBuilder();
        tip.Append(refs.Precision == ReferencePrecision.Texto
            ? "Por búsqueda de texto (aproximado). "
            : "Llamadores directos en el clon local. ");
        foreach (ReferenceSite site in refs.Sites.Take(5))
        {
            tip.Append($"\n{site.Path}:{site.Line}");
            if (site.Member is not null)
            {
                tip.Append($" — {site.Member}");
            }
        }

        if (refs.Total > Math.Min(refs.Sites.Count, 5))
        {
            tip.Append($"\n…y {refs.Total - Math.Min(refs.Sites.Count, 5)} más.");
        }

        Meta.Add(new MetaRow("Usado desde", value, tip.ToString()));
    }

    private static string Stamp(DetectionStamp stamp)
        => $"{stamp.Utc.ToLocalTime():dd/MM/yyyy HH:mm} · {stamp.By}";

    /// <summary>
    /// Quién lo juzgó la última vez: la casa y el modelo (F16 §C).
    /// <para>
    /// Se lee del último avistamiento y, si aquél no lo registró, del primero: los hallazgos de
    /// antes de F5.1b no guardaban modelo y los de antes de F14 no guardaban casa. Un proveedor en
    /// blanco es Copilot y no «desconocido» — no había otro (D-780).
    /// </para>
    /// <para>
    /// <b><c>auto</c> se escribe entero.</b> Es un modelo de verdad del selector de Copilot —el que
    /// deja elegir al proveedor—, así que el sello lo guarda tal cual y la ficha enseñaba «modelo
    /// auto», que se lee como una abreviatura rota. Se dice «modelo automático».
    /// </para>
    /// <para>
    /// <b>Y no se puede decir cuál salió.</b> Lo suyo sería enseñar el modelo real, pero el sello
    /// —<c>DetectionStamp</c>— no guarda con qué sesión se detectó, y el proveedor tampoco devuelve
    /// a qué resolvió su <c>auto</c>: no hay de dónde sacarlo. Enseñar el modelo configurado HOY
    /// sería peor que no decir nada, porque no es el que juzgó.
    /// </para>
    /// </summary>
    private static string Judge(Finding f)
    {
        string house = ProviderNames.Display(f.LastConfirmed.Provider ?? f.FirstDetected.Provider);
        string? model = f.LastConfirmed.Model ?? f.FirstDetected.Model;
        if (model is not { Length: > 0 })
        {
            return house;
        }

        string named = model.Equals(AutoModel, StringComparison.OrdinalIgnoreCase) ? "automático" : model;
        return $"{house} · modelo {named}";
    }

    /// <summary>El modelo que delega la elección en el proveedor. No es un id de modelo real.</summary>
    private const string AutoModel = "auto";

    private static string Short(string? sha)
        => string.IsNullOrWhiteSpace(sha) ? "—" : (sha!.Length <= 8 ? sha : sha[..8]);

    /// <summary>
    /// Lee el código del clon (F5.5 §3): el miembro completo, con los números de línea del fichero
    /// y el aviso correspondiente si lo que hay ya no es lo que se auditó.
    /// </summary>
    /// <summary>Nunca tumba la ficha: no poder corregir el ancla no impide leer el hallazgo.</summary>
    private void RepairAnchors(Finding f)
    {
        try
        {
            _anchors?.Repair(Slug, f, _machines.Load().ClonePathFor(Slug));
        }
        catch (Exception)
        {
            // Se pinta con lo que hay; el aviso del panel dirá lo que se sepa.
        }
    }

    private void LoadSnippet(Finding f)
    {
        Location? loc = f.Locations.FirstOrDefault();

        // F6.7: el panel se pide POR HALLAZGO, no por ubicación. El anclaje dice qué relación hay
        // entre el clon y lo que se auditó; el estado dice si eso es un problema — y sobre un
        // resuelto no lo es, porque ese cambio en el código es precisamente el arreglo.
        // Las huellas de los arreglos viajan con la petición (F12 §H.5): son lo que permite que el
        // aviso distinga «alguien cambió este código» de «lo cambió Atalaya, y falta verificarlo».
        SnippetPanel panel = SnippetReader.ForFinding(
            _machines.Load().ClonePathFor(Slug), f, _hub.Store.ListFixes(Slug));

        SnippetPath = loc?.Path ?? string.Empty;
        SnippetState = panel.State;
        SnippetTone = panel.Tone;
        SnippetNoticeOffersVerify = panel.CanVerify;
        OnPropertyChanged(nameof(IsVerificationPending));
        SnippetFirstLine = panel.FirstLine;
        SnippetHighlightLine = panel.HighlightLine;
        SnippetNotice = panel.Notice;
        Snippet = panel.Text;
        SnippetCaption = loc is null ? string.Empty : panel.Caption(loc.Path, panel.HighlightLine);
        SnippetMember = loc is null ? string.Empty : panel.Member ?? string.Empty;
        SnippetWhere = loc is null
            ? string.Empty
            : panel.HighlightLine > 0 ? $"{loc.Path}:{panel.HighlightLine}" : loc.Path;
    }

    private Ulid Id => Finding!.Id;

    // ------------------------------------------------------------------ gobernanza

    [RelayCommand]
    private void Silence()
    {
        if (Finding is null)
        {
            return;
        }

        if (SilenceExpiryDays < 0)
        {
            _toasts.Show("La caducidad no puede ser negativa: 0 días es un silencio permanente.");
            return;
        }

        DateTimeOffset? expiry = SilenceExpiryDays > 0 ? DateTimeOffset.UtcNow.AddDays(SilenceExpiryDays) : null;
        string? notes = string.IsNullOrWhiteSpace(SilenceNotes) ? null : SilenceNotes;

        // F5.12: el mismo formulario, dos alcances. Lo que cambia no es el motivo ni la caducidad
        // —la disciplina de gobernanza es la misma— sino sobre qué recae la decisión.
        if (SilenceScope == SilenceScope.Patron)
        {
            SilencePattern(notes, expiry);
            return;
        }

        _governance.Silence(Slug, Id, SilenceReason, notes, expiry);
        _toasts.Show(expiry is null
            ? "Silenciado de forma permanente."
            : $"Silenciado hasta el {expiry.Value.ToLocalTime():dd/MM/yyyy}.");
        Reload(Id);
    }

    /// <summary>
    /// El alcance ampliado (F5.12): este TIPO de problema deja de reportarse en ESTA aplicación.
    /// No hay diálogo de confirmación porque no queda ninguna pregunta que hacer: la frase se ha
    /// visto y editado aquí mismo, y el hallazgo origen se silencia con el patrón porque es, por
    /// construcción, del tipo que se acaba de callar.
    /// </summary>
    private void SilencePattern(string? notes, DateTimeOffset? expiry)
    {
        string exemplar = PatternExemplar.Trim();
        if (exemplar.Length == 0)
        {
            _toasts.Show("Escribe la frase que describe el tipo de problema: es lo que leerá el auditor.");
            return;
        }

        GovernanceService.PatternSilenceResult result =
            _governance.SilencePattern(Slug, Id, exemplar, SilenceReason, notes, expiry);

        string until = expiry is null
            ? "de forma permanente"
            : $"hasta el {expiry.Value.ToLocalTime():dd/MM/yyyy}";
        _toasts.Show($"Patrón {result.Pattern.ShortId} silenciado en {AppName} {until}: «{exemplar}»."
            + (result.SilencedSource ? " Este hallazgo queda silenciado." : ""));
        Reload(Id);
    }

    [RelayCommand]
    private void Unsilence()
    {
        if (Finding is null)
        {
            return;
        }

        _governance.Unsilence(Slug, Id);
        _toasts.Show("Des-silenciado: vuelve a contar en informes y auditorías.");
        Reload(Id);
    }

    /// <summary>
    /// Asignar. F5.5 la retira de la vista pero NO del view-model: el campo sigue en el modelo de
    /// datos y esta es la costura por la que volvería si algún día se usa.
    /// </summary>
    [RelayCommand]
    private void ApplyAssign()
    {
        if (Finding is null)
        {
            return;
        }

        _governance.Assign(Slug, Id, string.IsNullOrWhiteSpace(Assignee) ? null : Assignee.Trim());
        _toasts.Show("Asignación actualizada.");
        Reload(Id);
    }

    [RelayCommand]
    private void ApplySeverity()
    {
        if (Finding is null)
        {
            return;
        }

        if (Severity == Finding.Severity)
        {
            _toasts.Show($"El hallazgo ya es de severidad {SeverityNames.Display(Severity)}.");
            return;
        }

        Severity target = Severity;
        _governance.ChangeSeverity(Slug, Id, target);
        _toasts.Show($"Severidad reclasificada a {SeverityNames.Display(target)}, con tu nombre en el historial.");
        Reload(Id);
    }

    [RelayCommand]
    private void ResolveManually()
    {
        if (Finding is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Justification))
        {
            _toasts.Show("La resolución manual exige justificación: es lo único que queda escrito de por qué se cerró.");
            ManualResolutionExpanded = true;
            return;
        }

        _governance.ResolveManually(Slug, Id, Justification.Trim(), GitInfo.HeadSha(_machines.Load().ClonePathFor(Slug)));
        Justification = string.Empty;
        _toasts.Show("Resuelto manualmente y registrado con tu nombre.");
        Reload(Id);
    }

    [RelayCommand]
    private void Reopen()
    {
        if (Finding is null)
        {
            return;
        }

        if (Status != FindingStatus.Resuelto)
        {
            _toasts.Show("Solo se reabre lo que está resuelto.");
            return;
        }

        _governance.Reopen(Slug, Id, "reabierto manualmente");
        _toasts.Show("Reabierto: vuelve a estar activo.");
        Reload(Id);
    }

    [RelayCommand]
    private void AddComment()
    {
        if (Finding is null || string.IsNullOrWhiteSpace(NewComment))
        {
            return;
        }

        _governance.AddComment(Slug, Id, NewComment.Trim());
        NewComment = string.Empty;
        Reload(Id);
    }

    /// <summary>
    /// Cierra la disputa dando la razón al auditor que discrepó (F5.1b): se silencia como falso
    /// positivo, con tu nombre. No es una resolución — nunca hubo nada que arreglar.
    /// </summary>
    [RelayCommand]
    private void AcceptDispute()
    {
        if (Finding is null || Finding.Disputes.Count == 0)
        {
            _toasts.Show("Este hallazgo no tiene ninguna disputa abierta.");
            return;
        }

        _governance.ResolveDisputeAsFalsePositive(
            Slug, Id, string.IsNullOrWhiteSpace(SilenceNotes) ? null : SilenceNotes.Trim());
        _toasts.Show("Disputa aceptada: silenciado como falso positivo.");
        Reload(Id);
    }

    /// <summary>Cierra la disputa dando la razón a quien lo reportó: sigue siendo un defecto.</summary>
    [RelayCommand]
    private void DismissDispute()
    {
        if (Finding is null || Finding.Disputes.Count == 0)
        {
            _toasts.Show("Este hallazgo no tiene ninguna disputa abierta.");
            return;
        }

        _governance.DismissDispute(
            Slug, Id, string.IsNullOrWhiteSpace(SilenceNotes) ? null : SilenceNotes.Trim());
        _toasts.Show("Disputa descartada: sigue siendo un defecto.");
        Reload(Id);
    }

    // ------------------------------------------------------------------ acciones

    /// <summary>
    /// Re-verifica ESTE hallazgo (§5.4). Bajó de V3 en F5.4: un verify masivo sobre una selección
    /// no dejaba ver qué se le estaba preguntando al agente sobre cada uno.
    /// </summary>
    [RelayCommand]
    private async Task Verify()
    {
        if (Finding is null)
        {
            return;
        }

        // Verificar LEE el clon en los dos caminos: el auditor necesita re-anclar el fragmento y la
        // re-medición necesita el fichero. Sin clon no hay contra qué comprobar (F5.8 §3).
        if (!CanAudit)
        {
            _toasts.Show(AuditDisabledTooltip);
            return;
        }

        Ulid id = Id;
        IsBusy = true;
        VerifySteps.Start();
        try
        {
            // F30 §4 — los pasos salen BAJO EL BOTÓN que se acaba de pulsar, no en un toast:
            // «Verificando el hallazgo…» decía que había empezado y nada más, y esta pantalla se
            // quedaba quieta lo que tardara el agente. Lo que tarda el agente es ahora un paso en
            // curso con su reloj, igual que los demás.
            VerifyOutcome outcome = await Task.Run(
                () => _verify.RunAsync(Slug, new[] { id }, CancellationToken.None, VerifySteps));

            // F6.6 — el aviso dice el RESULTADO, no si hubo resultado. «El verify no pudo emitir
            // veredicto» era literalmente cierto y completamente inútil: no decía qué había pasado
            // ni qué hacer, y el usuario lo leyó tres veces sin enterarse de nada.
            _toasts.Show(outcome.Toast);
        }
        catch (Exception ex)
        {
            _toasts.Show($"No se pudo verificar: {ex.Message}");
        }
        finally
        {
            VerifySteps.Finish();
            IsBusy = false;
            Reload(id);
        }
    }

    /// <summary>
    /// Abre la ubicación principal en el editor configurado (§8), con tope de tiempo (F5.5 §6).
    /// El éxito es silencioso: la prueba de que funcionó es el editor abriéndose.
    /// </summary>
    [RelayCommand]
    private async Task OpenInEditor()
    {
        if (Finding is null || Finding.Locations.Count == 0)
        {
            _toasts.Show("Este hallazgo no tiene una ubicación que abrir.");
            return;
        }

        Location loc = Finding.Locations[0];

        // D-021 — la línea que se manda es la RE-ANCLADA cuando la hay. El panel del snippet ya
        // resolvió dónde está el código de verdad: mandar `loc.Line` cuando la unidad ha cambiado
        // es mandar a leer otra cosa creyendo que es la suya. Y cuando no se ancla, se abre en la
        // original y el toast lo dice, en vez de fingir precisión.
        (int line, LineOrigin origin) = LineToOpen(loc);

        _toasts.Show("Abriendo en el editor…");
        EditorOpenResult result = await _editor.OpenAsync(Slug, loc.Path, line, origin, loc.Line);
        _toasts.Show(EditorLauncher.Toast(result));
    }

    /// <summary>
    /// Qué línea se abre y de dónde sale (D-021). El estado del snippet es quien lo sabe: es el
    /// mismo cálculo que ya pinta el panel, así que la ficha y el editor no pueden discrepar.
    /// </summary>
    internal (int Line, LineOrigin Origin) LineToOpen(Location loc) => SnippetState switch
    {
        // El código está, pero en otro sitio: ahí se va, diciendo de dónde venía.
        SnippetState.Movido or SnippetState.Reanclado when SnippetHighlightLine > 0
            => (SnippetHighlightLine, LineOrigin.Reanclada),

        // Ni el código anclado ni el símbolo aparecen: la original, y se dice.
        SnippetState.Cambiado or SnippetState.NoLocalizado => (loc.Line, LineOrigin.SinAnclar),

        _ => (loc.Line, LineOrigin.Anclada),
    };

    /// <summary>
    /// Lo que dice el botón mientras se busca quién usa el código (F6.8 §3): la recolección
    /// recorre el clon y en una solución grande eso se nota. Un botón que no responde y no dice
    /// nada se pulsa otra vez.
    /// </summary>
    [ObservableProperty]
    private string _fixPromptActionLabel = "Generar prompt de arreglo";

    /// <summary>
    /// El encargo para el agente (§5.7), <b>con sus referencias</b> (F6.8). La recolección va en
    /// un hilo de fondo y con presupuesto de tiempo: la UI nunca se bloquea y la generación nunca
    /// tarda minutos. Si la recolección no puede, el prompt sale igual con el aviso de que va sin
    /// ellas — no generarlo sería peor que generarlo incompleto.
    /// </summary>
    [RelayCommand]
    private async Task GenerateFixPrompt()
    {
        if (Finding is null)
        {
            return;
        }

        Finding target = Finding;
        Ulid id = Id;
        string? clone = _machines.Load().ClonePathFor(Slug);

        ReferenceReport refs;
        FixPromptActionLabel = "Buscando quién usa este código…";
        try
        {
            refs = await Task.Run(() => _references.Collect(clone, target));
        }
        finally
        {
            FixPromptActionLabel = "Generar prompt de arreglo";
        }

        _lastReferences = (id, refs);

        // F7: el prompt old school viaja con las convenciones de la casa, igual que la sesión
        // interactiva. Lo que cambia es la salida del conflicto: aquí no hay a quien preguntar, así
        // que se declara como riesgo (FixPromptBuilder, regla 7).
        DirectiveBundle directives = _directives?.Bundle(Slug, clone, DirectiveScope.Arreglo)
                                     ?? DirectiveBundle.Empty;
        string prompt = FixPromptBuilder.Build(target, refs, directives);
        bool copied = true;
        try
        {
            Clipboard.SetText(prompt);
        }
        catch
        {
            // Sin portapapeles el prompt no se pierde: queda guardado como comentario.
            copied = false;
        }

        _governance.AddComment(Slug, id, prompt, kind: "fix-prompt");
        _toasts.Show((copied
            ? "Prompt de arreglo copiado al portapapeles y guardado en los comentarios."
            : "Prompt de arreglo guardado en los comentarios (el portapapeles no estaba disponible).")
            + " " + ReferenceSummary(refs));
        Reload(id);
    }

    // ------------------------------------------------------------------ F33: pide verificación

    /// <summary>
    /// <b>Este hallazgo PIDE una verificación</b> (F33). Es lo que pone verde el botón de la
    /// botonera, y lo que hace que el aviso no necesite un segundo botón con el mismo rótulo.
    /// <para>
    /// <b>Una acción, un botón.</b> Había dos «Verificar ahora» a la vez —uno dentro del aviso
    /// ámbar y otro en la botonera— haciendo exactamente lo mismo con el mismo comando. Dos botones
    /// iguales no son dos caminos: son una duda sobre cuál es el bueno. El estado se enseña con el
    /// <b>estilo</b> del que ya está, no duplicándolo.
    /// </para>
    /// <para>
    /// <b>Los dos estados que lo piden, y de dónde salen.</b> El <b>anclaje</b>, por
    /// <c>SnippetPanel.OffersVerify</c> —cambiado, movido, re-anclado, no localizado y fichero que
    /// ya no está (D-225, BUGFIX-ANCLA)—, que el estado del hallazgo puede retirar y nunca añadir:
    /// sobre un resuelto o un silenciado no se pide nada. Y el <b>arreglo sin verificar</b> (D-557):
    /// arreglar no resuelve, así que un <c>FixProposed</c> o un <c>FixCommitted</c> sin veredicto
    /// después deja el hallazgo a medio cerrar aunque el ancla esté intacta.
    /// </para>
    /// </summary>
    public bool IsVerificationPending => SnippetNoticeOffersVerify || HasUnverifiedFix;

    /// <summary>
    /// <b>CUÁL de los dos estados de F33 pide la verificación</b> (F34). Es lo que viaja a
    /// <see cref="AssistedFixLauncher.Check"/> para que «Arreglar con agente» se apague con la
    /// razón al lado, y la razón dice cuál es: un ancla perdida se recupera mirando el código y un
    /// arreglo sin veredicto se cierra sabiendo si funcionó, así que no son la misma frase.
    /// <para>
    /// El anclaje va PRIMERO cuando coinciden: sin ancla no se sabe siquiera dónde miraría el
    /// verificador el arreglo anterior, así que es la que hay que resolver antes.
    /// </para>
    /// </summary>
    private PendingVerification PendingVerificationState => SnippetNoticeOffersVerify
        ? PendingVerification.AnclaPerdida
        : HasUnverifiedFix
            ? PendingVerification.ArregloSinVerificar
            : PendingVerification.Ninguna;

    /// <summary>
    /// Hay un arreglo asistido posterior al último veredicto (D-557). Se mira el HISTORIAL y no un
    /// campo, porque no existe ninguno: lo que el modelo guarda son los eventos, y el orden entre
    /// ellos es el dato — un arreglo de ayer verificado hoy no pide nada.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVerificationPending))]
    private bool _hasUnverifiedFix;

    /// <summary>
    /// Los eventos que cierran la pregunta que abre un arreglo: un veredicto del verificador, o una
    /// resolución por cualquier vía. Un <c>Reanchored</c> también cuenta — lo escribe el verify
    /// cuando el auditor no llegó a pronunciarse pero sí se miró el código.
    /// </summary>
    private static readonly FindingEvent[] ClosesTheFix =
    {
        FindingEvent.Confirmed, FindingEvent.Resolved, FindingEvent.Disputed,
        FindingEvent.Inconclusive, FindingEvent.Reanchored, FindingEvent.NotLocated,
    };

    private static bool UnverifiedFix(Finding f)
    {
        if (f.Status is FindingStatus.Resuelto or FindingStatus.Silenciado)
        {
            return false;
        }

        int fixedAt = -1;
        int verifiedAt = -1;
        for (int i = 0; i < f.History.Count; i++)
        {
            FindingEvent kind = f.History[i].Event;
            if (kind is FindingEvent.FixProposed or FindingEvent.FixCommitted)
            {
                fixedAt = i;
            }
            else if (ClosesTheFix.Contains(kind))
            {
                verifiedAt = i;
            }
        }

        return fixedAt >= 0 && verifiedAt < fixedAt;
    }

    // ------------------------------------------------------------------ F33: copiar los bloques

    /// <summary>
    /// <b>El hallazgo, como texto plano</b> (F33): el título y después descripción, impacto y
    /// recomendación con sus párrafos, separados por una línea en blanco. Sin markdown y sin
    /// metadatos — esto se pega en un correo o en un prompt, y unos asteriscos ahí son ruido.
    /// </summary>
    internal static string FindingText(Finding f)
    {
        var parts = new List<string> { f.Title.Trim() };
        Append(parts, "Descripción", f.Description);
        Append(parts, "Impacto", f.Impact);
        Append(parts, "Recomendación", f.Recommendation);
        return string.Join("\n\n", parts);

        static void Append(List<string> into, string label, string? body)
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                into.Add($"{label}\n{body!.Trim()}");
            }
        }
    }

    /// <summary>
    /// <b>Los metadatos, como texto plano</b> (F33): una línea por fila, <c>Etiqueta: valor</c>, en
    /// el orden en que se ven.
    /// <para>
    /// Con una excepción declarada: el «(+7 ubicaciones más)» de la fila «Unidad» se <b>expande</b>
    /// a las rutas reales, una por línea. En pantalla el resumen está bien porque las ubicaciones
    /// tienen su propia lista debajo; copiado no sirve de nada — quien lo pega en un correo quiere
    /// las rutas, que es justo lo que ese paréntesis esconde.
    /// </para>
    /// </summary>
    internal static string MetaText(IEnumerable<MetaRow> rows, Finding f)
    {
        var lines = new List<string>();
        foreach (MetaRow row in rows)
        {
            if (row.Label == "Unidad" && f.Locations.Count > 1)
            {
                lines.Add($"{row.Label}: {f.Locations[0].Path}:{f.Locations[0].Line}");
                foreach (Location extra in f.Locations.Skip(1))
                {
                    lines.Add($"{extra.Path}:{extra.Line}");
                }

                continue;
            }

            lines.Add($"{row.Label}: {row.Value}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>Copia «El hallazgo» al portapapeles, con el mismo aviso que las demás copias.</summary>
    [RelayCommand]
    private void CopyFinding() => CopyToClipboard(
        Finding is null ? string.Empty : FindingText(Finding), "El hallazgo");

    /// <summary>Copia «Metadatos» al portapapeles.</summary>
    [RelayCommand]
    private void CopyMeta() => CopyToClipboard(
        Finding is null ? string.Empty : MetaText(Meta, Finding), "Los metadatos");

    /// <summary>
    /// El portapapeles, con el mismo comportamiento que las copias que ya había: si no está
    /// disponible se dice, en vez de fingir que se copió (el patrón de «Copiar error» y del prompt
    /// de arreglo).
    /// </summary>
    private void CopyToClipboard(string text, string what)
    {
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
            _toasts.Show($"{what} copiado al portapapeles.");
        }
        catch (Exception)
        {
            _toasts.Show($"No se pudo copiar {what.ToLowerInvariant()}: el portapapeles no estaba disponible.");
        }
    }

    // ------------------------------------------------------------------ arreglar con agente (F6.9)

    /// <summary>
    /// El botón existe cuando la función está activada. Que se pueda PULSAR es otra cosa —clon,
    /// árbol limpio, nadie más usando el agente— y eso lo dice <see cref="CanStartFix"/>.
    /// </summary>
    [ObservableProperty]
    private bool _showAssistedFix;

    /// <summary>Se puede lanzar ahora mismo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssistedFixTooltip))]
    private bool _canStartFix;

    /// <summary>Qué falta, cuando no se puede. Se escribe bajo el botón gris.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssistedFixTooltip))]
    private string _assistedFixBlock = string.Empty;

    /// <summary>
    /// Qué hace el botón, o qué falta para poder pulsarlo. Un botón gris sin explicación se pulsa
    /// otra vez y luego se da por roto (misma lección que D-529).
    /// </summary>
    public string AssistedFixTooltip => CanStartFix
        ? "Abre una sesión con el agente: arregla este hallazgo sobre tu clon local explicando lo "
          + "que hace y preguntándote en las decisiones. No commitea nada."
          + (AssistedFixEngine.Length == 0 ? string.Empty : $" Se arreglará con {AssistedFixEngine}.")
        : AssistedFixBlock;

    /// <summary>
    /// Con quién se arreglaría: «Claude Code, modelo opus» (F16). Se dice en el botón porque es el
    /// último sitio antes de gastar, y porque el diff que salga de ahí hay que poder atribuirlo.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssistedFixTooltip))]
    private string _assistedFixEngine = string.Empty;

    /// <summary>
    /// Vuelve a mirar las precondiciones. Se llama al cargar la ficha y tras vincular el clon: el
    /// estado que gobierna este botón (el árbol de trabajo, la sesión en curso) cambia por debajo
    /// sin que la ficha se entere.
    /// </summary>
    private void RefreshAssistedFix()
    {
        if (_fixLauncher is null || _fix is null)
        {
            ShowAssistedFix = false;
            CanStartFix = false;
            AssistedFixBlock = string.Empty;
            return;
        }

        FixLaunchDecision decision = _fixLauncher.Check(Slug, Finding, pending: PendingVerificationState);
        ShowAssistedFix = decision.Block != FixBlock.Desactivado;
        CanStartFix = decision.CanStart;
        AssistedFixBlock = decision.Message;
        AssistedFixEngine = _fixLauncher.EngineLabel;
    }

    /// <summary>
    /// Lanza el arreglo asistido y se va a su vista. El generador de prompt de al lado NO se toca:
    /// son dos caminos, el de siempre y el nuevo, y quien prefiera el suyo lo tiene donde estaba.
    /// </summary>
    [RelayCommand]
    private async Task StartAssistedFix()
    {
        if (Finding is null || _fix is null || _fixLauncher is null)
        {
            return;
        }

        // Se vuelve a comprobar aquí, no solo al pintar: entre abrir la ficha y pulsar, el usuario
        // ha podido editar el clon o lanzar una auditoría.
        FixLaunchDecision decision = _fixLauncher.Check(Slug, Finding, pending: PendingVerificationState);
        RefreshAssistedFix();
        if (!decision.CanStart)
        {
            _toasts.Show(decision.Message);
            return;
        }

        Ulid id = Id;
        string slug = Slug;

        // Navegar primero: la sesión narra desde el primer segundo y hay que estar delante para
        // verlo. Y el arranque NO se espera aquí — la sesión dura minutos.
        if (_navigation is not null)
        {
            await _navigation.NavigateToAsync<AssistedFixViewModel>();
        }

        _ = _fix.StartAsync(new FixSessionRequest(slug, id));
    }

    /// <summary>
    /// Del historial al informe del arreglo (H9.1 §1). El evento «arreglo propuesto» dice que
    /// alguien intentó arreglar esto; sin este camino, saber QUÉ hizo exigía buscar el informe a
    /// mano en V7 entre todos los de la aplicación.
    /// </summary>
    [RelayCommand]
    private Task OpenSessionReport(HistoryRow? row)
        => row is { SessionId: { Length: > 0 } sessionId } && _navigation is not null
            ? _navigation.NavigateToAsync<ReportsViewModel>(vm => vm.ShowReport(Slug, sessionId))
            : Task.CompletedTask;

    /// <summary>La frase del aviso sobre las referencias: qué se encontró, o por qué no se miró.</summary>
    private static string ReferenceSummary(ReferenceReport refs)
    {
        if (!refs.Collected)
        {
            return $"Va sin la lista de llamadores: {refs.Unavailable}.";
        }

        string approximate = refs.Precision == ReferencePrecision.Texto
            ? " (por búsqueda de texto: aproximadas)"
            : string.Empty;

        return refs.Total switch
        {
            0 => "No se encontraron llamadores en el clon" + approximate + ".",
            1 => "Incluye 1 sitio de uso" + approximate + ".",
            _ => $"Incluye {refs.Total} sitios de uso{approximate}.",
        };
    }
}
