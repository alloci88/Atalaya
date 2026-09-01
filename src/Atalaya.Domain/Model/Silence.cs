using Atalaya.Domain.Ids;

namespace Atalaya.Domain.Model;

/// <summary>
/// A silence (§2), con caducidad opcional. Silenciar es siempre una acción humana con autor y
/// motivo. Un silencio caducado es inexistente a efectos de filtrado.
/// <para>
/// F4: la clave pasa a ser el <b>ULID del hallazgo</b> (<see cref="FindingUlid"/>), no el
/// fingerprint. El hallazgo silenciado sigue existiendo con estado
/// <see cref="FindingStatus.Silenciado"/> y el auditor lo ve en la lista de existentes de su
/// unidad, así que puede declararlo "presente" sin que reaparezca.
/// </para>
/// </summary>
public sealed class Silence
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>El hallazgo silenciado. Es la clave: <c>silences/{ulid}.json</c>.</summary>
    public Ulid FindingUlid { get; set; }

    public SilenceReason Reason { get; set; }

    public string? Notes { get; set; }

    public required string By { get; set; }

    public DateTimeOffset Utc { get; set; }

    /// <summary>Null = never expires.</summary>
    public DateTimeOffset? ExpiresUtc { get; set; }

    /// <summary>
    /// De dónde vino este silencio (F5.12). Null = lo silenció una persona sobre ESTE hallazgo, que
    /// es la vía de siempre. Con valor = lleva el <b>ejemplar</b> del patrón que lo silenció de
    /// paso, para que la ficha pueda decir «silenciado al silenciar el patrón "…"» en vez de dejar
    /// creer que alguien miró este caso concreto y decidió sobre él.
    /// <para>
    /// Guarda el TEXTO del ejemplar y no el id del patrón a propósito: la procedencia tiene que
    /// seguir siendo legible cuando el patrón se des-silencie o se reescriba, y un id colgando de
    /// un fichero que ya no existe no explica nada.
    /// </para>
    /// <para>
    /// Es procedencia, no semántica: un silencio nacido de un patrón suprime, caduca y se levanta
    /// exactamente igual que cualquier otro.
    /// </para>
    /// </summary>
    public string? ByPatternExemplar { get; set; }

    /// <summary>
    /// El patrón que lo puso (F12 §F). Con esto el silencio por patrón pasa a ser <b>derivado</b>:
    /// un hallazgo está silenciado por patrón mientras ese patrón siga VIGENTE, y retirarlo lo
    /// devuelve a activo al instante y gratis.
    /// <para>
    /// Antes solo se guardaba el texto del ejemplar, y el veredicto «silenciado» quedaba congelado
    /// en el hallazgo: retirar el patrón no revivía nada de lo que había tapado, y solo otra
    /// auditoría —pagada— podía cambiarlo. Es el mismo principio que la deriva: lo que se puede
    /// derivar de un hecho vigente no se persiste como veredicto.
    /// </para>
    /// <para>
    /// El texto del ejemplar se conserva al lado a propósito (ver <see cref="ByPatternExemplar"/>):
    /// es lo que hace legible la procedencia, y es además el enganche de los silencios escritos
    /// antes de F12, que no traen id.
    /// </para>
    /// <para>
    /// <b>Null = lo silenció una persona sobre ESTE hallazgo</b>, y eso sí es un hecho del hallazgo:
    /// se conserva pase lo que pase con los patrones.
    /// </para>
    /// </summary>
    public Ulid? ByPatternId { get; set; }

    /// <summary>A silence is live (suppresses) until its expiry, if any.</summary>
    public bool IsLiveAt(DateTimeOffset now) => ExpiresUtc is null || ExpiresUtc.Value > now;

    /// <summary>Expired-but-present: the UI lists these as "caducado, revisar" (§2).</summary>
    public bool IsExpiredAt(DateTimeOffset now) => ExpiresUtc is not null && ExpiresUtc.Value <= now;
}
