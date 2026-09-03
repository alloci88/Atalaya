using Atalaya.Domain.Ids;

namespace Atalaya.Domain.Model;

/// <summary>
/// <b>Marcar posibles duplicados, sin fusionar nada</b> (F23 §5).
/// <para>
/// <b>El problema es de lectura, no de datos.</b> En el informe de referencia hay 25 hallazgos y
/// unos 20 defectos: el auditor describe el mismo problema dos veces con palabras distintas en
/// pasadas distintas —la mutación de <c>DefaultRequestHeaders</c> aparece dos veces, el
/// <c>.Result</c> bloqueante y el «síncrono sobre async» son el mismo—. Quien conoce el código lo
/// ve; quien lo lee para decidir, cuenta 25.
/// </para>
/// <para>
/// <b>La aplicación NO fusiona.</b> Decidir que dos descripciones son el mismo defecto es un juicio
/// sobre el código, y equivocarse borra un hallazgo real. Se marca y decide la persona.
/// </para>
/// <para>
/// <b>El criterio, y por qué es éste.</b> Misma unidad, misma regla, mismo símbolo y las líneas a
/// <see cref="MaxLineDistance"/> o menos. Se midió contra el caso de referencia, comparando con los
/// pares que un humano señaló como duplicados de verdad:
/// </para>
/// <list type="table">
/// <item><term>regla + línea ≤ 0</term><description>2 ciertos de 4 · 0 falsos</description></item>
/// <item><term>regla + línea ≤ 3</term><description>3 de 4 · 3 falsos</description></item>
/// <item><term>regla + línea ≤ 12</term><description>4 de 4 · <b>11 falsos</b></description></item>
/// <item><term>regla + símbolo + línea ≤ 5</term><description>3 de 4 · 2 falsos</description></item>
/// <item><term><b>…y el símbolo NO es la clase</b></term><description><b>3 de 4 · 1 falso</b></description></item>
/// </list>
/// <para>
/// <b>El símbolo es lo que hace el trabajo</b>: sin él, «división por cero en CargaMediaPorMetro» y
/// «división por cero en CargaEspecifica» caen a cinco líneas una de otra con la misma regla y se
/// marcarían como el mismo defecto, cuando son dos métodos distintos. Ampliar la distancia sin el
/// símbolo no gana cobertura: gana ruido, y una marca que falla la mitad de las veces se deja de
/// mirar a la tercera.
/// </para>
/// <para>
/// <b>Lo que este criterio NO pilla, dicho:</b> dos hallazgos que el auditor ancló a alturas
/// distintas del código —uno a la clase y otro al método— no comparten símbolo, y dos que describen
/// el mismo defecto bajo reglas distintas (un <c>Timeout</c> ausente y un <c>CancellationToken</c>
/// ausente) no comparten regla. Los dos casos existen en el informe de referencia y los dos se
/// quedan sin marcar. Es el precio de no inventar.
/// </para>
/// <para>
/// <b>Y desde F24 este criterio se aplica DOS veces</b>, con esta misma implementación y no con una
/// copia: aquí, al escribir el informe, y en la puerta de <c>submit_findings</c>, donde un hallazgo
/// que se parece a uno que ya existe se le devuelve al auditor para que diga si de verdad es otro.
/// Son la misma pregunta en dos momentos, así que tienen que dar la misma respuesta — si el filtro
/// tuviera su propio umbral, el informe marcaría cosas que el filtro dejó pasar y al revés, y nadie
/// sabría cuál de los dos números mirar. El criterio vive en
/// <see cref="AreSimilar(VariantKey, VariantKey)"/>, las dos capas lo llaman, y hay un test que lo
/// fija.
/// </para>
/// </summary>
public static class DuplicateHints
{
    /// <summary>
    /// A cuántas líneas puede estar un duplicado. Medido, no elegido: ver la tabla de arriba.
    /// </summary>
    public const int MaxLineDistance = 5;

    /// <summary>
    /// Lo ÚNICO que el criterio mira de un hallazgo: su regla, su ubicación principal y su símbolo
    /// (F24). Existe para poder hacer la pregunta sobre algo que <b>todavía no es</b> un
    /// <see cref="Finding"/> — el payload que el auditor acaba de entregar y que la app aún no ha
    /// aceptado. Sin esta forma, el filtro de la puerta habría tenido que reimplementar el criterio
    /// sobre el payload, que es exactamente lo que no puede pasar.
    /// </summary>
    /// <param name="RuleId">La regla declarada. Se compara sin distinguir mayúsculas.</param>
    /// <param name="Path">La ruta de la ubicación PRINCIPAL (ver <see cref="AreSimilar(Finding, Finding)"/>).</param>
    /// <param name="Line">La línea de esa misma ubicación.</param>
    /// <param name="Symbol">El miembro, tal y como lo escribió el auditor; puede venir cualificado.</param>
    public readonly record struct VariantKey(string RuleId, string Path, int Line, string? Symbol);

    /// <summary>
    /// La clave de un hallazgo ya guardado. <c>null</c> cuando no tiene ubicación: sin un sitio no
    /// hay nada que comparar, y el criterio no adivina.
    /// </summary>
    public static VariantKey? KeyOf(Finding f)
        => f.Locations.Count == 0
            ? null
            : new VariantKey(f.RuleId, f.Locations[0].Path, f.Locations[0].Line, f.Symbol);

    /// <summary>
    /// Los pares sospechosos de <paramref name="findings"/>, en el orden en que se le pasan — que
    /// es el orden en el que se van a leer. Cada hallazgo se marca <b>una sola vez</b>, contra el
    /// primero al que se parece: encadenar marcas («A duplica a B, que duplica a C») convierte una
    /// pista en un grafo, y lo que se quiere es que el lector mire dos cosas y decida.
    /// </summary>
    public static IReadOnlyDictionary<Ulid, Finding> Of(IReadOnlyList<Finding> findings)
    {
        var hints = new Dictionary<Ulid, Finding>();
        for (int i = 0; i < findings.Count; i++)
        {
            for (int j = 0; j < i; j++)
            {
                if (!AreSimilar(findings[j], findings[i]))
                {
                    continue;
                }

                hints[findings[i].Id] = findings[j];
                break;
            }
        }

        return hints;
    }

    /// <summary>
    /// ¿Son estos dos el mismo defecto contado dos veces? Ver el criterio de la clase.
    /// <para>
    /// La ubicación que se compara es la <b>PRINCIPAL</b>, no todas. Un hallazgo con varias
    /// ubicaciones es un defecto sistémico —el mismo problema en sitios distintos—, y es justo el
    /// que menos probable es que duplique a un defecto puntual. Cruzando todas contra todas bastaba
    /// con que dos ubicaciones cualesquiera cayeran cerca para marcar dos cosas que no tienen que
    /// ver.
    /// </para>
    /// </summary>
    public static bool AreSimilar(Finding a, Finding b)
        => KeyOf(a) is { } ka && KeyOf(b) is { } kb && AreSimilar(ka, kb);

    /// <summary>
    /// <b>El criterio, y el único sitio donde está escrito</b> (F24). Lo llaman la marca del informe
    /// y el filtro de <c>submit_findings</c>; ver la explicación y la tabla de medida en la clase.
    /// </summary>
    public static bool AreSimilar(VariantKey a, VariantKey b)
    {
        if (!string.Equals(a.RuleId, b.RuleId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase)
            || Math.Abs(a.Line - b.Line) > MaxLineDistance)
        {
            return false;
        }

        string member = Member(a.Symbol);
        if (member.Length == 0 || !string.Equals(member, Member(b.Symbol), StringComparison.Ordinal))
        {
            return false;
        }

        // Y el símbolo tiene que ser un MIEMBRO, no la clase. Cuando el auditor ancla un hallazgo
        // a la clase entera solo está diciendo «en algún sitio de este fichero», que no distingue
        // nada: medido, era la mitad de las marcas falsas del caso de referencia.
        return !IsTypeItself(member, a.Path) && !IsTypeItself(Member(b.Symbol), b.Path);
    }

    /// <summary>
    /// ¿El símbolo es el tipo del fichero y no un miembro suyo? Se compara con el nombre del
    /// fichero, que es la convención de la casa (un tipo por fichero) y lo único que hay a mano.
    /// </summary>
    private static bool IsTypeItself(string member, string path)
        => string.Equals(
            member,
            Path.GetFileNameWithoutExtension(path),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// El miembro al que apunta un símbolo, sin la clase. El auditor lo escribe de las dos maneras
    /// —<c>EnviarParteAsync</c> y <c>ClienteRemoto.EnviarParteAsync</c> conviven en el mismo
    /// informe—, así que compararlos enteros diría que son sitios distintos cuando son el mismo.
    /// </summary>
    private static string Member(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return string.Empty;
        }

        string trimmed = symbol.Trim();
        int dot = trimmed.LastIndexOf('.');
        return dot >= 0 && dot < trimmed.Length - 1 ? trimmed[(dot + 1)..] : trimmed;
    }
}
