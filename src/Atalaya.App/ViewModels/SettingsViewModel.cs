using System.Collections.ObjectModel;
using Atalaya.App.Services;
using Atalaya.App.Views;
using Atalaya.Domain.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>
/// Una opción del desplegable de modelos (F5.1). El precio solo aparece si el SDK lo publica:
/// un multiplicador inventado sería peor que ninguno.
/// </summary>
/// <summary>
/// Una casa entre las que elegir en Ajustes (F14). Solo el identificador y el nombre: la
/// disponibilidad —si el CLI está, si hay sesión iniciada— se enseña en Cuenta, que es donde se
/// arregla. Repetir aquí ese estado obligaría a mantener dos sitios diciendo lo mismo.
/// </summary>
public sealed record ProviderOption(string Id, string Name);

/// <summary>
/// Una sección de Ajustes en la lista lateral de la vista (F26 Parte C).
/// <para>
/// Es un objeto observable y no un <c>record</c> porque <see cref="IsActive"/> cambia sin que la
/// lista cambie: el estilo de la entrada se pinta con un <c>DataTrigger</c> sobre él, igual que en
/// el raíl (D-954). Reconstruir la colección al cambiar de sección haría parpadear las cinco.
/// </para>
/// </summary>
public sealed partial class SettingsSectionItem : ObservableObject
{
    public SettingsSectionItem(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public string Key { get; }

    public string Label { get; }

    [ObservableProperty] private bool _isActive;
}

public sealed record ModelOption(string Id, string Label)
{
    public static ModelOption From(AgentModel model)
    {
        string name = string.Equals(model.Name, model.Id, StringComparison.Ordinal)
            ? model.Id
            : $"{model.Name} ({model.Id})";
        return new ModelOption(
            model.Id,
            model.Multiplier is { } m ? $"{name} · ×{m:0.##}" : name);
    }

    /// <summary>El modelo configurado, cuando no se ha podido preguntar al SDK cuáles hay.</summary>
    public static ModelOption Unverified(string id) => new(id, $"{id} (configurado)");
}

/// <summary>
/// La marca «Guardado ✓» de UN ajuste (R5). Observable y por ajuste, no una bandera de la página:
/// lo que se guarda es un interruptor concreto y lo que lo dice tiene que estar a su lado.
/// </summary>
public sealed partial class SettingSaved : ObservableObject
{
    [ObservableProperty] private bool _shown;
}

/// <summary>
/// Las marcas, indexadas por el nombre del ajuste. Es un indexador y no diez propiedades porque la
/// vista enlaza <c>Saved[MaxPassesPerUnit].Shown</c> y así añadir un ajuste no obliga a añadir aquí
/// nada: la marca nace la primera vez que alguien pregunta por ella.
/// </summary>
public sealed class SettingSavedMarks
{
    private readonly Dictionary<string, SettingSaved> _marks = new(StringComparer.Ordinal);

    /// <summary>
    /// La marca de un ajuste. SIEMPRE la misma instancia para el mismo nombre: un enlace de WPF se
    /// engancha al objeto que recibe, y devolver uno nuevo cada vez dejaría la vista escuchando a
    /// una marca que nadie vuelve a encender.
    /// </summary>
    public SettingSaved this[string field]
    {
        get
        {
            if (!_marks.TryGetValue(field, out SettingSaved? mark))
            {
                mark = new SettingSaved();
                _marks[field] = mark;
            }

            return mark;
        }
    }
}

/// <summary>
/// Ajustes (§8) — preferencias ÚNICAMENTE desde F2 (D4): todo lo de la conexión vive en «Cuenta».
/// <para>
/// F5.7 la deja en cuatro secciones con un mismo ritmo —General, Auditoría, Sincronización y una
/// zona peligrosa al final— y retira dos cosas que no eran ajustes de nadie: el interruptor de
/// «arreglo asistido», que entonces era el <i>feature flag</i> de un H9 sin construir y no estaba
/// conectado a nada, y las «Opciones avanzadas» (PAT de respaldo, TLS, override de la URL del
/// hub). El soporte de PAT sigue en el código —<see cref="SettingsService.GetPat"/> y
/// <see cref="HubContext"/> lo usan— y el override de <c>hubUrl</c> sigue disponible editando
/// <c>appsettings.deploy.json</c>, que es exactamente el público de esa opción.
/// </para>
/// <para>
/// <b>F6.9 devuelve el interruptor del arreglo asistido</b>, porque ya hay algo al otro lado. La
/// regla de D-275 no cambia —un control que no cambia ningún comportamiento es peor que no
/// tenerlo—: lo que cambia es que ahora sí lo cambia.
/// </para>
/// <para>
/// Y añade lo único que faltaba para poder empezar de cero: el restablecimiento de fábrica, con la
/// confirmación fuerte que su alcance exige (§5).
/// </para>
/// <para>
/// <b>F13 se lleva el umbral de unidad grande.</b> Esta página guarda lo de ESTA máquina, y aquel
/// umbral clasifica un inventario que comparte todo el equipo: es política de cada aplicación y se
/// edita en su Inventario. Aquí queda dicho dónde está, sin control que lo edite — dos sitios
/// editables para el mismo valor son dos verdades esperando a discrepar.
/// </para>
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly IAuditorProvider _agent;

    /// <summary>
    /// Los proveedores disponibles (F14). Opcional: sin registro la página funciona como antes —un
    /// solo auditor, el que se le inyecte— y no enseña el selector. Los tests que solo ejercitan
    /// los ajustes numéricos no tienen por qué montar dos casas.
    /// </summary>
    private readonly AuditorProviderRegistry? _providers;
    private readonly ToastCenter _toasts;
    private readonly FactoryResetService _reset;
    private readonly IFactoryResetConfirmer _confirmer;
    private readonly HubContext _hub;
    private readonly NavigationService _navigation;

    /// <summary>
    /// Las tarifas de la organización (R2 §2). Opcionales por lo mismo que el «Acerca de»: los
    /// tests que solo ejercitan los ajustes numéricos no montan un hub con su tabla, y sin servicio
    /// la sección se enseña vacía con su motivo en vez de reventar.
    /// </summary>
    private readonly ModelRatesService? _rates;

    /// <summary>
    /// Quién está instalado en esta máquina (R13 §2). Opcional como el resto: los tests que solo
    /// ejercitan los ajustes numéricos no montan una detección, y sin ella la lista sale con lo
    /// que siempre está —el manejador del sistema— en vez de reventar.
    /// </summary>
    private readonly EditorDetector? _editors;

    /// <summary>El mismo lanzador que usa la ficha del hallazgo: «Probar» prueba lo que se usa.</summary>
    private readonly EditorLauncher? _launcher;

    /// <summary>En qué aplicación se está, para probar contra SU repositorio.</summary>
    private readonly ActiveApp? _activeApp;

    /// <summary>El fichero con el que probar cuando no hay ninguna aplicación abierta.</summary>
    private readonly string _ownFile;

    /// <summary>Plazo para que el SDK conteste con su catálogo antes de rendirse.</summary>
    private static readonly TimeSpan ModelListTimeout = TimeSpan.FromSeconds(30);

    public SettingsViewModel(
        SettingsService settings,
        IAuditorProvider agent,
        ToastCenter toasts,
        FactoryResetService reset,
        IFactoryResetConfirmer confirmer,
        HubContext hub,
        NavigationService navigation,
        AuditorProviderRegistry? providers = null,
        ModelRatesService? rates = null,
        CostReconciliationService? costGaps = null,
        EditorDetector? editors = null,
        EditorLauncher? launcher = null,
        ActiveApp? activeApp = null,
        AppPaths? paths = null)
    {
        _settings = settings;
        _agent = agent;
        _providers = providers;
        _toasts = toasts;
        _reset = reset;
        _confirmer = confirmer;
        _hub = hub;
        _navigation = navigation;
        _rates = rates;
        Rates = rates is null ? null : new ModelRatesViewModel(rates, costGaps);
        BuildSections();
        _editors = editors;
        _launcher = launcher;
        _activeApp = activeApp;
        _ownFile = paths?.SettingsJson ?? string.Empty;
        AppSettings s = settings.Current;
        _editor = s.Editor;
        BuildEditorOptions(s.Editor);
        _showCostIn = CostCurrencies.Label(CostCurrencies.Parse(s.CostCurrency));
        _isLightTheme = string.Equals(s.Theme, "light", StringComparison.OrdinalIgnoreCase);
        _pollingSeconds = s.PollingSeconds;
        _freshnessDays = s.Thresholds.FreshnessDays;
        _maxPassesPerUnit = s.MaxPassesPerUnit;
        _copilotTimeoutMinutes = s.CopilotTimeoutMinutes;
        _enableAssistedFix = s.EnableAssistedFix;
        _exhaustiveSweep = s.ExhaustiveSweep;
        // F14 — el proveedor elegido, y el modelo DE ESE proveedor. Los dos campos de modelo son
        // independientes porque sus espacios de nombres no se solapan, así que ir y volver entre
        // casas conserva las dos elecciones en vez de dejar una configurada con un id imposible.
        // Solo los SELECCIONABLES (F14, adenda): Copilot siempre, y los opcionales únicamente si
        // están instalados. Ofrecer una casa que no está en la máquina sería un desplegable con una
        // opción que no funciona, y para quien no use Claude Code el selector ni siquiera aparece
        // —con una sola opción no hay nada que elegir—, que es lo que la regla pide: ninguna
        // exigencia y ninguna merma para quien no lo tenga.
        _selectedProviderId = providers?.Current.ProviderId ?? string.Empty;
        foreach (IAuditorProvider provider in providers?.Selectable ?? Array.Empty<IAuditorProvider>())
        {
            Providers.Add(new ProviderOption(provider.ProviderId, provider.ProviderName));
        }

        // PROV-2 §2 — el modelo es el DE ESE proveedor, buscado por su identificador. Cuando la
        // página se monta sin registro —los tests que solo ejercitan los ajustes numéricos—, el
        // proveedor es el que se inyectó: ya no hay «el campo de Copilot» al que caer.
        _selectedModelId = settings.ModelFor(ModelOwnerId);

        // Hasta que el proveedor conteste, el desplegable enseña el modelo configurado: así nunca
        // está vacío ni «elige» en silencio uno distinto del que se está usando.
        Models.Add(ModelOption.Unverified(_selectedModelId));
    }

    // --- Proveedor de auditoría (F14) ---

    /// <summary>
    /// Las casas entre las que se puede elegir. Vacía cuando la página se monta sin registro, y
    /// entonces el selector no se enseña: un desplegable con una sola opción no es una elección.
    /// </summary>
    public ObservableCollection<ProviderOption> Providers { get; } = new();

    /// <summary>Hay algo que elegir de verdad.</summary>
    public bool HasProviderChoice => Providers.Count > 1;

    [ObservableProperty] private string _selectedProviderId;

    /// <summary>
    /// Cambiar de proveedor recarga la lista de modelos y recupera el modelo que ESA casa tenía
    /// elegido. Sin esto, el desplegable de modelos seguiría enseñando los de la otra —y guardar
    /// dejaría configurado un id que el proveedor nuevo no reconoce.
    /// </summary>
    partial void OnSelectedProviderIdChanged(string value)
    {
        if (_providers is null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        SelectedModelId = _settings.ModelFor(value);
        Models.Clear();
        Models.Add(ModelOption.Unverified(SelectedModelId));
        OnPropertyChanged(nameof(ProviderNotice));
        _ = RefreshModels();
    }

    /// <summary>
    /// Qué significa la elección, dicho donde se toma. Las dos mitades importan: que se aplica a la
    /// SIGUIENTE sesión —cambiarlo a mitad de un barrido cambiaría de juez sin avisar— y que es una
    /// preferencia personal de esta máquina, no una política del equipo (F13, D-769): lo que llega
    /// al hub no es el ajuste, es con quién se auditó aquella vez.
    /// </summary>
    public string ProviderNotice =>
        "Con quién auditas TÚ, en esta máquina: cada uno usa la cuenta que tiene. Se aplica a la "
        + "siguiente sesión, y queda escrito en ella y en su informe. " + FixerNotice;

    /// <summary>
    /// Quién arregla, <b>preguntándoselo al proveedor</b> (PROV-2 §5). Hasta aquí esta línea decía
    /// «El arreglo asistido sigue siendo de Copilot», que es falso desde F16: arregla el proveedor
    /// elegido, y solo si sabe (<c>IAssistedFixProvider</c>). Una casa escrita a mano en un texto
    /// es una afirmación que nadie vuelve a comprobar; el nombre lo declara el propio proveedor.
    /// <para>
    /// Se mira el <b>seleccionado</b> en la página y no el guardado, igual que
    /// <see cref="CurrentProvider"/>: el resto del aviso ya habla de la elección que se está
    /// haciendo, y decir «arregla X» mientras el desplegable enseña Y sería el mismo desfase otra
    /// vez, solo que de un segundo de duración.
    /// </para>
    /// </summary>
    private string FixerNotice
        => CurrentProvider is Atalaya.Agents.IAssistedFixProvider
            ? $"El arreglo asistido también es de {CurrentProvider.ProviderName}."
            : $"{CurrentProvider.ProviderName} audita, pero no hace arreglos asistidos.";

    public override string Title => "Ajustes";

    /// <summary>F26 §A.</summary>
    public override string RailKey => "settings";

    [ObservableProperty] private string _editor;

    /// <summary>
    /// Los editores que se ofrecen: <b>solo los que están en esta máquina</b>, más el manejador del
    /// sistema, que está siempre.
    /// <para>
    /// Ofrecer un editor no instalado es ofrecer un fallo, y hasta R13 ese fallo era mudo: elegir
    /// Visual Studio sin Visual Studio abría el fichero con el manejador del sistema y decía que
    /// todo había ido bien. El editor que el ajuste ya nombraba se queda en la lista aunque no se
    /// encuentre, <b>marcado</b>: quitarlo cambiaría el ajuste del usuario por la espalda, que es
    /// otra forma del mismo defecto.
    /// </para>
    /// </summary>
    public List<EditorOption> EditorOptions { get; } = [];

    // La lista NO se reconstruye al cambiar de editor: sustituir el ItemsSource mientras el
    // desplegable está eligiendo es la forma más rápida de que WPF devuelva un SelectedValue nulo
    // y el ajuste cambie solo. Se construye al abrir la página y al pulsar «Probar», y ahí se
    // restaura la selección a mano.
    private void BuildEditorOptions(string configured)
    {
        IReadOnlyList<DetectedEditor> offered = _editors?.Offer(configured)
            ?? EditorRegistry.All
                .Where(e => e.Kind != EditorKind.Program)
                .Select(e => new DetectedEditor(e, null, Detected: true))
                .ToList();

        EditorOptions.Clear();
        EditorOptions.AddRange(offered.Select(d => new EditorOption(d.Editor.Id, d.Label)));
        OnPropertyChanged(nameof(EditorOptions));

        // Y si el cambio de lista se ha llevado por delante la selección, se devuelve: un ajuste
        // que cambia sin que nadie lo toque es el defecto de esta tanda con otra cara.
        if (!string.Equals(Editor, configured, StringComparison.Ordinal))
        {
            _persisting = true;
            try
            {
                Editor = configured;
            }
            finally
            {
                _persisting = false;
            }
        }
    }

    /// <summary>
    /// <b>Probar</b> (R13 §2): abre un fichero de verdad en una línea conocida y dice qué comando
    /// se lanzó y si volvió. Sin esto, la única forma de saber si el editor elegido funciona era
    /// necesitarlo.
    /// </summary>
    [RelayCommand]
    private async Task TestEditor()
    {
        if (_launcher is null)
        {
            _toasts.Show("No se puede probar el editor desde aquí.");
            return;
        }

        // Se vuelve a mirar quién está instalado: instalar un editor y probarlo tiene que ser un
        // gesto, no un reinicio.
        _editors?.Refresh();
        BuildEditorOptions(Editor);

        EditorOpenResult result = await _launcher.TestAsync(_activeApp?.Slug, _ownFile);

        // El comando va SIEMPRE que se haya llegado a construir, abriera o no: cuando no abre es
        // justo cuando hace falta verlo — sobre todo si lo escribió el usuario.
        _toasts.Show(result.Command.Length > 0
            ? $"{EditorLauncher.Toast(result)} · lanzado: {result.Command}"
            : EditorLauncher.Toast(result));
    }

    /// <summary>
    /// <b>En qué divisa se enseña el coste</b> (F29 §2). Es una preferencia de esta máquina —el hub
    /// sigue guardando tokens y credits— y por eso vive en Ajustes: no le cambia el número a nadie
    /// del equipo, solo la unidad en la que lo lee quien está delante.
    /// </summary>
    [ObservableProperty] private string _showCostIn;

    /// <summary>Las dos opciones del desplegable, en el orden en el que se leen.</summary>
    public IReadOnlyList<string> Currencies { get; } = new[]
    {
        CostCurrencies.Label(CostCurrency.Credits),
        CostCurrencies.Label(CostCurrency.Usd),
    };

    [ObservableProperty] private bool _isLightTheme;
    [ObservableProperty] private int _pollingSeconds;
    [ObservableProperty] private int _freshnessDays;
    [ObservableProperty] private int _maxPassesPerUnit;
    [ObservableProperty] private int _copilotTimeoutMinutes;

    /// <summary>
    /// El interruptor del arreglo asistido (F6.9). Vuelve al UI porque desde F6.9 hay algo detrás:
    /// F5.7 §2 (D-275) lo retiró por ser un control conectado a nada, no por ser una mala idea, y
    /// el flag se conservó en la configuración exactamente para este día.
    /// <para>
    /// <b>Encendido por defecto</b>: el arreglo es supervisado por diseño —el agente narra, pide
    /// permiso fichero a fichero fuera del hallazgo y no puede commitear—, así que apagarlo de
    /// serie escondería una capacidad segura. Quien no la quiera, la apaga aquí.
    /// </para>
    /// </summary>
    [ObservableProperty] private bool _enableAssistedFix;

    /// <summary>
    /// <b>Modo exhaustivo</b> (R2 §1). Apagado de fábrica. Encendido, cada pasada del barrido es una
    /// petición nueva con el prompt recompuesto — el camino de respaldo de F25 (D-922), con un
    /// interruptor delante y sin ninguna rama propia.
    /// <para>
    /// Se aplica a la SIGUIENTE sesión: la que esté corriendo termina como empezó.
    /// </para>
    /// </summary>
    [ObservableProperty] private bool _exhaustiveSweep;

    /// <summary>
    /// El aviso del modo exhaustivo, <b>literal y en un solo sitio</b> (R2 §1). Son los números que
    /// M2 midió (D-917, D-920) y no una impresión: quien enciende esto tiene que ver el precio antes
    /// de pagarlo. Si alguna vez se vuelve a medir, se cambia aquí y en DECISIONS, no en el XAML.
    /// </summary>
    public const string ExhaustiveWarning =
        "Aumenta el coste de forma drástica (M2: ×3 por unidad) y puede producir hallazgos "
        + "duplicados.";

    /// <summary>
    /// Lo que el modo HACE, delante de lo que cuesta (F26 §C, revisión).
    /// <para>
    /// La ayuda de antes describía lo que el modo <b>no</b> hace cuando está apagado —«cada unidad
    /// se audita como UNA conversación y cada pasada es un turno suyo»— y hacía falta leerla dos
    /// veces para saber qué pasaba al encenderlo. Un interruptor se explica por lo que enciende.
    /// </para>
    /// <para>
    /// Es UNA frase y va en los dos sitios: la línea de ayuda de la fila y el tooltip del icono de
    /// aviso. Que sean la misma propiedad es lo que impide que el precio acabe escrito con dos
    /// cifras distintas.
    /// </para>
    /// <para>
    /// <b>La otra mitad de M2 —lo que se GANA, dos defectos de gravedad media más por cada
    /// veinte— se va al MANUAL.</b> En la fila estorbaba: quien mira este interruptor está
    /// decidiendo si paga el triple, y el argumento a favor se lee entero o no se lee.
    /// </para>
    /// </summary>
    public string ExhaustiveNotice =>
        "Cada pasada vuelve a ser una petición nueva con el prompt completo. " + ExhaustiveWarning;

    // ================================================================ Las secciones (F26 §C)

    /// <summary>
    /// <b>Ajustes deja de ser un scroll y pasa a cinco secciones</b> (F26 Parte C).
    /// <para>
    /// <b>Por qué lista lateral y no pestañas.</b> Las dos caben; la lista gana por tres razones y
    /// ninguna es estética. Una: los rótulos son largos («Proveedor y modelo», «Apariencia») y una
    /// tira horizontal de cinco los apretaría contra el título de la página justo a 1280, que es el
    /// ancho que hay que soportar (principio 1). Dos: la lista es EL MISMO gesto que el raíl una
    /// capa más abajo —barra de «estás aquí», fondo teñido, peso en la activa— y así no hay que
    /// aprender dos formas de decir dónde estás (principio 7). Y tres: crece. Añadir una sección a
    /// una columna no cuesta nada; a una tira de pestañas le queda el ancho que le queda.
    /// </para>
    /// <para>
    /// La sección es además el DESTINO de un enlace: Métricas manda aquí cuando falta una tarifa, y
    /// llegar a la página entera para tener que buscar la tabla no es llegar.
    /// </para>
    /// </summary>
    public const string ProviderSection = "provider";

    public const string AuditSection = "audit";

    public const string RatesSection = "rates";

    public const string AppearanceSection = "appearance";

    public const string AdvancedSection = "advanced";

    /// <summary>Las secciones, como DATOS: es lo que permite pintarlas con un solo estilo.</summary>
    public ObservableCollection<SettingsSectionItem> Sections { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProvider))]
    [NotifyPropertyChangedFor(nameof(ShowAudit))]
    [NotifyPropertyChangedFor(nameof(ShowRates))]
    [NotifyPropertyChangedFor(nameof(ShowAppearance))]
    [NotifyPropertyChangedFor(nameof(ShowAdvanced))]
    private string _section = ProviderSection;

    public bool ShowProvider => Section == ProviderSection;

    public bool ShowAudit => Section == AuditSection;

    public bool ShowRates => Section == RatesSection;

    public bool ShowAppearance => Section == AppearanceSection;

    public bool ShowAdvanced => Section == AdvancedSection;

    partial void OnSectionChanged(string value)
    {
        foreach (SettingsSectionItem item in Sections)
        {
            item.IsActive = item.Key == value;
        }
    }

    [RelayCommand]
    private void SelectSection(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            Section = key;
        }
    }

    private void BuildSections()
    {
        Sections.Clear();
        Sections.Add(new SettingsSectionItem(ProviderSection, "Proveedor y modelo"));
        Sections.Add(new SettingsSectionItem(AuditSection, "Auditoría"));
        Sections.Add(new SettingsSectionItem(RatesSection, "Tarifas"));
        Sections.Add(new SettingsSectionItem(AppearanceSection, "Apariencia"));
        Sections.Add(new SettingsSectionItem(AdvancedSection, "Avanzado"));
        OnSectionChanged(Section);
    }

    // --- Tarifas por modelo (R2 §2) ---

    /// <summary>
    /// <b>La tabla de tarifas, ya no en un diálogo</b> (F26 Parte C). R2 la trajo desde Métricas y
    /// la dejó detrás de un botón que abría una ventana; ahora es la sección «Tarifas» de esta
    /// página, con su tabla, su edición y su lista de modelos usados sin tarifa.
    /// <para>
    /// El fichero NO se mueve: sigue en la raíz del hub (D-786), porque un precio es del contrato de
    /// la organización con su proveedor y no de la aplicación ni del puesto. Lo que cambia es dónde
    /// se edita. Y no hay nada que activar: la tabla se siembra sola al abrir el hub, así que esta
    /// sección es para corregir un precio y para añadir el de un modelo nuevo.
    /// </para>
    /// <para>
    /// Nula cuando no hay servicio —tests que solo tocan los ajustes numéricos, o un hub que aún no
    /// existe—: la sección se enseña entonces con su motivo, no vacía y sin explicación.
    /// </para>
    /// </summary>
    public ModelRatesViewModel? Rates { get; }

    /// <summary>Hay tabla que enseñar. Es lo que separa «no hay tarifas» de «no hay hub».</summary>
    public bool CanManageRates => Rates is not null;

    // --- Modelo (F5.1) ---

    /// <summary>Lo que el SDK lista para esta cuenta. Nunca una lista escrita a mano.</summary>
    public ObservableCollection<ModelOption> Models { get; } = new();

    [ObservableProperty] private string _selectedModelId;

    /// <summary>Por qué la lista no es la del SDK (offline, sin credencial, sin asiento).</summary>
    [ObservableProperty] private string _modelsNotice = string.Empty;

    /// <summary>
    /// El proveedor cuya lista de modelos se está enseñando. Es el SELECCIONADO en la página, no
    /// el guardado en los ajustes: cambiar el desplegable tiene que refrescar los modelos antes de
    /// guardar nada, o se elegiría un modelo a ciegas.
    /// </summary>
    private IAuditorProvider CurrentProvider
        => _providers is null ? _agent : _providers.ById(SelectedProviderId);

    /// <summary>
    /// <b>De quién es el modelo que esta página edita</b> (PROV-2 §2): la clave con la que se lee
    /// y se guarda en <c>AppSettings.ProviderModels</c>. Es el identificador que declara el propio
    /// proveedor, nunca el nombre de un campo suyo — que es lo que permitía que el modelo de una
    /// casa acabara guardado encima del de otra.
    /// </summary>
    private string ModelOwnerId => CurrentProvider.ProviderId;

    public override Task LoadAsync() => RefreshModels();

    /// <summary>
    /// Pide al SDK los modelos disponibles para esta cuenta. Si no se puede (sin red, sin
    /// credencial, sin asiento) deja el modelo configurado con un aviso: Ajustes sigue usable y no
    /// se inventa una lista que luego el asiento rechazaría.
    /// </summary>
    [RelayCommand]
    private async Task RefreshModels()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        // Acotado: pedir la lista arranca el runtime de Copilot, y una red que traga paquetes
        // dejaría Ajustes girando para siempre. Vencido el plazo se enseña el modelo configurado.
        using var timeout = new CancellationTokenSource(ModelListTimeout);
        try
        {
            IReadOnlyList<AgentModel> models = await CurrentProvider.ListModelsAsync(timeout.Token);
            if (models.Count == 0)
            {
                ModelsNotice = $"{CurrentProvider.ProviderName} no devolvió ningún modelo; "
                    + "se mantiene el configurado.";
                return;
            }

            string chosen = SelectedModelId;
            Models.Clear();
            foreach (AgentModel model in models)
            {
                Models.Add(ModelOption.From(model));
            }

            // El modelo guardado puede haber desaparecido del catálogo (retirado, o cambio de
            // plan). Se conserva como opción marcada en vez de saltar en silencio a otro.
            if (!string.IsNullOrWhiteSpace(chosen) && Models.All(m => m.Id != chosen))
            {
                Models.Insert(0, ModelOption.Unverified(chosen));
                ModelsNotice = $"El modelo «{chosen}» ya no figura entre los de tu cuenta.";
            }
            else
            {
                ModelsNotice = string.Empty;
            }

            SelectedModelId = chosen;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            ModelsNotice = $"No se pudo obtener la lista de modelos ({CurrentProvider.ProviderName} "
                + "no respondió a tiempo). "
                + "Se muestra el modelo configurado.";
        }
        catch (Exception ex)
        {
            ModelsNotice = "No se pudo obtener la lista de modelos ("
                + ex.Message.Trim()
                + "). Se muestra el modelo configurado.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Vuelca a los ajustes SOLO lo que esta página edita. Lo que ya no tiene control —el override
    /// de la URL del hub, el TLS estricto y el PAT— se queda como esté en el fichero: retirar un
    /// control de la interfaz no puede significar borrar el valor de quien lo tenía puesto.
    /// <para>
    /// Los mínimos se aplican aquí y se APUNTAN en <paramref name="corrections"/>, para que
    /// guardar pueda decir lo que ha cambiado (BUGFIX-AJUSTES). Un valor corregido en silencio se
    /// vive exactamente igual que un ajuste que no ajusta: escribes 0, no pasa nada, y no hay forma
    /// de saber si el número que mandó fue el tuyo.
    /// </para>
    /// </summary>
    private AppSettings BuildSettings(List<string> corrections)
    {
        AppSettings s = _settings.Current;
        s.Editor = Editor;
        s.CostCurrency = CostCurrencies.Save(SelectedCurrency);
        s.Theme = IsLightTheme ? "light" : "dark";
        s.PollingSeconds = Floor(
            PollingSeconds, SettingsLimits.MinPollingSeconds, "la sincronización del hub", "segundos", corrections);
        // Se parte de los umbrales vigentes y solo se pisan los editables: construir un
        // LocalThresholds nuevo devolvería a sus valores por defecto los que la página no edita —
        // hoy, el umbral heredado que la mudanza de F13 todavía tiene que poder ofrecer.
        s.Thresholds.FreshnessDays = Floor(
            FreshnessDays, SettingsLimits.MinFreshnessDays, "la frescura", "días", corrections);
        // Tope 1 = una pasada única; por eso el barrido no necesita ningún selector de modo por
        // lanzamiento (F5.1).
        s.MaxPassesPerUnit = Floor(
            MaxPassesPerUnit, SettingsLimits.MinMaxPassesPerUnit, "el tope de pasadas", "pasada", corrections);
        if (!string.IsNullOrWhiteSpace(SelectedProviderId))
        {
            s.AuditorProvider = SelectedProviderId;
        }

        // El modelo se guarda en la entrada de SU proveedor: guardar el de una casa encima del de
        // otra la dejaría con un id que no reconoce.
        //
        // PROV-2 §2 — aquí estaba REPETIDO el `switch` de SettingsService, y con el mismo defecto:
        // todo lo que no fuera Claude Code caía en el campo de Copilot, así que un tercer
        // proveedor se guardaba encima del suyo. Ahora la clave es el identificador y el criterio
        // vive en el servicio, que es donde estaba escrito que viviera.
        if (!string.IsNullOrWhiteSpace(SelectedModelId) && ModelOwnerId.Length > 0)
        {
            s.ProviderModels[ModelOwnerId] = SelectedModelId.Trim();
        }
        s.CopilotTimeoutMinutes = Floor(
            CopilotTimeoutMinutes, SettingsLimits.MinCopilotTimeoutMinutes,
            "el timeout de Copilot", "minuto", corrections);
        s.EnableAssistedFix = EnableAssistedFix;
        s.ExhaustiveSweep = ExhaustiveSweep;
        return s;
    }

    /// <summary>
    /// <b>El hueco de coste se reconcilia en el inventario de cada aplicación</b> (F29 §1), y desde
    /// aquí solo se lleva hasta allí. Tarifas es para precios.
    /// </summary>
    [RelayCommand]
    private async Task GoToPortfolio() => await _navigation.NavigateToAsync<PortfolioViewModel>();

    /// <summary>La divisa elegida, leída de la etiqueta del desplegable.</summary>
    private CostCurrency SelectedCurrency
        => string.Equals(ShowCostIn, CostCurrencies.Label(CostCurrency.Usd), StringComparison.Ordinal)
            ? CostCurrency.Usd
            : CostCurrency.Credits;

    /// <summary>
    /// El mínimo de un campo, aplicado y CONTADO. La frase se redacta aquí —donde se conoce el
    /// campo, el número y la unidad— y no en el toast, para que no pueda decir un mínimo distinto
    /// del que se aplicó.
    /// </summary>
    private static int Floor(int value, int minimum, string what, string unit, List<string> corrections)
    {
        int applied = SettingsLimits.Clamp(value, minimum, out bool corrected);
        if (corrected)
        {
            corrections.Add($"{what}: el mínimo es {minimum} {unit}");
        }

        return applied;
    }

    // ================================================================ Se guarda al cambiar (R5)

    /// <summary>
    /// <b>Los ajustes que esta página edita</b>, en UN sitio. De aquí sale qué cambio dispara un
    /// guardado, así que añadir un ajuste a la página es añadirlo aquí — y si se olvida, el ajuste
    /// deja de guardarse en silencio, que es exactamente lo que un test recorre por reflexión.
    /// <para>
    /// Es la misma razón por la que D-987 calculaba la marca de sucio con una huella y no campo a
    /// campo: diez ganchos que hay que mantener iguales acaban siendo nueve (D-966).
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> Editable = new[]
    {
        nameof(Editor),
        nameof(ShowCostIn),
        nameof(IsLightTheme),
        nameof(PollingSeconds),
        nameof(FreshnessDays),
        nameof(MaxPassesPerUnit),
        nameof(CopilotTimeoutMinutes),
        nameof(EnableAssistedFix),
        nameof(ExhaustiveSweep),
        nameof(SelectedProviderId),
        nameof(SelectedModelId),
    };

    /// <summary>
    /// Las marcas «Guardado ✓», una por ajuste. La vista enlaza la del control que pinta; el
    /// view-model solo dice cuál se acaba de guardar.
    /// </summary>
    public SettingSavedMarks Saved { get; } = new();

    /// <summary>
    /// Guardando. Volcar los mínimos aplicados de vuelta a las cajas cambia propiedades editables,
    /// y sin esta bandera cada corrección dispararía otro guardado — y ese, otro.
    /// </summary>
    private bool _persisting;

    /// <summary>
    /// <b>Cada ajuste se guarda en el momento de cambiarlo</b> (R5, sustituye a D-987).
    /// <para>
    /// D-987 puso la barra al pie con «Guardar», «Descartar» y «Hay cambios sin guardar» porque el
    /// botón vivía al fondo de un scroll de dos pantallas y cambiar de página perdía lo tocado en
    /// silencio. Resolvía el síntoma dejando la causa: que hubiera un paso entre tocar un ajuste y
    /// que el ajuste valiera. Sin ese paso no hay nada que perder, nada que descartar y nada de lo
    /// que avisar al salir — con lo que <b>P-19</b> (confirmar la salida con cambios sin guardar)
    /// deja de tener trabajo que hacer.
    /// </para>
    /// <para>
    /// El enganche es UNO —esta notificación— y no diez <c>partial void On…Changed</c>, por lo
    /// mismo que la huella de D-987: diez ganchos iguales acaban siendo nueve.
    /// </para>
    /// <para>
    /// <b>Tarifas no entra aquí</b>: escribe en el hub, que es un fichero del equipo con su commit
    /// y su atribución, y eso se pulsa. Por eso conserva su «Guardar tarifas».
    /// </para>
    /// </summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (_persisting || e.PropertyName is null || !Editable.Contains(e.PropertyName))
        {
            return;
        }

        Persist(e.PropertyName);
    }

    /// <summary>
    /// Escribe los ajustes y enciende la marca del campo que lo provocó.
    /// <para>
    /// <b>El toast solo sale cuando hay algo que decir.</b> Un aviso por cada interruptor sería
    /// ruido —la marca de al lado ya dice que se ha guardado—, pero un mínimo aplicado se sigue
    /// contando: un valor corregido en silencio se vive igual que un ajuste que no ajusta
    /// (BUGFIX-AJUSTES). Escribes 0, no pasa nada, y no hay forma de saber qué número mandó.
    /// </para>
    /// </summary>
    private void Persist(string field)
    {
        _persisting = true;
        try
        {
            var corrections = new List<string>();
            _settings.Save(BuildSettings(corrections));

            // Las cajas enseñan lo que de verdad quedó guardado, que es donde se ve el mínimo.
            MaxPassesPerUnit = _settings.Current.MaxPassesPerUnit;
            PollingSeconds = _settings.Current.PollingSeconds;
            CopilotTimeoutMinutes = _settings.Current.CopilotTimeoutMinutes;
            FreshnessDays = _settings.Current.Thresholds.FreshnessDays;

            // Solo cuando es el tema lo que ha cambiado: sustituir la paleta en cada guardado
            // sería rehacer los diccionarios al mover cualquier interruptor.
            if (field == nameof(IsLightTheme))
            {
                ThemeService.Apply(IsLightTheme ? "light" : "dark");
            }

            // F29 §2 — la divisa se aplica al momento y en todas partes, como el tema: se cambia en
            // Ajustes y se va a mirar Métricas, no se reinicia la aplicación.
            if (field == nameof(ShowCostIn))
            {
                CostFormat.Currency = SelectedCurrency;
            }

            if (corrections.Count > 0)
            {
                _toasts.Show("Ajustes guardados, con correcciones — "
                    + string.Join(" · ", corrections) + ".");
            }

            Flash(field);
        }
        finally
        {
            _persisting = false;
        }
    }

    /// <summary>
    /// Enciende la marca del ajuste. Baja y sube —un pulso— para que dos guardados seguidos del
    /// mismo campo vuelvan a empezar la cuenta en vez de dejarla desvaneciéndose.
    /// </summary>
    private void Flash(string field)
    {
        SettingSaved mark = Saved[field];
        mark.Shown = false;
        mark.Shown = true;
    }

    // ---------- Zona peligrosa (F5.7 §5) ----------

    /// <summary>
    /// Restablecimiento de fábrica. Pregunta con los números delante, exige teclear RESET y solo
    /// entonces llama al servicio, que es atómico: o se borra el hub Y esta máquina, o no se toca
    /// nada. El resultado —bueno o malo— se cuenta por toast, y al terminar la app se va a
    /// «Cuenta», que es la pantalla de primer arranque.
    /// </summary>
    [RelayCommand]
    private async Task FactoryReset()
    {
        FactoryResetImpact impact = _reset.Describe();
        var confirmation = new FactoryResetConfirmation(impact);
        if (!_confirmer.Confirm(confirmation) || !confirmation.CanReset)
        {
            // Un «sí» sin la palabra escrita (una vista mal enlazada, un confirmador ajeno) no
            // abre la puerta: la regla se vuelve a mirar aquí, no solo en el diálogo.
            return;
        }

        IsBusy = true;
        FactoryResetResult result;
        try
        {
            string by = _hub.ResolveIdentity().Name;
            result = await Task.Run(() => _reset.Reset(by));
        }
        catch (Exception ex)
        {
            _toasts.Show($"No se pudo restablecer de fábrica: {ex.Message}. No se ha borrado nada.");
            return;
        }
        finally
        {
            IsBusy = false;
        }

        _toasts.Show(result.Message);
        if (result.Done)
        {
            await _navigation.NavigateToAsync<AccountViewModel>();
        }
    }
}
