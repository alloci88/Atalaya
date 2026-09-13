using System.Net;
using System.Text;

namespace Atalaya.OpenAI.Tests;

/// <summary>
/// <b>Un endpoint OpenAI-compatible que vive dentro del proceso</b> (PROV-3, paso 0).
/// <para>
/// Todos los tests de esta entrega corren contra esto y <b>ninguno toca la red</b>: no hay clave
/// de nadie, no hay factura y no hay un test que falle porque hoy la API va lenta. Se le dan
/// respuestas grabadas —con su código, sus cabeceras y su cuerpo— y devuelve la siguiente cada vez
/// que le llaman, apuntando lo que recibió para que el test lo pueda mirar.
/// </para>
/// <para>
/// El SSE se sirve como lo sirve una API de verdad: líneas `data: {json}` separadas por línea en
/// blanco y un `data: [DONE]` al final. Montarlo bien aquí es lo que hace que el lector se pruebe
/// de verdad y no contra una forma inventada.
/// </para>
/// </summary>
internal sealed class EndpointFalso : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _respuestas = new();

    /// <summary>Lo que se le pidió, en orden: método, URL, cabeceras y cuerpo.</summary>
    public List<PeticionVista> Recibidas { get; } = new();

    /// <summary>Cuántas veces se le ha llamado. El test del reintento vive de esto.</summary>
    public int Llamadas => Recibidas.Count;

    public EndpointFalso Responde(HttpResponseMessage respuesta)
    {
        _respuestas.Enqueue(respuesta);
        return this;
    }

    /// <summary>Un turno de streaming: los trozos que se le pasen, en orden, y el `[DONE]`.</summary>
    public EndpointFalso RespondeStream(params string[] eventos)
    {
        var sb = new StringBuilder();
        foreach (string e in eventos)
        {
            sb.Append("data: ").Append(e).Append("\n\n");
        }

        sb.Append("data: [DONE]\n\n");

        var respuesta = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/event-stream"),
        };

        return Responde(respuesta);
    }

    /// <summary>Un fallo con su código y su cuerpo, como lo manda una API de verdad.</summary>
    public EndpointFalso RespondeError(HttpStatusCode codigo, string cuerpo = "{\"error\":{\"message\":\"no\"}}")
        => Responde(new HttpResponseMessage(codigo)
        {
            Content = new StringContent(cuerpo, Encoding.UTF8, "application/json"),
        });

    /// <summary>Una respuesta normal, sin streaming (lo que usa «Probar»).</summary>
    public EndpointFalso RespondeJson(string cuerpo, HttpStatusCode codigo = HttpStatusCode.OK)
        => Responde(new HttpResponseMessage(codigo)
        {
            Content = new StringContent(cuerpo, Encoding.UTF8, "application/json"),
        });

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string cuerpo = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var cabeceras = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers)
        {
            cabeceras[h.Key] = string.Join(", ", h.Value);
        }

        Recibidas.Add(new PeticionVista(
            request.Method.Method,
            request.RequestUri?.ToString() ?? string.Empty,
            cabeceras,
            cuerpo));

        if (_respuestas.Count == 0)
        {
            throw new InvalidOperationException(
                $"El endpoint falso se quedó sin respuestas grabadas en la llamada {Recibidas.Count}. "
                + "O el código llama más veces de las que el test esperaba, o al test le falta una.");
        }

        return _respuestas.Dequeue();
    }

    // ============================================================ dos ejemplos, para empezar

    /// <summary>
    /// Un turno que contesta en prosa y cierra. Los deltas van partidos a propósito: una respuesta
    /// de verdad no llega entera, y un lector que solo funcione con el texto de una pieza no sirve.
    /// </summary>
    public static string[] TurnoDeTexto(string texto)
    {
        var trozos = new List<string>();
        foreach (char c in texto)
        {
            string contenido = System.Text.Json.JsonSerializer.Serialize(c.ToString());
            trozos.Add("{\"choices\":[{\"index\":0,\"delta\":{\"content\":" + contenido + "}}]}");
        }

        trozos.Add(
            "{\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],"
            + "\"usage\":{\"prompt_tokens\":120,\"completion_tokens\":8,"
            + "\"prompt_tokens_details\":{\"cached_tokens\":100}}}");

        return trozos.ToArray();
    }

    /// <summary>
    /// Un turno que llama a una herramienta. Los argumentos llegan partidos en dos deltas, que es
    /// como los manda una API de verdad y donde se rompe un lector ingenuo.
    /// </summary>
    public static string[] TurnoDeHerramienta(string tool, string argumentos)
    {
        int mitad = argumentos.Length / 2;
        string a = System.Text.Json.JsonSerializer.Serialize(argumentos[..mitad]);
        string b = System.Text.Json.JsonSerializer.Serialize(argumentos[mitad..]);
        string nombre = System.Text.Json.JsonSerializer.Serialize(tool);

        return new[]
        {
            "{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,"
            + "\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":" + nombre
            + ",\"arguments\":\"\"}}]}}]}",

            "{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,"
            + "\"function\":{\"arguments\":" + a + "}}]}}]}",

            "{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,"
            + "\"function\":{\"arguments\":" + b + "}}]}}]}",

            "{\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}],"
            + "\"usage\":{\"prompt_tokens\":200,\"completion_tokens\":30}}",
        };
    }
}

/// <summary>Lo que el endpoint falso vio llegar.</summary>
/// <param name="Metodo">GET, POST…</param>
/// <param name="Url">La dirección entera.</param>
/// <param name="Cabeceras">Las de la petición, para poder mirar cómo viajó la clave.</param>
/// <param name="Cuerpo">El JSON que se mandó, para poder comprobar el prompt y las herramientas.</param>
internal sealed record PeticionVista(
    string Metodo,
    string Url,
    IReadOnlyDictionary<string, string> Cabeceras,
    string Cuerpo);
