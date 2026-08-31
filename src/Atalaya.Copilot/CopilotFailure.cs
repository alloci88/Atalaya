using System.Text;
using System.Text.RegularExpressions;

namespace Atalaya.Copilot;

/// <summary>
/// De qué se ha quejado el proveedor, y qué se puede hacer con ello (BUGFIX-CUOTA).
/// <para>
/// <b>El SDK no tipa sus fallos.</b> Todo llega como una excepción cualquiera con un texto dentro
/// —el parte del 2026-08-31 es literalmente
/// <c>System.InvalidOperationException: Session error: You have exceeded your monthly quota
/// (Request ID: FA81:…)</c>—, así que clasificar es leer ese texto. Eso obliga a dos disciplinas:
/// </para>
/// <list type="number">
/// <item>
/// <b>El orden importa, y el más específico va primero.</b> «quota» vivía dentro del detector de
/// «sin asiento», así que una organización sin peticiones premium recibía «tu cuenta no tiene
/// asiento en GitHub»: una causa inventada, con el remedio equivocado detrás (hablar con IT en vez
/// de esperar al reset). Cuota se mira ANTES que asiento, y por eso.
/// </item>
/// <item>
/// <b>Ante la duda, el dato crudo.</b> Lo que no case con una firma reconocible sale como
/// <see cref="AgentProblem.Unknown"/> enseñando el texto del proveedor tal cual. Un mensaje bonito
/// con la causa equivocada es peor que un error feo con la causa verdadera (N-2).
/// </item>
/// </list>
/// </summary>
public static class CopilotFailure
{
    /// <summary>
    /// El identificador opaco que GitHub cuelga al final de sus errores. Se quita ANTES de buscar
    /// códigos HTTP: es una ristra hexadecimal separada por dos puntos, y un <c>403</c> o un
    /// <c>401</c> pueden aparecer dentro de ella por pura casualidad. Clasificar una cuota agotada
    /// como «credenciales rechazadas» porque su Request ID llevaba un 401 dentro sería exactamente
    /// el fallo que este módulo viene a arreglar, cometido otra vez.
    /// </summary>
    private static readonly Regex RequestId = new(
        @"\(\s*request\s*id\s*:[^)]*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Un código HTTP suelto, con frontera: <c>403</c> sí, el <c>403</c> de <c>F403A</c> no.</summary>
    private static readonly Regex HttpCode = new(
        @"(?<![0-9a-z])(401|403|429|500|502|503|504)(?![0-9a-z])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// La clasificación. <paramref name="hasToken"/> separa «no hay credencial» de «la credencial
    /// no vale», que es la distinción que F2.3 ya hacía y que se conserva entera.
    /// </summary>
    public static AgentProblem Classify(Exception? ex, bool hasToken)
    {
        if (ex is null)
        {
            return AgentProblem.Unknown;
        }

        string text = Searchable(ex);

        // El modelo primero: su remedio es de un clic y no se parece a ninguno de los otros (F5.15).
        if (IsModelUnavailable(text))
        {
            return AgentProblem.ModelUnavailable;
        }

        // Cuota ANTES que asiento. Éste es el arreglo: las dos hablan de «no puedes usar Copilot»,
        // pero una se resuelve esperando y la otra hablando con quien administra la organización.
        if (IsQuota(text))
        {
            return AgentProblem.QuotaExhausted;
        }

        if (SaysNoSeat(text))
        {
            return AgentProblem.NoSeat;
        }

        if (SaysAuth(text))
        {
            return hasToken ? AgentProblem.TokenRejected : AgentProblem.NotAuthenticated;
        }

        if (IsOffline(ex, text))
        {
            return AgentProblem.Offline;
        }

        // Un 403 pelado, sin ninguna palabra que diga de qué va. Con credencial válida detrás, la
        // lectura más probable es que a esa cuenta no le dejan usar Copilot — pero es una
        // conjetura, así que el mensaje llevará SIEMPRE el error crudo al lado para que quien lo
        // lea pueda contradecirlo. Sin credencial no se conjetura nada: falta lo primero.
        if (HasCode(text, "403"))
        {
            return hasToken ? AgentProblem.NoSeat : AgentProblem.NotAuthenticated;
        }

        if (!hasToken)
        {
            return AgentProblem.NotAuthenticated;
        }

        return AgentProblem.Unknown;
    }

    /// <summary>La clasificación con su mensaje y el crudo del proveedor ya al lado.</summary>
    public static AgentReadiness Diagnose(Exception? ex, bool hasToken, string? modelId = null)
    {
        AgentProblem problem = Classify(ex, hasToken);
        string raw = Raw(ex);
        return new AgentReadiness(false, Message(problem, ex, modelId, raw), problem, raw);
    }

    /// <summary>El texto que se le enseña a una persona para cada diagnóstico.</summary>
    public static string Message(AgentProblem problem, Exception? ex, string? modelId, string raw) => problem switch
    {
        AgentProblem.QuotaExhausted => CopilotHelp.QuotaExhausted(PeriodOf(Searchable(ex))),
        AgentProblem.NoSeat => HasCode(Searchable(ex), "403") && !SaysNoSeat(Searchable(ex))
            // Conjetura, y se dice que lo es: el proveedor solo devolvió un 403.
            ? CopilotHelp.NoSeat + " (El proveedor solo ha devuelto un 403 sin más detalle: "
              + "si tu asiento está bien, mira el error de abajo antes de reclamar nada.)"
            : CopilotHelp.NoSeat,
        AgentProblem.TokenRejected => CopilotHelp.TokenRejected,
        AgentProblem.NotAuthenticated => CopilotHelp.NoAccount,
        AgentProblem.Offline => CopilotHelp.Offline,
        AgentProblem.ModelUnavailable => CopilotHelp.ModelUnavailable(modelId),
        _ => CopilotHelp.Unknown(raw),
    };

    /// <summary>
    /// El error del proveedor tal cual, con el tipo delante y la cadena de causas detrás. Es lo que
    /// se copia y se le pega a quien administra la organización: el Request ID de GitHub va aquí
    /// dentro, y es lo único con lo que ellos pueden buscar la petición concreta.
    /// </summary>
    public static string Raw(Exception? ex)
    {
        if (ex is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (sb.Length > 0)
            {
                sb.Append(" ← ");
            }

            sb.Append(e.GetType().Name).Append(": ").Append(e.Message?.Trim());
        }

        return sb.ToString();
    }

    /// <summary>La cuota agotada, que es el caso que nació mal clasificado.</summary>
    public static bool IsQuota(Exception? ex) => IsQuota(Searchable(ex));

    /// <summary>
    /// <c>true</c> cuando reintentar es tirar llamadas contra un grifo cerrado. Se declara aquí y
    /// no en quien llama para que ningún camino futuro pueda decidir lo contrario por su cuenta.
    /// </summary>
    public static bool IsRetryable(AgentProblem problem) => problem == AgentProblem.Offline;

    /// <summary>
    /// Cuota agotada. Las firmas salen del error REAL del 2026-08-31 («You have exceeded your
    /// monthly quota») y de cómo GitHub nombra lo mismo en su facturación: peticiones premium,
    /// límite de uso, límite de gasto. <c>429</c> entra porque es el código con el que el
    /// proveedor corta por consumo.
    /// </summary>
    private static bool IsQuota(string text)
        => text.Contains("quota")
        || text.Contains("premium request")
        || text.Contains("premium interaction")
        || text.Contains("usage limit")
        || text.Contains("spending limit")
        || text.Contains("budget")
        || text.Contains("exceeded your monthly")
        || text.Contains("rate limit")
        || text.Contains("too many requests")
        || HasCode(text, "429");

    /// <summary>
    /// Sin asiento, y solo cuando el proveedor lo DICE. Se han quitado de aquí «quota» —que era el
    /// fallo— y «403»/«forbidden» a secas, que no dicen de qué van y ahora se tratan como conjetura
    /// declarada al final de la cadena.
    /// </summary>
    private static bool SaysNoSeat(string text)
        => text.Contains("seat")
        || text.Contains("not entitled")
        || text.Contains("entitlement")
        || text.Contains("subscription")
        || text.Contains("copilot is not enabled")
        || text.Contains("copilot_not_enabled")
        || text.Contains("no access to copilot")
        || text.Contains("access to copilot");

    private static bool SaysAuth(string text)
        => text.Contains("not authenticated")
        || text.Contains("authentication")
        || text.Contains("unauthorized")
        || text.Contains("bad credentials")
        || text.Contains("custom provider")
        || HasCode(text, "401");

    private static bool IsOffline(Exception ex, string text)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException or TimeoutException or System.Net.Sockets.SocketException)
            {
                return true;
            }
        }

        return text.Contains("no such host")
            || text.Contains("network")
            || text.Contains("connection refused")
            || text.Contains("connection reset")
            || text.Contains("name or service not known")
            || text.Contains("timed out")
            || text.Contains("service unavailable")
            || text.Contains("bad gateway")
            || HasCode(text, "502")
            || HasCode(text, "503")
            || HasCode(text, "504");
    }

    /// <summary>
    /// El modelo rechazado (F5.15). Se exigen las DOS piezas —que mencione un modelo y que niegue
    /// su disponibilidad— para no confundirlo con cualquier mensaje que nombre uno de pasada.
    /// </summary>
    private static bool IsModelUnavailable(string text)
    {
        if (!text.Contains("model"))
        {
            return false;
        }

        return text.Contains("not available")
            || text.Contains("unknown model")
            || text.Contains("unsupported model")
            || text.Contains("model_not_found");
    }

    /// <summary>
    /// Cuándo se renueva, SOLO si el proveedor lo dice. El error real dice «monthly», así que se
    /// puede decir «mensual» sin inventar; lo que no dice —la fecha exacta del reset— no se dice.
    /// </summary>
    private static string? PeriodOf(string text)
        => text.Contains("monthly") ? "mensual"
            : text.Contains("daily") ? "diaria"
            : null;

    private static bool HasCode(string text, string code)
    {
        foreach (Match m in HttpCode.Matches(text))
        {
            if (m.Groups[1].Value == code)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// El texto sobre el que se clasifica: toda la cadena de mensajes, en minúsculas y SIN el
    /// Request ID. Ver <see cref="RequestId"/> para por qué se quita.
    /// </summary>
    private static string Searchable(Exception? ex)
    {
        var sb = new StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            sb.Append(e.Message).Append(' ');
        }

        return RequestId.Replace(sb.ToString(), " ").ToLowerInvariant();
    }
}
