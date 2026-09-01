using System.Text.Json;
using System.Text.Json.Nodes;

namespace Atalaya.ClaudeCode;

/// <summary>
/// Una herramienta tal y como la ve el auditor por MCP: nombre, para qué sirve, qué argumentos
/// admite y qué hace la aplicación cuando la llama.
/// </summary>
/// <param name="Name">
/// El nombre EXACTO que ve el modelo. Es el mismo que ve Copilot (<c>submit_finding</c>,
/// <c>report_verdicts</c>…): el vocabulario del auditor no puede depender de la casa, o el mismo
/// prompt significaría cosas distintas según quién lo lea.
/// </param>
/// <param name="Description">La descripción, palabra por palabra la misma que la de Copilot.</param>
/// <param name="InputSchema">JSON Schema de los argumentos. Lo que MCP llama <c>inputSchema</c>.</param>
/// <param name="Handler">
/// Qué hace la aplicación. Recibe los argumentos ya parseados y devuelve el objeto que se le
/// contesta al modelo; lanzar aquí se convierte en un <c>isError</c> con el texto de la excepción,
/// nunca en una tubería rota.
/// </param>
public sealed record McpTool(
    string Name,
    string Description,
    JsonNode InputSchema,
    Func<JsonElement, object?> Handler);

/// <summary>
/// Ayudas para escribir esquemas JSON a mano sin que el fichero se convierta en una pared de
/// llaves. No pretende ser un generador: los esquemas de las seis tools son fijos y se leen mejor
/// escritos que derivados.
/// </summary>
internal static class Schema
{
    public static JsonObject Object(params (string Name, JsonNode Schema, bool Required)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach ((string name, JsonNode schema, bool isRequired) in properties)
        {
            props[name] = schema;
            if (isRequired)
            {
                required.Add(name);
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

    public static JsonObject Text(string description)
        => new() { ["type"] = "string", ["description"] = description };

    public static JsonObject Integer(string description)
        => new() { ["type"] = "integer", ["description"] = description };

    public static JsonObject Array(JsonNode items, string description)
        => new() { ["type"] = "array", ["items"] = items, ["description"] = description };
}
