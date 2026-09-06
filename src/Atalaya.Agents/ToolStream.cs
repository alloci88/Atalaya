namespace Atalaya.Agents;

/// <summary>En qué punto está una llamada a herramienta que el modelo está escribiendo (F30 §2).</summary>
public enum ToolStreamPhase
{
    /// <summary>El modelo ha empezado a escribir la llamada. Todavía no hay argumentos.</summary>
    Started,

    /// <summary>Han llegado más argumentos: ya se puede decir cuántos elementos van.</summary>
    Input,
}

/// <summary>
/// Una llamada a herramienta <b>mientras el modelo la escribe</b> (F30 §2).
/// <para>
/// <b>Por qué existe.</b> D-1014 midió dónde está el minuto de silencio de una auditoría: no en la
/// red ni en Atalaya —16 ms—, sino en el modelo <b>escribiendo los argumentos</b>. Un hallazgo
/// cuesta ~246 tokens de escritura y el caudal medido es de 64 tokens/s, así que reportar once son
/// unos 42 s en los que la pantalla no tenía nada que enseñar. Y no era por falta de datos: las dos
/// casas emiten esos argumentos según se escriben —<c>input_json_delta</c> en Claude Code,
/// <c>AssistantToolCallDeltaEvent.InputDelta</c> en Copilot— y Atalaya los descartaba.
/// </para>
/// <para>
/// <b>El total NO viaja, y no es un olvido.</b> Lo que llega es un array JSON que se va escribiendo;
/// cuántos elementos va a tener no se sabe hasta que cierra. Se dice lo que hay —«3 hallazgos»— y
/// no «3 de 11», porque el 11 habría que inventarlo y esta fase tiene prohibido inventar progreso.
/// </para>
/// </summary>
/// <param name="Tool">El nombre corto de la herramienta, ya sin el prefijo del servidor MCP.</param>
/// <param name="Items">Cuántos elementos completos van escritos. Cero en <see cref="ToolStreamPhase.Started"/>.</param>
/// <param name="Last">El último elemento que se completó, para poder enseñarlo. Null si no lo hay.</param>
public sealed record ToolStream(ToolStreamPhase Phase, string Tool, int Items = 0, string? Last = null);

/// <summary>
/// Un proveedor que sabe contar lo que el modelo está escribiendo (F30 §2).
/// <para>
/// <b>Interfaz aparte y no un miembro de <see cref="IAuditorProvider"/></b>, por lo mismo que
/// <c>IThreadedAuditor</c> y <c>IAssistedFixProvider</c>: un evento en la interfaz común obligaría a
/// declararlo a la treintena de dobles minúsculos que los tests usan para ejercitar un camino, y
/// serían treinta copias vacías esperando a quedarse desfasadas. Quien sabe narrar, lo dice; quien
/// no, no se entera de que existe.
/// </para>
/// </summary>
public interface INarratingAuditor
{
    /// <summary>Una llamada a herramienta, según el modelo la escribe.</summary>
    event Action<ToolStream>? ToolStreamed;
}
