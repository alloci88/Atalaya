using System.Threading.Channels;
using Atalaya.Agents;
using Microsoft.Extensions.Logging;

namespace Atalaya.ClaudeCode;

/// <summary>
/// <b>El hilo de una unidad</b> (F25): un solo proceso del CLI vivo mientras dura el barrido de la
/// unidad, y una pasada por turno de la conversación.
/// <para>
/// <b>La mecánica no es nueva y por eso se puede intentar.</b> Es la misma que el arreglo asistido
/// usa desde F16 (<see cref="ClaudeCliRunner.RunConversationAsync"/>): la entrada
/// <c>--input-format stream-json</c>, un mensaje de usuario por turno, y la aplicación decidiendo
/// cuándo se acabó cerrando stdin. Lo verificado allí contra el CLI real es justo lo que el
/// barrido necesita: el <c>session_id</c> se conserva entre turnos y el modelo recuerda lo
/// anterior, también con <c>--no-session-persistence</c>.
/// </para>
/// <para>
/// <b>Lo que aquí se invierte respecto de F16.</b> Allí quien decide el turno siguiente es el
/// usuario, y el bucle del runner tira de él con <c>nextTurn</c>. Aquí quien decide es el
/// coordinador del barrido, que empuja: <see cref="TurnAsync"/> deja el texto en un canal y espera
/// a que el turno cierre. El runner sigue siendo el de siempre; lo único que cambia es de qué lado
/// está el que espera.
/// </para>
/// <para>
/// <b>El corte de F21 y el hilo son alternativos</b>, y aquí se ve por qué: el corte interrumpe la
/// invocación en cuanto el auditor entrega <c>unit_done</c>, y la invocación es la conversación
/// entera. Cortar la mata. Así que en producción el hilo no lleva corte —el turno siguiente es
/// trabajo, no cortesía—, y cuando el banco lo arma para medirlo, el turno cortado sigue contando
/// como pasada y el hilo queda <see cref="Closed"/>: la pasada siguiente abrirá uno nuevo. Lo que
/// no se hace nunca es intentar reanudar un hilo cortado.
/// </para>
/// </summary>
internal sealed class ClaudeUnitThread : IUnitThread
{
    private readonly ClaudeCliRunner _runner;
    private readonly ClaudeRun _template;
    private readonly Action<string>? _onText;
    private readonly Action<UsageSample>? _onUsage;
    private readonly ILogger _logger;
    private readonly Func<ClaudeRunOutcome, Exception?> _explain;
    private readonly IAsyncDisposable _host;
    private readonly string _configPath;

    private readonly Channel<string> _prompts =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// <b>La vida de la conversación es la de la UNIDAD, no la de una pasada.</b> Y es una
    /// distinción con consecuencias: el coordinador crea y DESTRUYE un
    /// <c>CancellationTokenSource</c> por pasada —el de los techos de F19— y si la conversación
    /// colgara de aquél, a partir de la pasada 2 estaría atada a una fuente ya desechada, que no
    /// puede cancelar nunca. El hilo tiene el suyo, encadenado al de la unidad, y el de cada turno
    /// solo gobierna la ESPERA de ese turno.
    /// </summary>
    private CancellationTokenSource? _lifetime;

    /// <summary>Quien espera a que el turno en curso cierre. Se releva en cada turno.</summary>
    private TaskCompletionSource? _turnClosed;

    private Task<ClaudeRunOutcome>? _conversation;
    private bool _disposed;
    private bool _closed;

    /// <summary>
    /// El corte de F21, si el banco lo ha armado. Se arma UNA vez para toda la conversación y no
    /// por turno, porque más de una no puede haber: en cuanto cae, la conversación termina.
    /// </summary>
    private readonly ClaudeCut? _cut;

    internal ClaudeUnitThread(
        ClaudeCliRunner runner,
        ClaudeRun template,
        IAsyncDisposable host,
        string configPath,
        Action<string>? onText,
        Action<UsageSample>? onUsage,
        Func<ClaudeRunOutcome, Exception?> explain,
        ILogger logger,
        ClaudeCut? cut = null)
    {
        _runner = runner;
        _template = template;
        _host = host;
        _configPath = configPath;
        _onText = onText;
        _onUsage = onUsage;
        _explain = explain;
        _logger = logger;
        _cut = cut;
    }

    /// <inheritdoc/>
    public bool Closed => _closed || _disposed;

    /// <summary>
    /// Una pasada. Vuelve cuando el auditor cierra SU turno, que es exactamente lo que hace
    /// <c>AuditUnitAsync</c> en el camino de respaldo: así el coordinador aplica su regla de
    /// parada sobre lo mismo, sin enterarse de por dónde viajó el prompt.
    /// </summary>
    public async Task TurnAsync(string prompt, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Se releva ANTES de que el prompt pueda salir: si el turno cerrara primero, la señal
        // llegaría a un relevo que todavía no existe y esta pasada esperaría para siempre.
        Volatile.Write(ref _turnClosed, closed);

        if (_conversation is null)
        {
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _conversation = StartAsync(prompt, _lifetime.Token);
        }
        else
        {
            await _prompts.Writer.WriteAsync(prompt, ct);
        }

        // La conversación puede morir a mitad de turno —el CLI se cae, la cuota se agota—, y
        // entonces nadie va a cerrar este turno. Se espera a lo que ocurra primero, y también a
        // que la pasada se cancele: los techos por pasada de F19 tienen que valer aquí igual que
        // en producción, o el brazo estaría corriendo con un presupuesto que el otro no tiene.
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task cancelled = Task.Delay(Timeout.Infinite, waiting.Token);
        Task done;
        try
        {
            done = await Task.WhenAny(closed.Task, _conversation, cancelled);
        }
        finally
        {
            // Sin esto, cada turno dejaría detrás una espera infinita que nadie va a resolver.
            waiting.Cancel();
        }

        if (done == cancelled)
        {
            // Un turno no se puede abandonar a medias dejando la conversación viva: se corta
            // entera, y el coordinador lo lee como lo que es, una pasada cortada.
            _closed = true;
            _lifetime?.Cancel();
            ct.ThrowIfCancellationRequested();
        }

        if (done == _conversation)
        {
            // La conversación se acabó dentro del turno. No queda hilo, pase lo que pase.
            _closed = true;

            // Y si se acabó porque LA CORTAMOS nosotros, la pasada está servida: el auditor ya
            // había entregado su `unit_done` —eso es lo que dispara el corte— y lo único que se
            // interrumpió fue la vuelta de cortesía. Cuenta como pasada, y la siguiente abrirá
            // otro hilo.
            if (_cut is { Cut: true })
            {
                return;
            }

            Fail(await _conversation);
        }
        else if (_conversation.IsCompleted)
        {
            // El turno cerró y la conversación se fue justo detrás: sirvió, pero no hay siguiente.
            _closed = true;
        }
    }

    /// <summary>
    /// Cierra la conversación: el canal se completa, el runner deja de tener turno siguiente que
    /// pedir y cierra stdin, que es el fin para el CLI. Se espera al desenlace porque es el que
    /// trae las cuentas del final (el modelo auxiliar del CLI no aparece en ningún otro sitio,
    /// D-879), y se recogen la tubería MCP y la configuración pase lo que pase.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _closed = true;
        _prompts.Writer.TryComplete();

        try
        {
            if (_conversation is not null)
            {
                ClaudeRunOutcome outcome = await _conversation;
                if (outcome.Failed)
                {
                    _logger.LogWarning(
                        "Claude Code (hilo) terminó mal: {Problem} — {Message}",
                        outcome.Problem, outcome.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Claude Code (hilo): la conversación no cerró limpia");
        }
        finally
        {
            _lifetime?.Dispose();
            await _host.DisposeAsync();
            TryDelete(_configPath);
        }
    }

    private Task<ClaudeRunOutcome> StartAsync(string first, CancellationToken ct)
        => _runner.RunConversationAsync(
            _template with { Prompt = first },
            _onText,
            usage => _onUsage?.Invoke(usage),
            NextTurnAsync,
            closed: () => false,
            ready: null,
            ct,
            _cut);

    /// <summary>
    /// Lo llama el runner justo cuando un turno acaba. Dos cosas, en este orden: relevar a quien
    /// esperaba ese turno, y quedarse esperando el siguiente. Devolver <c>null</c> —el canal
    /// completado— es lo que termina la conversación.
    /// </summary>
    private async Task<string?> NextTurnAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref _turnClosed, null)?.TrySetResult();

        try
        {
            return await _prompts.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private void Fail(ClaudeRunOutcome outcome)
    {
        if (_explain(outcome) is { } problem)
        {
            throw problem;
        }

        // Terminó bien pero sin cerrar el turno que se esperaba: el CLI se fue por su cuenta y la
        // pasada NO se ha servido. No es una avería del proveedor —una petición nueva funcionaría
        // ahora mismo—, así que se dice con el tipo que lleva ese remedio dentro y el barrido
        // rehace la pasada como se hacía antes.
        throw new UnitThreadBrokenException(
            "Claude Code cerró la conversación de la unidad sin terminar la pasada.",
            outcome.Message);
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Claude Code (hilo): no se pudo borrar {Path}: {Message}", path, ex.Message);
        }
    }
}
