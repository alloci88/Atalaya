namespace Atalaya.Agents;

/// <summary>
/// El proveedor ha rechazado la operación, y ya se sabe POR QUÉ (BUGFIX-CUOTA).
/// <para>
/// Existe porque «no se ha podido usar el proveedor» tenía un solo tipo de excepción —la de
/// autenticación— y por debajo se colaban cuota, asiento, red y lo desconocido. Quien la captura
/// necesita las tres cosas que trae: el <see cref="Problem"/> para decidir qué ofrecer, el
/// <see cref="Exception.Message"/> para enseñarlo, y el <see cref="Detail"/> crudo para que se
/// pueda copiar.
/// </para>
/// <para>
/// F14: se llamaba <c>CopilotProviderException</c>. El nombre mentía en cuanto hubo un segundo
/// proveedor — quien la captura no está capturando «un fallo de Copilot», sino «un fallo del
/// proveedor que esté auditando». Los <c>catch</c> de la aplicación miran ESTE tipo, no el de
/// ninguna casa concreta: es lo que hace que el circuito de errores funcione igual con los dos.
/// </para>
/// </summary>
public class AuditorProviderException : Exception
{
    public AuditorProviderException(
        string message, AgentProblem problem, string? detail = null, Exception? inner = null)
        : base(message, inner)
    {
        Problem = problem;
        Detail = detail;
    }

    public AgentProblem Problem { get; }

    /// <summary>El error del proveedor tal cual (tipo + texto + Request ID). Copiable.</summary>
    public string? Detail { get; }

    /// <summary>Reintentar solo tiene sentido si el problema es transitorio. La cuota NO lo es.</summary>
    public bool IsRetryable => Retryable(Problem);

    /// <summary>
    /// La regla, en un solo sitio: solo la red es transitoria. Ni la cuota, ni el asiento, ni la
    /// credencial, ni un CLI que no está mejoran por volver a intentarlo — y reintentar contra una
    /// cuota agotada gasta las peticiones del reset siguiente (BUGFIX-CUOTA).
    /// </summary>
    public static bool Retryable(AgentProblem problem) => problem == AgentProblem.Offline;
}

/// <summary>
/// La operación falló por la CREDENCIAL (§6.1): no hay sesión iniciada, o la que hay no vale.
/// Lleva el texto de ayuda para que la interfaz pueda decir qué hacer en vez de enseñar un error
/// crudo del proveedor.
/// <para>
/// Sigue siendo UNA de las hijas de <see cref="AuditorProviderException"/> y no el cajón de todo.
/// </para>
/// </summary>
public sealed class AuditorAuthenticationException : AuditorProviderException
{
    public AuditorAuthenticationException(string message, Exception? inner = null)
        : base(message, AgentProblem.NotAuthenticated, null, inner) { }

    public AuditorAuthenticationException(
        string message, AgentProblem problem, string? detail, Exception? inner = null)
        : base(message, problem, detail, inner) { }
}

/// <summary>
/// La sesión no se pudo CREAR porque el modelo pedido no está disponible para esta cuenta (F5.15).
/// <para>
/// Nace del parte del 2026-08-26: <c>session.create</c> falló con «Model gpt-5 is not available» y
/// la aplicación se quedó muda —ni error, ni botón de parar, ni forma de volver a la vista—. Tiene
/// tipo propio porque tiene REMEDIO propio: elegir otro modelo en Ajustes. Un error genérico no
/// puede ofrecer ese enlace, y sin el enlace el usuario no sabe que la cura está a dos clics.
/// </para>
/// </summary>
public sealed class AuditorModelUnavailableException : AuditorProviderException
{
    public AuditorModelUnavailableException(
        string? modelId, string message, string? detail = null, Exception? inner = null)
        : base(message, AgentProblem.ModelUnavailable, detail, inner)
        => ModelId = modelId;

    /// <summary>
    /// El caso normal: la frase se deriva del modelo, que es la única variable que tiene. Existe
    /// para que ningún proveedor tenga que reescribir un texto que ya es común
    /// (<see cref="AuditorHelp.ModelUnavailable"/>).
    /// </summary>
    public AuditorModelUnavailableException(string? modelId, Exception? inner = null)
        : this(modelId, AuditorHelp.ModelUnavailable(modelId), null, inner) { }

    /// <summary>El id que se pidió, para poder nombrarlo. Null si no se había configurado ninguno.</summary>
    public string? ModelId { get; }
}
