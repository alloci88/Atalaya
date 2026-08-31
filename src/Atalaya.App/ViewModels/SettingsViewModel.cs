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
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly ICopilotAgent _agent;
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
        ICopilotAgent agent,
        ToastCenter toasts,
        FactoryResetService reset,
        IFactoryResetConfirmer confirmer,
        HubContext hub,
        NavigationService navigation,
        IAboutDialog? about = null,
        DeployConfig? deploy = null)
    {
        _settings = settings;
        _agent = agent;
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
        _largeUnitLoc = s.DefaultThresholds.LargeUnitLoc;
        _freshnessDays = s.DefaultThresholds.FreshnessDays;
        _maxPassesPerUnit = s.MaxPassesPerUnit;
        _copilotTimeoutMinutes = s.CopilotTimeoutMinutes;
        _enableAssistedFix = s.EnableAssistedFix;
        _selectedModelId = s.CopilotModel;

        // Hasta que el SDK conteste, el desplegable enseña el modelo configurado: así nunca está
        // vacío ni «elige» en silencio uno distinto del que se está usando.
        Models.Add(ModelOption.Unverified(s.CopilotModel));
    }

    public override string Title => "Ajustes";

    [ObservableProperty] private string _editor;
    [ObservableProperty] private bool _isLightTheme;
    [ObservableProperty] private int _pollingSeconds;
    [ObservableProperty] private int _largeUnitLoc;
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
            IReadOnlyList<AgentModel> models = await _agent.ListModelsAsync(timeout.Token);
            if (models.Count == 0)
            {
                ModelsNotice = "El SDK no devolvió ningún modelo; se mantiene el configurado.";
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
            ModelsNotice = "No se pudo obtener la lista de modelos (Copilot no respondió a tiempo). "
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
    /// Vuelca a los ajustes SOLO lo que esta página edita. Lo que ya no tiene control —el flag de
    /// H9, el override de la URL del hub, el TLS estricto y el PAT— se queda como esté en el
    /// fichero: retirar un control de la interfaz no puede significar borrar el valor de quien lo
    /// tenía puesto.
    /// </summary>
    private AppSettings BuildSettings()
    {
        AppSettings s = _settings.Current;
        s.Editor = Editor;
        s.Theme = IsLightTheme ? "light" : "dark";
        s.PollingSeconds = Math.Max(15, PollingSeconds);
        // Se parte de los umbrales vigentes y solo se pisan los editables: construir un
        // Thresholds nuevo devolvía MaxTokensPerUnit y ClaimTtlMinutes a sus valores por defecto
        // cada vez que alguien pulsaba Guardar.
        s.DefaultThresholds.LargeUnitLoc = LargeUnitLoc;
        s.DefaultThresholds.FreshnessDays = FreshnessDays;
        // Tope 1 = una pasada única; por eso el barrido no necesita ningún selector de modo por
        // lanzamiento (F5.1).
        s.MaxPassesPerUnit = Math.Max(1, MaxPassesPerUnit);
        s.CopilotModel = string.IsNullOrWhiteSpace(SelectedModelId)
            ? s.CopilotModel
            : SelectedModelId.Trim();
        s.CopilotTimeoutMinutes = Math.Max(1, CopilotTimeoutMinutes);
        s.EnableAssistedFix = EnableAssistedFix;
        return s;
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
        _settings.Save(BuildSettings());

        MaxPassesPerUnit = _settings.Current.MaxPassesPerUnit;
        PollingSeconds = _settings.Current.PollingSeconds;
        CopilotTimeoutMinutes = _settings.Current.CopilotTimeoutMinutes;
        ThemeService.Apply(IsLightTheme ? "light" : "dark");
        // Toast global (F5.3): el aviso vivía al fondo de la página y no se veía sin bajar hasta
        // él — justo debajo del botón que lo provocaba, pero fuera de la pantalla.
        _toasts.Show("Ajustes guardados.");
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
