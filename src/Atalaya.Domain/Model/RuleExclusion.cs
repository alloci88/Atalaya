namespace Atalaya.Domain.Model;

/// <summary>
/// Una regla del catálogo que NO aplica a esta aplicación (F5.10). Es el segundo alcance del
/// silencio: «este caso concreto es un falso positivo aquí» tiene su silencio por ULID; «la regla
/// {ruleId} no aplica a este proyecto» tiene esto.
/// <para>
/// <b>Es por-aplicación, nunca global al hub.</b> Una app sin requisitos de i18n no quiere oír
/// hablar de localización; la de al lado puede tenerla como requisito de contrato. Una exclusión
/// global convertiría una decisión de un proyecto en una ceguera de todos, y ese es exactamente el
/// error que no se puede deshacer leyendo un informe: lo que no se reporta no se ve.
/// </para>
/// <para>
/// Comparte la disciplina de gobernanza del silencio: motivo obligatorio, notas, caducidad
/// opcional, autor y fecha. Y comparte su semántica de caducidad — una exclusión caducada es
/// <b>inexistente</b> a efectos de filtrado (<see cref="IsLiveAt"/>), aunque la UI la siga
/// listando como «caducada — revisar» (<see cref="IsExpiredAt"/>).
/// </para>
/// </summary>
public sealed class RuleExclusion
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>La regla excluida. Es la clave: <c>rule-exclusions/{ruleId}.json</c>.</summary>
    public required string RuleId { get; set; }

    /// <summary>Mismo cajón de motivos que el silencio: excluir es silenciar con otro alcance.</summary>
    public SilenceReason Reason { get; set; }

    public string? Notes { get; set; }

    public required string By { get; set; }

    public DateTimeOffset Utc { get; set; }

    /// <summary>Null = no caduca.</summary>
    public DateTimeOffset? ExpiresUtc { get; set; }

    /// <summary>Una exclusión suprime hasta su caducidad, si la tiene.</summary>
    public bool IsLiveAt(DateTimeOffset now) => ExpiresUtc is null || ExpiresUtc.Value > now;

    /// <summary>Caducada-pero-presente: la UI la lista como «caducada, revisar», igual que un silencio.</summary>
    public bool IsExpiredAt(DateTimeOffset now) => ExpiresUtc is not null && ExpiresUtc.Value <= now;
}

/// <summary>
/// Las reglas excluidas de una aplicación en un instante dado, ya filtradas por caducidad (F5.10).
/// <para>
/// Existe para que la pregunta «¿está excluida esta regla?» se conteste igual en los dos sitios
/// que la hacen —la composición del brief y la ingestión— sin que ninguno tenga que acordarse de
/// filtrar caducadas. Es un valor inmutable: se calcula una vez al arrancar la sesión, así que una
/// exclusión que caduca a mitad de sesión no cambia las reglas del juego a media partida.
/// </para>
/// </summary>
public sealed class RuleExclusionSet
{
    private readonly HashSet<string> _ruleIds;

    private RuleExclusionSet(HashSet<string> ruleIds) => _ruleIds = ruleIds;

    /// <summary>Ninguna exclusión: lo que usa cualquier ruta que no las conozca.</summary>
    public static RuleExclusionSet Empty { get; } = new(new HashSet<string>(StringComparer.Ordinal));

    /// <summary>Las que están VIVAS en <paramref name="now"/>. Las caducadas no entran.</summary>
    public static RuleExclusionSet From(IEnumerable<RuleExclusion> exclusions, DateTimeOffset now)
        => new(new HashSet<string>(
            exclusions.Where(e => e.IsLiveAt(now)).Select(e => e.RuleId),
            StringComparer.Ordinal));

    public bool IsEmpty => _ruleIds.Count == 0;

    public int Count => _ruleIds.Count;

    public bool Excludes(string? ruleId)
        => !string.IsNullOrEmpty(ruleId) && _ruleIds.Contains(ruleId!);

    public IReadOnlyCollection<string> RuleIds => _ruleIds;
}
