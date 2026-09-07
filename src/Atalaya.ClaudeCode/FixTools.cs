using System.Text.Json;
using System.Text.Json.Nodes;
using Atalaya.Agents;

namespace Atalaya.ClaudeCode;

/// <summary>
/// El catálogo de una sesión de arreglo, con la única cosa que el driver necesita saber de él:
/// si el agente ya cerró.
/// </summary>
/// <remarks>
/// <c>fix_done</c> es TERMINAL —en Copilot lo declara el SDK— y aquí no hay quien lo declare: MCP
/// no tiene el concepto. Así que el driver lo mira: en cuanto la tool se llama, al agente no se le
/// da otro turno. Sin esto, una conversación seguiría pidiéndole al usuario qué decir después de
/// que el arreglo estuviera cerrado.
/// </remarks>
public sealed class FixToolSet
{
    internal FixToolSet(IReadOnlyList<McpTool> tools) => Tools = tools;

    public IReadOnlyList<McpTool> Tools { get; }

    /// <summary>El agente ha llamado a <c>fix_done</c>.</summary>
    public bool Closed { get; internal set; }
}

/// <summary>
/// El catálogo de herramientas del ARREGLO ASISTIDO, traducido a MCP (F16).
/// <para>
/// <b>Las mismas cuatro que ve Copilot, palabra por palabra</b>, y no por copia: los nombres y las
/// descripciones salen de <see cref="FixToolText"/>, que las comparten los dos drivers. El encargo
/// de la sesión —<c>FixSessionPrompt</c>— es el mismo para las dos casas y nombra estas tools; si
/// aquí se llamaran distinto, el mismo encargo significaría dos cosas.
/// </para>
/// <para>
/// <b>Y una quinta que en Copilot no hace falta declarar.</b> Allí <c>ask_user</c> la pone el
/// runtime y desemboca en <c>OnUserInputRequest</c>; aquí el CLI se lanza sin ninguna herramienta
/// propia (<c>--tools ""</c>), así que la pregunta al usuario también la sirve Atalaya. Mismo
/// nombre, mismos argumentos, misma tarjeta en la conversación: lo que cambia es el transporte, no
/// el contrato.
/// </para>
/// <para>
/// <b>Ninguna de las cinco toca el disco por su cuenta.</b> Todo pasa por el
/// <see cref="IFixToolbox"/> de la aplicación, que es quien lleva el presupuesto de lecturas, la
/// copia de seguridad previa a la primera edición, el permiso fichero a fichero y la pausa. El
/// driver solo transporta.
/// </para>
/// </summary>
public static class FixTools
{
    /// <summary>
    /// Las cinco tools de una sesión de arreglo, sobre el toolbox y el canal de preguntas de la
    /// aplicación.
    /// </summary>
    /// <param name="ct">
    /// La cancelación de la sesión. Va aquí porque <c>ask_user</c> espera a una PERSONA: sin ella,
    /// pulsar «Detener» dejaría al modelo esperando una respuesta que ya no va a llegar nunca.
    /// </param>
    public static FixToolSet ForFix(
        IFixToolbox toolbox, IUserQuestions questions, CancellationToken ct)
    {
        FixToolSet? set = null;

        JsonObject edit = Schema.Object(
            ("oldText", Schema.Text("El texto EXACTO que hay que encontrar. Vacío = crear el fichero."), true),
            ("newText", Schema.Text("Lo que lo sustituye. Vacío = borrar ese fragmento."), true),
            ("replaceAll", Schema.Boolean("Permite que el fragmento aparezca varias veces."), false));

        var tools = new List<McpTool>
        {
            new(
                FixToolText.ReadFile,
                FixToolText.ReadFileDescription,
                Schema.Object(
                    ("path", Schema.Text("Ruta del fichero, relativa a la raíz del clon."), true),
                    ("startLine", Schema.Integer("Primera línea a devolver, base 1. Omítelo para empezar por el principio."), false),
                    ("endLine", Schema.Integer("Última línea a devolver, base 1. Omítelo para leer hasta donde quepa."), false)),
                args => toolbox.ReadFile(
                    Text(args, "path"), Number(args, "startLine"), Number(args, "endLine"))),

            new(
                FixToolText.ApplyEdit,
                FixToolText.ApplyEditDescription,
                Schema.Object(
                    ("path", Schema.Text("Ruta del fichero, relativa a la raíz del clon."), true),
                    ("reason", Schema.Text("Por qué tocas ESTE fichero, en una frase."), true),
                    ("edits", Schema.Array(edit, "Las ediciones que hay que aplicar."), true)),
                args => toolbox.ApplyEdit(
                    Text(args, "path"), Text(args, "reason"), ReadEdits(args))),

            new(
                FixToolText.RunBuildAndTests,
                FixToolText.RunBuildAndTestsDescription,
                // Sin argumentos, y es una salvaguarda y no una comodidad (D-548): lo que se
                // ejecuta lo decide Atalaya. Una tool que aceptara un comando sería una shell.
                Schema.Object(),
                _ => toolbox.RunBuildAndTests()),

            new(
                FixToolText.FixDone,
                FixToolText.FixDoneDescription,
                Schema.Object(
                    ("summary", Schema.Text("Qué cambiaste y por qué, con los ficheros tocados."), true),
                    ("commitTitle", Schema.Text("≤72 caracteres, imperativo, con el identificador del hallazgo."), true),
                    ("commitDescription", Schema.Text("El qué y el porqué del cambio."), true),
                    ("risks", Schema.Text("Lo que queda pendiente de revisión humana, o vacío."), false)),
                args =>
                {
                    toolbox.FixDone(new FixDoneArgs(
                        Text(args, "summary"),
                        Text(args, "commitTitle"),
                        Text(args, "commitDescription"),
                        Text(args, "risks") is { Length: > 0 } risks ? risks : null));
                    set!.Closed = true;
                    return new { ok = true };
                }),

            new(
                FixToolText.AskUser,
                FixToolText.AskUserDescription,
                Schema.Object(
                    ("question", Schema.Text("La pregunta, con la consecuencia concreta de cada opción."), true),
                    ("choices", Schema.Array(Schema.Text("Una opción."), "Opciones cerradas; vacío = respuesta libre."), false),
                    ("allowFreeform", Schema.Boolean("El usuario puede escribir algo que no está en la lista."), false)),
                args =>
                {
                    IReadOnlyList<string> choices = ReadChoices(args);
                    bool free = Flag(args, "allowFreeform") ?? choices.Count == 0;

                    // Se espera a una persona, y eso puede tardar minutos. Bloquear aquí es
                    // correcto —el modelo está esperando el resultado de SU tool— y no calla al
                    // servidor MCP: las llamadas se atienden en paralelo (ver AtalayaMcpServer).
                    string? answer = questions
                        .AskAsync(Text(args, "question"), choices, free, ct)
                        .GetAwaiter()
                        .GetResult();

                    // Una pregunta sin respuesta NO se contesta con una cadena vacía que el modelo
                    // leería como «me da igual»: se dice que la sesión se está cerrando.
                    return answer is null
                        ? new { answer = "La sesión se ha detenido y el usuario no va a contestar. Cierra con fix_done o para." }
                        : new { answer };
                }),
        };

        set = new FixToolSet(tools);
        return set;
    }

    // ---- Lectura de argumentos ---------------------------------------------------------------
    //
    // Mismo criterio que en el catálogo de auditoría: tolerante en la forma, estricto en el fondo.
    // Un modelo que manda un número donde se pidió texto no puede tumbar la sesión; lo que sí
    // rechaza —un fragmento ambiguo, una ruta fuera del clon— lo rechaza el toolbox, que es quien
    // debe decidirlo, y su error viaja de vuelta para que el modelo se corrija.

    private static FixEdit[] ReadEdits(JsonElement args)
        => Items(args, "edits")
            .Select(e => new FixEdit(Text(e, "oldText"), Text(e, "newText"), Flag(e, "replaceAll") ?? false))
            .ToArray();

    private static IReadOnlyList<string> ReadChoices(JsonElement args)
        => Items(args, "choices")
            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : e.ToString())
            .Where(s => s.Length > 0)
            .ToList();

    private static string Text(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                _ => value.ToString(),
            }
            : string.Empty;

    /// <summary>
    /// Un entero opcional. Tolerante en la forma —un modelo que manda <c>"185"</c> con comillas no
    /// puede tumbar la lectura— y <c>null</c> cuando no viene o no es un número: ahí el toolbox
    /// hace lo de siempre, que es leer desde el principio.
    /// </summary>
    private static int? Number(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out int n) => n,
            JsonValueKind.String when int.TryParse(value.GetString(), out int n) => n,
            _ => null,
        };
    }

    private static bool? Flag(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out bool b) => b,
            _ => null,
        };
    }

    private static JsonElement[] Items(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();
}
