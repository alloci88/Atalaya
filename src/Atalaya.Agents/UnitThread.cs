namespace Atalaya.Agents;

/// <summary>
/// <b>Una unidad es una CONVERSACIÓN, y cada pasada un turno</b> (F25). Es el modo de producción
/// del barrido, no una palanca.
/// <para>
/// <b>De dónde sale.</b> Hasta aquí cada pasada era una petición nueva: se recomponía el prompt
/// entero —reglas, rúbrica, catálogo, unidad y la lista de existentes, que crece— y se mandaba. El
/// andamiaje son ~27.000 tokens por llamada y el código ~285, así que el prefijo se reescribía en
/// caché tantas veces como pasadas tuviera la unidad. Medido contra ese barrido, el hilo cuesta
/// <b>un 69 % menos por unidad</b> y escribe <b>un 79 % menos de caché en las pasadas 2..N</b>, con
/// la pasada 1 costando lo mismo en los dos.
/// </para>
/// <para>
/// <b>Y lo que cuesta se dice igual de claro.</b> Sobre el caso de referencia, el barrido de hoy
/// llega a 20 de 20 defectos pagando seis pasadas; el hilo se queda en <b>17-18 de 20</b>: un
/// modelo que tiene delante su propia respuesta anterior converge antes, y al converger se deja
/// dos hallazgos tardíos. Es una decisión de producto tomada con las dos cifras delante, no un
/// efecto que se descubrió después.
/// </para>
/// <para>
/// Un proveedor que no la implemente no es un proveedor roto: es uno cuyo barrido corre por el
/// camino de respaldo —una petición por pasada, como antes—, y eso se dice en la sesión.
/// </para>
/// </summary>
public interface IThreadedAuditor
{
    /// <summary>
    /// Abre el hilo de UNA unidad. El <paramref name="toolbox"/> es el mismo para todos los turnos
    /// —es el del barrido—, así que lo que se reporte en el turno 1 lo cuenta la misma contabilidad
    /// que lo del turno 5.
    /// </summary>
    Task<IUnitThread> OpenUnitThreadAsync(IAuditToolbox toolbox, CancellationToken ct);
}

/// <summary>
/// El hilo abierto de una unidad. Cada <see cref="TurnAsync"/> es una pasada: vuelve cuando el
/// auditor cierra su turno, igual que <c>AuditUnitAsync</c> vuelve cuando cierra la pasada.
/// <para>
/// <b>Cerrarlo es cerrar la conversación</b>, y hay que hacerlo pase lo que pase: un proveedor
/// esperando un turno que ya no va a llegar es una sesión colgada gastando cuota.
/// </para>
/// </summary>
public interface IUnitThread : IAsyncDisposable
{
    /// <summary>
    /// <b>La conversación ya no puede recibir otro turno.</b> Lo consulta el barrido al terminar
    /// cada pasada: un hilo cerrado no se reanuda —la siguiente pasada abre uno nuevo desde cero,
    /// con el prompt recompuesto—, y eso queda contado como un reinicio con su motivo.
    /// </summary>
    bool Closed { get; }

    /// <summary>Un turno. El primero lleva el prompt entero; los siguientes, la continuación.</summary>
    Task TurnAsync(string prompt, CancellationToken ct);
}

/// <summary>
/// <b>La conversación no puede continuar, y la pasada no se ha servido</b> (F25 §2).
/// <para>
/// Es el primero de los tres respaldos: el proveedor no pudo reanudar la sesión, o la sesión
/// caducó, o el CLI se fue sin cerrar el turno. Tiene tipo propio porque tiene REMEDIO propio y no
/// es el mismo que el de una cuota agotada: la pasada se rehace <b>como se hacía antes</b>, una
/// petición nueva con el prompt recompuesto y la lista de existentes, y el barrido sigue. Una
/// unidad no se pierde porque una conversación se caiga.
/// </para>
/// <para>
/// Lo que NO entra aquí es un fallo que una petición nueva tampoco arreglaría —cuota, credencial,
/// asiento, modelo—: ésos suben tal cual como <see cref="AuditorProviderException"/> y el barrido
/// se cierra en orden, que es lo que BUGFIX-CUOTA dejó decidido.
/// </para>
/// </summary>
public sealed class UnitThreadBrokenException : AuditorProviderException
{
    public UnitThreadBrokenException(string message, string? detail = null, Exception? inner = null)
        : base(message, AgentProblem.Unknown, detail, inner) { }
}

/// <summary>
/// <b>Qué ULIDs ha creado el auditor en el barrido en curso</b>, en el orden en que se crearon.
/// <para>
/// Existe por una consecuencia del hilo que no se ve hasta que se quita la lista del prompt: antes
/// el auditor conocía el ULID de lo que él mismo reportó <b>porque la pasada siguiente se lo volvía
/// a listar</b>, no porque la aplicación se lo dijera. <c>submit_finding</c> contestaba
/// <c>{accepted, duplicateOf, error}</c> y nada más. En un hilo no hay lista que reenviar, así que
/// sin esto el auditor no podría pronunciarse sobre lo que creó él — y la reconciliación de F4 se
/// quedaría a medias por una tontería.
/// </para>
/// <para>
/// Es de <b>solo lectura</b> y no cambia ni un veredicto: quien la usa es el catálogo de
/// herramientas, que devuelve el id en el resultado de los dos <c>submit</c>
/// (<see cref="SweepReceipts"/>).
/// </para>
/// </summary>
public interface ISweepCreations
{
    /// <summary>Los ULIDs creados en el barrido de la unidad en curso, en orden de creación.</summary>
    IReadOnlyList<string> CreatedInSweep { get; }
}
