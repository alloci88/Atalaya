using Atalaya.Agents;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;

namespace Atalaya.Copilot;

/// <summary>
/// <b>Los turnos de una conversación de Copilot</b>, vistos desde dentro del hilo.
/// <para>
/// Existe por una razón concreta y no por gusto de abstraer: crear una <c>CopilotSession</c> de
/// verdad exige un asiento, y esta máquina no lo tiene (403 en <c>models.list</c>). Sin esta costura
/// el hilo de Copilot sería el único camino de producción que ningún test puede recorrer, y lo que
/// no se puede recorrer se rompe sin que nadie lo vea.
/// </para>
/// </summary>
internal interface ICopilotTurns : IAsyncDisposable
{
    /// <summary>Un turno. Vuelve cuando el agente deja su turno en reposo.</summary>
    Task SendAsync(string prompt, TimeSpan timeout, CancellationToken ct);
}

/// <summary>La sesión de verdad. No decide nada: traduce un turno en un envío.</summary>
internal sealed class LiveCopilotTurns : ICopilotTurns
{
    private readonly CopilotSession _session;

    internal LiveCopilotTurns(CopilotSession session) => _session = session;

    public Task SendAsync(string prompt, TimeSpan timeout, CancellationToken ct)
        => _session.SendAndWaitAsync(prompt, timeout, ct);

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}

/// <summary>
/// <b>El hilo de una unidad, en Copilot</b> (F25): una sesión viva mientras dure el barrido de la
/// unidad, y una pasada por turno.
/// <para>
/// Es más simple que el de Claude Code y no por casualidad: allí hay que invertir un bucle que tira
/// del turno siguiente, y aquí <c>SendAndWaitAsync</c> ya vuelve cuando el turno acaba. El
/// <c>unit_done</c> de Copilot es <b>terminal</b> desde el primer día, así que el turno cierra ahí
/// sin vuelta de cortesía — que es justo lo que el corte de F21 tuvo que inventar para la otra casa.
/// </para>
/// </summary>
internal sealed class CopilotUnitThread : IUnitThread
{
    private readonly ICopilotTurns _turns;
    private readonly Func<TimeSpan> _timeout;
    private readonly Func<Exception, Exception> _translate;
    private readonly ILogger _logger;
    private bool _closed;
    private bool _disposed;

    internal CopilotUnitThread(
        ICopilotTurns turns,
        Func<TimeSpan> timeout,
        Func<Exception, Exception> translate,
        ILogger logger)
    {
        _turns = turns;
        _timeout = timeout;
        _translate = translate;
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool Closed => _closed || _disposed;

    /// <summary>
    /// Una pasada. Un fallo aquí <b>cierra el hilo</b> pase lo que pase: una sesión que ha
    /// contestado con un error no es una sesión de la que uno se pueda fiar para el turno siguiente.
    /// <para>
    /// Lo que cambia es qué se lanza. Si el proveedor se ha quejado de algo con remedio propio
    /// —cuota, credencial, asiento, modelo— sube tal cual y el barrido se cierra en orden. Si es
    /// «no sé qué ha pasado» —que es como llega una sesión que ya no existe o que ha caducado—, se
    /// dice que <b>la conversación se rompió</b>: la pasada se rehace como se hacía antes, con una
    /// petición nueva, y la unidad no se pierde.
    /// </para>
    /// </summary>
    public async Task TurnAsync(string prompt, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            await _turns.SendAsync(prompt, _timeout(), ct);
        }
        catch (OperationCanceledException)
        {
            _closed = true;
            throw;
        }
        catch (Exception ex)
        {
            _closed = true;
            Exception translated = _translate(ex);
            throw translated is AuditorProviderException { Problem: AgentProblem.Unknown } unknown
                ? new UnitThreadBrokenException(unknown.Message, unknown.Detail, ex)
                : translated;
        }
    }

    /// <summary>
    /// Cierra la sesión. Se traga lo que salga: el barrido ya ha terminado con esta unidad y una
    /// excepción al recoger la mesa se llevaría por delante el resto de la sesión.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _closed = true;

        try
        {
            await _turns.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copilot (hilo): la conversación no cerró limpia");
        }
    }
}
