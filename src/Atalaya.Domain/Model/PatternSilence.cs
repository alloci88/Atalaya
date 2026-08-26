using Atalaya.Domain.Ids;

namespace Atalaya.Domain.Model;

/// <summary>
/// Un TIPO de problema que esta aplicación ha decidido no ver más (F5.12). Es el segundo alcance
/// del silencio: «este caso concreto es un falso positivo aquí» tiene su silencio por ULID; «los
/// bloques catch vacíos no me interesan en este proyecto» tiene esto.
/// <para>
/// El alcance lo define una FRASE —el <see cref="Exemplar"/>—, no un identificador de catálogo.
/// La pregunta «¿este hallazgo es del mismo tipo que aquel?» es semántica, y en este proyecto las
/// preguntas semánticas las contesta el LLM en el momento de auditar: el ejemplar viaja en el
/// prompt de cada unidad y el auditor decide. Sustituye a la exclusión por regla, que convertía el
/// silenciado en mantenimiento de taxonomía sin dueño (ver DECISIONS, F5.12).
/// </para>
/// <para>
/// <b>Es por-aplicación, nunca global al hub.</b> Un tipo de problema que sobra en una app puede
/// ser contractual en la de al lado, y el error no se descubre leyendo un informe: lo que no se
/// reporta no se ve. La ruta lo hace imposible por construcción.
/// </para>
/// <para>
/// Comparte la disciplina de gobernanza del silencio: motivo obligatorio, notas, caducidad
/// opcional, autor y fecha. Y su semántica de caducidad — un patrón caducado es <b>inexistente</b>
/// a efectos de filtrado (<see cref="IsLiveAt"/>), aunque la gestión lo siga listando como
/// «caducado — revisar» (<see cref="IsExpiredAt"/>).
/// </para>
/// </summary>
public sealed class PatternSilence
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Identidad del patrón. Es la clave: <c>pattern-silences/{ulid}.json</c>.</summary>
    public Ulid Id { get; set; }

    /// <summary>
    /// El identificador CORTO que viaja en el prompt y que el auditor cita en <c>unit_done</c>
    /// (<c>P-1</c>, <c>P-2</c>…). Un ULID de 26 caracteres por patrón en cada prompt es ruido que
    /// el modelo copia mal; un <c>P-3</c> no. Es estable durante la vida del patrón: editar el
    /// ejemplar no lo cambia, así que un informe viejo sigue nombrando lo mismo.
    /// </summary>
    public required string ShortId { get; set; }

    /// <summary>
    /// La frase que define el alcance: «bloques catch vacíos que ocultan excepciones». Se propone
    /// automáticamente desde el hallazgo origen y el usuario la pule antes de confirmar — es lo
    /// único que el auditor va a leer, así que tiene que decir exactamente lo que se quiere callar.
    /// </summary>
    public required string Exemplar { get; set; }

    /// <summary>
    /// El hallazgo desde el que se creó el patrón. Null en los patrones migrados de las viejas
    /// exclusiones por regla, que no nacieron de ningún hallazgo concreto.
    /// </summary>
    public Ulid? SourceFindingUlid { get; set; }

    /// <summary>Mismo cajón de motivos que el silencio: es la misma decisión con otro alcance.</summary>
    public SilenceReason Reason { get; set; }

    public string? Notes { get; set; }

    public required string By { get; set; }

    public DateTimeOffset Utc { get; set; }

    /// <summary>Null = no caduca.</summary>
    public DateTimeOffset? ExpiresUtc { get; set; }

    /// <summary>
    /// Cuántas detecciones han declarado los auditores haber suprimido por este patrón, acumulado
    /// desde que se creó. Es <b>cuánto trabaja</b> el patrón: uno con 0 supresiones tras varios
    /// ciclos es un patrón que quizá no hacía falta, y uno con 200 explica por sí solo por qué la
    /// cobertura de esta app se lee como se lee.
    /// </summary>
    public int Suppressions { get; set; }

    /// <summary>Cuándo se contó la última supresión. Null si nunca ha suprimido nada.</summary>
    public DateTimeOffset? LastSuppressionUtc { get; set; }

    /// <summary>Un patrón suprime hasta su caducidad, si la tiene.</summary>
    public bool IsLiveAt(DateTimeOffset now) => ExpiresUtc is null || ExpiresUtc.Value > now;

    /// <summary>Caducado-pero-presente: la gestión lo lista como «caducado, revisar».</summary>
    public bool IsExpiredAt(DateTimeOffset now) => ExpiresUtc is not null && ExpiresUtc.Value <= now;
}

/// <summary>
/// Los patrones silenciados de una aplicación en un instante dado, ya filtrados por caducidad
/// (F5.12).
/// <para>
/// Existe para que la pregunta «¿qué se le dice al auditor que NO reporte?» se conteste igual en
/// los dos sitios que la hacen —la composición del prompt y la validación de lo que el auditor
/// cita en <c>unit_done</c>— sin que ninguno tenga que acordarse de filtrar caducados. Es un valor
/// inmutable: se calcula una vez al arrancar la sesión, así que un patrón que caduca a mitad de
/// sesión no cambia las reglas del juego a media partida.
/// </para>
/// </summary>
public sealed class PatternSilenceSet
{
    private readonly Dictionary<string, PatternSilence> _byShortId;

    private PatternSilenceSet(IReadOnlyList<PatternSilence> patterns)
    {
        Patterns = patterns;
        _byShortId = new Dictionary<string, PatternSilence>(StringComparer.OrdinalIgnoreCase);
        foreach (PatternSilence p in patterns)
        {
            _byShortId[p.ShortId] = p;
        }
    }

    /// <summary>
    /// A partir de cuántos patrones VIVOS la gestión pide revisar. No es un límite duro —la lista
    /// sigue siendo despreciable frente al contenido de una unidad— sino la señal de que el
    /// silenciado se está usando como taxonomía, que es justo lo que F5.12 vino a evitar.
    /// </summary>
    public const int SoftCap = 50;

    /// <summary>Ningún patrón: lo que usa cualquier ruta que no los conozca.</summary>
    public static PatternSilenceSet Empty { get; } = new(Array.Empty<PatternSilence>());

    /// <summary>Los que están VIVOS en <paramref name="now"/>. Los caducados no entran.</summary>
    public static PatternSilenceSet From(IEnumerable<PatternSilence> patterns, DateTimeOffset now)
        => new(patterns
            .Where(p => p.IsLiveAt(now))
            .OrderBy(p => p.Utc)
            .ToList());

    /// <summary>Los patrones vivos, en orden de creación: el orden en que se escriben en el prompt.</summary>
    public IReadOnlyList<PatternSilence> Patterns { get; }

    public bool IsEmpty => Patterns.Count == 0;

    public int Count => Patterns.Count;

    /// <summary>
    /// El patrón que el auditor cita por su id corto, o null si citó uno que no existe. Tolerante
    /// con los espacios y las mayúsculas porque el id lo reescribe un modelo, no un programa.
    /// </summary>
    public PatternSilence? ByShortId(string? shortId)
        => string.IsNullOrWhiteSpace(shortId) ? null
            : _byShortId.TryGetValue(shortId!.Trim(), out PatternSilence? p) ? p : null;
}

/// <summary>Asigna el id corto de un patrón nuevo dentro de una aplicación (F5.12).</summary>
public static class PatternShortId
{
    public const string Prefix = "P-";

    /// <summary>
    /// El siguiente id libre: el mayor de los que hay, más uno. Se calcula sobre los patrones
    /// EXISTENTES —vivos y caducados— en vez de llevar un contador en <c>app.json</c> porque el id
    /// corto no es una identidad histórica: es un handle para el prompt y para la ficha. Si se
    /// borran todos los patrones y se crea otro, vuelve a ser <c>P-1</c> sin que nada mienta — el
    /// informe que citaba el viejo guarda su ejemplar escrito al lado.
    /// </summary>
    public static string Next(IEnumerable<PatternSilence> existing)
    {
        int max = 0;
        foreach (PatternSilence p in existing)
        {
            if (p.ShortId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(p.ShortId.AsSpan(Prefix.Length), out int n)
                && n > max)
            {
                max = n;
            }
        }

        return Prefix + (max + 1);
    }
}
