using Atalaya.Domain.Ids;

namespace Atalaya.Domain.Model;

/// <summary>
/// Un fichero que un arreglo dejó escrito, y con qué contenido EXACTO lo dejó (F9 §2).
/// </summary>
/// <param name="Path">La ruta de la unidad en el repo de la aplicación, con barras normales.</param>
/// <param name="ContentHash">
/// <see cref="Hashing.HashUtil.ContentHash"/> del fichero tal y como quedó al acabar el arreglo.
/// Es la huella que permite reconocer, más tarde, cuál de los commits del usuario recogió ESTE
/// trabajo: un commit cuyo contenido para esa ruta case con esta huella es, con certeza y no por
/// aproximación, el commit que publicó este arreglo.
/// </param>
public sealed record FixFileStamp(string Path, string ContentHash);

/// <summary>
/// Lo que un arreglo de la propia aplicación dejó hecho en el clon (F9 §2), en
/// <c>apps/{slug}/fixes/{ulid}.json</c>.
/// <para>
/// <b>Por qué esto es un HECHO y vive en el hub.</b> La deriva —qué unidades han cambiado desde
/// que se auditaron— se deriva del historial local en cada consulta y no se persiste nunca. Pero
/// «este arreglo dejó estos ficheros con este contenido» no se puede derivar de nada: pasó una vez,
/// en una máquina, y si no se escribe se pierde. Sin él, cada arreglo realimentaría la lista de
/// unidades cambiadas y el ciclo no convergería jamás.
/// </para>
/// <para>
/// <b>Por qué huellas de contenido y no el hash del commit.</b> Al acabar un arreglo los cambios
/// están en el clon del usuario, sin commitear, y el hash del commit que los recoja todavía no
/// existe. Lo único que la aplicación sabe con certeza en ese momento es QUÉ dejó escrito, así que
/// eso es lo que guarda.
/// <br />
/// <b>Sigue siendo así desde F32</b>, que revocó D-556 y dejó que «Me quedo los cambios» commitee:
/// el commit es POSTERIOR y voluntario —el usuario puede no pulsarlo nunca, o publicarlo con su
/// herramienta—, así que la huella no puede depender de él. Cuando sí se pulsa,
/// <see cref="CommitSha"/> se rellena encima: un atajo, no la prueba.
/// </para>
/// </summary>
public sealed class FixRecord
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>El ULID de la sesión <c>fix</c> que lo produjo: un arreglo, un registro.</summary>
    public Ulid Id { get; init; }

    public required string AppSlug { get; set; }

    /// <summary>El hallazgo que se estaba arreglando, para poder volver de la lista a su ficha.</summary>
    public string? FindingId { get; set; }

    /// <summary>Su alias legible («OPT-0002») cuando lo tenía.</summary>
    public string? FindingAlias { get; set; }

    public DateTimeOffset Utc { get; set; }

    public required string By { get; set; }

    /// <summary>
    /// HEAD del clon al terminar el arreglo: el commit del que DEBERÍA colgar el que lo recoja.
    /// Informativo —la atribución la decide la huella de contenido—, pero es lo que permite
    /// explicar en una frase por qué un arreglo dejó de reconocerse.
    /// </summary>
    public string? BaseCommit { get; set; }

    /// <summary>
    /// El commit que publicó este arreglo, cuando lo hizo la propia aplicación con «Me quedo los
    /// cambios» (F32). Corto, como el resto de hashes que se enseñan.
    /// <para>
    /// <b>Es un atajo, no la prueba.</b> La atribución la sigue decidiendo la huella de contenido
    /// de <see cref="Files"/> (D-685): un arreglo commiteado fuera de Atalaya —o enmendado
    /// después— no tiene este dato y se reconoce igual. Va como propiedad opcional, así que los
    /// registros escritos antes de F32 se leen sin tocarlos (el patrón de D-571).
    /// </para>
    /// </summary>
    public string? CommitSha { get; set; }

    public List<FixFileStamp> Files { get; set; } = new();
}
