using System.IO.Pipes;
using System.Security.Cryptography;

namespace Atalaya.ClaudeCode;

/// <summary>
/// Sirve el servidor MCP de Atalaya por una tubería con nombre, para que el puente
/// (<c>Atalaya.Mcp</c>) se lo dé al CLI de <c>claude</c> por stdio (F14).
/// <para>
/// <b>Por qué hay un puente en medio.</b> Un servidor MCP por stdio lo LANZA el cliente como
/// proceso hijo: <c>claude</c> arranca un programa y le habla por su entrada y su salida estándar.
/// Atalaya es una aplicación de escritorio que ya está corriendo, con el toolbox de la sesión vivo
/// en memoria — no puede ser ese hijo. Así que el hijo es un relé de veinte líneas
/// (<c>Atalaya.Mcp.exe</c>) que no entiende nada de lo que transporta: pasa bytes de su stdin a
/// esta tubería y de esta tubería a su stdout. Toda la lógica —el catálogo, las llamadas, la
/// validación— vive aquí dentro, junto al toolbox que persiste de verdad.
/// </para>
/// <para>
/// <b>Por qué una tubería y no un puerto.</b> Un socket en <c>localhost</c> lo puede abrir
/// cualquier proceso de la máquina, y por él viajan los hallazgos de la auditoría. Una tubería con
/// nombre la protege el sistema con la ACL de quien la crea, no hace falta elegir puerto, y no
/// deja ningún servicio a la escucha cuando la sesión acaba. El nombre además es aleatorio y de un
/// solo uso: vale para UNA sesión y se tira.
/// </para>
/// </summary>
public sealed class McpPipeHost : IAsyncDisposable
{
    private readonly AtalayaMcpServer _server;
    private readonly Action<string>? _trace;
    private readonly CancellationTokenSource _stopping = new();
    private NamedPipeServerStream? _pipe;
    private Task? _serving;

    /// <param name="retention">
    /// La herramienta cuya respuesta se retiene para poder cortar la pasada sin pagar la llamada
    /// siguiente (F21 §2). Null —lo normal— es el comportamiento de siempre: se contesta todo.
    /// </param>
    public McpPipeHost(
        IEnumerable<McpTool> tools, Action<string>? trace = null, ToolRetention? retention = null)
    {
        _server = new AtalayaMcpServer(tools, trace, retention);
        _trace = trace;
        PipeName = $"atalaya-mcp-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}";
    }

    /// <summary>
    /// El nombre de la tubería de ESTA sesión. Aleatorio y de un solo uso: aunque la ACL ya
    /// restringe quién puede abrirla, un nombre que no se puede adivinar quita hasta la
    /// posibilidad de intentarlo.
    /// </summary>
    public string PipeName { get; }

    /// <summary>Cuántas veces llamó el auditor a una tool. Va a las métricas de la unidad.</summary>
    public int ToolCalls => _server.ToolCalls;

    /// <summary>Herramientas atendiéndose ahora mismo, sin contar la retenida (F21 §2).</summary>
    public int Busy => _server.Busy;

    /// <summary>
    /// Empieza a escuchar. Vuelve en cuanto la tubería está creada —NO espera al cliente—, porque
    /// el cliente es el CLI y todavía no se ha lanzado: si esto esperase, nadie llegaría a
    /// lanzarlo y las dos partes se quedarían esperándose.
    /// </summary>
    public void Start()
    {
        _pipe = new NamedPipeServerStream(
            PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        _serving = ServeAsync(_pipe, _stopping.Token);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            await pipe.WaitForConnectionAsync(ct);
            _trace?.Invoke($"MCP: el puente conectó por {PipeName}");
            await _server.ServeAsync(pipe, pipe, ct);
        }
        catch (OperationCanceledException)
        {
            // La sesión terminó y ya no hace falta escuchar. Es el final normal.
        }
        catch (IOException ex)
        {
            // El puente se fue de golpe (el CLI murió). Que la tubería se rompa NO es un fallo de
            // la auditoría: quien decide cómo terminó la sesión es el flujo de eventos del CLI.
            _trace?.Invoke($"MCP: la tubería se cerró ({ex.Message})");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        if (_serving is not null)
        {
            try
            {
                await _serving;
            }
            catch (Exception)
            {
                // Ya está: se está cerrando.
            }
        }

        _pipe?.Dispose();
        _stopping.Dispose();
    }
}
