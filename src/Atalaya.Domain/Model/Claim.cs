using Atalaya.Domain.Hashing;

namespace Atalaya.Domain.Model;

/// <summary>
/// An ephemeral claim on a unit (§2), stored as <c>claims/{unitHash}.json</c> and deleted
/// on release. Has a TTL; the owning session heartbeats it every TTL/2 so long sessions
/// keep it alive while a dead session releases it in ≤ TTL.
/// </summary>
public sealed class Claim
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>The unit's repo-relative path.</summary>
    public required string Unit { get; set; }

    public required string Module { get; set; }

    public required string By { get; set; }

    public required string Machine { get; set; }

    /// <summary>Last heartbeat/creation time. Renewed by the owning session.</summary>
    public DateTimeOffset Utc { get; set; }

    public int TtlMinutes { get; set; } = 30;

    /// <summary>Stable identity (§2): SHA-256 of the normalized unit path.</summary>
    public string UnitHash => HashUtil.UnitHash(Unit);

    public DateTimeOffset ExpiresAt => Utc.AddMinutes(TtlMinutes);

    /// <summary>When the owning session should next heartbeat (TTL/2).</summary>
    public DateTimeOffset HeartbeatDueAt => Utc.AddMinutes(TtlMinutes / 2.0);

    /// <summary>Expired claims are ignorable and deletable by anyone (§2).</summary>
    public bool IsExpiredAt(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>
    /// Si este claim puede ANUNCIARSE como actividad en curso (BUGFIX-ACTIVIDAD).
    /// <para>
    /// No basta con que no haya caducado: el TTL lo escribe quien crea el claim, así que una
    /// versión futura —o una máquina con el reloj movido— podría dejar uno anunciándose durante
    /// días. Lo que se mira aquí es el silencio: <b>cuánto hace que nadie lo refresca</b>, contra
    /// un margen que decide QUIEN LEE y no quien escribe.
    /// </para>
    /// <para>
    /// Manda el más estricto de los dos: caducado no se anuncia, y callado más de
    /// <see cref="ClaimRules.MaxSilence"/> tampoco.
    /// </para>
    /// </summary>
    public bool AnnouncesActivityAt(DateTimeOffset now)
        => !IsExpiredAt(now) && now - Utc <= ClaimRules.MaxSilence;
}

/// <summary>
/// Lo que decide el LECTOR de un claim, y no su autor (BUGFIX-ACTIVIDAD).
/// </summary>
public static class ClaimRules
{
    /// <summary>
    /// Cuánto puede llevar un claim sin refrescarse y seguir contando como «alguien está auditando
    /// esto ahora mismo».
    /// <para>
    /// <b>Treinta minutos, y por qué.</b> Es el mismo margen que el TTL con el que nacen los
    /// claims, así que no estrena una segunda regla que pudiera contradecir a la primera. Y el
    /// número sale de qué error se prefiere: por debajo, una unidad grande que de verdad se está
    /// auditando dejaría de anunciarse a mitad y dos personas podrían pisarse; por encima, a un
    /// compañero se le cierra el portátil y su tarjeta miente al equipo entero durante horas.
    /// Media hora es lo que tarda de sobra una unidad, y lo que nadie acepta como «ahora mismo».
    /// </para>
    /// <para>
    /// Es un margen de LECTURA: no borra nada de nadie. Un claim ajeno y callado sigue en el hub —
    /// no es nuestro—, simplemente deja de anunciarse. Mejor no decir nada que mentir.
    /// </para>
    /// </summary>
    public static readonly TimeSpan MaxSilence = TimeSpan.FromMinutes(30);
}
