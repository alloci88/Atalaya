namespace Atalaya.Agents;

/// <summary>
/// Los NOMBRES y las DESCRIPCIONES de las herramientas del AUDITOR, en un solo sitio (PROV-2 §3).
/// <para>
/// <b>Por qué una fuente y no dos catálogos.</b> Es la misma razón que <see cref="FixToolText"/>
/// (D-805) y ya estaba escrita en D-777: con dos motores detrás del mismo contrato, la superficie
/// que ve el agente es lo único que hace comparables a las dos casas. Si una tool se llamara
/// distinto —o su descripción dijera algo distinto— el mismo prompt de unidad significaría dos
/// cosas, y una discrepancia entre proveedores dejaría de poder atribuirse al modelo. Hasta PROV-2
/// la igualdad la sostenía la disciplina: el texto estaba copiado a mano en los dos drivers, y
/// <c>unit_done</c> ya había divergido.
/// </para>
/// <para>
/// <b>Las siete, y ni una más.</b> Seis son el toolbox de la auditoría, en el orden del catálogo
/// —que es el que el prompt nombra, con <c>unit_done</c> la última (D-883)—, y la séptima,
/// <see cref="SubmitVerdict"/>, es la única de una sesión de verificación.
/// </para>
/// <para>
/// <b>La terminalidad NO está en el texto.</b> «Llamar a esto es haber terminado» es una propiedad
/// de la herramienta y se declara en <see cref="Terminal"/>; cómo se le dice al modelo es cosa del
/// transporte. El SDK de Copilot tiene un concepto para eso (<c>IsTerminal</c>); MCP no lo tiene y
/// su driver lo compone con palabras a partir de esta misma marca. Una frase de más metida en la
/// descripción compartida es exactamente la divergencia que esta clase viene a impedir.
/// </para>
/// </summary>
public static class AuditToolText
{
    public const string SubmitFindings = "submit_findings";

    public const string SubmitFindingsDescription =
        "PREFERIDA. Reporta TODOS los hallazgos de la unidad en UNA sola llamada, pasando un array. "
        + "Devuelve un array de {accepted, duplicateOf, error, id} en el mismo orden; el id es el ULID "
        + "del hallazgo creado, y es con el que luego le das veredicto o le añades ubicaciones.";

    public const string SubmitFinding = "submit_finding";

    public const string SubmitFindingDescription =
        "Fallback singular. Úsala solo si por alguna razón no puedes agrupar; cada llamada añade un turno. "
        + "Devuelve {accepted, duplicateOf, error, id}, con el ULID del hallazgo creado.";

    public const string ReportVerdicts = "report_verdicts";

    public const string ReportVerdictsDescription =
        "OBLIGATORIA cuando la unidad tiene hallazgos existentes. Un array con un veredicto por CADA "
        + "hallazgo listado: {findingId (ULID exacto de la lista), verdict "
        + "(presente|arreglado|no-es-defecto|no-verificable), evidence}. Usa 'arreglado' SOLO si el "
        + "código cambió y por eso el problema ya no está; si lo que ocurre es que discrepas de quien "
        + "lo reportó, usa 'no-es-defecto' con tu razonamiento. Devuelve un array de {accepted, error} "
        + "en el mismo orden.";

    public const string AddLocations = "add_locations";

    public const string AddLocationsDescription =
        "Extiende un hallazgo YA existente con ubicaciones nuevas de esta misma unidad. Úsala cuando "
        + "el MISMO defecto aparece en varios sitios: un defecto sistémico es UN hallazgo con N "
        + "ubicaciones, no N hallazgos. findingId debe ser un ULID de la lista de existentes o de uno "
        + "que hayas reportado en esta unidad.";

    /// <summary>
    /// Cierra la unidad, y es TERMINAL (ver <see cref="Terminal"/>). La descripción no lo dice: lo
    /// dice la marca, y cada transporte la traduce a lo suyo.
    /// </summary>
    public const string UnitDone = "unit_done";

    public const string UnitDoneDescription =
        "Cierra la unidad en curso con un resumen. Si el prompt trae TIPOS DE PROBLEMA SILENCIADOS y "
        + "te has callado alguna detección por uno de ellos, declara cuántas en suppressedByPattern: "
        + "un array de {patternId (el id EXACTO del prompt, p. ej. P-2), count}. Déjalo vacío si no te "
        + "has callado nada.";

    public const string ReadSignatures = "read_signatures";

    public const string ReadSignaturesDescription =
        "Devuelve las firmas (no cuerpos) de las dependencias directas de la unidad.";

    /// <summary>La única de una sesión de VERIFICACIÓN. Usa el ULID, nunca el displayId mutable.</summary>
    public const string SubmitVerdict = "submit_verdict";

    public const string SubmitVerdictDescription =
        "Registra el veredicto de un hallazgo por su ULID: confirmado | resuelto | no-verificable.";

    /// <summary>
    /// Las seis de la auditoría, EN EL ORDEN del catálogo. El orden es el del prompt y no se toca
    /// (D-883): <c>unit_done</c> va la última porque es la que cierra.
    /// </summary>
    public static IReadOnlyList<string> Audit { get; } = new[]
    {
        SubmitFindings, SubmitFinding, ReportVerdicts, AddLocations, UnitDone, ReadSignatures,
    };

    /// <summary>La de la verificación, en lista para poder afirmar sobre ella igual que sobre las otras.</summary>
    public static IReadOnlyList<string> Verify { get; } = new[] { SubmitVerdict };

    /// <summary>
    /// <b>Las que CIERRAN el turno</b>: llamarlas es haber terminado. Es una propiedad declarada de
    /// la herramienta, no un texto — para que una terminal futura la herede sin que nadie tenga que
    /// acordarse de escribir la misma frase en el driver que la necesite.
    /// </summary>
    public static IReadOnlyCollection<string> Terminal { get; } = new[] { UnitDone };

    /// <summary>¿Llamar a esta herramienta es haber terminado?</summary>
    public static bool IsTerminal(string toolName) => Terminal.Contains(toolName);

    /// <summary>La descripción de una herramienta por su nombre, o vacío si no es del catálogo.</summary>
    public static string DescriptionOf(string toolName) => toolName switch
    {
        SubmitFindings => SubmitFindingsDescription,
        SubmitFinding => SubmitFindingDescription,
        ReportVerdicts => ReportVerdictsDescription,
        AddLocations => AddLocationsDescription,
        UnitDone => UnitDoneDescription,
        ReadSignatures => ReadSignaturesDescription,
        SubmitVerdict => SubmitVerdictDescription,
        _ => string.Empty,
    };
}
