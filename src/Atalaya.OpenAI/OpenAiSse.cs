using System.Text;
using System.Text.Json;

namespace Atalaya.OpenAI;

/// <summary>
/// <b>Lo que llega por el cable, montado en un turno</b> (PROV-3 §5).
/// <para>
/// El dialecto manda la respuesta en trozos: líneas <c>data: {json}</c> separadas por una línea en
/// blanco y un <c>data: [DONE]</c> al final. Lo que trae cada trozo es un DELTA, no un estado, y
/// ahí es donde se rompe un lector ingenuo:
/// </para>
/// <list type="number">
/// <item>
/// <b>El texto llega carácter a carácter</b>, o casi. Se acumula, y cada pedazo se entrega a
/// <c>onText</c> en cuanto llega — el hilo de actividad (F30) enseña la respuesta mientras se
/// escribe y no cinco minutos después.
/// </item>
/// <item>
/// <b>Los argumentos de una herramienta vienen PARTIDOS.</b> El primer delta trae el <c>id</c>, el
/// <c>type</c> y el <c>function.name</c> con los argumentos vacíos, y los siguientes van
/// concatenando <c>function.arguments</c> a trozos. Se casan por <c>index</c>. Quedarse con el
/// último delta —que es lo que sale de leer esto como si fuera un objeto y no un delta— deja una
/// llamada con el nombre bien y los argumentos a medias: el bucle la ejecutaría con medio JSON.
/// </item>
/// <item>
/// <b>El <c>usage</c> llega en el último evento CUANDO LLEGA.</b> Hay endpoints de este dialecto
/// que no lo mandan al hacer streaming, y ausente se devuelve <c>null</c> — «no lo dijo»— y jamás
/// cero. Cero es una cifra, y una cifra falsa en el pie del coste es peor que un hueco declarado.
/// </item>
/// </list>
/// </summary>
internal static class OpenAiSse
{
    /// <summary>Lo que se guarda de un cuerpo que no era SSE, para poder enseñarlo.</summary>
    private const int MaxUnexpected = 500;

    /// <summary>
    /// El turno, o <c>Turn</c> a null si no llegó un solo <c>data:</c> — que significa que esto no
    /// era un streaming y que quien llama tiene que pararlo en vez de devolver un turno vacío.
    /// </summary>
    internal readonly record struct Read(ChatTurn? Turn, string Unexpected);

    /// <summary>
    /// Lee el cuerpo entero y devuelve el turno ya ensamblado.
    /// <para>
    /// <b>Una línea <c>data:</c> que no sea JSON se ignora en silencio</b>, y es a propósito: los
    /// comentarios de keep-alive (<c>: ping</c>), las líneas <c>event:</c> y el <c>data:</c> vacío
    /// que mandan algunos endpoints al abrir son ruido del protocolo, y tumbar una unidad entera
    /// de auditoría por uno de ellos costaría mucho más de lo que salvaría.
    /// </para>
    /// </summary>
    internal static async Task<Read> ReadAsync(Stream body, Action<string>? onText, CancellationToken ct)
    {
        var assembler = new Assembler();
        var unexpected = new StringBuilder();
        bool sawEvent = false;

        using var reader = new StreamReader(body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                // El separador entre eventos. En este dialecto cada evento es UNA línea `data:`,
                // así que no hay nada que cerrar aquí.
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                // `: comentario`, `event: …`, `id: …`. Ruido del protocolo — salvo que no haya
                // llegado NINGÚN evento, en cuyo caso esto es el cuerpo de algo que no era SSE y
                // hay que poder enseñarlo.
                if (!sawEvent && unexpected.Length < MaxUnexpected)
                {
                    unexpected.Append(line).Append(' ');
                }

                continue;
            }

            string payload = line[5..].Trim();
            if (payload.Length == 0)
            {
                continue;
            }

            if (payload == "[DONE]")
            {
                sawEvent = true;
                break;
            }

            sawEvent = true;
            assembler.Feed(payload, onText);
        }

        return sawEvent
            ? new Read(assembler.Build(), string.Empty)
            : new Read(null, unexpected.ToString().Trim());
    }

    /// <summary>
    /// El montador: recibe deltas y al final entrega un <see cref="ChatTurn"/>. Está separado del
    /// lector de líneas para que la regla que importa —cómo se casan los deltas— se pueda mirar
    /// sin un <c>Stream</c> delante.
    /// </summary>
    private sealed class Assembler
    {
        private readonly StringBuilder _text = new();
        private readonly List<Draft> _tools = new();
        private ChatUsage? _usage;
        private string? _finish;

        internal void Feed(string json, Action<string>? onText)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return;
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                // El consumo, SOLO si viene como objeto. Muchos endpoints mandan `"usage": null`
                // en todos los trozos menos el último: leerlo sin mirar el tipo borraría el bueno.
                if (root.TryGetProperty("usage", out JsonElement usage)
                    && usage.ValueKind == JsonValueKind.Object)
                {
                    _usage = ReadUsage(usage);
                }

                if (!root.TryGetProperty("choices", out JsonElement choices)
                    || choices.ValueKind != JsonValueKind.Array)
                {
                    // Azure abre con un evento sin `choices` (filtros de contenido). No es un error.
                    return;
                }

                foreach (JsonElement choice in choices.EnumerateArray())
                {
                    FeedChoice(choice, onText);
                }
            }
        }

        internal ChatTurn Build()
        {
            var calls = new List<ChatToolCall>(_tools.Count);
            foreach (Draft draft in _tools)
            {
                if (string.IsNullOrEmpty(draft.Name))
                {
                    // Un hueco sin nombre no es una llamada: es un delta que llegó suelto.
                    continue;
                }

                // El id casi siempre viene en el primer delta. Cuando un endpoint no lo manda hay
                // que inventar uno igualmente: el mensaje `tool` de la respuesta lo exige, y sin
                // él el bucle no podría contestar a la llamada que acaba de ejecutar.
                calls.Add(new ChatToolCall(
                    string.IsNullOrEmpty(draft.Id) ? $"call_{draft.Index}" : draft.Id!,
                    draft.Name!,
                    draft.Arguments.ToString()));
            }

            return new ChatTurn(_text.ToString(), calls, _usage, _finish);
        }

        private void FeedChoice(JsonElement choice, Action<string>? onText)
        {
            if (choice.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (choice.TryGetProperty("finish_reason", out JsonElement finish)
                && finish.ValueKind == JsonValueKind.String)
            {
                _finish = finish.GetString();
            }

            if (!choice.TryGetProperty("delta", out JsonElement delta)
                || delta.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (delta.TryGetProperty("content", out JsonElement content)
                && content.ValueKind == JsonValueKind.String
                && content.GetString() is { Length: > 0 } piece)
            {
                _text.Append(piece);
                onText?.Invoke(piece);
            }

            if (delta.TryGetProperty("tool_calls", out JsonElement calls)
                && calls.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement call in calls.EnumerateArray())
                {
                    FeedToolCall(call);
                }
            }
        }

        /// <summary>
        /// <b>Aquí es donde se casan los deltas.</b> El nombre y el id se quedan con el PRIMERO que
        /// llegue —el dialecto los manda una sola vez— y los argumentos se CONCATENAN, que es lo
        /// único que hace que una llamada con argumentos largos llegue entera.
        /// </summary>
        private void FeedToolCall(JsonElement call)
        {
            if (call.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            Draft draft = Locate(call);

            if (string.IsNullOrEmpty(draft.Id)
                && call.TryGetProperty("id", out JsonElement id)
                && id.ValueKind == JsonValueKind.String)
            {
                draft.Id = id.GetString();
            }

            if (!call.TryGetProperty("function", out JsonElement function)
                || function.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (string.IsNullOrEmpty(draft.Name)
                && function.TryGetProperty("name", out JsonElement name)
                && name.ValueKind == JsonValueKind.String)
            {
                draft.Name = name.GetString();
            }

            if (function.TryGetProperty("arguments", out JsonElement arguments)
                && arguments.ValueKind == JsonValueKind.String)
            {
                draft.Arguments.Append(arguments.GetString());
            }
        }

        /// <summary>
        /// A qué llamada pertenece este delta. Por <c>index</c>, que es lo que dice el dialecto; y
        /// si el endpoint no lo manda —los hay—, por el <c>id</c>, y en último término al último
        /// abierto. Lo que NO se hace es abrir una llamada nueva por cada delta: eso es
        /// exactamente el fallo que deja los argumentos partidos en trozos sueltos.
        /// </summary>
        private Draft Locate(JsonElement call)
        {
            if (call.TryGetProperty("index", out JsonElement index)
                && index.ValueKind == JsonValueKind.Number
                && index.TryGetInt32(out int i))
            {
                foreach (Draft existing in _tools)
                {
                    if (existing.Index == i)
                    {
                        return existing;
                    }
                }

                var draft = new Draft { Index = i };
                _tools.Add(draft);
                return draft;
            }

            if (call.TryGetProperty("id", out JsonElement id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() is { Length: > 0 } known)
            {
                foreach (Draft existing in _tools)
                {
                    if (existing.Id == known)
                    {
                        return existing;
                    }
                }
            }

            if (_tools.Count > 0)
            {
                return _tools[^1];
            }

            var first = new Draft { Index = 0 };
            _tools.Add(first);
            return first;
        }

        private static ChatUsage ReadUsage(JsonElement usage)
        {
            long cached = 0;
            if (usage.TryGetProperty("prompt_tokens_details", out JsonElement details)
                && details.ValueKind == JsonValueKind.Object)
            {
                cached = Number(details, "cached_tokens");
            }

            return new ChatUsage(
                Number(usage, "prompt_tokens"),
                Number(usage, "completion_tokens"),
                cached);
        }

        private static long Number(JsonElement element, string name)
            => element.TryGetProperty(name, out JsonElement value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetInt64(out long n)
                ? n
                : 0;

        /// <summary>Una llamada a medio montar.</summary>
        private sealed class Draft
        {
            internal int Index { get; init; }

            internal string? Id { get; set; }

            internal string? Name { get; set; }

            internal StringBuilder Arguments { get; } = new();
        }
    }
}
