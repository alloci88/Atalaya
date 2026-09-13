using System.Text.Json;
using System.Text.Json.Nodes;
using Atalaya.Agents;

namespace Atalaya.OpenAI;

/// <summary>
/// Una herramienta de este dialecto: lo que se le manda al endpoint y lo que hace la aplicación
/// cuando el modelo la llama.
/// </summary>
/// <param name="Spec">Nombre, descripción y esquema, tal y como viajan en <c>tools</c>.</param>
/// <param name="Handler">
/// Qué hace la aplicación. Recibe los argumentos ya parseados y devuelve el objeto que se le
/// contesta al modelo; <b>lanzar aquí se convierte en el contenido de un mensaje <c>tool</c></b>
/// con el texto del error, nunca en una excepción que tumbe la pasada.
/// </param>
/// <param name="IsTerminal">Llamarla es haber terminado. Sale de <c>AuditToolText.Terminal</c>.</param>
public sealed record ChatFunction(ChatToolSpec Spec, Func<JsonElement, object?> Handler, bool IsTerminal = false)
{
    public string Name => Spec.Name;
}

/// <summary>
/// <b>El catálogo del auditor traducido a <c>functions</c></b> (PROV-3 §4).
/// <para>
/// <b>El nombre y la descripción no se escriben aquí.</b> Salen de <see cref="AuditToolText"/>, en
/// <c>Atalaya.Agents</c>, que es la misma constante que leen el driver de Copilot y el de Claude
/// Code (PROV-2 §3). Lo que esta clase hace es una traducción de FORMA: el mismo texto metido en
/// la caja que este dialecto entiende. Si aquí se reescribiera una descripción, el prompt de
/// unidad —que es el mismo para las tres casas— significaría tres cosas y una discrepancia entre
/// proveedores dejaría de poder atribuirse al modelo.
/// </para>
/// <para>
/// <b>El ESQUEMA sí es de aquí</b>, porque cada protocolo pide el suyo, y sigue el criterio del
/// catálogo MCP: los campos que la aplicación exige van en <c>required</c>, los opcionales no, y
/// las piezas que se repiten —la ubicación, el hallazgo— se escriben una vez y se clonan al
/// insertarlas (un <c>JsonNode</c> solo admite un padre, y reutilizarlo revienta al MONTAR el
/// catálogo, o sea antes de la primera llamada al modelo).
/// </para>
/// <para>
/// <b>La terminalidad se dice con palabras, como en MCP.</b> El SDK de Copilot tiene el concepto
/// (<c>IsTerminal</c>); <c>chat/completions</c> no lo tiene —una <c>function</c> es nombre,
/// descripción y esquema y nada más—, así que la marca se traduce a texto al publicar el catálogo,
/// aquí y una sola vez (<see cref="TerminalSuffix"/>), y no se mete en la descripción compartida.
/// </para>
/// </summary>
public static class AuditorFunctions
{
    /// <summary>
    /// Lo que se le añade a una terminal. Con el espacio delante: se pega a la descripción.
    /// <para>
    /// Son las mismas palabras que usa el transporte de MCP, y están escritas otra vez en vez de
    /// referenciadas porque <c>Atalaya.OpenAI</c> no conoce a <c>Atalaya.ClaudeCode</c> ni debe:
    /// dos casas no se referencian entre sí. Lo que NO puede divergir —el nombre y la descripción
    /// de la herramienta— no vive en ninguna de las dos.
    /// </para>
    /// </summary>
    public const string TerminalSuffix = " Cuando la llames, HAS TERMINADO: no digas nada más.";

    /// <summary>
    /// Cómo se le contesta al modelo. <b>Las mismas opciones que el servidor MCP</b>: los nulos no
    /// viajan y los nombres salen tal cual del tipo. No es pulcritud — es que las dos casas tienen
    /// que enseñarle al modelo el MISMO recibo de la misma llamada, o una comparación entre
    /// proveedores estaría midiendo el serializador.
    /// </summary>
    internal static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Las SEIS de una sesión de auditoría, en el orden del catálogo (D-883): <c>unit_done</c> va
    /// la penúltima porque ése es el orden que el prompt nombra, y no se toca aquí.
    /// <para>
    /// Los dos <c>submit</c> devuelven el ULID de lo que crean cuando el toolbox sabe decirlo
    /// (F25 §4, D-916). El casado es <see cref="SweepReceipts"/>, el mismo de las otras dos casas:
    /// dos copias de ese criterio serían dos auditorías distintas.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ChatFunction> ForAudit(IAuditToolbox toolbox)
    {
        JsonObject location = Esquema.Objeto(
            ("path", Esquema.Texto("Ruta del fichero, relativa a la raíz del repositorio."), true),
            ("line", Esquema.Entero("Línea (1-based) donde está el defecto."), true),
            ("snippet", Esquema.Texto("La línea exacta, tal cual, para poder re-anclar el hallazgo."), false));

        JsonObject finding = Esquema.Objeto(
            ("ruleId", Esquema.Texto("Identificador de la regla del catálogo."), true),
            ("pillar", Esquema.Texto("Pilar al que pertenece."), true),
            ("severity", Esquema.Texto("critica | alta | media | baja."), true),
            ("title", Esquema.Texto("Titular del defecto, una línea."), true),
            ("description", Esquema.Texto("Qué está mal y por qué."), true),
            ("impact", Esquema.Texto("Qué puede pasar si no se arregla."), true),
            ("recommendation", Esquema.Texto("Qué hacer para arreglarlo."), true),
            ("locations", Esquema.Lista(location, "Dónde ocurre. Al menos una."), true),
            ("symbol", Esquema.Texto(
                "El miembro que contiene el defecto (método, propiedad, campo). Ponlo SIEMPRE: es lo que "
                + "distingue dos defectos parecidos en miembros distintos. Si de verdad no está dentro de "
                + "ninguno, pon el tipo."), false));

        JsonObject verdict = Esquema.Objeto(
            ("findingId", Esquema.Texto("El ULID EXACTO de la lista de existentes."), true),
            ("verdict", Esquema.Texto("presente | arreglado | no-es-defecto | no-verificable."), true),
            ("evidence", Esquema.Texto("Por qué. Obligatoria."), true));

        JsonObject suppression = Esquema.Objeto(
            ("patternId", Esquema.Texto("El id EXACTO del prompt, p. ej. P-2."), true),
            ("count", Esquema.Entero("Cuántas detecciones te callaste por él."), true));

        // Si el toolbox no sabe decir qué ha creado, no se inventa: los `submit` contestan lo de
        // antes de F25, sin id. Es exactamente lo que hace el catálogo MCP.
        var creations = toolbox as ISweepCreations;

        var tools = new List<ChatFunction>
        {
            Funcion(
                AuditToolText.SubmitFindings,
                Esquema.Objeto(("findings", Esquema.Lista(finding, "Los hallazgos de esta unidad."), true)),
                args =>
                {
                    int before = SweepReceipts.Mark(creations);
                    return SweepReceipts.Of(
                        creations, toolbox.SubmitFindings(LeerHallazgos(args, "findings")), before);
                }),

            Funcion(
                AuditToolText.SubmitFinding,
                finding,
                args =>
                {
                    int before = SweepReceipts.Mark(creations);
                    return SweepReceipts.Of(creations, toolbox.SubmitFinding(LeerHallazgo(args)), before);
                }),

            Funcion(
                AuditToolText.ReportVerdicts,
                Esquema.Objeto(("verdicts", Esquema.Lista(verdict, "Un veredicto por hallazgo existente."), true)),
                args => toolbox.ReportVerdicts(LeerVeredictos(args))),

            Funcion(
                AuditToolText.AddLocations,
                Esquema.Objeto(
                    ("findingId", Esquema.Texto("ULID del hallazgo que se extiende."), true),
                    ("locations", Esquema.Lista(location, "Las ubicaciones nuevas."), true)),
                args => toolbox.AddLocations(Texto(args, "findingId"), LeerUbicaciones(args, "locations"))),

            Funcion(
                AuditToolText.UnitDone,
                Esquema.Objeto(
                    ("unitPath", Esquema.Texto("La unidad que cierras."), true),
                    ("summary", Esquema.Texto("Resumen de lo que has hecho en ella."), true),
                    ("suppressedByPattern", Esquema.Lista(suppression, "Lo que te callaste por patrón."), false)),
                args =>
                {
                    toolbox.UnitDone(Texto(args, "unitPath"), Texto(args, "summary"), LeerSupresiones(args));
                    return new { ok = true };
                },
                // La marca sale del catálogo compartido y no de un `true` escrito aquí: la misma
                // herramienta no puede cerrar el turno en una casa y no en la otra.
                terminal: AuditToolText.IsTerminal(AuditToolText.UnitDone)),

            Funcion(
                AuditToolText.ReadSignatures,
                Esquema.Objeto(("path", Esquema.Texto("Ruta de la dependencia."), true)),
                args => new { signatures = toolbox.ReadSignatures(Texto(args, "path")) }),
        };

        return tools;
    }

    /// <summary>La única de una sesión de VERIFICACIÓN. Ninguna es terminal: aquí no se cierra unidad.</summary>
    public static IReadOnlyList<ChatFunction> ForVerify(IVerifyToolbox toolbox)
        => new List<ChatFunction>
        {
            Funcion(
                AuditToolText.SubmitVerdict,
                Esquema.Objeto(
                    ("findingUlid", Esquema.Texto("El ULID exacto del hallazgo."), true),
                    ("verdict", Esquema.Texto("confirmado | resuelto | no-verificable."), true),
                    ("evidence", Esquema.Texto("Por qué. Obligatoria."), true)),
                args =>
                {
                    toolbox.SubmitVerdict(
                        Texto(args, "findingUlid"), Texto(args, "verdict"), Texto(args, "evidence"));
                    return new { ok = true };
                }),
        };

    /// <summary>
    /// <b>Ejecuta una llamada y devuelve lo que va en el mensaje <c>tool</c></b>.
    /// <para>
    /// Nunca lanza, y es deliberado por dos motivos distintos. Uno: la aplicación rechaza payloads
    /// inválidos con un error tipado —un ULID que no está en la lista, una severidad inventada— y
    /// eso es NORMAL; el modelo tiene que poder leerlo y corregirse, así que viaja como resultado.
    /// Dos: un nombre que no existe se contesta también como resultado y no como excepción, porque
    /// una pasada entera no se puede perder porque el modelo se inventara una herramienta.
    /// </para>
    /// <para>
    /// Las <b>guardas de dominio salen gratis</b>: ejecutar es llamar al <c>IAuditToolbox</c> de la
    /// aplicación, que ya trae las suyas —el ULID acotado a la unidad, las ubicaciones dentro de
    /// ella, el casado de <c>SweepReceipts</c>—. Aquí no se reimplementa ninguna.
    /// </para>
    /// </summary>
    public static string Execute(IReadOnlyDictionary<string, ChatFunction> tools, ChatToolCall call)
    {
        if (!tools.TryGetValue(call.Name, out ChatFunction? tool))
        {
            return JsonSerializer.Serialize(
                new { accepted = false, error = $"No existe la herramienta «{call.Name}»." }, Json);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            return JsonSerializer.Serialize(tool.Handler(doc.RootElement), Json);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { accepted = false, error = ex.Message }, Json);
        }
    }

    /// <summary>
    /// <b>Cuántos elementos trae la llamada</b>, para el hilo de actividad: los hallazgos de un
    /// <c>submit_findings</c>, los veredictos de un <c>report_verdicts</c>, las ubicaciones de un
    /// <c>add_locations</c>. Cero cuando la herramienta no lleva lista.
    /// <para>
    /// Aquí los argumentos llegan YA completos —los deltas los ensambla el transporte—, así que
    /// esto es un dato medido y no una estimación de progreso. El total sí se sabe; lo que no se
    /// puede decir, y no se dice, es cuántos quedan por llegar mientras llegan (F30 §2).
    /// </para>
    /// </summary>
    public static int CountItems(ChatToolCall call)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Array)
                {
                    return p.Value.GetArrayLength();
                }
            }
        }
        catch (JsonException)
        {
            // Argumentos que no son JSON: el modelo se equivocó y lo dirá el ejecutor, no esto.
        }

        return 0;
    }

    private static ChatFunction Funcion(
        string name, JsonObject parameters, Func<JsonElement, object?> handler, bool terminal = false)
        => new(
            new ChatToolSpec(
                name,
                terminal ? AuditToolText.DescriptionOf(name) + TerminalSuffix : AuditToolText.DescriptionOf(name),
                parameters.ToJsonString()),
            handler,
            terminal);

    // ---- El esquema, a mano ---------------------------------------------------------------
    //
    // No pretende ser un generador: los esquemas de las siete son fijos y se leen mejor escritos
    // que derivados. Lo único que tiene de listo es el clonado al insertar, por lo de siempre.

    private static class Esquema
    {
        public static JsonObject Objeto(params (string Nombre, JsonNode Esquema, bool Obligatorio)[] propiedades)
        {
            var props = new JsonObject();
            var required = new JsonArray();
            foreach ((string nombre, JsonNode esquema, bool obligatorio) in propiedades)
            {
                // Clonado: un JsonNode solo admite un padre, y la ubicación se reutiliza en
                // `submit_finding` y en `add_locations` — que es lo natural, es la misma forma.
                props[nombre] = esquema.DeepClone();
                if (obligatorio)
                {
                    required.Add(nombre);
                }
            }

            var result = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = props,
            };

            if (required.Count > 0)
            {
                result["required"] = required;
            }

            return result;
        }

        public static JsonObject Texto(string descripcion)
            => new() { ["type"] = "string", ["description"] = descripcion };

        public static JsonObject Entero(string descripcion)
            => new() { ["type"] = "integer", ["description"] = descripcion };

        public static JsonObject Lista(JsonNode items, string descripcion)
            => new() { ["type"] = "array", ["items"] = items.DeepClone(), ["description"] = descripcion };
    }

    // ---- Lectura de argumentos ------------------------------------------------------------
    //
    // Con tolerancia, y por el mismo motivo que en el catálogo MCP: un modelo manda a veces un
    // número donde se pidió texto, o se deja un opcional, y eso NO puede tumbar la unidad. Lo que
    // sí se respeta es lo que la aplicación considera obligatorio — ahí el toolbox rechaza con su
    // error tipado, que es quien debe decidirlo, y el modelo lo lee y se corrige.

    private static string Texto(JsonElement args, string nombre)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(nombre, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                _ => value.ToString(),
            }
            : string.Empty;

    private static int Numero(JsonElement args, string nombre)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(nombre, out JsonElement value))
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

    private static JsonElement[] Elementos(JsonElement args, string nombre)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(nombre, out JsonElement value)
           && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();

    private static SubmitFindingArgs LeerHallazgo(JsonElement e)
        => new(
            Texto(e, "ruleId"),
            Texto(e, "pillar"),
            Texto(e, "severity"),
            Texto(e, "title"),
            Texto(e, "description"),
            Texto(e, "impact"),
            Texto(e, "recommendation"),
            LeerUbicaciones(e, "locations"),
            Texto(e, "symbol") is { Length: > 0 } symbol ? symbol : null);

    private static SubmitFindingArgs[] LeerHallazgos(JsonElement args, string nombre)
        => Elementos(args, nombre).Select(LeerHallazgo).ToArray();

    private static SubmitLocation[] LeerUbicaciones(JsonElement args, string nombre)
        => Elementos(args, nombre)
            .Select(e => new SubmitLocation(
                Texto(e, "path"),
                Numero(e, "line"),
                Texto(e, "snippet") is { Length: > 0 } snippet ? snippet : null))
            .ToArray();

    private static VerdictArgs[] LeerVeredictos(JsonElement args)
        => Elementos(args, "verdicts")
            .Select(e => new VerdictArgs(Texto(e, "findingId"), Texto(e, "verdict"), Texto(e, "evidence")))
            .ToArray();

    private static SuppressedByPatternArgs[] LeerSupresiones(JsonElement args)
        => Elementos(args, "suppressedByPattern")
            .Select(e => new SuppressedByPatternArgs(Texto(e, "patternId"), Numero(e, "count")))
            .ToArray();
}
