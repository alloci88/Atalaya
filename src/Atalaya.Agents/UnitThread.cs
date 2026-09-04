namespace Atalaya.Agents;

/// <summary>
/// <b>Una unidad como una CONVERSACIÓN, y cada pasada un turno</b> (M2). Es la costura de una
/// MEDIDA del banco, no una capacidad del producto.
/// <para>
/// Hoy cada pasada de un barrido es una petición nueva: se recompone el prompt entero —reglas,
/// rúbrica, catálogo, unidad y la lista de existentes, que crece— y se manda. El andamiaje son
/// ~27.000 tokens por llamada y el código ~285 (F18), así que el prefijo se reescribe en caché
/// tantas veces como pasadas tenga la unidad, y eso es el 63 % de la factura de una sesión (F20,
/// D-871). Un hilo lo manda una vez.
/// </para>
/// <para>
/// <b>Esto ya se cayó una vez, y por eso la costura es del banco.</b> F20 §3 (D-874) implementó la
/// conversación compartida, midió un 91 % menos de escritura en la llamada que abre cada pasada, y
/// se cayó por lo otro: el auditor dejaba de llamar a herramientas por completo a partir de la
/// segunda pasada. La condición que dejó escrita para el día que se reintentara es la que gobierna
/// este contrato: <b>la prueba no es que ahorre, es que las pasadas ≥ 2 sigan encontrando lo que
/// encuentran hoy</b>.
/// </para>
/// <para>
/// Un proveedor que no la implemente no es un proveedor roto: es uno con el que esta medida no se
/// puede hacer, y el parte lo dice con esas palabras.
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
    /// <summary>Un turno. El primero lleva el prompt entero; los siguientes, la continuación.</summary>
    Task TurnAsync(string prompt, CancellationToken ct);
}

/// <summary>
/// <b>Qué ULIDs ha creado el auditor en el barrido en curso</b>, en el orden en que se crearon.
/// <para>
/// Existe por una consecuencia de M2 que no se ve hasta que se quita la lista del prompt: hoy el
/// auditor conoce el ULID de lo que él mismo reportó <b>porque la pasada siguiente se lo vuelve a
/// listar</b>, no porque la aplicación se lo diga. <c>submit_finding</c> contesta
/// <c>{accepted, duplicateOf, error}</c> y nada más. En un hilo no hay lista que reenviar, así que
/// sin esto el auditor no puede pronunciarse sobre lo que creó él — y la reconciliación de F4, que
/// es la variable que esta medida no puede degradar, se quedaría a medias por una tontería.
/// </para>
/// <para>
/// Es de <b>solo lectura</b> y no cambia ni un veredicto: quien la usa es el brazo del banco, que
/// devuelve el id en el resultado de la herramienta. En producción nadie la mira.
/// </para>
/// </summary>
public interface ISweepCreations
{
    /// <summary>Los ULIDs creados en el barrido de la unidad en curso, en orden de creación.</summary>
    IReadOnlyList<string> CreatedInSweep { get; }
}
