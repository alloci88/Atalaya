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
public sealed partial class FindingDetailViewModel : ViewModelBase
{
    private readonly HubContext _hub;
    private readonly GovernanceService _governance;
    private readonly MachineConfigStore _machines;
    private readonly VerifyCoordinator _verify;
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
    /// Quién hace la pregunta del alcance «toda la aplicación» (F5.10): qué se hace con los
    /// hallazgos que ya existen de esa regla. No se puede excluir sin pasar por aquí.
    /// </summary>
    private readonly IExcludeRuleConfirmer _excludeConfirmer;

    public FindingDetailViewModel(
        HubContext hub,
        GovernanceService governance,
        MachineConfigStore machines,
        VerifyCoordinator verify,
        EditorLauncher editor,
        ToastCenter toasts,
        CloneLinkService links,
        LinkCloneFlow linkFlow,
        IExcludeRuleConfirmer excludeConfirmer,
        AnchorRepair? anchors = null)
    {
        _hub = hub;
        _excludeConfirmer = excludeConfirmer;
        ScopeOptions = new[]
        {
            new SilenceScopeOption(SilenceScope.Hallazgo, scope => SilenceScope = scope) { IsSelected = true },
            new SilenceScopeOption(SilenceScope.Regla, scope => SilenceScope = scope),
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
    }

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
    }

    public override string Title => Finding is null ? "Hallazgo" : $"{Finding.DisplayId ?? Finding.Id.ToString()}";

    [ObservableProperty] private string _slug = string.Empty;
    [ObservableProperty] private Finding? _finding;
    [ObservableProperty] private string _ruleText = string.Empty;

    // Governance inputs
    /// <summary>
    /// El alcance del silencio (F5.10). Arranca siempre en «solo este hallazgo»: es el gesto de
    /// todos los días, y el que no puede equivocarse por inercia. Excluir una regla entera se
    /// elige a propósito.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SilenceActionLabel))]
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
    [ObservableProperty] private string _snippetNotice = string.Empty;
    [ObservableProperty] private string _snippetPath = string.Empty;
    [ObservableProperty] private SnippetState _snippetState = SnippetState.SinUbicacion;

    /// <summary>Hay código que pintar. Si no, el panel se retira y queda solo el aviso.</summary>
    public bool HasSnippet => Snippet.Length > 0;

    /// <summary>Hay algo que advertir sobre el código antes de que se lea.</summary>
    public bool HasSnippetNotice => SnippetNotice.Length > 0;

    /// <summary>El aviso lleva «Verificar ahora» solo cuando verificar arregla lo que avisa.</summary>
    public bool SnippetNoticeOffersVerify => SnippetPanel.OffersVerify(SnippetState);

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

    /// <summary>Solo se des-silencia lo silenciado.</summary>
    public bool CanUnsilence => Status == FindingStatus.Silenciado;

    /// <summary>Silenciar algo ya silenciado no hace nada: el botón se retira.</summary>
    public bool CanSilence => Status != FindingStatus.Silenciado;

    /// <summary>«Reabrir» aparece SOLO si está resuelto (F5.5 §4).</summary>
    public bool CanReopen => Status == FindingStatus.Resuelto;

    /// <summary>Un hallazgo ya resuelto no se vuelve a resolver a mano.</summary>
    public bool CanResolveManually => Status != FindingStatus.Resuelto;

    /// <summary>El silencio vigente, escrito: motivo, autor y caducidad.</summary>
    public string SilenceSummary
    {
        get
        {
            if (Finding is null)
            {
                return string.Empty;
            }

            Silence? silence = _hub.Store.TryReadSilence(Slug, Finding.Id);
            if (silence is null)
            {
                return string.Empty;
            }

            string expiry = silence.ExpiresUtc is null
                ? "permanente"
                : $"caduca el {silence.ExpiresUtc.Value.ToLocalTime():dd/MM/yyyy}";

            // F5.10: un silencio nacido de una exclusión de regla lo DICE. Sin esto, la ficha
            // afirmaba que alguien había mirado este caso concreto y decidido sobre él, cuando lo
            // que hubo fue una decisión sobre la regla entera — y de ahí salen las dos preguntas
            // que nadie podría contestar: por qué está silenciado y a quién preguntarle.
            if (!string.IsNullOrEmpty(silence.ByRuleExclusion))
            {
                return $"Silenciado por exclusión de regla ({silence.ByRuleExclusion}, por {silence.By})"
                    + $" · {SilenceReasonNames.Display(silence.Reason)} · {expiry}";
            }

            return $"Silenciado por {silence.By} · {SilenceReasonNames.Display(silence.Reason)} · {expiry}";
        }
    }

    public bool HasSilence => SilenceSummary.Length > 0;

    // ------------------------------------------------------------------ F5.10 · alcance

    /// <summary>El nombre de la app del hallazgo: lo que se lee en el texto de consecuencia.</summary>
    public string AppName
    {
        get
        {
            string? name = Slug.Length == 0 ? null : _hub.Store.TryReadApp(Slug)?.Name;
            return string.IsNullOrWhiteSpace(name) ? Slug : name!;
        }
    }

    /// <summary>La regla del hallazgo abierto. Es lo que se excluiría con el alcance ampliado.</summary>
    public string RuleId => Finding?.RuleId ?? string.Empty;

    /// <summary>
    /// Las dos opciones de alcance, cada una con la frase que dice QUÉ PASA si se elige. La
    /// consecuencia no es un tooltip: es lo que separa «silenciar esto» de «dejar de mirar esto en
    /// toda la aplicación», y quien las confunde no se entera hasta la auditoría siguiente.
    /// </summary>
    public IReadOnlyList<SilenceScopeOption> ScopeOptions { get; }

    /// <summary>
    /// Re-escribe los textos de las opciones con la regla y la app del hallazgo abierto. Se
    /// re-escriben en vez de reconstruirse para no perder el radio marcado en cada recarga.
    /// </summary>
    private void RefreshScopeOptions()
    {
        SilenceScopeOption solo = ScopeOptions[0];
        solo.Label = "Solo este hallazgo";
        solo.Consequence = "Este caso concreto deja de contar. La regla sigue vigente: otras "
            + "auditorías pueden volver a reportarla en otros sitios.";

        SilenceScopeOption regla = ScopeOptions[1];
        regla.Label = RuleId.Length == 0
            ? "Esta regla en toda la aplicación"
            : $"Esta regla en toda la aplicación ({RuleId})";
        regla.Consequence = RuleId.Length == 0
            ? "Ninguna auditoría de esta aplicación volverá a reportar esta regla."
            : $"Ninguna auditoría de {AppName} volverá a reportar {RuleId}.";
    }

    /// <summary>El botón cambia de nombre con el alcance: no hace lo mismo en los dos.</summary>
    public string SilenceActionLabel
        => SilenceScope == SilenceScope.Regla ? "Excluir la regla" : "Silenciar";

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

    partial void OnSnippetStateChanged(SnippetState value)
        => OnPropertyChanged(nameof(SnippetNoticeOffersVerify));

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
        OnPropertyChanged(nameof(CanUnsilence));
        OnPropertyChanged(nameof(CanReopen));
        OnPropertyChanged(nameof(CanResolveManually));
        OnPropertyChanged(nameof(SilenceSummary));
        OnPropertyChanged(nameof(HasSilence));
        OnPropertyChanged(nameof(AppName));
        OnPropertyChanged(nameof(RuleId));
        RefreshScopeOptions();
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
        RuleText = Atalaya.Copilot.RuleCatalog.Find(Finding.RuleId)?.Look ?? string.Empty;

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

        Meta.Add(new MetaRow(
            "Identificador", f.DisplayId ?? "(sin alias todavía)",
            "El alias es de presentación; la identidad es el ULID."));
        Meta.Add(new MetaRow("Aplicación", app));

        if (loc is not null)
        {
            string extra = f.Locations.Count > 1 ? $"  (+{f.Locations.Count - 1} ubicaciones más)" : string.Empty;
            Meta.Add(new MetaRow("Unidad", $"{loc.Path}:{loc.Line}{extra}", Mono: true));
        }

        Meta.Add(new MetaRow(
            "Origen", AuditModeNames.Display(f.Origin),
            "La clase de sesión en la que se detectó."));
        Meta.Add(new MetaRow("Primera detección", Stamp(f.FirstDetected)));
        Meta.Add(new MetaRow("Última confirmación", Stamp(f.LastConfirmed)));
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
    }

    private static string Stamp(DetectionStamp stamp)
        => $"{stamp.Utc.ToLocalTime():dd/MM/yyyy HH:mm} · {stamp.By}";

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
        SnippetPanel panel = SnippetReader.Read(
            _machines.Load().ClonePathFor(Slug), loc, f.LastConfirmed.Commit,
            SymbolAnchor.Candidates(f.Symbol, f.Title));

        SnippetPath = loc?.Path ?? string.Empty;
        SnippetState = panel.State;
        SnippetFirstLine = panel.FirstLine;
        SnippetHighlightLine = panel.HighlightLine;
        SnippetNotice = panel.Notice;
        Snippet = panel.Text;
        SnippetCaption = loc is null ? string.Empty : panel.Caption(loc.Path, panel.HighlightLine);
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

        // F5.10: el mismo formulario, dos alcances. Lo que cambia no es el motivo ni la caducidad
        // —la disciplina de gobernanza es la misma— sino sobre qué recae la decisión.
        if (SilenceScope == SilenceScope.Regla)
        {
            ExcludeRule(notes, expiry);
            return;
        }

        _governance.Silence(Slug, Id, SilenceReason, notes, expiry);
        _toasts.Show(expiry is null
            ? "Silenciado de forma permanente."
            : $"Silenciado hasta el {expiry.Value.ToLocalTime():dd/MM/yyyy}.");
        Reload(Id);
    }

    /// <summary>
    /// El alcance ampliado (F5.10): la regla deja de aplicar a ESTA aplicación. Antes de escribir
    /// nada se pregunta qué hacer con los hallazgos que ya existen — nunca se decide por omisión.
    /// </summary>
    private void ExcludeRule(string? notes, DateTimeOffset? expiry)
    {
        string rule = Finding!.RuleId;
        int active = _governance.CountActiveWithRule(Slug, rule);

        // Sin hallazgos activos no hay nada que preguntar: la pregunta es qué hacer con ELLOS.
        // Un diálogo que dice «hay 0 hallazgos, ¿los silencio?» es un clic sin contenido.
        ExcludeRuleChoice choice = active == 0
            ? ExcludeRuleChoice.ExcludeOnly
            : _excludeConfirmer.Ask(new ExcludeRuleConfirmation(rule, AppName, active));

        if (choice == ExcludeRuleChoice.Cancel)
        {
            _toasts.Show("Exclusión cancelada: no se ha tocado nada.");
            return;
        }

        GovernanceService.RuleExclusionResult result = _governance.ExcludeRule(
            Slug, rule, SilenceReason, notes, expiry,
            silenceExisting: choice == ExcludeRuleChoice.ExcludeAndSilence);

        string until = expiry is null
            ? "de forma permanente"
            : $"hasta el {expiry.Value.ToLocalTime():dd/MM/yyyy}";
        _toasts.Show(result.SilencedFindings > 0
            ? $"Regla {rule} excluida en {AppName} {until} · {result.SilencedFindings} hallazgo(s) silenciado(s)."
            : $"Regla {rule} excluida en {AppName} {until}. Los hallazgos que ya existían siguen activos.");
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

        // Verificar es una acción de AUDITAR: re-ancla contra los ficheros del clon y le pide
        // veredicto al agente. Sin clon no hay contra qué anclar (F5.8 §3).
        if (!CanAudit)
        {
            _toasts.Show(AuditDisabledTooltip);
            return;
        }

        Ulid id = Id;
        IsBusy = true;
        _toasts.Show("Verificando el hallazgo…");
        try
        {
            int applied = await Task.Run(() => _verify.RunAsync(Slug, new[] { id }, CancellationToken.None));
            _toasts.Show(applied > 0
                ? "Verificado: el veredicto está aplicado y en el historial."
                : "El verify no pudo emitir veredicto. Mira el historial.");
        }
        catch (Exception ex)
        {
            _toasts.Show($"No se pudo verificar: {ex.Message}");
        }
        finally
        {
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
        _toasts.Show("Abriendo en el editor…");
        bool opened = await _editor.OpenAsync(Slug, loc.Path, loc.Line);
        if (!opened)
        {
            _toasts.Show("No se pudo abrir el editor. Revisa el editor configurado en Ajustes y la ruta del clon.");
        }
    }

    [RelayCommand]
    private void GenerateFixPrompt()
    {
        if (Finding is null)
        {
            return;
        }

        string prompt = FixPromptBuilder.Build(Finding);
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

        _governance.AddComment(Slug, Id, prompt, kind: "fix-prompt");
        _toasts.Show(copied
            ? "Prompt de arreglo copiado al portapapeles y guardado en los comentarios."
            : "Prompt de arreglo guardado en los comentarios (el portapapeles no estaba disponible).");
        Reload(Id);
    }
}
