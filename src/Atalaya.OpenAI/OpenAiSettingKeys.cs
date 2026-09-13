namespace Atalaya.OpenAI;

/// <summary>
/// <b>Cómo se llama en los ajustes lo que esta casa necesita</b> (PROV-3 §2).
/// <para>
/// Las claves viven aquí, en el proyecto del proveedor, y no en el de la aplicación: es la misma
/// regla de PROV-2 que puso el identificador en la casa que lo usa. La aplicación guarda y devuelve
/// un par nombre-valor sin saber qué significa, que es lo que permite que la casa siguiente traiga
/// los suyos sin tocar Ajustes por dentro.
/// </para>
/// <para>
/// <b>La clave de API no está aquí</b>, y no es un olvido: no es un ajuste. Va por
/// <c>AgentHostServices.Secret</c>, al almacén cifrado (§3).
/// </para>
/// </summary>
public static class OpenAiSettingKeys
{
    /// <summary>La raíz de la API, sin `/chat/completions`.</summary>
    public const string BaseUrl = "baseUrl";

    /// <summary>Cómo viaja la clave: el nombre de un valor de <see cref="OpenAiAuth"/>.</summary>
    public const string Auth = "auth";

    /// <summary>
    /// Lee el modo de autenticación guardado. Lo que no se reconoce —o falta— es `Bearer`, que es
    /// lo que hacen todos menos Azure: equivocarse hacia el caso común deja un fallo con remedio
    /// («cambia el desplegable») en vez de uno sin causa visible.
    /// </summary>
    public static OpenAiAuth AuthOf(string? guardado)
        => Enum.TryParse(guardado, ignoreCase: true, out OpenAiAuth auth) ? auth : OpenAiAuth.Bearer;
}
