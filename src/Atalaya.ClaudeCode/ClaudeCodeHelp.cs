using Atalaya.Agents;

namespace Atalaya.ClaudeCode;

/// <summary>
/// Lo que se le dice al usuario cuando Claude Code no puede auditar (F14).
/// <para>
/// Cada frase dice las tres cosas que la pantalla Cuenta necesita: QUÉ pasa, que NO es algo que
/// Atalaya pueda arreglar por dentro, y el gesto exacto que lo arregla. Atalaya no gestiona ni
/// guarda credenciales de Anthropic —usa el login que el CLI ya tiene, igual que con Copilot usa
/// el de GitHub—, así que el remedio SIEMPRE está en la terminal del usuario y nunca en Ajustes.
/// </para>
/// </summary>
public static class ClaudeCodeHelp
{
    /// <summary>El CLI no está instalado. Instalar es el gesto, no iniciar sesión.</summary>
    public const string CliMissing =
        "Claude Code no está instalado en esta máquina. Instálalo (npm install -g "
        + "@anthropic-ai/claude-code) e inicia sesión en tu terminal con `claude` — Atalaya usa tu "
        + "suscripción, no guarda ninguna credencial de Anthropic.";

    /// <summary>El CLI está pero nadie ha iniciado sesión.</summary>
    public const string NotLoggedIn =
        "Claude Code está instalado pero no has iniciado sesión. Abre una terminal, ejecuta "
        + "`claude` y completa el login con tu cuenta; Atalaya usará esa sesión, igual que con "
        + "Copilot usa tu login de GitHub.";

    /// <summary>Autenticado y utilizable. Nombra la cuenta cuando el CLI la da.</summary>
    public static string Ready(string? account)
        => string.IsNullOrWhiteSpace(account)
            ? "Claude Code disponible."
            : $"Claude Code disponible ({account}).";

    /// <summary>
    /// Sin red. Es transitorio, y decirlo evita que alguien se ponga a reinstalar el CLI por un
    /// proxy caído.
    /// </summary>
    public const string Offline =
        "No hay conexión con Anthropic (o su servicio no responde). Comprueba la red o el proxy y "
        + "reintenta: este fallo es transitorio.";

    /// <summary>
    /// Se acabó la cuota de la suscripción. No es la credencial ni el plan: son peticiones, y
    /// tiene su propio remedio —esperar al reset— igual que la de Copilot (BUGFIX-CUOTA).
    /// </summary>
    public const string QuotaExhausted =
        "Tu suscripción de Claude ha agotado su cuota de uso por ahora. No es un problema de "
        + "credenciales ni hay nada que tocar en Atalaya: hay que esperar al reset, o auditar "
        + "mientras tanto con Copilot desde Ajustes. Atalaya no reintenta sola.";

    /// <summary>
    /// El servidor MCP de Atalaya no llegó a conectar (F14). Merece frase propia porque es el
    /// fallo que MÁS silenciosamente estropea una auditoría: verificado contra el CLI real, una
    /// sesión con el servidor caído sigue adelante, el modelo se queda sin herramientas y la
    /// sesión TERMINA «con éxito» sin un solo hallazgo. Ver D-782.
    /// </summary>
    public const string McpUnavailable =
        "Atalaya no ha podido ofrecerle sus herramientas a Claude Code, así que el auditor se "
        + "habría quedado sin forma de reportar nada. La sesión se ha parado antes de gastar: no "
        + "es un fallo de tu suscripción. Si se repite, reinicia Atalaya.";

    /// <summary>
    /// Lo que no se ha sabido clasificar: el crudo delante, sin proponer causa. Es la lección de
    /// BUGFIX-CUOTA (N-2) — un mensaje bonito con la causa equivocada manda a alguien a arreglar
    /// lo que no está roto.
    /// </summary>
    public static string Unknown(string raw)
        => "Claude Code ha rechazado la operación y Atalaya no reconoce el motivo, así que no se lo "
        + "inventa. Éste es el error tal cual lo ha devuelto el proveedor"
        + (string.IsNullOrWhiteSpace(raw) ? "." : $": {raw}");

    /// <summary>La frase que corresponde a cada causa, para no repartir el <c>switch</c>.</summary>
    public static string For(AgentProblem problem, string? raw = null, string? modelId = null)
        => problem switch
        {
            AgentProblem.CliMissing => CliMissing,
            AgentProblem.NotAuthenticated => NotLoggedIn,
            AgentProblem.TokenRejected => NotLoggedIn,
            AgentProblem.Offline => Offline,
            AgentProblem.QuotaExhausted => QuotaExhausted,
            AgentProblem.ModelUnavailable => AuditorHelp.ModelUnavailable(modelId),
            _ => Unknown(raw ?? string.Empty),
        };
}
