namespace Atalaya.OpenAI;

/// <summary>
/// <b>Cómo se le habla al endpoint</b> — el segundo punto donde se tocan el transporte y lo que va
/// encima (PROV-3, paso 0).
/// <para>
/// Existe por lo mismo que <see cref="ChatTurn"/>: el transporte y el bucle de herramientas se
/// escriben a la vez, y sin un verbo acordado antes cada uno habría inventado el suyo. Debajo hay
/// `HttpClient`, SSE y la taxonomía de errores; encima, un bucle que manda mensajes y recibe
/// turnos y que <b>no sabe una palabra de HTTP</b> — que es lo que permite probarlo sin red.
/// </para>
/// <para>
/// <b>Lanza excepciones ya clasificadas</b> (<c>Auditor*Exception</c> del vocabulario común): quien
/// conoce el dialecto de los fallos de esta casa es el transporte, y traducirlos es suyo. Quien
/// llama no mira códigos HTTP.
/// </para>
/// </summary>
public interface IChatEndpoint
{
    /// <summary>
    /// Un turno: se mandan los mensajes de la conversación y las herramientas que se ofrecen, y se
    /// devuelve lo que el modelo contestó, ya ensamblado de los deltas.
    /// </summary>
    /// <param name="messages">La conversación entera, como la exige este dialecto.</param>
    /// <param name="tools">
    /// Las herramientas ofrecidas, con su nombre y su descripción salidos de <c>AuditToolText</c>.
    /// Vacío para un turno sin herramientas.
    /// </param>
    /// <param name="onText">
    /// Se llama con cada trozo de texto según llega, para el hilo de actividad (F30). El bucle no
    /// espera al final para enseñar algo.
    /// </param>
    /// <param name="ct">El token de la sesión. Cancelar aborta la petición en curso.</param>
    Task<ChatTurn> SendAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatToolSpec> tools,
        Action<string>? onText,
        CancellationToken ct);

    /// <summary>
    /// <b>La llamada mínima de «Probar»</b> (PROV-3 §2): la más barata que demuestre que el
    /// endpoint contesta y que el modelo existe. No forma parte de auditar y por eso está aquí y no
    /// en el bucle: lo que Ajustes necesita saber es si la configuración vale, no si el modelo
    /// razona bien.
    /// </summary>
    /// <returns>Qué pasó, en una frase para una persona, y si salió bien.</returns>
    Task<ChatProbe> ProbeAsync(CancellationToken ct);
}

/// <summary>El resultado de «Probar», tal y como se enseña en la línea de debajo del botón.</summary>
/// <param name="Ok">Conecta y el modelo existe.</param>
/// <param name="Message">
/// Qué pasó, escrito para quien está mirando Ajustes: qué revisar y dónde, nunca un código a secas.
/// </param>
/// <param name="Detail">
/// Lo que contestó el endpoint, crudo y recortado, para el caso en que el mensaje no baste. Null si
/// no hay nada que enseñar. <b>Nunca lleva la clave</b>.
/// </param>
public sealed record ChatProbe(bool Ok, string Message, string? Detail = null);
