using System.IO.Pipes;

// El puente MCP de Atalaya (F14).
//
// QUÉ ES. Un relé de bytes, y nada más. El CLI de `claude` lanza los servidores MCP por stdio como
// procesos hijos suyos; Atalaya es una aplicación de escritorio que ya está corriendo, con el
// toolbox de la sesión vivo en memoria, y no puede ser ese hijo. Este programa sí: `claude` lo
// arranca, él se conecta a la tubería con nombre que Atalaya le indica, y a partir de ahí copia lo
// que entra por su stdin hacia la tubería y lo que sale de la tubería hacia su stdout.
//
// LO QUE NO HACE, que es el punto. No parsea JSON, no conoce el protocolo, no sabe qué es un
// hallazgo y no toma ninguna decisión. Todo eso vive en `AtalayaMcpServer`, dentro de la
// aplicación, junto al toolbox que valida y persiste. Un relé que no entiende lo que transporta no
// puede corromperlo, no puede quedarse desfasado cuando el catálogo de tools cambie, y no hay que
// probarlo dos veces.
//
// SU STDOUT ES EL PROTOCOLO. Por eso este programa no escribe NUNCA nada por su cuenta —ni un
// banner, ni un error, ni una traza—: una sola línea suelta rompería la conversación JSON-RPC. Lo
// que vaya mal se dice por stderr, que el CLI recoge aparte, y se sale con un código distinto de 0.

if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
{
    await Console.Error.WriteLineAsync(
        "Atalaya.Mcp es el puente MCP interno de Atalaya; no se ejecuta a mano. "
        + "Uso: Atalaya.Mcp <nombre-de-tuberia>");
    return 2;
}

string pipeName = args[0];

try
{
    await using var pipe = new NamedPipeClientStream(
        ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

    // Si Atalaya no está escuchando en 10 s, no va a estarlo: es preferible morir aquí —el CLI lo
    // marca como servidor "failed" y el driver para la sesión— que quedarse esperando para siempre
    // con el auditor sin herramientas al otro lado.
    await pipe.ConnectAsync(10_000, CancellationToken.None);

    await using Stream input = Console.OpenStandardInput();
    await using Stream output = Console.OpenStandardOutput();

    // Las dos direcciones a la vez, y se acaba en cuanto UNA de las dos se cierra: si el CLI cierra
    // su entrada la sesión terminó, y si la tubería cae Atalaya se fue. Esperar a la otra mitad
    // solo dejaría un proceso colgado.
    Task toAtalaya = CopyAsync(input, pipe);
    Task toClaude = CopyAsync(pipe, output);
    await Task.WhenAny(toAtalaya, toClaude);

    return 0;
}
catch (TimeoutException)
{
    await Console.Error.WriteLineAsync(
        $"Atalaya no está escuchando en la tubería «{pipeName}».");
    return 3;
}
catch (Exception ex)
{
    await Console.Error.WriteLineAsync($"El puente MCP de Atalaya falló: {ex.Message}");
    return 4;
}

// Copia con descarga inmediata. El buffer por defecto de un Stream esperaría a llenarse, y aquí
// cada mensaje es una línea que el otro lado necesita YA: con buffer, el CLI se quedaría esperando
// una respuesta que está escrita pero sin salir.
static async Task CopyAsync(Stream from, Stream to)
{
    byte[] buffer = new byte[16 * 1024];
    try
    {
        int read;
        while ((read = await from.ReadAsync(buffer)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, read));
            await to.FlushAsync();
        }
    }
    catch (IOException)
    {
        // El otro extremo se cerró a mitad. Es un final, no una avería.
    }
    catch (ObjectDisposedException)
    {
    }
}
