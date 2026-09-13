using Atalaya.OpenAI;

namespace Atalaya.App.Services;

/// <summary>
/// <b>La configuración del endpoint por API, vista desde Ajustes</b> (PROV-3 §§2-3).
/// <para>
/// <b>Por qué existe en vez de que el view-model lo haga a mano.</b> Los cuatro datos de esta
/// casa no viven en el mismo sitio, y ése es justamente el punto de la entrega: la URL, el modo
/// de autenticación y el modelo van a <c>settings.json</c> —texto plano, que se abre, se pega en
/// un mensaje y acaba en una captura— y la clave va al almacén cifrado con DPAPI. Un view-model
/// que escribiera los cuatro por su cuenta tendría cuatro oportunidades de equivocarse de sitio,
/// y equivocarse una vez es publicar la clave.
/// </para>
/// <para>
/// <b>Nada de esto se queda en memoria.</b> Cada propiedad lee del servicio en cada uso, por lo
/// de BUGFIX-AJUSTES: capturar la URL al construir haría que cambiarla no sirviera hasta
/// reiniciar.
/// </para>
/// </summary>
public sealed class OpenAiEndpointSettings
{
    /// <summary>Lo que se espera por una prueba antes de darla por perdida.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly SettingsService _settings;
    private readonly ProviderSecretStore _secrets;
    private readonly ChatEndpointFactory? _endpoints;

    /// <param name="endpoints">
    /// Cómo se monta el transporte. <b>Opcional a propósito</b>: mientras no esté cableado,
    /// «Probar» lo dice en línea en vez de reventar, y el resto de la sección —guardar la URL, el
    /// modo, el modelo y la clave— funciona igual.
    /// </param>
    public OpenAiEndpointSettings(
        SettingsService settings,
        ProviderSecretStore secrets,
        ChatEndpointFactory? endpoints = null)
    {
        _settings = settings;
        _secrets = secrets;
        _endpoints = endpoints;
    }

    /// <summary>
    /// De quién es esta configuración. Sale de la constante del proveedor y nunca de una cadena
    /// escrita a mano (PROV-2 §1).
    /// </summary>
    public static string ProviderId => OpenAiCompatibleProvider.Id;

    /// <summary>Lo que se enseña en el hueco del campo de la URL, que NO es un valor por defecto.</summary>
    public static string UrlPlaceholder => OpenAiEndpoint.UrlDeEjemplo;

    /// <summary>La URL base guardada, o vacío.</summary>
    public string BaseUrl => _settings.ProviderOption(ProviderId, OpenAiSettingKeys.BaseUrl) ?? string.Empty;

    /// <summary>Cómo se presenta la clave. Lo que no se reconoce es `Bearer`.</summary>
    public OpenAiAuth Auth => OpenAiSettingKeys.AuthOf(_settings.ProviderOption(ProviderId, OpenAiSettingKeys.Auth));

    /// <summary>El modelo, del mapa por proveedor de siempre. No hay un sitio nuevo para éste.</summary>
    public string Model => _settings.ModelFor(ProviderId);

    /// <summary>¿Hay clave guardada? Sin devolverla: es lo único que la pantalla necesita saber.</summary>
    public bool HasKey => _secrets.Has(ProviderId);

    /// <summary>Lo configurado ahora mismo, tal y como lo lee el proveedor al auditar.</summary>
    public OpenAiEndpoint Endpoint => new(BaseUrl, Model, Auth);

    /// <summary>Guarda la URL base —o la borra, con vacío— en <c>settings.json</c>.</summary>
    public void SaveBaseUrl(string? baseUrl)
        => _settings.SetProviderOption(ProviderId, OpenAiSettingKeys.BaseUrl, baseUrl);

    /// <summary>Guarda el modo de autenticación. Es el NOMBRE del valor, no su número.</summary>
    public void SaveAuth(OpenAiAuth auth)
        => _settings.SetProviderOption(ProviderId, OpenAiSettingKeys.Auth, auth.ToString());

    /// <summary>
    /// Guarda la clave —o la borra, con vacío— en el almacén cifrado. <b>Nunca en los ajustes</b>,
    /// y por eso este camino es otro: la clave no pasa por <see cref="AppSettings"/> en ningún
    /// momento, ni siquiera de paso.
    /// </summary>
    public void SaveKey(string? key) => _secrets.Set(ProviderId, key);

    /// <summary>
    /// <b>Probar</b> (PROV-3 §2): lo que se puede decir sin red, primero, y solo después la
    /// llamada. Una URL mal escrita no tiene por qué costar treinta segundos de espera para que
    /// la respuesta sea «no se pudo conectar».
    /// <para>
    /// La frase de vuelta se enseña tal cual debajo del botón, así que dice qué revisar y dónde;
    /// el detalle crudo —lo que contestó el endpoint— va aparte y plegado, y <b>nunca lleva la
    /// clave</b>.
    /// </para>
    /// </summary>
    public async Task<ChatProbe> ProbeAsync(CancellationToken ct)
    {
        OpenAiEndpoint endpoint = Endpoint;

        if (OpenAiEndpoint.WhyNot(endpoint.BaseUrl) is { } mal)
        {
            return new ChatProbe(false, mal);
        }

        if (string.IsNullOrWhiteSpace(endpoint.Model))
        {
            return new ChatProbe(false,
                "Falta el modelo. Escríbelo aquí arriba: cada endpoint tiene los suyos y no hay "
                + "una lista común que ofrecer.");
        }

        string? key = _secrets.Get(ProviderId);
        if (string.IsNullOrWhiteSpace(key))
        {
            return new ChatProbe(false,
                "Falta la clave de API. Se guarda cifrada en esta máquina, nunca en el hub.");
        }

        if (_endpoints is null)
        {
            return new ChatProbe(false, "No se puede probar el endpoint desde aquí.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            return await _endpoints(endpoint, key).ProbeAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ChatProbe(false,
                $"El endpoint no contestó en {ProbeTimeout.TotalSeconds:0} segundos. Comprueba la "
                + "URL base y que esta máquina llegue hasta ella.");
        }
        catch (Exception ex)
        {
            // El transporte traduce lo que conoce; lo que llegue aquí es lo que no supo clasificar
            // nadie, y decirlo con su texto crudo detrás es más útil que un «error desconocido».
            return new ChatProbe(false, "No se pudo hablar con el endpoint.", ex.Message);
        }
    }
}
