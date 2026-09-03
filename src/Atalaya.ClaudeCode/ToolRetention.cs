namespace Atalaya.ClaudeCode;

/// <summary>
/// <b>Retener la respuesta de una herramienta para poder cortar sin pagar la vuelta siguiente</b>
/// (F21 §2).
/// <para>
/// <b>El problema.</b> Cada pasada termina con una llamada que no hace nada: después del resultado
/// de <c>unit_done</c> el modelo tiene que contestar, y contestar es una llamada entera con el
/// prompt dentro. F20 la midió: <b>escribe 29.786 tokens en caché a 1,25 ×</b>, cerca del 70 % del
/// coste de entrada de la pasada. Es lo más caro que queda, y no aporta un solo dato.
/// </para>
/// <para>
/// <b>Por qué no basta con «matar el proceso pronto».</b> Una petición ya enviada y cortada a
/// medias <b>se factura igual y encima no queda registrada</b> — lo peor de los dos mundos.
/// Medido contra el CLI real (2.1.259): interrumpiendo a mitad de respuesta, el modelo principal
/// desaparece ENTERO del <c>modelUsage</c> del evento final, que pasa a declarar solo el modelo
/// auxiliar. Así que el corte no puede confiar en llegar «a tiempo»: hay que impedir que la
/// petición SALGA.
/// </para>
/// <para>
/// <b>Cómo se impide.</b> Verificado, no supuesto: el CLI no manda el turno siguiente hasta tener
/// <b>todos</b> los resultados de herramienta del turno. Reteniendo la respuesta de
/// <c>unit_done</c> —el efecto ya se ha aplicado, el resumen de cobertura ya está apuntado; lo
/// único que no se escribe es la contestación— el CLI se queda esperando. Medido: con
/// <c>unit_done</c> retenida durante 25 segundos, el contador de peticiones abiertas no se movió.
/// Y de propina resuelve lo que preocupaba a F19 (D-863) sobre que <c>unit_done</c> truncara el
/// resto del turno: ya no depende de que el modelo la ponga la última.
/// </para>
/// <para>
/// <b>Y es REVERSIBLE, que es la mitad del diseño.</b> Si al llegar el momento las cuentas no
/// están (<see cref="ClaudeStreamReader.AccountingIsComplete"/>), se <see cref="Release"/> y la
/// pasada termina como siempre, pagando su llamada de cortesía y diciéndolo en el informe. Nunca
/// un corte con hueco: primero las cuentas, después el ahorro (N-2).
/// </para>
/// </summary>
public sealed class ToolRetention
{
    private readonly string _toolName;

    private readonly TaskCompletionSource _held =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="toolName">
    /// La herramienta cuya respuesta se retiene. Es <c>unit_done</c> y solo <c>unit_done</c>: es la
    /// única que el auditor llama cuando ya no le queda nada que entregar, así que es el único
    /// punto en el que retener no le quita trabajo a nadie.
    /// </param>
    public ToolRetention(string toolName) => _toolName = toolName;

    /// <summary>¿Es ésta la herramienta que se retiene?</summary>
    public bool Holds(string name) => string.Equals(name, _toolName, StringComparison.Ordinal);

    /// <summary>
    /// Se cumple en cuanto la herramienta retenida <b>ya ha pasado por el toolbox</b> y su
    /// respuesta se ha quedado sin escribir. Es la señal de que el CLI está parado y de que se
    /// puede cortar — cuando las cuentas lo permitan.
    /// </summary>
    public Task Retained => _held.Task;

    /// <summary>Si ya está retenida. Para preguntarlo sin esperar.</summary>
    public bool IsRetained => _held.Task.IsCompleted;

    /// <summary>Si ya se ha soltado (por corte o por renuncia al corte).</summary>
    public bool IsReleased => _released.Task.IsCompleted;

    /// <summary>
    /// Suelta lo retenido: la respuesta se escribe y la sesión sigue exactamente como si esto no
    /// existiera. Se llama al renunciar al corte y también al terminar, pase lo que pase — un
    /// relay esperando una liberación que no llega es una sesión colgada.
    /// </summary>
    public void Release() => _released.TrySetResult();

    internal void Hold() => _held.TrySetResult();

    internal Task WaitForReleaseAsync(CancellationToken ct) => _released.Task.WaitAsync(ct);
}
