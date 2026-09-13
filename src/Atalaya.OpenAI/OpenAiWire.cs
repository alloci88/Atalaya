namespace Atalaya.OpenAI;

/// <summary>
/// <b>El turno ya montado, que es donde se tocan el transporte y el bucle de herramientas</b>
/// (PROV-3, paso 0). Lo define el andamio a propósito: quien lee el SSE y quien ejecuta las
/// herramientas se escribieron en paralelo, y sin un tipo acordado antes cada uno habría inventado
/// el suyo.
/// <para>
/// Es lo que queda <b>después</b> de ensamblar los deltas: el texto entero de la respuesta, las
/// llamadas de herramienta con sus argumentos completos, el consumo si el endpoint lo mandó y por
/// qué se paró. El bucle no ve un solo `data:`.
/// </para>
/// </summary>
/// <param name="Text">Lo que el modelo dijo en prosa. Vacío si solo llamó herramientas.</param>
/// <param name="ToolCalls">Las llamadas de este turno, en el orden en que llegaron.</param>
/// <param name="Usage">
/// El consumo, o null si el endpoint no lo manda —que pasa: no todos los que hablan este dialecto
/// rellenan `usage` al hacer streaming—. Null es «no lo dijo», nunca cero.
/// </param>
/// <param name="FinishReason">`stop`, `tool_calls`, `length`… tal cual lo mandó el endpoint.</param>
public sealed record ChatTurn(
    string Text,
    IReadOnlyList<ChatToolCall> ToolCalls,
    ChatUsage? Usage,
    string? FinishReason);

/// <summary>Una llamada de herramienta, con sus argumentos ya completos en JSON.</summary>
/// <param name="Id">El identificador que el endpoint le puso; vuelve en el mensaje `tool`.</param>
/// <param name="Name">El nombre del catálogo (`AuditToolText`).</param>
/// <param name="ArgumentsJson">Los argumentos, como el modelo los escribió.</param>
public sealed record ChatToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// El consumo tal y como lo declara este dialecto.
/// <para>
/// <b>No hay caché escrita.</b> `chat/completions` no tiene ese concepto —la caché de prompt la
/// gestiona el endpoint y no la cobra aparte—, así que ese campo no se inventa: se declara que no
/// existe y el cálculo lo deja en cero, que aquí sí es la verdad y no un hueco.
/// </para>
/// </summary>
/// <param name="PromptTokens">`usage.prompt_tokens`. INCLUYE los cacheados, como el resto del dialecto.</param>
/// <param name="CompletionTokens">`usage.completion_tokens`.</param>
/// <param name="CachedPromptTokens">
/// `usage.prompt_tokens_details.cached_tokens` cuando el endpoint lo da; 0 cuando no, que es lo
/// normal fuera de OpenAI.
/// </param>
public sealed record ChatUsage(long PromptTokens, long CompletionTokens, long CachedPromptTokens = 0);

/// <summary>Un mensaje de la conversación, tal y como viaja en `messages`.</summary>
/// <param name="Role">`system`, `user`, `assistant` o `tool`.</param>
/// <param name="Content">El texto, o null en un `assistant` que solo llamó herramientas.</param>
/// <param name="ToolCalls">Las llamadas, cuando el mensaje es el `assistant` que las pidió.</param>
/// <param name="ToolCallId">A qué llamada contesta, cuando el mensaje es un `tool`.</param>
public sealed record ChatMessage(
    string Role,
    string? Content = null,
    IReadOnlyList<ChatToolCall>? ToolCalls = null,
    string? ToolCallId = null)
{
    public static ChatMessage User(string text) => new("user", text);

    public static ChatMessage Tool(string toolCallId, string result) => new("tool", result, ToolCallId: toolCallId);
}

/// <summary>
/// Una herramienta como la ve el endpoint: nombre, descripción y esquema de argumentos.
/// <para>
/// El nombre y la descripción salen SIEMPRE de `AuditToolText`, que es la fuente única de PROV-2:
/// aquí no se escribe ni una palabra de lo que el modelo lee, solo se traduce de forma.
/// </para>
/// </summary>
public sealed record ChatToolSpec(string Name, string Description, string ParametersJson);
