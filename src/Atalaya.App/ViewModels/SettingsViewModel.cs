using System.Collections.ObjectModel;
using Atalaya.App.Services;
using Atalaya.App.Views;
using Atalaya.Copilot;
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
    /// De dónde salen los enlaces del «Acerca de» (BUGFIX-VERSION). Opcional por lo mismo que el
    /// diálogo: sin él no hay enlaces, y el diálogo lo dice — nunca uno roto.
    /// </summary>
    private readonly DeployConfig? _deploy;
    private readonly IAboutDialog? _about;

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
        IAboutDialog? about = null,
        DeployConfig? deploy = null,
        AuditorProviderRegistry? providers = null)
    {
        _settings = settings;
        _agent = agent;
        _providers = providers;
        _toasts = toasts;
        _reset = reset;
        _confirmer = confirmer;
        _hub = hub;
        _navigation = navigation;
        _about = about;
        _deploy = deploy;
        AppSettings s = settings.Current;
        _editor = s.Editor;
        _isLightTheme = string.Equals(s.Theme, "light", StringComparison.OrdinalIgnoreCase);
        _pollingSeconds = s.PollingSeconds;
        _freshnessDays = s.Thresholds.FreshnessDays;
        _maxPassesPerUnit = s.MaxPassesPerUnit;
        _copilotTimeoutMinutes = s.CopilotTimeoutMinutes;
        _enableAssistedFix = s.EnableAssistedFix;
        // F14 — el proveedor elegido, y el modelo DE ESE proveedor. Los dos campos de modelo son
        // independientes porque sus espacios de nombres no se solapan, así que ir y volver entre
        // casas conserva las dos elecciones en vez de dejar una configurada con un id imposible.
        _selectedProviderId = providers?.Current.ProviderId ?? string.Empty;
        foreach (IAuditorProvider provider in providers?.All ?? Array.Empty<IAuditorProvider>())
        {
            Providers.Add(new ProviderOption(provider.ProviderId, provider.ProviderName));
        }

        _selectedModelId = _selectedProviderId.Length > 0
            ? settings.ModelFor(_selectedProviderId)
            : s.CopilotModel;

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
        + "siguiente sesión, y queda escrito en ella y en su informe. El arreglo asistido sigue "
        + "siendo de Copilot.";

    public override string Title => "Ajustes";

    [ObservableProperty] private string _editor;
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

        // El modelo se guarda en el campo de SU proveedor: guardar el de Claude Code encima del de
        // Copilot dejaría a la otra casa con un id que no reconoce.
        if (!string.IsNullOrWhiteSpace(SelectedModelId))
        {
            if (string.IsNullOrWhiteSpace(SelectedProviderId))
            {
                s.CopilotModel = SelectedModelId.Trim();
            }
            else if (string.Equals(SelectedProviderId, "claude-code", StringComparison.OrdinalIgnoreCase))
            {
                s.ClaudeCodeModel = SelectedModelId.Trim();
            }
            else
            {
                s.CopilotModel = SelectedModelId.Trim();
            }
        }
        s.CopilotTimeoutMinutes = Floor(
            CopilotTimeoutMinutes, SettingsLimits.MinCopilotTimeoutMinutes,
            "el timeout de Copilot", "minuto", corrections);
        s.EnableAssistedFix = EnableAssistedFix;
        return s;
    }

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

    /// <summary>
    /// El «Acerca de» (F6.4 §3). Opcional en el constructor porque los tests de ajustes no montan
    /// ventanas: sin él, el gesto no hace nada en vez de reventar.
    /// </summary>
    [RelayCommand]
    private void ShowAbout() => _about?.Show(AboutInfo.Create(_hub, _deploy));

    [RelayCommand]
    private void Save()
    {
        var corrections = new List<string>();
        _settings.Save(BuildSettings(corrections));

        // Las cajas enseñan lo que de verdad quedó guardado. Antes solo se refrescaban tres; el
        // umbral y la frescura se quedaban enseñando un número que el fichero no tenía.
        MaxPassesPerUnit = _settings.Current.MaxPassesPerUnit;
        PollingSeconds = _settings.Current.PollingSeconds;
        CopilotTimeoutMinutes = _settings.Current.CopilotTimeoutMinutes;
        FreshnessDays = _settings.Current.Thresholds.FreshnessDays;
        ThemeService.Apply(IsLightTheme ? "light" : "dark");
        // Toast global (F5.3): el aviso vivía al fondo de la página y no se veía sin bajar hasta
        // él — justo debajo del botón que lo provocaba, pero fuera de la pantalla.
        _toasts.Show(corrections.Count == 0
            ? "Ajustes guardados."
            : "Ajustes guardados, con correcciones — " + string.Join(" · ", corrections) + ".");
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
