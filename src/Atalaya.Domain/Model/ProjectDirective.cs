using Atalaya.Domain.Ids;

namespace Atalaya.Domain.Model;

/// <summary>
/// Para qué sirve una directiva (F7). No es una etiqueta descriptiva: decide en qué prompts
/// viaja su contenido, y por eso se cura a mano.
/// <para>
/// Un ADR de arquitectura aplica a los dos flujos —informa el criterio del auditor y el estilo
/// del arreglo—. Una skill de «cómo escribir specs» no aplica a ninguno: es material del
/// proyecto, pero nada de lo que dice cambia cómo se audita una clase ni cómo se arregla un
/// defecto. Meterla en los dos prompts «por si acaso» gastaría presupuesto en ruido, así que
/// existe <see cref="Ninguno"/> y existe una persona que decide.
/// </para>
/// </summary>
public enum DirectiveScope
{
    /// <summary>
    /// Detectada y NO activada. Es un estado con dueño humano, no la ausencia de decisión: se
    /// persiste para que el candidato descartado no vuelva a anunciarse como nuevo en cada
    /// re-escaneo.
    /// </summary>
    Ninguno,

    /// <summary>Viaja en el prompt del auditor y en el del verificador.</summary>
    Auditoria,

    /// <summary>Viaja en el prompt de arreglo —el generador old school y la sesión interactiva—.</summary>
    Arreglo,

    Ambos,
}

/// <summary>
/// El REGISTRO de que un fichero del repo de una app es una directiva del proyecto (F7), guardado
/// como <c>apps/{slug}/directives/{ulid}.json</c>.
/// <para>
/// <b>Aquí no hay contenido, y es lo importante.</b> Las directivas viven versionadas en el repo
/// de cada aplicación, que es su sitio: son del proyecto, no de Atalaya. El hub registra
/// únicamente CUÁLES son —ruta, ámbito, prioridad y quién lo decidió— y el contenido se lee del
/// clon local en el momento de componer el prompt, así que siempre viaja la versión vigente. Una
/// copia sincronizada al hub sería una segunda verdad que empieza a envejecer el día que se
/// escribe, y el equipo acabaría auditando contra unas convenciones que ya nadie sigue.
/// </para>
/// <para>
/// Un fichero por directiva, como todo lo demás del hub: dos personas que activan directivas
/// distintas a la vez no colisionan en git.
/// </para>
/// </summary>
public sealed class ProjectDirective
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Identidad de la entrada. Es la clave: <c>directives/{ulid}.json</c>.</summary>
    public Ulid Id { get; set; }

    /// <summary>
    /// Ruta del fichero RELATIVA a la raíz del clon, con barras hacia delante. Es la clave real
    /// de la directiva —lo que se busca en el clon— aunque no dé nombre al fichero: el ULID lo
    /// hace para que renombrar una ruta no obligue a mover un fichero del hub.
    /// </summary>
    public required string Path { get; set; }

    /// <summary>
    /// Qué clase de directiva es, según el catálogo que la detectó
    /// (<c>agents</c>, <c>adr</c>, <c>skills</c>…), o <c>manual</c> si la añadió una persona.
    /// Informativo: agrupa la lista del panel y explica de dónde salió el candidato.
    /// </summary>
    public required string Kind { get; set; }

    /// <summary>El ámbito curado. <see cref="DirectiveScope.Ninguno"/> = vista y no activada.</summary>
    public DirectiveScope Scope { get; set; } = DirectiveScope.Ninguno;

    /// <summary>
    /// Prioridad de inclusión cuando el presupuesto no da para todo: menor entra antes. El
    /// usuario lo edita en el panel. Empates —lo normal al principio, todo a 0— se rompen por
    /// ruta, para que el orden sea estable entre máquinas y el prompt no cambie según quién lo
    /// componga.
    /// </summary>
    public int Order { get; set; }

    public required string By { get; set; }

    public DateTimeOffset Utc { get; set; }

    /// <summary>Activa = su contenido viaja en algún prompt.</summary>
    public bool IsActive => Scope != DirectiveScope.Ninguno;

    /// <summary>¿Viaja en los prompts de auditoría (auditor y verificador)?</summary>
    public bool AppliesToAudit => Scope is DirectiveScope.Auditoria or DirectiveScope.Ambos;

    /// <summary>¿Viaja en los prompts de arreglo (generador y sesión interactiva)?</summary>
    public bool AppliesToFix => Scope is DirectiveScope.Arreglo or DirectiveScope.Ambos;

    /// <summary>¿Aplica a <paramref name="purpose"/>? Nunca se pregunta por <c>Ambos</c>.</summary>
    public bool AppliesTo(DirectiveScope purpose) => purpose switch
    {
        DirectiveScope.Auditoria => AppliesToAudit,
        DirectiveScope.Arreglo => AppliesToFix,
        DirectiveScope.Ambos => IsActive,
        _ => false,
    };
}

/// <summary>Los nombres con los que se lee un ámbito en la interfaz (F7).</summary>
public static class DirectiveScopeNames
{
    public static string Display(DirectiveScope scope) => scope switch
    {
        DirectiveScope.Auditoria => "Auditoría",
        DirectiveScope.Arreglo => "Arreglo",
        DirectiveScope.Ambos => "Ambos",
        _ => "Sin activar",
    };
}

/// <summary>
/// Qué directivas viajaron en una sesión y con qué contenido exacto (F7). Va en el informe: sin
/// esto, «se auditó con las convenciones del proyecto» sería una afirmación sin forma de
/// comprobarla, y el contenido vive en un repo que se mueve.
/// </summary>
/// <param name="Path">Ruta relativa al clon.</param>
/// <param name="ContentHash">
/// SHA-256 del contenido ÍNTEGRO leído del clon, incluso cuando solo viajó su principio: es lo
/// que permite volver al fichero de aquel día en el historial del repo de la app.
/// </param>
/// <param name="Truncated">Viajó solo el principio, por presupuesto.</param>
/// <param name="Omitted">No viajó nada: el presupuesto se agotó antes de llegar a ella.</param>
public sealed record DirectiveRecord(
    string Path,
    string ContentHash,
    bool Truncated = false,
    bool Omitted = false);
