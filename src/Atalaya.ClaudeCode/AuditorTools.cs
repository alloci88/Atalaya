using System.Text.Json;
using System.Text.Json.Nodes;
using Atalaya.Agents;

namespace Atalaya.ClaudeCode;

/// <summary>
/// El catálogo de herramientas del auditor, traducido a MCP (F14).
/// <para>
/// <b>Los nombres y las descripciones son LOS MISMOS que ve Copilot</b>, palabra por palabra. No es
/// pulcritud: el prompt de la unidad —que es el mismo para los dos proveedores— nombra estas tools
/// y explica cuándo usar cada una. Si aquí se llamaran distinto, o si la descripción dijera algo
/// distinto, el mismo prompt significaría dos cosas y las dos casas no serían comparables. Toda la
/// gracia de tener un segundo auditor es que discrepen sobre el CÓDIGO, no sobre las instrucciones.
/// </para>
/// <para>
/// <b>Y son exactamente éstas.</b> No hay consola, ni ficheros, ni red: el código viaja en el
/// prompt, como siempre. El CLI se lanza además con la lista de permitidas cerrada a estas mismas
/// (<see cref="ClaudeCodeProvider"/>), así que la restricción está por partida doble — lo que no se
/// ofrece y lo que no se permite.
/// </para>
/// </summary>
public static class AuditorTools
{
    /// <summary>El prefijo con el que el CLI de Claude Code nombra las tools de un servidor MCP.</summary>
    /// <remarks>
    /// Verificado contra el CLI real: un servidor declarado como <c>atalaya</c> con una tool
    /// <c>submit_finding</c> aparece ante el modelo como <c>mcp__atalaya__submit_finding</c>, y ése
    /// es el nombre que hay que pasarle a <c>--allowedTools</c>. Se construye aquí y no se escribe
    /// a mano en el driver para que no puedan discrepar.
    /// </remarks>
    public const string ServerName = "atalaya";

    /// <summary>El nombre cualificado de una tool, tal y como el CLI se lo enseña al modelo.</summary>
    public static string Qualified(string toolName) => $"mcp__{ServerName}__{toolName}";

    /// <summary>Las tools de una sesión de AUDITORÍA, sobre el toolbox que persiste de verdad.</summary>
    public static IReadOnlyList<McpTool> ForAudit(IAuditToolbox toolbox)
    {
        JsonObject location = Schema.Object(
            ("path", Schema.Text("Ruta del fichero, relativa a la raíz del repositorio."), true),
            ("line", Schema.Integer("Línea (1-based) donde está el defecto."), true),
            ("snippet", Schema.Text("La línea exacta, tal cual, para poder re-anclar el hallazgo."), false));

        JsonObject finding = Schema.Object(
            ("ruleId", Schema.Text("Identificador de la regla del catálogo."), true),
            ("pillar", Schema.Text("Pilar al que pertenece."), true),
            ("severity", Schema.Text("critica | alta | media | baja."), true),
            ("title", Schema.Text("Titular del defecto, una línea."), true),
            ("description", Schema.Text("Qué está mal y por qué."), true),
            ("impact", Schema.Text("Qué puede pasar si no se arregla."), true),
            ("recommendation", Schema.Text("Qué hacer para arreglarlo."), true),
            ("locations", Schema.Array(location, "Dónde ocurre. Al menos una."), true),
            ("symbol", Schema.Text(
                "El miembro que contiene el defecto (método, propiedad, campo). Ponlo SIEMPRE: es lo que "
                + "distingue dos defectos parecidos en miembros distintos. Si de verdad no está dentro de "
                + "ninguno, pon el tipo."), false));

        JsonObject verdict = Schema.Object(
            ("findingId", Schema.Text("El ULID EXACTO de la lista de existentes."), true),
            ("verdict", Schema.Text("presente | arreglado | no-es-defecto | no-verificable."), true),
            ("evidence", Schema.Text("Por qué. Obligatoria."), true));

        JsonObject suppression = Schema.Object(
            ("patternId", Schema.Text("El id EXACTO del prompt, p. ej. P-2."), true),
            ("count", Schema.Integer("Cuántas detecciones te callaste por él."), true));

        return new List<McpTool>
        {
            new(
                "submit_findings",
                "PREFERIDA. Reporta TODOS los hallazgos de la unidad en UNA sola llamada, pasando un array. "
                + "Devuelve un array de {accepted, duplicateOf, error} en el mismo orden.",
                Schema.Object(("findings", Schema.Array(finding, "Los hallazgos de esta unidad."), true)),
                args => toolbox.SubmitFindings(ReadFindings(args, "findings"))),

            new(
                "submit_finding",
                "Fallback singular. Úsala solo si por alguna razón no puedes agrupar; cada llamada añade un turno.",
                finding,
                args => toolbox.SubmitFinding(ReadFinding(args))),

            new(
                "report_verdicts",
                "OBLIGATORIA cuando la unidad tiene hallazgos existentes. Un array con un veredicto por CADA "
                + "hallazgo listado: {findingId (ULID exacto de la lista), verdict "
                + "(presente|arreglado|no-es-defecto|no-verificable), evidence}. Usa 'arreglado' SOLO si el "
                + "código cambió y por eso el problema ya no está; si lo que ocurre es que discrepas de quien "
                + "lo reportó, usa 'no-es-defecto' con tu razonamiento. Devuelve un array de {accepted, error} "
                + "en el mismo orden.",
                Schema.Object(("verdicts", Schema.Array(verdict, "Un veredicto por hallazgo existente."), true)),
                args => toolbox.ReportVerdicts(ReadVerdicts(args))),

            new(
                "add_locations",
                "Extiende un hallazgo YA existente con ubicaciones nuevas de esta misma unidad. Úsala cuando "
                + "el MISMO defecto aparece en varios sitios: un defecto sistémico es UN hallazgo con N "
                + "ubicaciones, no N hallazgos. findingId debe ser un ULID de la lista de existentes o de uno "
                + "que hayas reportado en esta unidad.",
                Schema.Object(
                    ("findingId", Schema.Text("ULID del hallazgo que se extiende."), true),
                    ("locations", Schema.Array(location, "Las ubicaciones nuevas."), true)),
                args => toolbox.AddLocations(
                    Text(args, "findingId"),
                    ReadLocations(args, "locations"))),

            new(
                "unit_done",
                "Cierra la unidad en curso con un resumen. Si el prompt trae TIPOS DE PROBLEMA SILENCIADOS y "
                + "te has callado alguna detección por uno de ellos, declara cuántas en suppressedByPattern: "
                + "un array de {patternId (el id EXACTO del prompt, p. ej. P-2), count}. Déjalo vacío si no te "
                + "has callado nada. Cuando la llames, HAS TERMINADO: no digas nada más.",
                Schema.Object(
                    ("unitPath", Schema.Text("La unidad que cierras."), true),
                    ("summary", Schema.Text("Resumen de lo que has hecho en ella."), true),
                    ("suppressedByPattern", Schema.Array(suppression, "Lo que te callaste por patrón."), false)),
                args =>
                {
                    toolbox.UnitDone(Text(args, "unitPath"), Text(args, "summary"), ReadSuppressions(args));
                    return new { ok = true };
                }),

            new(
                "read_signatures",
                "Devuelve las firmas (no cuerpos) de las dependencias directas de la unidad.",
                Schema.Object(("path", Schema.Text("Ruta de la dependencia."), true)),
                args => new { signatures = toolbox.ReadSignatures(Text(args, "path")) }),
        };
    }

    /// <summary>
    /// <b>Las mismas tools, con el ULID de vuelta</b> — el brazo `--hilo` del banco (M2).
    /// <para>
    /// Cambia UNA cosa y solo una: <c>submit_finding</c> y <c>submit_findings</c> contestan además
    /// el <c>id</c> de lo que acaban de crear. En producción no hace falta porque la pasada
    /// siguiente vuelve a listar todo lo de la unidad —incluido lo que reportó la pasada anterior—
    /// y el auditor recupera ahí el ULID. En un hilo no hay lista que reenviar: sin el id, el
    /// auditor no puede dar veredicto sobre lo que creó él, y la reconciliación de F4 —que es la
    /// variable que M2 no puede degradar— se quedaría coja por una tontería.
    /// </para>
    /// <para>
    /// <b>Nada de esto toca producción.</b> El catálogo de producción es
    /// <see cref="ForAudit"/> y sale de aquí intacto; esto es otro catálogo, que solo construye el
    /// brazo. Y si el toolbox no sabe decir qué ha creado (<see cref="ISweepCreations"/>), no se
    /// inventa: se devuelven las tools de siempre.
    /// </para>
    /// </summary>
    public static IReadOnlyList<McpTool> ForThreadedAudit(IAuditToolbox toolbox)
    {
        IReadOnlyList<McpTool> tools = ForAudit(toolbox);
        if (toolbox is not ISweepCreations creations)
        {
            return tools;
        }

        return tools
            .Select(t => t.Name switch
            {
                "submit_findings" => t with
                {
                    Description = t.Description.Replace(
                        "Devuelve un array de {accepted, duplicateOf, error} en el mismo orden.",
                        "Devuelve un array de {accepted, duplicateOf, error, id} en el mismo orden; "
                        + "el id es el ULID del hallazgo creado y es con el que luego le das veredicto.",
                        StringComparison.Ordinal),
                    Handler = WithIds(t.Handler, creations),
                },
                "submit_finding" => t with { Handler = WithIds(t.Handler, creations) },
                _ => t,
            })
            .ToList();
    }

    /// <summary>
    /// Envuelve el handler de un submit para que el resultado lleve el ULID de lo creado.
    /// <para>
    /// La correspondencia se hace por <b>orden</b> y no por título: los ULIDs nuevos aparecen en
    /// <see cref="ISweepCreations.CreatedInSweep"/> en el mismo orden en que el lote se procesó, así
    /// que el i-ésimo aceptado se casa con el i-ésimo id nuevo. Un rechazado no consume ninguno.
    /// </para>
    /// </summary>
    private static Func<JsonElement, object?> WithIds(
        Func<JsonElement, object?> inner, ISweepCreations creations)
        => args =>
        {
            int before = creations.CreatedInSweep.Count;
            object? result = inner(args);
            IReadOnlyList<string> created = creations.CreatedInSweep;

            int next = before;
            string? Take() => next < created.Count ? created[next++] : null;

            return result switch
            {
                SubmitFindingResult one => Describe(one, one.Accepted ? Take() : null),
                SubmitFindingsResult many => new
                {
                    Results = many.Results.Select(r => Describe(r, r.Accepted ? Take() : null)).ToList(),
                },
                _ => result,
            };
        };

    /// <summary>
    /// El resultado de siempre más el id. Los nombres de campo son los de PRODUCCIÓN
    /// —<c>Accepted</c>, <c>DuplicateOf</c>, <c>Error</c>: los del récord, que es lo que el
    /// servidor MCP serializa sin política de nombres— para que la única diferencia entre los dos
    /// brazos sea el id y no además el formato. Un id nulo no se enseña, igual que el servidor
    /// omite los nulos.
    /// </summary>
    private static object Describe(SubmitFindingResult r, string? id)
        => id is null
            ? new { r.Accepted, r.DuplicateOf, r.Error }
            : new { r.Accepted, r.DuplicateOf, r.Error, Id = id };

    /// <summary>La única tool de una sesión de VERIFICACIÓN.</summary>
    public static IReadOnlyList<McpTool> ForVerify(IVerifyToolbox toolbox)
        => new List<McpTool>
        {
            new(
                "submit_verdict",
                "Registra el veredicto de un hallazgo por su ULID: confirmado | resuelto | no-verificable.",
                Schema.Object(
                    ("findingUlid", Schema.Text("El ULID exacto del hallazgo."), true),
                    ("verdict", Schema.Text("confirmado | resuelto | no-verificable."), true),
                    ("evidence", Schema.Text("Por qué. Obligatoria."), true)),
                args =>
                {
                    toolbox.SubmitVerdict(
                        Text(args, "findingUlid"), Text(args, "verdict"), Text(args, "evidence"));
                    return new { ok = true };
                }),
        };

    // ---- Lectura de argumentos ------------------------------------------------------------
    //
    // Se leen a mano y con tolerancia. Un modelo manda a veces un número donde se pidió texto, o
    // se deja un campo opcional: eso NO puede tumbar la unidad. Lo que sí se respeta es lo que la
    // aplicación considera obligatorio — ahí el toolbox rechaza con su error tipado, que es quien
    // debe decidirlo, y el modelo lo lee y se corrige.

    private static string Text(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                _ => value.ToString(),
            }
            : string.Empty;

    private static int Number(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out JsonElement value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out int n) => n,
            JsonValueKind.String when int.TryParse(value.GetString(), out int n) => n,
            _ => 0,
        };
    }

    private static JsonElement[] Items(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();

    private static SubmitFindingArgs ReadFinding(JsonElement e)
        => new(
            Text(e, "ruleId"),
            Text(e, "pillar"),
            Text(e, "severity"),
            Text(e, "title"),
            Text(e, "description"),
            Text(e, "impact"),
            Text(e, "recommendation"),
            ReadLocations(e, "locations"),
            Text(e, "symbol") is { Length: > 0 } symbol ? symbol : null);

    private static SubmitFindingArgs[] ReadFindings(JsonElement args, string name)
        => Items(args, name).Select(ReadFinding).ToArray();

    private static SubmitLocation[] ReadLocations(JsonElement args, string name)
        => Items(args, name)
            .Select(e => new SubmitLocation(
                Text(e, "path"),
                Number(e, "line"),
                Text(e, "snippet") is { Length: > 0 } snippet ? snippet : null))
            .ToArray();

    private static VerdictArgs[] ReadVerdicts(JsonElement args)
        => Items(args, "verdicts")
            .Select(e => new VerdictArgs(Text(e, "findingId"), Text(e, "verdict"), Text(e, "evidence")))
            .ToArray();

    private static SuppressedByPatternArgs[] ReadSuppressions(JsonElement args)
        => Items(args, "suppressedByPattern")
            .Select(e => new SuppressedByPatternArgs(Text(e, "patternId"), Number(e, "count")))
            .ToArray();
}
