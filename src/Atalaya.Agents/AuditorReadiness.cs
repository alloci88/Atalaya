namespace Atalaya.Agents;

/// <summary>
/// Why the provider is not ready. Lets the UI show a *specific* remedy instead of one generic
/// "not authenticated" for very different situations (F2.3 diagnostics).
/// <para>
/// Nació como la taxonomía de Copilot y se queda tal cual al hacerse común (F14): cada valor
/// nombra una CAUSA con su propio remedio, no un detalle de proveedor. Un CLI sin sesión iniciada
/// y un asiento sin licencia son <see cref="NotAuthenticated"/> y <see cref="NoSeat"/> con
/// independencia de quién los diga; lo que cambia es la frase que los acompaña, y eso vive en el
/// texto de ayuda de cada proveedor.
/// </para>
/// </summary>
public enum AgentProblem
{
    None = 0,

    /// <summary>
    /// No hay credencial utilizable. En Copilot: ni token de cuenta ni login del CLI. En Claude
    /// Code: el CLI está pero nadie ha iniciado sesión en él.
    /// </summary>
    NotAuthenticated,

    /// <summary>The token is valid but the account has no seat assigned.</summary>
    NoSeat,

    /// <summary>The credential was rejected (revoked / expired) — reconnect the account.</summary>
    TokenRejected,

    /// <summary>Could not reach the provider.</summary>
    Offline,

    /// <summary>
    /// El modelo configurado no existe o la cuenta no puede usarlo (F5.15). No es un problema de
    /// credenciales ni de asiento: la sesión no arranca porque se le pidió al runtime un modelo que
    /// ya no sirve. Tiene remedio de un clic —elegir otro en Ajustes— y por eso se distingue.
    /// </summary>
    ModelUnavailable,

    /// <summary>
    /// El proveedor se ha quedado sin peticiones (BUGFIX-CUOTA). No es un problema de la cuenta ni
    /// de la credencial: el asiento está, la licencia está, y lo que falta son peticiones. Tiene
    /// tipo propio porque tiene REMEDIO propio —esperar al reset, o bajar a un modelo con
    /// multiplicador menor—, y porque el remedio equivocado (reclamar un asiento que ya se tiene)
    /// hace perder el tiempo a dos personas.
    /// </summary>
    QuotaExhausted,

    /// <summary>
    /// El programa que hace de proveedor no está en esta máquina (F14). Es distinto de
    /// <see cref="NotAuthenticated"/> y la diferencia importa: ahí el remedio es iniciar sesión,
    /// aquí es instalar. Decir «no estás autenticado» a quien no tiene el CLI lo manda a buscar un
    /// login que no existe todavía.
    /// </summary>
    CliMissing,

    Unknown,
}

/// <summary>Whether the provider can run, with a human-readable reason (§6.1 help screen).</summary>
/// <param name="Detail">
/// El error del proveedor tal cual —tipo, texto y Request ID—, para poder copiarlo y pegarlo
/// (BUGFIX-CUOTA). Va SEPARADO del mensaje: la frase es para decidir qué hacer, el crudo es para
/// que quien administre la organización pueda buscar la petición concreta. Vacío cuando no hay
/// excepción detrás, como en las comprobaciones que fallan por respuesta y no por error.
/// </param>
public sealed record AgentReadiness(
    bool Ready, string Message, AgentProblem Problem = AgentProblem.None, string? Detail = null);

/// <summary>
/// Un modelo disponible, tal y como lo ofrece el proveedor (F5.1). Se le pregunta siempre a él:
/// una lista escrita a mano caduca en cuanto el proveedor añade o retira un modelo, y el usuario
/// acabaría eligiendo uno que su cuenta no sirve.
/// </summary>
/// <param name="Id">Identificador que se le pasa al proveedor (p. ej. <c>gpt-5</c>, <c>opus</c>).</param>
/// <param name="Name">Nombre para mostrar; si el proveedor no lo trae, cae al <paramref name="Id"/>.</param>
/// <param name="Multiplier">
/// Multiplicador de facturación relativo a la tarifa base, cuando el proveedor lo publica.
/// Null = no lo da; no se inventa.
/// </param>
public sealed record AgentModel(string Id, string Name, double? Multiplier = null);
