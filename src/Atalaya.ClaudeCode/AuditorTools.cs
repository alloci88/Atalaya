using System.Text.Json;
using System.Text.Json.Nodes;
using Atalaya.Agents;

namespace Atalaya.ClaudeCode;

/// <summary>
/// El catálogo de herramientas del auditor, traducido a MCP (F14).
/// <para>
/// <b>Los nombres y las descripciones son LOS MISMOS que ve Copilot</b>, palabra por palabra, y
/// desde PROV-2 §3 lo son porque salen de la MISMA constante: <see cref="AuditToolText"/>, en
/// <c>Atalaya.Agents</c>. No es pulcritud: el prompt de la unidad —que es el mismo para los dos
/// proveedores— nombra estas tools y explica cuándo usar cada una. Si aquí se llamaran distinto, o
/// si la descripción dijera algo distinto, el mismo prompt significaría dos cosas y las dos casas
/// no serían comparables. Toda la gracia de tener un segundo auditor es que discrepen sobre el
/// CÓDIGO, no sobre las instrucciones — y con el texto copiado a mano ya había divergido
/// <c>unit_done</c>.
/// </para>
/// <para>
/// <b>Lo único que este catálogo añade de suyo es la TERMINALIDAD</b>, y no como texto distinto:
/// como marca (<c>McpTool.IsTerminal</c>), leída de la misma fuente. MCP no tiene el concepto que
/// el SDK de Copilot sí tiene, así que el transporte la traduce a palabras al publicar el catálogo
/// (<see cref="McpTerminal"/>) — las mismas palabras para cualquier terminal.
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

    /// <summary>
    /// Las tools de una sesión de AUDITORÍA, sobre el toolbox que persiste de verdad.
    /// <para>
    /// <b>Los dos <c>submit</c> devuelven el ULID de lo que crean</b> (F25 §4, D-916). Nació en el
    /// brazo de medida de M2 y ahora es producción, porque en un hilo es la ÚNICA forma de que el
    /// auditor pueda pronunciarse sobre lo que él mismo reportó: no hay pasada siguiente que le
    /// vuelva a listar la unidad entera. Y de paso hace alcanzable la rama que <c>add_locations</c>
    /// tenía desde F4.1 para «un ULID que hayas reportado en esta unidad», que hasta hoy no se podía
    /// usar dentro de la pasada que lo creó porque nadie le decía el nombre.
    /// </para>
    /// </summary>
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

        var tools = new List<McpTool>
        {
            new(
                AuditToolText.SubmitFindings,
                AuditToolText.SubmitFindingsDescription,
                Schema.Object(("findings", Schema.Array(finding, "Los hallazgos de esta unidad."), true)),
                args => toolbox.SubmitFindings(ReadFindings(args, "findings"))),

            new(
                AuditToolText.SubmitFinding,
                AuditToolText.SubmitFindingDescription,
                finding,
                args => toolbox.SubmitFinding(ReadFinding(args))),

            new(
                AuditToolText.ReportVerdicts,
                AuditToolText.ReportVerdictsDescription,
                Schema.Object(("verdicts", Schema.Array(verdict, "Un veredicto por hallazgo existente."), true)),
                args => toolbox.ReportVerdicts(ReadVerdicts(args))),

            new(
                AuditToolText.AddLocations,
                AuditToolText.AddLocationsDescription,
                Schema.Object(
                    ("findingId", Schema.Text("ULID del hallazgo que se extiende."), true),
                    ("locations", Schema.Array(location, "Las ubicaciones nuevas."), true)),
                args => toolbox.AddLocations(
                    Text(args, "findingId"),
                    ReadLocations(args, "locations"))),

            new(
                AuditToolText.UnitDone,
                AuditToolText.UnitDoneDescription,
                Schema.Object(
                    ("unitPath", Schema.Text("La unidad que cierras."), true),
                    ("summary", Schema.Text("Resumen de lo que has hecho en ella."), true),
                    ("suppressedByPattern", Schema.Array(suppression, "Lo que te callaste por patrón."), false)),
                args =>
                {
                    toolbox.UnitDone(Text(args, "unitPath"), Text(args, "summary"), ReadSuppressions(args));
                    return new { ok = true };
                },
                // Y es TERMINAL. La marca sale del catálogo compartido, no de un `true` escrito
                // aquí: la misma herramienta no puede cerrar el turno en una casa y no en la otra.
                IsTerminal: AuditToolText.IsTerminal(AuditToolText.UnitDone)),

            new(
                AuditToolText.ReadSignatures,
                AuditToolText.ReadSignaturesDescription,
                Schema.Object(("path", Schema.Text("Ruta de la dependencia."), true)),
                args => new { signatures = toolbox.ReadSignatures(Text(args, "path")) }),
        };

        // Y si el toolbox no sabe decir qué ha creado, no se inventa: van las tools tal cual y el
        // resultado de los `submit` es el de antes de F25, sin id.
        return toolbox is ISweepCreations creations ? WithCreatedIds(tools, creations) : tools;
    }

    /// <summary>
    /// Los dos <c>submit</c>, contestando además el ULID de lo que acaban de crear. El casado
    /// —por orden, y un rechazado no consume ninguno— vive en <see cref="SweepReceipts"/>, que es
    /// el mismo que usa Copilot: dos copias de ese criterio serían dos auditorías distintas.
    /// </summary>
    private static IReadOnlyList<McpTool> WithCreatedIds(
        IReadOnlyList<McpTool> tools, ISweepCreations creations)
        => tools
            .Select(t => t.Name is AuditToolText.SubmitFindings or AuditToolText.SubmitFinding
                ? t with { Handler = WithIds(t.Handler, creations) }
                : t)
            .ToList();

    private static Func<JsonElement, object?> WithIds(
        Func<JsonElement, object?> inner, ISweepCreations creations)
        => args =>
        {
            int before = SweepReceipts.Mark(creations);
            return inner(args) switch
            {
                SubmitFindingResult one => SweepReceipts.Of(creations, one, before),
                SubmitFindingsResult many => SweepReceipts.Of(creations, many, before),
                var other => other,
            };
        };

    /// <summary>La única tool de una sesión de VERIFICACIÓN.</summary>
    public static IReadOnlyList<McpTool> ForVerify(IVerifyToolbox toolbox)
        => new List<McpTool>
        {
            new(
                AuditToolText.SubmitVerdict,
                AuditToolText.SubmitVerdictDescription,
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
