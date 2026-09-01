using Atalaya.Agents;

namespace Atalaya.ClaudeCode;

/// <summary>
/// De qué se ha quejado Claude Code, en el vocabulario común (F14).
/// <para>
/// Es el gemelo de <c>CopilotFailure</c> y sigue su misma regla, que es la lección de
/// BUGFIX-CUOTA: <b>la cuota se mira antes que el asiento</b>, y <b>lo que no se reconoce sale
/// como desconocido con el texto crudo delante</b> en vez de proponer una causa. Un mensaje bonito
/// con la causa equivocada manda a alguien a arreglar lo que no está roto.
/// </para>
/// <para>
/// Las firmas salen de lo que el CLI devuelve de verdad —se ejecutó contra el binario 2.1.252 para
/// escribirlas— más las formas conocidas de nombrar lo mismo. Cada vez que aparezca una nueva en
/// un log, su firma se añade aquí (igual que se hace con la de Copilot, D-707).
/// </para>
/// </summary>
public static class ClaudeFailure
{
    /// <summary>
    /// La causa, a partir de lo que trae el evento <c>result</c>: el texto, el
    /// <c>terminal_reason</c> y el código HTTP cuando lo hay.
    /// </summary>
    /// <param name="apiErrorStatus">
    /// <c>api_error_status</c>. El 404 de un modelo inexistente llega por aquí —verificado— y es
    /// la señal más fiable de las tres, así que se mira antes que el texto.
    /// </param>
    public static AgentProblem Classify(string? resultText, string? terminalReason = null, int? apiErrorStatus = null)
    {
        string text = (resultText ?? string.Empty).ToLowerInvariant();
        string reason = (terminalReason ?? string.Empty).ToLowerInvariant();

        // 1 — La cuota primero, SIEMPRE. Es la regla que costó descubrir en BUGFIX-CUOTA: el
        //     detector de «sin asiento» llevaba «quota» dentro y por eso la cuota agotada se leía
        //     como una licencia que faltaba, mandando a reclamar un asiento que ya se tenía.
        if (IsQuota(text) || apiErrorStatus == 429)
        {
            return AgentProblem.QuotaExhausted;
        }

        // 2 — El modelo. El 404 con `unrecognized_model` es literal del CLI.
        if (apiErrorStatus == 404 || IsModel(text))
        {
            return AgentProblem.ModelUnavailable;
        }

        // 3 — La credencial.
        if (apiErrorStatus is 401 or 403 || IsAuth(text))
        {
            return AgentProblem.NotAuthenticated;
        }

        // 4 — La red. Va la última de las conocidas porque sus palabras («connection», «network»)
        //     aparecen de refilón en errores que no son de red.
        if (IsOffline(text) || reason == "network_error")
        {
            return AgentProblem.Offline;
        }

        return AgentProblem.Unknown;
    }

    private static bool IsQuota(string text)
        => text.Contains("quota")
        || text.Contains("rate limit")
        || text.Contains("usage limit")
        || text.Contains("exceeded your")
        || text.Contains("out of credit")
        || text.Contains("insufficient credit")
        || text.Contains("upgrade to continue");

    private static bool IsModel(string text)
        => text.Contains("unrecognized_model")
        || text.Contains("issue with the selected model")
        || (text.Contains("model") && (text.Contains("not available") || text.Contains("does not exist")));

    private static bool IsAuth(string text)
        => text.Contains("not logged in")
        || text.Contains("please log in")
        || text.Contains("authentication")
        || text.Contains("unauthorized")
        || text.Contains("invalid api key")
        || text.Contains("oauth token has expired");

    private static bool IsOffline(string text)
        => text.Contains("econnrefused")
        || text.Contains("enotfound")
        || text.Contains("etimedout")
        || text.Contains("getaddrinfo")
        || text.Contains("network error")
        || text.Contains("could not reach");

    /// <summary>
    /// El error tal cual, para poder copiarlo y pegarlo. Va SEPARADO del mensaje: la frase es para
    /// decidir qué hacer, el crudo es para buscar la petición concreta (BUGFIX-CUOTA).
    /// </summary>
    public static string? Raw(Exception? ex)
        => ex is null ? null : $"{ex.GetType().Name}: {ex.Message}";
}
