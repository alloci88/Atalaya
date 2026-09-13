namespace Atalaya.OpenAI;

/// <summary>Cómo se presenta la clave en la petición. Dos valores, y no hay un tercero.</summary>
public enum OpenAiAuth
{
    /// <summary>`Authorization: Bearer {clave}`. Lo que hacen OpenAI y casi todos.</summary>
    Bearer,

    /// <summary>`api-key: {clave}`. Lo que pide Azure OpenAI, y el único motivo de que esto sea un enum.</summary>
    ApiKey,
}

/// <summary>
/// <b>A qué endpoint se habla</b> (PROV-3 §2). Lo que cabe en `settings.json`: la URL, el modelo y
/// cómo se presenta la clave. <b>La clave NO está aquí</b> —vive en el almacén de secretos (§3)— y
/// este tipo no la conoce ni la puede filtrar por descuido a un log o a un volcado de ajustes.
/// </summary>
/// <param name="BaseUrl">
/// La raíz de la API, sin `/chat/completions`: `https://api.openai.com/v1`,
/// `https://{recurso}.openai.azure.com/openai/deployments/{despliegue}`,
/// `http://localhost:11434/v1`.
/// </param>
/// <param name="Model">El modelo, texto libre: cada endpoint tiene los suyos y no hay lista común.</param>
/// <param name="Auth">Cómo viaja la clave.</param>
public sealed record OpenAiEndpoint(string BaseUrl, string Model, OpenAiAuth Auth = OpenAiAuth.Bearer)
{
    /// <summary>Lo que se enseña en el hueco del campo, que no es un valor por defecto.</summary>
    public const string UrlDeEjemplo = "https://api.openai.com/v1";

    /// <summary>Está configurado lo mínimo para intentar una llamada.</summary>
    public bool IsConfigured
        => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);

    /// <summary>
    /// <b>Por qué esta URL no vale</b>, o <c>null</c> si vale (PROV-3 §8).
    /// <para>
    /// <b>`http://` solo en local.</b> Un endpoint en claro fuera de esta máquina manda el prompt
    /// —que lleva el código auditado— y la clave por la red sin cifrar, y en una red corporativa
    /// eso lo ve cualquiera. En `localhost` no sale de la máquina, y es justo donde viven Ollama y
    /// LM Studio, que son la forma de probar Atalaya sin factura: prohibirlo ahí sería cerrar la
    /// única puerta gratis por una amenaza que no existe.
    /// </para>
    /// </summary>
    public static string? WhyNot(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return "Falta la URL base del endpoint.";
        }

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return "La URL base no es una dirección válida. Tiene que empezar por «https://» "
                + "—o por «http://» si es un endpoint local— y no llevar «/chat/completions».";
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttp)
        {
            return $"«{uri.Scheme}» no es un protocolo con el que se pueda hablar con una API.";
        }

        return IsLocal(uri)
            ? null
            : "Una URL «http://» manda el prompt —con tu código dentro— y la clave sin cifrar. "
              + "Solo se admite en un endpoint local (localhost o 127.0.0.1); para cualquier otro, "
              + "usa «https://».";
    }

    /// <summary>La máquina de uno, en sus tres nombres.</summary>
    private static bool IsLocal(Uri uri)
        => uri.IsLoopback
           || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>La dirección de `chat/completions` para esta base, sin barras dobles.</summary>
    public Uri ChatCompletions() => new(BaseUrl.TrimEnd('/') + "/chat/completions");

    /// <summary>La de `models`, que es lo que «Probar» pregunta primero si el endpoint la sirve.</summary>
    public Uri Models() => new(BaseUrl.TrimEnd('/') + "/models");
}
