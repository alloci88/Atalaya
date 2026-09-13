using System.Net;
using Atalaya.Agents;

namespace Atalaya.OpenAI;

/// <summary>
/// <b>De qué se ha quejado el endpoint, en el vocabulario común</b> (PROV-3 §7).
/// <para>
/// Es el gemelo de los clasificadores de las otras dos casas y sigue su misma regla: <b>lo que no
/// se reconoce sale como desconocido con el crudo delante</b>, nunca con una causa propuesta. Un
/// mensaje bonito con la causa equivocada manda a alguien a arreglar lo que no está roto (N-2).
/// </para>
/// <para>
/// Aquí el código HTTP es la señal, y es mejor señal que el texto: este dialecto lo hablan siete
/// casas y ninguna escribe el mismo cuerpo de error, pero todas devuelven los mismos códigos. El
/// texto del endpoint no se interpreta — se enseña.
/// </para>
/// <para>
/// <b>Cada frase dice qué revisar y dónde</b>, y ninguna nombra a otro proveedor: quien lee esto
/// está configurando un endpoint por API, y mandarle a mirar la instalación de otra cosa es
/// hacerle perder la tarde.
/// </para>
/// </summary>
public static class OpenAiFailure
{
    /// <summary>Lo que se conserva del cuerpo de error para poder copiarlo y pegarlo.</summary>
    private const int MaxDetail = 500;

    // ==================================================================== la tabla, en un solo sitio

    /// <summary>
    /// El código HTTP en el vocabulario común. <b>401/403</b> credencial, <b>404</b> modelo o URL,
    /// <b>429</b> cuota, <b>5xx</b> el proveedor no responde. Lo demás no se adivina.
    /// </summary>
    public static AgentProblem Classify(HttpStatusCode code) => (int)code switch
    {
        401 or 403 => AgentProblem.NotAuthenticated,
        404 => AgentProblem.ModelUnavailable,
        429 => AgentProblem.QuotaExhausted,
        >= 500 => AgentProblem.Offline,
        _ => AgentProblem.Unknown,
    };

    /// <summary>
    /// <b>Dónde se reintenta y dónde no.</b> Solo el 429 y el 5xx: son los dos que mejoran solos.
    /// <para>
    /// Un 4xx NO se reintenta nunca, y la razón no es de elegancia: una clave que no vale no
    /// empieza a valer por mandarla otra vez, y contra una API de pago cada intento es una línea
    /// en la factura. La misma regla que <c>AuditorProviderException.Retryable</c> aplica a la
    /// cuota del otro dialecto — con la diferencia de que aquí el 429 sí suele ser «vas demasiado
    /// deprisa» y no «se acabó el mes», y por eso se le concede UN intento y no más.
    /// </para>
    /// </summary>
    public static bool ShouldRetry(HttpStatusCode code)
        => (int)code == 429 || (int)code >= 500;

    // ========================================================================= las frases, y su porqué

    /// <summary>Falta la URL, el modelo o la clave: no se llega a llamar.</summary>
    public const string NoKey =
        "Falta la clave de API del endpoint. Pégala en Ajustes → Proveedor y modelo: se guarda "
        + "cifrada en esta máquina y no viaja al hub.";

    /// <summary>Sin modelo no hay nada que pedir, y no hay lista de la que elegir por él.</summary>
    public const string NoModel =
        "Falta el modelo. Escríbelo en Ajustes → Proveedor y modelo: cada endpoint tiene los suyos "
        + "y no hay una lista común que ofrecer.";

    /// <summary>401 y 403. Los dos remedios están en el mismo sitio, y el segundo se olvida.</summary>
    public static string Credential(int code)
        => $"El endpoint ha rechazado la clave de API (HTTP {code}). Revísala en Ajustes → "
           + "Proveedor y modelo, y mira también CÓMO viaja: «Bearer» para casi todos los "
           + "endpoints, «api-key» solo para Azure. Con el modo equivocado una clave buena se "
           + "rechaza igual. Atalaya no lo reintenta: insistir con una credencial que no vale no "
           + "la arregla, y contra una API de pago cada intento cuenta.";

    /// <summary>
    /// 404. Dice las DOS cosas que puede ser, porque en este dialecto lo son de verdad: el
    /// endpoint devuelve 404 tanto si el modelo no existe como si la URL base apunta a otro sitio.
    /// Proponer solo una manda a media gente a cambiar lo que estaba bien.
    /// </summary>
    public static string ModelOrUrl(string? model)
        => (string.IsNullOrWhiteSpace(model)
                ? "El endpoint ha contestado 404: o el modelo configurado no existe ahí, "
                : $"El endpoint ha contestado 404: o el modelo «{model}» no existe ahí, ")
           + "o la URL base no apunta a donde crees. Revisa las dos en Ajustes → Proveedor y "
           + "modelo. La URL base termina en la raíz de la API —«…/v1», por ejemplo— y NO lleva "
           + "«/chat/completions»: ese trozo lo pone Atalaya.";

    /// <summary>429. Ni la clave ni el modelo, y no hay nada que tocar en Ajustes.</summary>
    public const string Quota =
        "El endpoint ha devuelto 429: o se ha agotado la cuota, o se le está pidiendo más deprisa "
        + "de lo que admite. No es la clave ni el modelo, y no hay nada que cambiar en Ajustes: "
        + "hay que esperar a que se reponga, o pasar a un plan o un modelo con más margen. Atalaya "
        + "lo ha reintentado una vez y no insiste más — insistir contra una cuota agotada solo "
        + "gasta lo que venga después.";

    /// <summary>5xx. Transitorio, y decirlo evita que alguien se ponga a repasar la clave.</summary>
    public static string NotResponding(int code)
        => $"El endpoint no ha podido atender la petición (HTTP {code}). No es tu clave ni tu "
           + "modelo: el fallo está del otro lado y es transitorio. Atalaya lo ha reintentado una "
           + "vez. Si se repite, comprueba en Ajustes → Proveedor y modelo que la URL base es la "
           + "que quieres y vuelve a intentarlo en un rato.";

    /// <summary>No hubo respuesta: ni conexión, ni cabeceras dentro del tope de D-208.</summary>
    public static string Unreachable(string baseUrlHint)
        => $"El endpoint no ha contestado en {OpenAiHttp.Deadline.TotalSeconds:0} segundos, o no "
           + "se ha podido conectar con él. Comprueba que la URL base de Ajustes → Proveedor y "
           + "modelo se alcanza desde esta red y que, si sales por un proxy corporativo, lo deja "
           + "pasar. Atalaya lo ha reintentado una vez." + baseUrlHint;

    /// <summary>
    /// Lo que no se sabe clasificar: el crudo delante, sin proponer causa. Es la lección de
    /// BUGFIX-CUOTA y la N-2 a la vez.
    /// </summary>
    public static string Unknown(int code)
        => $"El endpoint ha rechazado la petición con un HTTP {code} que Atalaya no sabe "
           + "interpretar, así que no se inventa la causa. Debajo va el error tal y como lo ha "
           + "devuelto, para poder copiarlo.";

    /// <summary>
    /// <b>200, pero no en streaming.</b> Merece frase propia porque es el fallo que peor se nota:
    /// un endpoint que ignora <c>stream: true</c> devuelve un JSON normal, el lector de SSE no
    /// encuentra un solo <c>data:</c> y el turno saldría VACÍO — sin texto, sin llamadas y sin
    /// error. La unidad terminaría «bien» y sin un solo hallazgo, que es exactamente la clase de
    /// fallo silencioso que costó descubrir en D-782. Se para aquí y se dice.
    /// </summary>
    public const string NotStreaming =
        "El endpoint ha contestado, pero no en streaming: no ha mandado un solo evento. Lo más "
        + "probable es que la URL base de Ajustes → Proveedor y modelo no sea la de una API "
        + "compatible con «chat/completions» —una página de inicio de sesión o un portal de proxy "
        + "también contestan 200—. Debajo va lo que devolvió.";

    // ================================================================== y la excepción ya clasificada

    /// <summary>
    /// La excepción que corresponde a una respuesta de error, con su tipo: la credencial y el
    /// modelo tienen hijas propias porque tienen REMEDIO propio, y quien las captura ofrece el
    /// gesto exacto sin volver a mirar un código HTTP.
    /// </summary>
    public static AuditorProviderException Exception(HttpStatusCode status, string? body, string? model)
    {
        int code = (int)status;
        string? detail = Detail(status, body);

        return Classify(status) switch
        {
            AgentProblem.NotAuthenticated
                => new AuditorAuthenticationException(Credential(code), AgentProblem.NotAuthenticated, detail),
            AgentProblem.ModelUnavailable
                => new AuditorModelUnavailableException(model, ModelOrUrl(model), detail),
            AgentProblem.QuotaExhausted
                => new AuditorProviderException(Quota, AgentProblem.QuotaExhausted, detail),
            AgentProblem.Offline
                => new AuditorProviderException(NotResponding(code), AgentProblem.Offline, detail),
            _ => new AuditorProviderException(Unknown(code), AgentProblem.Unknown, detail),
        };
    }

    /// <summary>No hubo respuesta. Transitorio, y por tanto <c>Offline</c>.</summary>
    public static AuditorProviderException NoAnswer(Exception? inner, string? baseUrl = null)
        => new(
            Unreachable(string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : $" URL: {baseUrl}"),
            AgentProblem.Offline,
            AuditorHelp.RawFailure(inner),
            inner);

    /// <summary>200 sin un solo evento SSE: se para antes de devolver un turno vacío.</summary>
    public static AuditorProviderException NotStreamingException(string? body)
        => new(NotStreaming, AgentProblem.Unknown, Trim(body));

    /// <summary>
    /// El crudo copiable: el código delante —que es con lo que se busca en el panel del
    /// endpoint— y el cuerpo recortado detrás. <b>Nunca lleva la clave</b>: lo que se guarda es lo
    /// que el endpoint DEVOLVIÓ, jamás lo que se le mandó.
    /// </summary>
    private static string Detail(HttpStatusCode status, string? body)
    {
        string raw = Trim(body) ?? string.Empty;
        return raw.Length == 0
            ? $"HTTP {(int)status} {status}"
            : $"HTTP {(int)status} {status}: {raw}";
    }

    private static string? Trim(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        string raw = body.Trim();
        return raw.Length <= MaxDetail ? raw : raw[..MaxDetail] + "…";
    }
}
