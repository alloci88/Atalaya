namespace Atalaya.OpenAI;

/// <summary>
/// <b>Con qué se construye un transporte para una configuración concreta</b> (PROV-3 §2).
/// <para>
/// Vive junto a <see cref="IChatEndpoint"/> y no en la aplicación, y es deliberado: quien monta el
/// transporte necesita un <c>HttpMessageHandler</c> con la política de proxy y de TLS de la casa,
/// y eso lo sabe la composición —el único sitio del árbol que conoce a los proveedores—, no la
/// pantalla que edita los ajustes. Ajustes recibe este verbo ya relleno y lo llama; si nadie se lo
/// da, «Probar» lo dice en línea y el resto de la sección sigue funcionando.
/// </para>
/// <para>
/// <b>La clave viaja como parámetro y no se queda en ningún sitio de este camino</b>: quien la
/// tiene es el almacén cifrado, y de ahí sale justo para la llamada.
/// </para>
/// </summary>
public delegate IChatEndpoint ChatEndpointFactory(OpenAiEndpoint endpoint, string? apiKey);
