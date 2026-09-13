using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Atalaya.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atalaya.OpenAI;

/// <summary>
/// <b>El transporte</b> (PROV-3 §1, §5, §7 y §8): lo único de esta casa que sabe que debajo hay
/// HTTP.
/// <para>
/// Habla <c>POST {baseUrl}/chat/completions</c> con <c>stream: true</c>, lee el SSE, ensambla el
/// turno y traduce los fallos al vocabulario común. Encima queda un bucle de herramientas que no
/// ve un solo <c>data:</c> ni un solo código HTTP — que es lo que permite probarlo entero sin red.
/// </para>
/// <para>
/// <b>Sin SDK de nadie</b>, y está razonado en el <c>.csproj</c>: el dialecto lo hablan siete casas
/// y ninguna igual del todo, así que el ajuste por endpoint tiene que costar una línea y no una
/// versión de un paquete ajeno.
/// </para>
/// <para>
/// <b>El <c>HttpMessageHandler</c> entra por el constructor</b> y lo arma la composición
/// (<see cref="OpenAiHttp.CreateHandler"/>), que es la única que conoce los ajustes de la máquina.
/// Es la forma correcta por dos motivos a la vez: la política de proxy y TLS vive en un solo sitio,
/// y todos los tests corren contra un endpoint dentro del proceso.
/// </para>
/// </summary>
public sealed class OpenAiClient : IChatEndpoint, IDisposable
{
    private readonly Func<OpenAiEndpoint> _endpoint;
    private readonly Func<string?> _key;
    private readonly HttpClient _http;
    private readonly ILogger _log;

    /// <param name="endpoint">
    /// A qué se habla. Es una función y se lee en CADA llamada, por lo de BUGFIX-AJUSTES: cambiar
    /// la URL o el modelo en Ajustes tiene efecto sin reiniciar nada.
    /// </param>
    /// <param name="key">
    /// La clave, del almacén de secretos. Función y no valor: así el cliente nunca la retiene, y
    /// lo que no se retiene no acaba en un log ni en un volcado de memoria.
    /// </param>
    /// <param name="handler">
    /// Con qué red se sale. Lo monta la composición con la política de §8; los tests le pasan el
    /// endpoint falso.
    /// </param>
    public OpenAiClient(
        Func<OpenAiEndpoint> endpoint,
        Func<string?> key,
        HttpMessageHandler handler,
        ILogger? logger = null)
    {
        _endpoint = endpoint;
        _key = key;
        _log = logger ?? NullLogger.Instance;

        // `InfiniteTimeSpan` a propósito: el tope lo pone este cliente con un token propio, que es
        // lo único que permite distinguir «el endpoint no contesta» de «el usuario ha parado la
        // sesión» — con el Timeout del HttpClient las dos cosas llegan como la misma excepción.
        _http = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>La espera del único reintento. Interna y ajustable para que los tests no duerman.</summary>
    internal TimeSpan RetryWait { get; set; } = OpenAiHttp.RetryWait;

    /// <inheritdoc/>
    public async Task<ChatTurn> SendAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatToolSpec> tools,
        Action<string>? onText,
        CancellationToken ct)
    {
        OpenAiEndpoint endpoint = Ready();
        string body = Body(endpoint, messages, tools, stream: true, maxTokens: null);

        return await CallAsync(
            () => Request(HttpMethod.Post, endpoint.ChatCompletions(), endpoint, body, stream: true),
            async (response, deadline) =>
            {
                // El tope de D-208 acota la IDA. A partir de aquí el endpoint ya está contestando y
                // lo que queda es cuerpo: una unidad se escribe en minutos, y cortarla por larga
                // sería tirar trabajo bueno. Manda el token de la sesión, que es quien sabe cuándo
                // el usuario ha dicho basta.
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);

                using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                OpenAiSse.Read read = await OpenAiSse.ReadAsync(stream, onText, ct).ConfigureAwait(false);

                return read.Turn ?? throw OpenAiFailure.NotStreamingException(read.Unexpected);
            },
            endpoint.Model,
            endpoint.BaseUrl,
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<ChatProbe> ProbeAsync(CancellationToken ct)
    {
        OpenAiEndpoint endpoint;
        try
        {
            endpoint = Ready();
        }
        catch (AuditorProviderException ex)
        {
            return new ChatProbe(false, ex.Message, ex.Detail);
        }

        try
        {
            // 1 — La lista de modelos, que no gasta un token. Sirve para decir que SÍ y nunca para
            //     decir que no: hay endpoints que no la sirven, y los despliegues de Azure no se
            //     llaman en ella como se llaman al pedirlos.
            if (await ModelListedAsync(endpoint, ct).ConfigureAwait(false))
            {
                return new ChatProbe(
                    true,
                    $"Conecta, y el modelo «{endpoint.Model}» está en la lista del endpoint.");
            }

            // 2 — El turno más barato que existe: un mensaje y un token de respuesta. Es la única
            //     forma de demostrar que el modelo EXISTE cuando no hay lista de la que fiarse.
            await CallAsync(
                () => Request(
                    HttpMethod.Post,
                    endpoint.ChatCompletions(),
                    endpoint,
                    Body(endpoint, new[] { ChatMessage.User("ping") }, Array.Empty<ChatToolSpec>(), stream: false, maxTokens: 1),
                    stream: false),
                (_, _) => Task.FromResult(true),
                endpoint.Model,
                endpoint.BaseUrl,
                ct).ConfigureAwait(false);

            return new ChatProbe(true, $"Conecta y el modelo «{endpoint.Model}» responde.");
        }
        catch (AuditorProviderException ex)
        {
            return new ChatProbe(false, ex.Message, ex.Detail);
        }
    }

    public void Dispose() => _http.Dispose();

    // ================================================================ lo que hay que tener para llamar

    /// <summary>
    /// Lo que se comprueba ANTES de tocar la red, y que por tanto no cuesta ni una petición.
    /// <para>
    /// La regla de <c>http://</c> no se reimplementa: sale de <see cref="OpenAiEndpoint.WhyNot"/>,
    /// que es donde está escrita y razonada. Reescribirla aquí sería tener dos sitios donde decidir
    /// si la clave y el código auditado salen en claro por la red.
    /// </para>
    /// </summary>
    private OpenAiEndpoint Ready()
    {
        OpenAiEndpoint endpoint = _endpoint();

        if (OpenAiEndpoint.WhyNot(endpoint.BaseUrl) is { } mal)
        {
            throw new AuditorAuthenticationException(mal, AgentProblem.NotAuthenticated, null);
        }

        if (string.IsNullOrWhiteSpace(endpoint.Model))
        {
            throw new AuditorModelUnavailableException(null, OpenAiFailure.NoModel);
        }

        // Sin clave solo se llama a un endpoint LOCAL, y no es una laxitud: Ollama y LM Studio no
        // piden ninguna, y son la forma de probar Atalaya sin factura. Exigir una ahí obligaría a
        // inventarse un texto para rellenar el hueco. Fuera de esta máquina la clave es obligatoria
        // — que es lo que evita mandar una petición a ciegas y comerse el 401.
        if (string.IsNullOrWhiteSpace(_key()) && !IsLocal(endpoint.BaseUrl))
        {
            throw new AuditorAuthenticationException(OpenAiFailure.NoKey);
        }

        return endpoint;
    }

    private static bool IsLocal(string baseUrl)
        => Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out Uri? uri)
           && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    // ================================================================= la llamada, con su único reintento

    /// <summary>
    /// Una llamada: el tope de la ida, el reintento acotado y la traducción del fallo, en el único
    /// sitio donde viven.
    /// <para>
    /// <b>Un intento más, y solo uno, y solo en 429 y 5xx</b> (§7). El 4xx no se reintenta jamás:
    /// una clave que no vale no empieza a valer por mandarla otra vez, y contra una API de pago
    /// cada intento es una línea en la factura.
    /// </para>
    /// <para>
    /// <b>Un fallo a mitad del cuerpo tampoco se reintenta</b>, y es una consecuencia de dónde está
    /// escrito esto: cuando se decide reintentar todavía no se ha leído un solo evento, así que no
    /// hay forma de que un reintento duplique texto que el hilo de actividad ya enseñó.
    /// </para>
    /// </summary>
    private async Task<T> CallAsync<T>(
        Func<HttpRequestMessage> make,
        Func<HttpResponseMessage, CancellationTokenSource, Task<T>> onOk,
        string model,
        string baseUrl,
        CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(OpenAiHttp.Deadline);

            HttpResponseMessage? response = null;
            try
            {
                try
                {
                    using HttpRequestMessage request = make();
                    response = await _http
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (NoAnswer(ex, ct))
                {
                    if (attempt == 1)
                    {
                        _log.LogWarning(ex, "El endpoint no contestó; se reintenta una vez.");
                        await WaitAsync(ct).ConfigureAwait(false);
                        continue;
                    }

                    throw OpenAiFailure.NoAnswer(ex, baseUrl);
                }

                if (response.IsSuccessStatusCode)
                {
                    return await onOk(response, deadline).ConfigureAwait(false);
                }

                string raw = await RawAsync(response, ct).ConfigureAwait(false);

                if (attempt == 1 && OpenAiFailure.ShouldRetry(response.StatusCode))
                {
                    _log.LogWarning(
                        "El endpoint devolvió {Code}; se reintenta una vez.", (int)response.StatusCode);
                    await WaitAsync(ct).ConfigureAwait(false);
                    continue;
                }

                throw OpenAiFailure.Exception(response.StatusCode, raw, model);
            }
            finally
            {
                response?.Dispose();
            }
        }
    }

    /// <summary>
    /// <b>Esto es «no ha contestado», no «lo han parado».</b> La diferencia es toda: una
    /// cancelación del usuario sube tal cual —parar una sesión no es un error y no enseña aviso—,
    /// y solo lo que se cancela sin que nadie lo haya pedido es el tope de D-208 venciendo.
    /// </summary>
    private static bool NoAnswer(Exception ex, CancellationToken ct)
        => !ct.IsCancellationRequested
           && ex is HttpRequestException or OperationCanceledException;

    private Task WaitAsync(CancellationToken ct)
        => RetryWait <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(RetryWait, ct);

    private static async Task<string> RawAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // El código ya dice lo que hay que decir; quedarse sin el cuerpo no puede tapar eso.
            return string.Empty;
        }
    }

    // ============================================================================= «Probar», la barata

    /// <summary>
    /// <c>GET /models</c> y si el modelo configurado está en la lista. Cualquier fallo devuelve
    /// «no lo sé» en vez de propagarse: esta llamada existe para ahorrar un turno, no para juzgar.
    /// </summary>
    private async Task<bool> ModelListedAsync(OpenAiEndpoint endpoint, CancellationToken ct)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(OpenAiHttp.Deadline);

            using HttpRequestMessage request =
                Request(HttpMethod.Get, endpoint.Models(), endpoint, body: null, stream: false);
            using HttpResponseMessage response =
                await _http.SendAsync(request, deadline.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Lists(json, endpoint.Model);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool Lists(string json, string model)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement item in data.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("id", out JsonElement id)
                    && id.ValueKind == JsonValueKind.String
                    && string.Equals(id.GetString(), model, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ================================================================================ la petición

    /// <summary>
    /// La petición, con la clave puesta como la pida el endpoint: <c>Authorization: Bearer</c> o
    /// <c>api-key</c>. Es el único motivo de que <see cref="OpenAiAuth"/> sea un enum, y de que
    /// equivocarse ahí devuelva un 401 con una clave perfectamente buena.
    /// </summary>
    private HttpRequestMessage Request(
        HttpMethod method, Uri url, OpenAiEndpoint endpoint, string? body, bool stream)
    {
        var request = new HttpRequestMessage(method, url);

        string? key = _key();
        if (!string.IsNullOrWhiteSpace(key))
        {
            if (endpoint.Auth == OpenAiAuth.ApiKey)
            {
                request.Headers.TryAddWithoutValidation("api-key", key);
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }
        }

        request.Headers.Accept.ParseAdd(stream ? "text/event-stream" : "application/json");

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return request;
    }

    /// <summary>
    /// El cuerpo del dialecto. <c>tools</c> y <c>tool_choice</c> solo cuando hay herramientas: un
    /// <c>tools: []</c> vacío lo rechazan varios endpoints con un 400, y ofrecer una lista vacía no
    /// significa nada.
    /// <para>
    /// <c>stream_options.include_usage</c> va SIEMPRE que se hace streaming, y es lo que hace que
    /// haya consumo que contar: sin ese campo los endpoints que sí lo saben mandar no lo mandan, y
    /// el pie del coste saldría en blanco sin que nadie supiera por qué. Los que no lo conocen lo
    /// ignoran, que es lo normal en este dialecto con los campos de más.
    /// </para>
    /// </summary>
    private static string Body(
        OpenAiEndpoint endpoint,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatToolSpec> tools,
        bool stream,
        int? maxTokens)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("model", endpoint.Model);
            w.WriteBoolean("stream", stream);

            if (stream)
            {
                w.WriteStartObject("stream_options");
                w.WriteBoolean("include_usage", true);
                w.WriteEndObject();
            }

            if (maxTokens is { } max)
            {
                w.WriteNumber("max_tokens", max);
            }

            w.WriteStartArray("messages");
            foreach (ChatMessage message in messages)
            {
                WriteMessage(w, message);
            }

            w.WriteEndArray();

            if (tools.Count > 0)
            {
                w.WriteStartArray("tools");
                foreach (ChatToolSpec tool in tools)
                {
                    WriteTool(w, tool);
                }

                w.WriteEndArray();
                w.WriteString("tool_choice", "auto");
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteMessage(Utf8JsonWriter w, ChatMessage message)
    {
        w.WriteStartObject();
        w.WriteString("role", message.Role);

        bool calling = message.ToolCalls is { Count: > 0 };
        if (message.Content is not null)
        {
            w.WriteString("content", message.Content);
        }
        else if (!calling)
        {
            // Un mensaje sin contenido y sin llamadas no existe en el dialecto: `null` explícito
            // es lo que aceptan todos; omitir la clave lo rechazan algunos.
            w.WriteNull("content");
        }

        if (message.ToolCallId is { } toolCallId)
        {
            w.WriteString("tool_call_id", toolCallId);
        }

        if (message.ToolCalls is { Count: > 0 } calls)
        {
            w.WriteStartArray("tool_calls");
            foreach (ChatToolCall call in calls)
            {
                w.WriteStartObject();
                w.WriteString("id", call.Id);
                w.WriteString("type", "function");
                w.WriteStartObject("function");
                w.WriteString("name", call.Name);
                w.WriteString(
                    "arguments",
                    string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    private static void WriteTool(Utf8JsonWriter w, ChatToolSpec tool)
    {
        w.WriteStartObject();
        w.WriteString("type", "function");
        w.WriteStartObject("function");
        w.WriteString("name", tool.Name);
        w.WriteString("description", tool.Description);
        w.WritePropertyName("parameters");

        if (string.IsNullOrWhiteSpace(tool.ParametersJson))
        {
            // Una herramienta sin argumentos se declara con el objeto vacío, no sin `parameters`.
            w.WriteStartObject();
            w.WriteString("type", "object");
            w.WriteStartObject("properties");
            w.WriteEndObject();
            w.WriteEndObject();
        }
        else
        {
            w.WriteRawValue(tool.ParametersJson);
        }

        w.WriteEndObject();
        w.WriteEndObject();
    }
}
