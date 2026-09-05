using System.Collections.ObjectModel;
using Atalaya.App.Services;
using Atalaya.App.Views;
using Atalaya.ClaudeCode;
using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Atalaya.Copilot;

namespace Atalaya.App.ViewModels;

/// <summary>V2 Inventory (§8): module→unit tree with state, claims, filters, and cycle actions.</summary>
public sealed partial class InventoryViewModel : ViewModelBase, IAppScoped
{
    private readonly HubContext _hub;
    private readonly IUlidFactory _ulids;
    private readonly NavigationService _navigation;
    private readonly LiveSessionService _live;
    private readonly SettingsService _settings;
    private readonly CostEstimator _costs;
    private readonly IAuditLaunchConfirmer _confirmer;

    /// <summary>F5.8 §1: si esta máquina tiene el clon. Decide el modo solo-lectura.</summary>
    private readonly CloneLinkService _links;

    /// <summary>F5.8 §2: el mismo diálogo de vincular que abre el portafolio.</summary>
    private readonly LinkCloneFlow _linkFlow;

    /// <summary>F5.8 §2: el re-escaneo, ahora compartido con el flujo de vincular.</summary>
    private readonly InventoryRescanService _rescan;

    /// <summary>F5.10: la gestión de reglas excluidas de esta app.</summary>
    private readonly GovernanceService _governance;

    /// <summary>F5.10: quién abre esa gestión. Inyectada para poder probar el gesto sin ventana.</summary>
    private readonly IPatternSilencesDialog _patternsDialog;

    /// <summary>F7: el registro de directivas del proyecto de esta app.</summary>
    private readonly DirectiveService _directives;

    /// <summary>F7: quién abre su gestión. Inyectada por lo mismo que la de patrones.</summary>
    private readonly IDirectivesDialog _directivesDialog;

    /// <summary>F9: qué ha cambiado desde que se auditó. Se deriva del clon, no se persiste.</summary>
    private readonly DriftQuery _driftQuery;

    /// <summary>F9 §4: quién abre la lista de hallazgos sin código. Inyectada como las demás.</summary>
    private readonly IDeletedUnitsDialog _deletedDialog;

    /// <summary>La gobernanza, que es quien ejecuta la resolución por código eliminado (F9 §4).</summary>
    private readonly GovernanceService _governanceForDeleted;

    /// <summary>
    /// F5.7 §4: el resultado de una acción se cuenta por el toast global. El texto que vivía al
    /// fondo del panel del ciclo se quedaba pegado hasta la acción siguiente y, con la ventana
    /// corta, ni siquiera se veía.
    /// </summary>
    private readonly ToastCenter _toasts;

    /// <summary>Plegar y desplegar módulos: la MISMA lógica que V3 (F5.6 §1).</summary>
    private readonly GroupCollapse _collapse;

    /// <summary>
    /// Lo seleccionado, por ruta de unidad y para TODO el ciclo — no solo para lo que está a la
    /// vista. Es la pieza que hace que filtrar no deseleccione: los nodos del árbol se tiran y se
    /// reconstruyen en cada búsqueda, así que la selección no puede vivir en ellos.
    /// </summary>
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);

    /// <summary>El ciclo entero, sin filtrar: contra esto se cuentan pendientes y seleccionadas.</summary>
    private IReadOnlyList<InventoryUnit> _allUnits = Array.Empty<InventoryUnit>();

    /// <summary>
    /// La deriva vigente, por ruta. <c>null</c> mientras no se ha calculado: el inventario se abre
    /// SIN esperarla —leer estados y lanzar una auditoría no la necesitan— y las columnas aparecen
    /// cuando llega. Nunca se persiste (F9, principio rector).
    /// </summary>
    private IReadOnlyDictionary<string, UnitDrift> _drift =
        new Dictionary<string, UnitDrift>(StringComparer.Ordinal);

    /// <summary>El resultado entero, con sus avisos. <c>null</c> hasta el primer cálculo.</summary>
    private AppDrift? _driftResult;

    /// <summary>
    /// La selección actual salió de «Seleccionar cambiadas» (F9 §6). Es lo que decide el
    /// <c>trigger</c> de la sesión: cualquier otro gesto sobre la selección lo apaga, porque a
    /// partir de ahí ya no es la lista que propuso la deriva.
    /// </summary>
    private bool _selectionFromDrift;

    /// <summary>F13: la política de tamaño de la aplicación, que se edita aquí y no en Ajustes.</summary>
    /// <summary>
    /// Quién va a auditar, para poder DECIRLO en el diálogo de lanzamiento (F14). Es opcional
    /// porque el inventario funciona igual sin saberlo —los tests que ejercitan la selección y el
    /// barrido no tienen nada que decir sobre proveedores—: sin registro, el diálogo enseña lo que
    /// enseñaba antes y no se inventa un nombre.
    /// </summary>
    private readonly AuditorProviderRegistry? _providers;

    private readonly ThresholdPolicyService _thresholds;

    private readonly IThresholdsDialog _thresholdsDialog;

    /// <summary>
    /// Configurar el ciclo (F17 §4). Los dos son opcionales por lo mismo que el registro de
    /// proveedores: los tests que ejercitan la selección y el barrido no tienen nada que decir
    /// sobre temáticas, y sin flujo el reinicio hereda la configuración sin preguntar.
    /// </summary>
    private readonly CycleConfigService? _cycleConfig;

    private readonly CycleConfigFlow? _configFlow;

    public InventoryViewModel(
        HubContext hub, IUlidFactory ulids, NavigationService navigation, LiveSessionService live,
        SettingsService settings, CostEstimator costs, IAuditLaunchConfirmer confirmer,
        GroupExpansionMemory expansion, ToastCenter toasts,
        CloneLinkService links, LinkCloneFlow linkFlow, InventoryRescanService rescan,
        GovernanceService governance, IPatternSilencesDialog patternsDialog,
        DirectiveService directives, IDirectivesDialog directivesDialog,
        DriftQuery driftQuery, IDeletedUnitsDialog deletedDialog,
        ThresholdPolicyService thresholds, IThresholdsDialog thresholdsDialog,
        AuditorProviderRegistry? providers = null,
        CycleConfigService? cycleConfig = null, CycleConfigFlow? configFlow = null)
    {
        _cycleConfig = cycleConfig;
        _configFlow = configFlow;
        _providers = providers;
        _thresholds = thresholds;
        _thresholdsDialog = thresholdsDialog;
        _driftQuery = driftQuery;
        _deletedDialog = deletedDialog;
        _governanceForDeleted = governance;
        _governance = governance;
        _patternsDialog = patternsDialog;
        _directives = directives;
        _directivesDialog = directivesDialog;
        _hub = hub;
        _ulids = ulids;
        _navigation = navigation;
        _live = live;
        _settings = settings;
        _costs = costs;
        _confirmer = confirmer;
        _toasts = toasts;
        _links = links;
        _linkFlow = linkFlow;
        _rescan = rescan;
        _collapse = new GroupCollapse(expansion);
        _collapse.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
    }

    public override string Title => AppName is { Length: > 0 } ? $"Inventario · {AppName}" : "Inventario";

    /// <summary>F26 §A.</summary>
    public override string RailKey => "inventory";

    /// <summary>La miga ya lleva el nombre de la aplicación en el eslabón anterior; repetirlo sobra.</summary>
    public override string CrumbLabel => "Inventario";

    public override bool BelongsToApp => true;

    /// <inheritdoc />
    public string AppSlug => Slug;

    /// <inheritdoc />
    public string AppLabel => AppName is { Length: > 0 } ? AppName : Slug;

    public ObservableCollection<ModuleNode> Modules { get; } = new();

    [ObservableProperty] private string _slug = string.Empty;
    [ObservableProperty] private string _appName = string.Empty;
    [ObservableProperty] private int _cycleN = 1;
    [ObservableProperty] private int _totalUnits;
    [ObservableProperty] private int _auditedUnits;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnitsTooltip))]
    private int _largeUnits;
    [ObservableProperty] private int _pendingUnits;
    /// <summary>
    /// Cuántas AUDITORÍAS se han lanzado en este ciclo. Antes contaba TODAS las sesiones de la
    /// aplicación, de todos los ciclos y de todos los tipos: un número que no se podía explicar
    /// en una frase, y por eso desconcertaba.
    /// </summary>
    [ObservableProperty] private int _sessionCount;

    /// <summary>
    /// Patrones silenciados VIVOS en esta app (F5.12). Es un dato del panel del ciclo porque decide
    /// qué se va a reportar y qué no en cada auditoría de esta aplicación: una cobertura del 100 %
    /// con tres tipos de problema silenciados no significa lo mismo que una del 100 % sin ninguno.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PatternsTooltip))]
    private int _silencedPatterns;

    /// <summary>Los caducados, que ya no suprimen y esperan que alguien decida (F5.12).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PatternsTooltip))]
    private int _expiredPatterns;

    /// <summary>Lo que explica el número, incluido el caso «hay caducados que revisar».</summary>
    public string PatternsTooltip => SilencedPatterns == 0 && ExpiredPatterns == 0
        ? "Tipos de problema que las auditorías de esta aplicación no reportan. Ninguno por ahora."
        : $"{SilencedPatterns} tipo(s) de problema que las auditorías de esta aplicación no reportan"
          + (ExpiredPatterns > 0
              ? $" · {ExpiredPatterns} caducado(s) que ya no suprimen: revísalos."
              : ". Es por-aplicación: el mismo tipo puede ser crítico en otra.");

    /// <summary>
    /// Directivas ACTIVAS del proyecto (F7). Va en el panel del ciclo, junto a los patrones
    /// silenciados, por la misma razón que ellos: condiciona la lectura de todo lo demás. Una
    /// auditoría que conoce las convenciones deliberadas del proyecto no reporta lo mismo que una
    /// que las ignora, y quien lea la cobertura tiene que saber cuál de las dos está viendo.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectivesTooltip))]
    private int _activeDirectives;

    /// <summary>Candidatos detectados en el clon que nadie ha curado todavía (F7 §1).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectivesTooltip))]
    private int _newDirectiveCandidates;

    public string DirectivesTooltip => ActiveDirectives == 0 && NewDirectiveCandidates == 0
        ? "Los ficheros de convenciones del proyecto —AGENTS.md, ADRs, specs, skills— que informan al "
          + "auditor y al arreglo. Ninguno activo por ahora."
        : $"{ActiveDirectives} directiva(s) activa(s) informando al auditor y al arreglo"
          + (NewDirectiveCandidates > 0
              ? $" · {NewDirectiveCandidates} candidato(s) detectado(s) sin activar: decídelos tú."
              : ". Su contenido se lee del clon en cada uso: siempre viaja la versión vigente.");

    /// <summary>El ciclo con su fecha: «Ciclo 5 · iniciado 12 ago 2026» (F5.6 §5).</summary>
    [ObservableProperty] private string _cycleLabel = "Ciclo 1";

    /// <summary>La lupa del ciclo vigente (F17): un distintivo en el panel, con su color.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CycleThemeLabel))]
    [NotifyPropertyChangedFor(nameof(CycleThemeTooltip))]
    private AuditTheme _cycleTheme = AuditTheme.General;

    public string CycleThemeLabel => ThemeCatalog.Display(CycleTheme);

    public string CycleThemeTooltip => CycleTheme == AuditTheme.General
        ? "Ciclo General: el criterio completo. Es el ciclo de referencia."
        : $"Ciclo temático: el auditor busca SOLO defectos de {ThemeCatalog.Display(CycleTheme)} y reconcilia "
          + "solo los hallazgos de esa temática. Los de otras temáticas envejecen mientras dura. Un ciclo "
          + "temático no sustituye a uno General.";

    /// <summary>«opus (Claude Code)» o «sin preferencia»: el juez que el equipo prefiere para este ciclo.</summary>
    [ObservableProperty] private string _preferredModelLabel = "sin preferencia";

    /// <summary>
    /// El historial de temáticas del ciclo (F17.1), cuando lo hay: «Antes: Rendimiento (hasta 2 sep
    /// 15:26, cambiada por alopezciller)». Vacío si el ciclo no ha cambiado de lupa.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCycleThemeHistory))]
    private string _cycleThemeHistory = string.Empty;

    public bool HasCycleThemeHistory => CycleThemeHistory.Length > 0;

    /// <summary>La configuración vigente, para el aviso del lanzar (F17 §5).</summary>
    private CycleConfig _cycleConfigValue = CycleConfig.Default;

    /// <inheritdoc cref="CycleSummary.Tooltip"/>
    [ObservableProperty] private string _cycleTooltip = string.Empty;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isEmpty;

    /// <summary>
    /// Hay sitio para el resumen del ciclo a la derecha (F26 §B, D-975). Lo pone la VISTA al
    /// medirse: es lo único que sabe cuánto ancho le ha tocado — la ventana no basta, porque el
    /// raíl se pliega y se despliega.
    /// </summary>
    [ObservableProperty] private bool _wide = true;

    /// <summary>
    /// El resumen del ciclo, abierto como cajón sobre la lista (D-982).
    /// <para>
    /// Cuando no cabe al lado, el panel NO puede limitarse a desaparecer: dentro viven «Configurar
    /// ciclo» y los tres «Gestionar» de la gobernanza, y con la ventana en su tamaño mínimo —1100
    /// px, de los que el raíl se lleva 232— el umbral nunca se alcanza. Es decir: quien trabaje en
    /// ventana pequeña perdía esas acciones para siempre y sin ningún control que las devolviera.
    /// Ahora se pliega a un botón de la cabecera que lo abre encima.
    /// </para>
    /// </summary>
    [ObservableProperty] private bool _cycleDrawerOpen;

    /// <summary>Abre y cierra el cajón. Un solo gesto: el botón de la cabecera y el aspa.</summary>
    [RelayCommand]
    private void ToggleCycleDrawer() => CycleDrawerOpen = !CycleDrawerOpen;

    /// <summary>
    /// Al ensancharse, el cajón sobra: el panel vuelve a su sitio a la derecha y dejarlo abierto
    /// taparía la lista con lo mismo que ya se ve al lado.
    /// </summary>
    partial void OnWideChanged(bool value)
    {
        if (value)
        {
            CycleDrawerOpen = false;
        }
    }

    /// <summary>
    /// Si esta máquina tiene el clon de la app (F5.8 §3). El inventario se ABRE siempre —ver
    /// estados, quién audita y el resumen del ciclo no necesita el código—, pero lo que LANZA
    /// una auditoría o lee ficheros del clon queda deshabilitado y dice por qué.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAudit))]
    [NotifyPropertyChangedFor(nameof(IsReadOnly))]
    [NotifyPropertyChangedFor(nameof(ReadOnlyNotice))]
    [NotifyPropertyChangedFor(nameof(LinkActionLabel))]
    [NotifyPropertyChangedFor(nameof(AuditDisabledTooltip))]
    private CloneLink _link = CloneLink.Unknown(string.Empty);

    /// <inheritdoc cref="CloneLink.CanAudit"/>
    public bool CanAudit => Link.CanAudit;

    /// <summary>La barra de solo-lectura solo existe cuando de verdad lo es.</summary>
    public bool IsReadOnly => !Link.CanAudit;

    /// <summary>Lo que se lee en esa barra: el estado y lo que sigue siendo posible sin clon.</summary>
    /// <remarks>
    /// El salto de línea no es cosmético: el diagnóstico del caso ámbar termina en las DOS URLs,
    /// cada una en su renglón, y pegarle la frase siguiente a continuación la hacía leerse como
    /// parte de la última URL.
    /// </remarks>
    public string ReadOnlyNotice => Link.State == CloneLinkState.Problema
        ? $"Solo lectura: {Link.Problem}\nHallazgos, métricas e informes siguen accesibles."
        : "Solo lectura: no tienes un clon local de esta aplicación en esta máquina. "
          + "Puedes ver estados, quién audita, hallazgos, métricas e informes; para auditar hace falta el código.";

    /// <inheritdoc cref="CloneLink.ActionLabel"/>
    public string LinkActionLabel => Link.ActionLabel;

    /// <inheritdoc cref="CloneLink.DisabledActionTooltip"/>
    public string AuditDisabledTooltip => Link.DisabledActionTooltip;

    /// <summary>
    /// Cuántas unidades hay marcadas EN TODO EL CICLO. Es el número que no puede mentir: lanzar
    /// una auditoría cuesta dinero, y con 900 unidades nadie las cuenta a ojo.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionLabel))]
    private int _selectedCount;

    /// <summary>La barra de selección solo existe cuando hay algo seleccionado.</summary>
    [ObservableProperty] private bool _hasSelection;

    /// <summary>Simétrico: si ya están todas marcadas, lo útil es lo contrario (F5.6 §3).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingToggleTooltip))]
    private string _pendingToggleLabel = "Seleccionar pendientes";

    public string SelectionLabel => SelectedCount == 1
        ? "1 unidad seleccionada"
        : $"{SelectedCount} unidades seleccionadas";

    /// <summary>
    /// Qué es una unidad y, si las hay, dónde se han metido las «grandes». El recuento de grandes
    /// salió del panel por decisión del usuario (F5.6 §5) —no aporta a ese nivel—, pero sin
    /// decirlo en algún sitio el panel deja de cuadrar: auditadas + pendientes no suman el total.
    /// </summary>
    public string UnitsTooltip
    {
        get
        {
            const string What = "Cada fichero que se audita por separado; «Re-escanear» las pone al día.";
            return LargeUnits switch
            {
                0 => What,
                1 => What + " Una es demasiado grande para auditarla de una vez: sale marcada "
                          + "«Grande» en la lista, no cuenta como pendiente y no impide cerrar el ciclo.",
                _ => What + $" {LargeUnits} son demasiado grandes para auditarlas de una vez: salen "
                          + "marcadas «Grande» en la lista, no cuentan como pendientes y no impiden "
                          + "cerrar el ciclo.",
            };
        }
    }

    // ---- Deriva (F9) ----

    /// <summary>Se está calculando la deriva. El cálculo va fuera del hilo de UI (F9 §1.2).</summary>
    [ObservableProperty] private bool _isDriftLoading;

    /// <summary>Unidades auditadas cuyo código ha cambiado por mano ajena. Candidatas a re-auditar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDrift))]
    [NotifyPropertyChangedFor(nameof(ChangedToggleTooltip))]
    private int _changedUnits;

    /// <summary>
    /// Arregladas desde Atalaya y todavía sin verificar (F9 §2). Va SEPARADA de las cambiadas y no
    /// se suma con ellas: son dos acciones distintas —auditar y verificar— y sumarlas propondría
    /// gastar una auditoría en algo que se comprueba con un verify.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDrift))]
    private int _fixedPendingVerify;

    /// <summary>Auditadas cuyo historial no se puede comparar (F9 §1.1).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDrift))]
    private int _noHistoryUnits;

    /// <summary>Hallazgos activos cuyo código ya no existe (F9 §4).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOrphans))]
    private int _orphanFindings;

    public bool HasOrphans => OrphanFindings > 0;

    /// <summary>Hay algo de deriva que contar. Sin esto el panel no estrena líneas vacías.</summary>
    public bool HasDrift => ChangedUnits > 0 || FixedPendingVerify > 0 || NoHistoryUnits > 0;

    /// <summary>
    /// La deriva se ha podido calcular. Es distinto de que HAYA deriva: sin esto no se podría
    /// decir «sin deriva» con conocimiento de causa, que es justo lo que hay que poder decir.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoDrift))]
    private bool _driftIsKnown;

    /// <summary>
    /// Se ha mirado y no hay nada (F9.1 §2). El grupo se colapsa a una línea: tres ceros seguidos
    /// ocupan lo mismo que un dato y no dicen más que una frase.
    /// </summary>
    public bool HasNoDrift => DriftIsKnown && !HasDrift;

    /// <summary>Lo que se lee cuando no hay deriva: «Sin deriva respecto a «main»».</summary>
    [ObservableProperty] private string _noDriftLabel = string.Empty;

    /// <summary>
    /// Contra qué se ha medido, como SUBTÍTULO del grupo y no como línea suelta (F9.1 §2):
    /// «respecto a «main» (d996732)». El panel lo DICE (F9 §1.1).
    /// </summary>
    [ObservableProperty] private string _driftBranchLabel = string.Empty;

    /// <summary>Los avisos honestos: rama no por defecto, clon atrasado, cambios sin commitear.</summary>
    public ObservableCollection<string> DriftWarnings { get; } = new();

    public bool HasDriftWarnings => DriftWarnings.Count > 0;

    /// <summary>Por qué no hay deriva que enseñar (sin clon, no es un repo…). Vacío si la hay.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDriftProblem))]
    private string _driftProblem = string.Empty;

    public bool HasDriftProblem => DriftProblem.Length > 0;

    /// <summary>
    /// El filtro de deriva, propio y aparte del de estado: 0 todas · 1 cambiadas · 2 arregladas
    /// pendientes de verificar · 3 sin historial. Es una dimensión distinta, así que no puede
    /// compartir control con el estado de auditoría.
    /// </summary>
    [ObservableProperty] private int _driftFilter;

    partial void OnDriftFilterChanged(int value) => Rebuild();

    /// <summary>Simétrico, como el de pendientes: si ya están todas marcadas, lo útil es lo contrario.</summary>
    [ObservableProperty] private string _changedToggleLabel = "Seleccionar cambiadas";

    public string ChangedToggleTooltip => ChangedUnits == 0
        ? "No hay ninguna unidad auditada cuyo código haya cambiado desde su auditoría."
        : ChangedUnits == 1
            ? "Actúa sobre la única unidad cambiada del ciclo, esté o no a la vista."
            : $"Actúa sobre las {ChangedUnits} unidades cambiadas del ciclo, estén o no a la vista.";

    public string PendingToggleTooltip => PendingUnits == 1
        ? "Actúa sobre la única unidad pendiente del ciclo, esté o no a la vista."
        : $"Actúa sobre las {PendingUnits} unidades pendientes del ciclo, estén o no a la vista.";

    /// <inheritdoc cref="GroupCollapse.AllCollapsed"/>
    public bool AllCollapsed => _collapse.AllCollapsed;

    /// <inheritdoc cref="GroupCollapse.ToggleAllLabel"/>
    public string ToggleAllLabel => _collapse.ToggleAllLabel;

    /// <inheritdoc cref="GroupCollapse.HasGroups"/>
    public bool HasGroups => _collapse.HasGroups;

    public void SetApp(string slug) => Slug = slug;

    partial void OnSearchTextChanged(string value) => Rebuild();

    public override Task LoadAsync()
    {
        Rebuild();

        // La deriva NO bloquea la entrada (F9 §1.2): mirar estados, buscar o lanzar una auditoría
        // no la necesitan, y con 900 unidades cuesta décimas de segundo que no hay por qué esperar
        // mirando una pantalla vacía. Llega cuando llega y la vista se reconstruye.
        return RefreshDriftAsync();
    }

    /// <summary>
    /// Calcula la deriva en segundo plano y reconstruye. Se llama al entrar, tras re-escanear y
    /// tras vincular el clon: los tres gestos que pueden cambiar la respuesta.
    /// </summary>
    private async Task RefreshDriftAsync()
    {
        if (Slug.Length == 0)
        {
            return;
        }

        string slug = Slug;
        string? clone = Link.Path;
        IsDriftLoading = true;
        try
        {
            AppDrift drift = await Task.Run(() => _driftQuery.For(slug, clone));
            if (slug != Slug)
            {
                // La vista cambió de aplicación mientras se calculaba: lo que llega ya no es de
                // esta pantalla y pintarlo sería enseñar la deriva de otra app.
                return;
            }

            ApplyDrift(drift);
        }
        catch (Exception ex)
        {
            // N-2: un fallo se DICE. Sin esto, la deriva desaparecería sin explicación y el panel
            // se leería como «no hay nada que re-auditar», que es la mentira tranquilizadora.
            _driftResult = null;
            _drift = new Dictionary<string, UnitDrift>(StringComparer.Ordinal);
            DriftIsKnown = false;
            DriftProblem = $"No se pudo calcular la deriva: {ex.Message}";
        }
        finally
        {
            IsDriftLoading = false;
            Rebuild();
        }
    }

    private void ApplyDrift(AppDrift drift)
    {
        _driftResult = drift;
        _drift = drift.Units.ToDictionary(u => u.Path, StringComparer.Ordinal);
        DriftProblem = drift.Problem ?? string.Empty;
        DriftIsKnown = drift.Problem is null;
        string against = drift.Branch.Length > 0 ? $"«{drift.Branch}»" : "tu clon";
        DriftBranchLabel = DriftIsKnown ? $"respecto a {against} ({drift.Head})" : string.Empty;
        NoDriftLabel = $"Sin deriva respecto a {against}";

        DriftWarnings.Clear();
        foreach (string warning in drift.Warnings)
        {
            DriftWarnings.Add(warning);
        }

        OnPropertyChanged(nameof(HasDriftWarnings));
    }

    private void Rebuild()
    {
        Modules.Clear();
        AppConfig? app = string.IsNullOrEmpty(Slug) ? null : _hub.Store.TryReadApp(Slug);
        if (app is null)
        {
            _collapse.Adopt(Array.Empty<ModuleNode>());
            Link = CloneLink.Unknown(Slug);
            IsEmpty = true;
            return;
        }

        AppName = app.Name;
        CycleN = app.CurrentCycle;

        // Se recalcula en cada reconstrucción, que es lo que corre al entrar, al sincronizar y al
        // volver la ventana al primer plano (F5.8 §1). Un vínculo roto entre dos vistas de la
        // misma página no puede quedar diciendo que se puede auditar.
        Link = _links.For(app);
        InventoryCycle? inv = _hub.Store.TryReadInventory(Slug, CycleN);
        var claims = _hub.Store.ListClaims(Slug)
            .Where(c => !c.IsExpiredAt(DateTimeOffset.UtcNow))
            .ToDictionary(c => c.UnitHash, c => c.By);

        var units = inv?.Units ?? new List<InventoryUnit>();
        _allUnits = units;
        TotalUnits = units.Count;
        AuditedUnits = units.Count(u => u.State == UnitState.Auditada);
        LargeUnits = units.Count(u => u.State == UnitState.Grande);
        PendingUnits = units.Count(u => u.State == UnitState.Pendiente);
        OnPropertyChanged(nameof(PendingToggleTooltip));

        // F5.12: vivos y caducados se cuentan por separado porque significan cosas distintas —
        // uno vivo suprime, uno caducado solo pide una decisión.
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        var patterns = _hub.Store.ListPatternSilences(Slug);
        SilencedPatterns = patterns.Count(p => p.IsLiveAt(nowUtc));
        ExpiredPatterns = patterns.Count(p => p.IsExpiredAt(nowUtc));

        // F7: activas y candidatos-sin-curar se cuentan por separado. Un candidato NO cuenta como
        // directiva: detectar no es activar, y el panel tiene que decir cuántas decisiones esperan
        // a alguien sin sugerir que ya se han tomado.
        var directives = _hub.Store.ListDirectives(Slug);
        ActiveDirectives = directives.Count(d => d.IsActive);
        NewDirectiveCandidates = _directives.NewCandidates(Slug, Link.Path).Count;

        // F13: el umbral de tamaño es gobernanza de la aplicación y se lee de su app.json, que es
        // donde lo escribe «Umbrales · Gestionar». Enseñarlo aquí es lo que hace que un inventario
        // con 40 unidades grandes se pueda explicar sin abrir nada.
        LargeUnitLoc = app.Thresholds.LargeUnitLoc;
        RefreshLargeUnitOffer();

        // F17: la lupa y el juez preferido del ciclo, leídos del mismo fichero que las unidades.
        _cycleConfigValue = inv?.Config ?? CycleConfig.Default;
        CycleTheme = _cycleConfigValue.Theme;
        CycleThemeHistory = inv is null ? string.Empty : ThemeHistoryText.Previous(inv.Periods);
        PreferredModelLabel = !_cycleConfigValue.HasPreferredModel
            ? "sin preferencia"
            : string.IsNullOrWhiteSpace(_cycleConfigValue.PreferredProvider)
                ? _cycleConfigValue.PreferredModel!
                : $"{_cycleConfigValue.PreferredModel} ({ProviderNames.Display(_cycleConfigValue.PreferredProvider)})";

        var sessions = _hub.Store.ListSessions(Slug);
        CycleStart start = CycleSummary.StartOf(sessions, CycleN);
        CycleLabel = CycleSummary.Label(CycleN, start);
        CycleTooltip = CycleSummary.Tooltip(start);
        SessionCount = CycleSummary.LaunchesIn(sessions, CycleN);

        // Un re-escaneo o un reset pueden hacer desaparecer unidades: lo seleccionado se poda
        // contra el inventario vigente para que el contador nunca cuente fantasmas.
        _selected.IntersectWith(units.Select(u => u.Path));

        // F9 §3: los contadores de deriva se cuentan sobre el CICLO entero, no sobre lo filtrado.
        // Son un dato del ciclo, igual que «pendientes»: buscar no puede cambiarlos.
        ChangedUnits = _drift.Values.Count(d => d.State == DriftState.Modificada);
        FixedPendingVerify = _drift.Values.Count(d => d.State == DriftState.ArregladaPendienteDeVerificar);
        NoHistoryUnits = _drift.Values.Count(d => d.State == DriftState.HistorialNoDisponible);
        OrphanFindings = _driftResult?.Orphans.Count ?? 0;
        OnPropertyChanged(nameof(ChangedToggleTooltip));
        OnPropertyChanged(nameof(HasNoDrift));

        string search = SearchText.Trim();
        IEnumerable<InventoryUnit> filtered = string.IsNullOrEmpty(search)
            ? units
            : units.Where(u => u.Path.Contains(search, StringComparison.OrdinalIgnoreCase));

        filtered = DriftFilter switch
        {
            1 => filtered.Where(u => DriftOf(u) is { State: DriftState.Modificada }),
            2 => filtered.Where(u => DriftOf(u) is { State: DriftState.ArregladaPendienteDeVerificar }),
            3 => filtered.Where(u => DriftOf(u) is { State: DriftState.HistorialNoDisponible }),
            _ => filtered,
        };

        // Con el filtro de cambiadas puesto, el orden por defecto es el que contesta la pregunta:
        // más toqueteada, antes (F9 §3). El desempate por ruta lo hace estable — dos listas iguales
        // tienen que salir iguales, o parece que algo se ha movido solo.
        bool byCommits = DriftFilter == 1;
        var built = new List<ModuleNode>();
        var groups = filtered.GroupBy(u => u.Module).ToList();
        IEnumerable<IGrouping<string, InventoryUnit>> ordered = byCommits
            ? groups
                .OrderByDescending(g => g.Max(u => DriftOf(u)?.Commits ?? 0))
                .ThenBy(g => g.Key, StringComparer.Ordinal)
            : groups.OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var group in ordered)
        {
            var node = new ModuleNode { Name = group.Key, Slug = Slug };
            IEnumerable<InventoryUnit> rows = byCommits
                ? group
                    .OrderByDescending(u => DriftOf(u)?.Commits ?? 0)
                    .ThenBy(u => u.Path, StringComparer.Ordinal)
                : group.OrderBy(u => u.Path, StringComparer.Ordinal);

            foreach (InventoryUnit u in rows)
            {
                var unit = new UnitNode
                {
                    Path = u.Path,
                    Module = u.Module,
                    Loc = u.Loc,
                    State = u.State,
                    ClaimedBy = claims.TryGetValue(u.UnitHash, out string? by) ? by : null,
                    Drift = DriftOf(u),
                };

                // Se restaura antes de enganchar el aviso: reconstruir la vista no es seleccionar.
                unit.SetSelectedQuietly(_selected.Contains(u.Path));
                unit.SelectionChanged = OnUnitSelectionChanged;
                node.Units.Add(unit);
            }

            node.RefreshCheckState();
            node.SelectionRequested = OnModuleSelectionRequested;
            built.Add(node);
        }

        _collapse.Adopt(built);
        foreach (ModuleNode node in built)
        {
            Modules.Add(node);
        }

        RefreshSelectionState();
        IsEmpty = Modules.Count == 0;
    }

    /// <summary>Los nodos que están a la vista. Lo que se ve, no lo que hay.</summary>
    private IEnumerable<UnitNode> VisibleUnits => Modules.SelectMany(m => m.Units);

    private void OnUnitSelectionChanged(UnitNode unit)
    {
        // Tocar la selección a mano deja de ser «lo que propuso la deriva» (F9 §6).
        _selectionFromDrift = false;
        if (unit.IsSelected)
        {
            _selected.Add(unit.Path);
        }
        else
        {
            _selected.Remove(unit.Path);
        }

        foreach (ModuleNode module in Modules)
        {
            if (module.Name == unit.Module)
            {
                module.RefreshCheckState();
            }
        }

        RefreshSelectionState();
    }

    /// <summary>
    /// El gesto de la casilla del módulo (F5.13). La decisión de marcar o limpiar la toma el nodo;
    /// aquí se aplica sobre el conjunto de seleccionadas, que es el único dueño de la selección.
    /// <para>
    /// Un módulo NO es una unidad: lo que entra en el conjunto son SIEMPRE las rutas de sus hijas.
    /// El grupo no tiene ruta y no puede llegar nunca a la lista de lanzamiento.
    /// </para>
    /// </summary>
    private void OnModuleSelectionRequested(ModuleNode module, bool select)
    {
        _selectionFromDrift = false;
        foreach (UnitNode unit in module.Units)
        {
            if (select)
            {
                _selected.Add(unit.Path);
            }
            else
            {
                _selected.Remove(unit.Path);
            }
        }

        SyncVisibleFromSelection();
    }

    /// <summary>
    /// <b>LA lista.</b> Las unidades-hoja marcadas, y nada más: ni módulos, ni nodos de grupo, ni
    /// rutas que ya no estén en el inventario vigente.
    /// <para>
    /// Existe porque el incidente del 2026-08-26 enseñó lo que cuesta tener dos expresiones de «lo
    /// seleccionado»: el contador decía una cosa y lo que se lanzaba podía ser otra, y la diferencia
    /// se paga en tokens. La consumen los TRES sitios que necesitan saberlo —el contador de la
    /// barra, el diálogo de confirmación y la petición que va al coordinador—, así que no pueden
    /// discrepar ni aunque alguien lo intente.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> SelectedUnits()
        => _allUnits
            .Where(u => _selected.Contains(u.Path))
            .Select(u => u.Path)
            .ToList();

    /// <summary>
    /// Pone al día lo que lee la barra: el contador, su visibilidad y el sentido del botón de
    /// pendientes. El contador sale de <see cref="SelectedUnits"/> — de la MISMA lista que se va a
    /// auditar— y no de un recuento paralelo.
    /// </summary>
    private void RefreshSelectionState()
    {
        SelectedCount = SelectedUnits().Count;
        HasSelection = SelectedCount > 0;

        var pending = PendingPaths();
        bool allPendingSelected = pending.Count > 0 && pending.All(_selected.Contains);
        PendingToggleLabel = allPendingSelected ? "Deseleccionar pendientes" : "Seleccionar pendientes";

        var changed = ChangedPaths();
        bool allChangedSelected = changed.Count > 0 && changed.All(_selected.Contains);
        ChangedToggleLabel = allChangedSelected ? "Deseleccionar cambiadas" : "Seleccionar cambiadas";
    }

    private List<string> PendingPaths()
        => _allUnits.Where(u => u.State == UnitState.Pendiente).Select(u => u.Path).ToList();

    /// <summary>La deriva de una unidad, o null si no se ha calculado o nunca se auditó.</summary>
    private UnitDrift? DriftOf(InventoryUnit unit)
        => _drift.TryGetValue(unit.Path, out UnitDrift? d) ? d : null;

    /// <summary>
    /// Las cambiadas, en el orden que contesta la pregunta: más toqueteada, antes (F9 §3). Es la
    /// lista que marca «Seleccionar cambiadas» y la que se lanza.
    /// </summary>
    private List<string> ChangedPaths()
        => _allUnits
            .Select(u => (Unit: u, Drift: DriftOf(u)))
            .Where(x => x.Drift is { State: DriftState.Modificada })
            .OrderByDescending(x => x.Drift!.Commits)
            .ThenBy(x => x.Unit.Path, StringComparer.Ordinal)
            .Select(x => x.Unit.Path)
            .ToList();

    /// <summary>
    /// Marca o desmarca TODAS las unidades cambiadas del ciclo (F9 §3). Hace pareja con
    /// «Seleccionar pendientes» y desemboca en el MISMO flujo: mismo diálogo, misma estimación de
    /// coste, mismo barrido, misma reconciliación. La re-auditoría no estrena ningún camino nuevo.
    /// <para>
    /// Las «arregladas — pendientes de verificar» NO entran: se comprueban verificando, que es el
    /// instrumento que las detectó, y gastarles una auditoría entera sería pagar de más por menos.
    /// </para>
    /// </summary>
    [RelayCommand]
    private void SelectChanged()
    {
        var changed = ChangedPaths();
        if (changed.Count == 0)
        {
            _toasts.Show(_driftResult is null
                ? "La deriva todavía se está calculando."
                : FixedPendingVerify > 0
                    ? $"Ninguna unidad ha cambiado por mano ajena. Hay {FixedPendingVerify} arreglada(s) "
                      + "pendiente(s) de verificar: eso se comprueba con «Verificar», no re-auditando."
                    : "Ninguna unidad auditada ha cambiado desde su auditoría.");
            return;
        }

        bool select = !changed.All(_selected.Contains);
        foreach (string path in changed)
        {
            if (select)
            {
                _selected.Add(path);
            }
            else
            {
                _selected.Remove(path);
            }
        }

        // El trigger de la sesión sale de AQUÍ y no de adivinar por la forma de la lista (F9 §6):
        // una selección manual que por casualidad coincida con las cambiadas no es mantenimiento.
        _selectionFromDrift = select;
        SyncVisibleFromSelection();
    }

    /// <summary>
    /// Marca o desmarca TODAS las pendientes del ciclo, a la vista o no. Es la acción global que
    /// hace pareja con «Deseleccionar todo»; para recortar a un trozo está la casilla del módulo.
    /// </summary>
    [RelayCommand]
    private void SelectPending()
    {
        _selectionFromDrift = false;
        var pending = PendingPaths();
        if (pending.Count == 0)
        {
            _toasts.Show("No queda ninguna unidad pendiente en este ciclo.");
            return;
        }

        bool select = !pending.All(_selected.Contains);
        foreach (string path in pending)
        {
            if (select)
            {
                _selected.Add(path);
            }
            else
            {
                _selected.Remove(path);
            }
        }

        SyncVisibleFromSelection();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        _selectionFromDrift = false;
        _selected.Clear();
        SyncVisibleFromSelection();
    }

    /// <summary>
    /// Baja el conjunto de seleccionadas al árbol visible. Silencioso por nodo —el conjunto ya es
    /// la verdad— y luego se recalcula todo de una vez, en vez de una por casilla.
    /// </summary>
    private void SyncVisibleFromSelection()
    {
        foreach (UnitNode unit in VisibleUnits)
        {
            unit.SetSelectedQuietly(_selected.Contains(unit.Path));
        }

        foreach (ModuleNode module in Modules)
        {
            module.RefreshCheckState();
        }

        RefreshSelectionState();
    }

    /// <summary>
    /// El clic sobre la casilla del módulo. Existe como comando —y no solo como efecto de escribir
    /// <c>IsChecked</c>— porque la casilla ya no recibe el tri-estado: pinta
    /// <see cref="ModuleNode.IsAllSelected"/>, que es de dos estados, así que el gesto tiene que
    /// llegar por su propia vía en vez de deducirse de un valor que la vista ya no escribe.
    /// </summary>
    [RelayCommand]
    private void ToggleModule(ModuleNode? group) => group?.RequestToggle();

    /// <inheritdoc cref="GroupCollapse.Toggle"/>
    [RelayCommand]
    private void ToggleGroup(ModuleNode? group) => _collapse.Toggle(group);

    /// <inheritdoc cref="GroupCollapse.ToggleAll"/>
    [RelayCommand]
    private void ToggleAllGroups() => _collapse.ToggleAll();

    /// <summary>
    /// Abre la gestión de patrones silenciados de esta app (F5.12). No pide clon ni permiso: es
    /// gobernanza, y la gobernanza se lee y se edita sin tener el código delante (F5.8 §3).
    /// Al cerrarla se reconstruye la página, así que el contador refleja lo que se acaba de hacer.
    /// </summary>
    [RelayCommand]
    private void ManagePatternSilences()
    {
        if (Slug.Length == 0)
        {
            return;
        }

        var vm = new PatternSilencesViewModel(_hub, _governance, _toasts);
        vm.Load(Slug);
        _patternsDialog.Show(vm);
        Rebuild();
    }

    /// <summary>
    /// Abre la gestión de directivas del proyecto de esta app (F7 §1). A diferencia de la de
    /// patrones, esta SÍ quiere el clon —el catálogo busca en el repositorio y la vista previa lee
    /// el fichero—, pero se abre igualmente sin él: lo ya registrado se puede leer y desactivar sin
    /// tener el código delante, y el panel dice con todas las letras que no se ha podido mirar.
    /// </summary>
    [RelayCommand]
    private void ManageDirectives()
    {
        if (Slug.Length == 0)
        {
            return;
        }

        var vm = new DirectivesViewModel(_directives, _hub, _toasts);
        vm.Load(Slug, Link.Path);
        _directivesDialog.Show(vm);
        Rebuild();
    }

    /// <summary>El umbral vigente de la aplicación, en líneas. Lo enseña el panel de gobernanza.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThresholdsTooltip))]
    private int _largeUnitLoc = 1500;

    public string ThresholdsTooltip
        => $"A partir de {LargeUnitLoc} líneas una unidad sale «Grande» y no entra en la cola de "
           + "auditoría. Es política de esta aplicación: vive en el hub, vale para todo el equipo y "
           + "aplica en el próximo re-escaneo.";

    /// <summary>
    /// La oferta de mudanza (F13): esta máquina traía un umbral personal distinto del de fábrica —
    /// de cuando el ajuste era de Ajustes— y esta aplicación no lo tiene como política. Se ofrece
    /// UNA vez por aplicación, y la respuesta se apunta: una oferta que reaparece en cada visita es
    /// un aviso que se aprende a ignorar.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LargeUnitOfferLabel))]
    private bool _hasLargeUnitOffer;

    public string LargeUnitOfferLabel
        => $"Tenías {_settings.Current.Thresholds.LegacyLargeUnitLoc} LOC configurados en esta "
           + $"máquina, de cuando el umbral era un ajuste personal. Esta aplicación usa "
           + $"{LargeUnitLoc}. ¿Lo aplico a la política de «{AppName}», para todo el equipo?";

    /// <summary>
    /// ¿Hay algo que ofrecer? Solo si el valor heredado existe, dice algo distinto de la política
    /// vigente, y no se ha contestado ya por esta aplicación.
    /// </summary>
    private void RefreshLargeUnitOffer()
    {
        LocalThresholds local = _settings.Current.Thresholds;
        HasLargeUnitOffer = local.HasLegacyLargeUnit
            && local.LegacyLargeUnitLoc != LargeUnitLoc
            && !_settings.Current.LargeUnitOfferedApps.Contains(Slug, StringComparer.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(LargeUnitOfferLabel));
    }

    /// <summary>Lleva el umbral heredado a la política de ESTA aplicación, y lo publica.</summary>
    [RelayCommand]
    private void AcceptLargeUnitOffer()
    {
        int inherited = _settings.Current.Thresholds.LegacyLargeUnitLoc;
        if (Slug.Length == 0 || inherited <= 0)
        {
            return;
        }

        ThresholdPolicyResult result = _thresholds.Set(Slug, inherited, _thresholds.Read(Slug).LargeUnitChars);
        AnswerLargeUnitOffer();
        _toasts.Show(result.Saved
            ? $"Umbral de «{AppName}» = {result.LargeUnitLoc} LOC, ahora para todo el equipo. "
              + "Aplica en el próximo re-escaneo."
            : result.Message);
        Rebuild();
    }

    /// <summary>«Aquí no»: se apunta la respuesta y no se vuelve a preguntar por esta aplicación.</summary>
    [RelayCommand]
    private void DismissLargeUnitOffer()
    {
        AnswerLargeUnitOffer();
        RefreshLargeUnitOffer();
    }

    /// <summary>
    /// Apunta que esta aplicación ya contestó, y retira el valor heredado en cuanto no le quede
    /// ninguna por preguntar que pudiera quererlo. Un número que ya no gobierna nada no puede
    /// quedarse en el fichero invitando a que alguien lo lea.
    /// </summary>
    private void AnswerLargeUnitOffer()
    {
        AppSettings settings = _settings.Current;
        if (!settings.LargeUnitOfferedApps.Contains(Slug, StringComparer.OrdinalIgnoreCase))
        {
            settings.LargeUnitOfferedApps.Add(Slug);
        }

        var pending = _hub.Store.ListAppSlugs()
            .Where(s => !settings.LargeUnitOfferedApps.Contains(s, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (pending.Count == 0)
        {
            settings.Thresholds.LegacyLargeUnitLoc = 0;
            settings.LargeUnitOfferedApps.Clear();
        }

        _settings.Save(settings);
        HasLargeUnitOffer = false;
    }

    /// <summary>
    /// «Umbrales · Gestionar» (F13). Vive aquí y no en Ajustes porque lo que decide —qué unidades
    /// son grandes— se escribe en el hub y lo comparte el equipo entero.
    /// </summary>
    [RelayCommand]
    private void ManageThresholds()
    {
        if (Slug.Length == 0)
        {
            return;
        }

        var vm = new ThresholdsViewModel(_thresholds, _toasts);
        vm.Load(Slug, AppName, _allUnits);
        _thresholdsDialog.Show(vm);
        Rebuild();
    }

    /// <summary>
    /// Abre «Vincular clon local…» / «Reparar vínculo…» sin salir del inventario (F5.8 §3): el
    /// acceso directo que acompaña a cada acción deshabilitada. Al volver, la página se
    /// reconstruye, y con ella el modo solo-lectura.
    /// </summary>
    [RelayCommand]
    private void LinkClone()
    {
        if (Slug.Length == 0)
        {
            return;
        }

        _linkFlow.Run(Slug);
        Rebuild();

        // Vincular es justo lo que convierte «no se puede saber» en una respuesta: sin el clon no
        // había historial que comparar, y ahora lo hay.
        _ = RefreshDriftAsync();
    }

    [RelayCommand]
    private async Task Rescan()
    {
        // Re-escanear LEE el clon: sin él no hay nada que escanear. Se comprueba contra el mismo
        // estado que pinta el piloto, no con una comprobación propia (F5.8 §1).
        if (!CanAudit)
        {
            _toasts.Show(AuditDisabledTooltip);
            return;
        }

        string clone = Link.Path!;

        IsBusy = true;
        _toasts.Show("Re-escaneando…");
        try
        {
            RescanOutcome outcome = await Task.Run(() => _rescan.Rescan(Slug, clone));
            // F5.16: lo que le pasó a los hallazgos medidos se DICE. Un hallazgo que se resuelve en
            // silencio se lee como un hallazgo que ha desaparecido, y eso costó una investigación.
            // F7 §1: los candidatos nuevos se ANUNCIAN, no se activan. El aviso es informativo a
            // propósito — «se han detectado» y no «se han añadido»— porque quien lo lee tiene que
            // entender que todavía no ha pasado nada y que la decisión sigue siendo suya.
            string directives = outcome.Candidates.Count == 0
                ? string.Empty
                : $" · {outcome.Candidates.Count} fichero(s) de directivas detectado(s), sin activar: "
                  + "revísalos en «Directivas · Gestionar»";

            _toasts.Show((outcome.Measured.Total > 0
                ? $"Inventario actualizado · {outcome.Measured.Summary}"
                : "Inventario actualizado") + directives + ".");
        }
        catch (Exception ex)
        {
            _toasts.Show($"Error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            Rebuild();

            // Un re-escaneo mueve el inventario —altas, bajas, renombrados—, así que la foto de la
            // deriva anterior ya no describe estas unidades.
            _ = RefreshDriftAsync();
        }
    }

    /// <summary>
    /// «Configurar ciclo» (F17 §4): la temática y el juez preferido del ciclo vigente. Cambiar de
    /// temática con trabajo hecho avisa y re-siembra; es la misma operación se haga cuando se haga.
    /// </summary>
    [RelayCommand]
    private async Task ConfigureCycle()
    {
        if (_cycleConfig is null || _configFlow is null)
        {
            _toasts.Show("Configurar el ciclo no está disponible en esta instalación.");
            return;
        }

        CycleConfigPreview? preview = _cycleConfig.Preview(Slug);
        if (preview is null)
        {
            _toasts.Show("No hay ciclo que configurar todavía.");
            return;
        }

        CycleConfig? chosen = await _configFlow.AskAsync(preview, CycleConfigReason.Configurar);
        if (chosen is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            CycleConfigResult result = await Task.Run(() => _cycleConfig.Apply(Slug, chosen));
            _toasts.Show(result.Message);
        }
        finally
        {
            IsBusy = false;
            Rebuild();
            _ = RefreshDriftAsync();
        }
    }

    [RelayCommand]
    private async Task ResetCycle()
    {
        AppConfig? app = _hub.Store.TryReadApp(Slug);
        InventoryCycle? current = app is null ? null : _hub.Store.TryReadInventory(Slug, app.CurrentCycle);
        if (app is null || current is null)
        {
            return;
        }

        // F17 §4: reiniciar ya pone todo pendiente; que de paso se elija la lupa. Cancelar el
        // diálogo cancela el reinicio — nadie ha dicho que sí a nada. Sin flujo (tests, instalación
        // sin diálogo) el ciclo nuevo hereda la configuración del que se reinicia.
        CycleConfig config = current.Config;
        if (_configFlow is not null)
        {
            var preview = new CycleConfigPreview(Slug, app.Name, app.CurrentCycle + 1, current.Config, 0);
            CycleConfig? chosen = await _configFlow.AskAsync(preview, CycleConfigReason.Reinicio);
            if (chosen is null)
            {
                _toasts.Show("Reinicio cancelado. El ciclo sigue como estaba.");
                return;
            }

            config = chosen;
        }

        IsBusy = true;
        try
        {
            await Task.Run(() =>
            {
                int next = app.CurrentCycle + 1;
                var fresh = new InventoryCycle { CycleN = next, Config = config, OpenedUtc = DateTimeOffset.UtcNow };
                fresh.OpenThemeHistory(config.Theme, fresh.OpenedUtc, _hub.ResolveIdentity().Name);
                foreach (InventoryUnit u in current.Units)
                {
                    // Nothing is deleted; large units are re-evaluated against the threshold
                    // (§5.5) — la política de la aplicación, leída ahora (F13).
                    bool large = u.Loc > app.Thresholds.LargeUnitLoc;
                    fresh.Units.Add(new InventoryUnit
                    {
                        Path = u.Path,
                        Module = u.Module,
                        Loc = u.Loc,
                        ContentHash = u.ContentHash,
                        State = large ? UnitState.Grande : UnitState.Pendiente,
                    });
                }

                app.CurrentCycle = next;
                _hub.Store.WriteApp(app);
                _hub.Store.WriteInventory(Slug, fresh);
                _hub.Store.WriteSession(new AuditSession
                {
                    Id = _ulids.NewUlid(),
                    AppSlug = Slug,
                    Mode = AuditMode.Reset,
                    By = _hub.ResolveIdentity().Name,
                    Machine = Environment.MachineName,
                    StartedUtc = DateTimeOffset.UtcNow,
                    EndedUtc = DateTimeOffset.UtcNow,
                    CycleN = next,
                    Theme = config.Theme,
                });
                _hub.Sync?.CommitAndPush($"reset: {Slug} nuevo ciclo {next}");
            });

            _toasts.Show("Ciclo reiniciado. Nada se ha borrado.");
        }
        finally
        {
            IsBusy = false;
            Rebuild();
        }
    }

    [RelayCommand]
    private Task AuditSelection()
    {
        // La MISMA lista que cuenta la barra (F5.13). No se vuelve a derivar aquí: derivarla dos
        // veces es exactamente cómo el contador y el lanzamiento acabaron diciendo cosas distintas.
        IReadOnlyList<string> selected = SelectedUnits();

        if (selected.Count == 0)
        {
            _toasts.Show("Selecciona al menos una unidad (o usa «Seleccionar pendientes»).");
            return Task.CompletedTask;
        }

        return LaunchSession(AuditMode.Lotes, selected);
    }

    [RelayCommand]
    private Task ShowFindings() => _navigation.NavigateToAsync<FindingsViewModel>(vm => vm.SetApp(Slug));

    /// <summary>
    /// Abre la lista de hallazgos cuyo código ya no existe (F9 §4). No resuelve nada por su cuenta:
    /// enseña qué hay, con qué evidencia, y deja la acción a una persona.
    /// </summary>
    [RelayCommand]
    private void ShowDeletedUnits()
    {
        if (Slug.Length == 0 || _driftResult is null)
        {
            return;
        }

        var vm = new DeletedUnitsViewModel(_governanceForDeleted, _driftQuery, _toasts);
        vm.Load(Slug, AppName, Link.Path, _driftResult.Orphans);
        _deletedDialog.Show(vm);

        // Resolver hallazgos cambia lo que queda huérfano, así que la foto se vuelve a pedir.
        _driftQuery.Invalidate();
        _ = RefreshDriftAsync();
    }

    /// <summary>
    /// El tope de pasadas vigente, que es lo que multiplica el gasto. Vive en los ajustes de la
    /// máquina (D-095), no en la app auditada.
    /// </summary>
    private int MaxPasses => Math.Max(1, _settings.Current.MaxPassesPerUnit);

    /// <summary>
    /// Estima el coste de auditar esas unidades. Público para que la verificación humana y los
    /// tests puedan leer el mismo número que verá el diálogo.
    /// </summary>
    public CostEstimate EstimateFor(int units)
        => _costs.Estimate(Slug, units, MaxPasses, _providers?.Current.ProviderId);

    /// <summary>
    /// La confirmación de un lanzamiento, con el juez de la sesión escrito en el titular (F14).
    /// <para>
    /// Cuando el proveedor no factura a la organización —Claude Code va contra la suscripción de
    /// quien lo usa— no hay coste que prometer, y la propia estimación lo dice (F16-RETOQUE §1).
    /// Hasta aquí se acompañaba de una salvedad sobre la «tarifa de lista»; ya no hace falta,
    /// porque ya no se enseña ninguna cifra que pudiera confundirse con dinero.
    /// </para>
    /// </summary>
    internal AuditLaunchConfirmation ConfirmationFor(int units)
    {
        IAuditorProvider? provider = _providers?.Current;

        return new AuditLaunchConfirmation(
            AppName,
            EstimateFor(units),
            provider?.ProviderName ?? string.Empty,
            provider?.ModelName,
            preferenceNotice: PreferenceNoticeFor(provider));
    }

    /// <summary>
    /// El aviso del juez preferido (F17 §5), si el de esta máquina no es el del ciclo. El modelo que
    /// se compara es el CONFIGURADO para el proveedor actual: es el que va a resolver la sesión.
    /// </summary>
    internal string? PreferenceNoticeFor(IAuditorProvider? provider)
    {
        if (provider is null)
        {
            return null;
        }

        string configured = _settings.ModelFor(provider.ProviderId).Trim();
        string? model = configured.Length > 0 ? configured : provider.ModelName;
        return CyclePreference.Notice(_cycleConfigValue, provider.ProviderId, model, provider.ProviderName);
    }

    /// <summary>
    /// Desde cuántas unidades se pregunta. Configurable por app; por defecto 3 — el clic de más
    /// solo se justifica cuando el gasto es relevante (F5.6 §4).
    /// </summary>
    private int ConfirmThreshold
        => Math.Max(0, _hub.Store.TryReadApp(Slug)?.Thresholds.ConfirmLaunchUnits ?? 3);

    /// <summary>
    /// Lanzar es un acto EXPLÍCITO (F5.2): se arranca la sesión en el servicio y luego se navega a
    /// V5 para verla. Antes se navegaba y la vista arrancaba la sesión en su <c>LoadAsync</c>, de
    /// modo que cualquier recarga de página ejecutaba una auditoría entera — el bucle de D-085.
    /// <para>
    /// Desde F5.6 §4, por encima del umbral se pregunta antes, enseñando el gasto estimado. La
    /// estimación informa; el botón de confirmar nunca se bloquea por ella.
    /// </para>
    /// </summary>
    private async Task LaunchSession(AuditMode mode, IReadOnlyList<string> paths)
    {
        // La puerta de F5.8 §3, en el MODELO y no solo en el XAML: un botón gris es una cortesía
        // de la vista; auditar sin clon escribiría hallazgos sobre un código que no está.
        if (!CanAudit)
        {
            _toasts.Show(AuditDisabledTooltip);
            return;
        }

        if (paths.Count == 0)
        {
            _toasts.Show("No hay unidades para auditar.");
            return;
        }

        if (_live.IsRunning)
        {
            _toasts.Show("Ya hay una sesión en curso. Ábrela desde «Sesión en vivo».");
            await _navigation.NavigateToAsync<SessionViewModel>();
            return;
        }

        // F17 §5: con un juez distinto del preferido, el diálogo se enseña aunque la selección
        // no llegue al umbral — el aviso es lo que hay que ver, y no cabe en ningún otro sitio.
        AuditLaunchConfirmation confirmation = ConfirmationFor(paths.Count);
        if (paths.Count > ConfirmThreshold || confirmation.HasPreferenceNotice)
        {
            if (!_confirmer.Confirm(confirmation))
            {
                _toasts.Show("Lanzamiento cancelado. La selección sigue como estaba.");
                return;
            }
        }

        // El N que el usuario ha visto y aceptado viaja con la petición (F5.13). El coordinador lo
        // vuelve a comprobar contra lo que de verdad va a auditar: si alguna vez vuelven a
        // discrepar, la sesión muere antes de la primera llamada al modelo y no antes de la
        // factura. Es una salvaguarda de última línea, no la corrección — la corrección es que
        // contador y lista sean el mismo método.
        // F9 §6: solo se GUARDA. No cambia nada de cómo se audita — mismo diálogo, misma
        // estimación, mismo barrido, misma reconciliación—; sirve para que Métricas pueda algún día
        // separar la cobertura inicial del mantenimiento sin reinterpretar sesiones antiguas.
        SessionTrigger trigger = _selectionFromDrift ? SessionTrigger.Deriva : SessionTrigger.Manual;
        _ = _live.StartAsync(new SessionRequest(Slug, mode, paths, paths.Count, trigger), paths);
        await _navigation.NavigateToAsync<SessionViewModel>();
    }
}
