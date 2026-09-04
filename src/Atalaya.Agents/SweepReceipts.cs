namespace Atalaya.Agents;

/// <summary>
/// Lo que <c>submit_finding</c> contesta desde F25: lo de siempre <b>más el ULID de lo que acaba de
/// crear</b> (D-916).
/// <para>
/// <b>Por qué hacía falta.</b> Hasta aquí la respuesta era <c>{accepted, duplicateOf, error}</c> y
/// no había ninguna otra puerta por la que el auditor aprendiera el identificador de lo que él mismo
/// había reportado. En el barrido de una petición por pasada eso no se notaba: la pasada siguiente
/// volvía a listarle la unidad entera, con lo suyo incluido, y el ULID llegaba por el prompt. En un
/// hilo no hay lista que reenviar, así que sin esto el auditor no puede pronunciarse sobre lo que
/// creó y la reconciliación de F4 se queda coja por una tontería.
/// </para>
/// <para>
/// Y de paso hace alcanzable una rama que llevaba sin poder usarse desde F4.1: <c>add_locations</c>
/// acepta —y se lo dice al modelo— un <c>findingId</c> «que hayas reportado en esta unidad», y
/// <c>SessionToolbox.AddLocations</c> tiene la rama puesta desde D-090. Dentro de la pasada que lo
/// creaba era inalcanzable, porque el modelo no puede nombrar un ULID que nadie le ha dicho.
/// </para>
/// </summary>
/// <param name="Id">
/// El ULID del hallazgo creado. <b>Null cuando no se creó nada</b>: un rechazo o un duplicado no
/// crea ninguno, y fingirle un id sería peor que dejarlo vacío.
/// </param>
public sealed record SubmitFindingReceipt(
    bool Accepted, string? DuplicateOf = null, string? Error = null, string? Id = null);

/// <summary>El lote, con un recibo por hallazgo y en el mismo orden.</summary>
public sealed record SubmitFindingsReceipt(IReadOnlyList<SubmitFindingReceipt> Results);

/// <summary>
/// <b>Casar lo aceptado con lo creado</b>, en un solo sitio para los dos proveedores.
/// <para>
/// La correspondencia es por <b>orden</b> y no por título: los ULIDs nuevos aparecen en
/// <see cref="ISweepCreations.CreatedInSweep"/> en el mismo orden en que el lote se procesó, así que
/// el i-ésimo aceptado se casa con el i-ésimo id nuevo. <b>Un rechazado no consume ninguno.</b>
/// </para>
/// <para>
/// Vive en <c>Atalaya.Agents</c> y no en un driver porque la regla tiene que ser la misma en las dos
/// casas: el prompt es el mismo para las dos y nombra el id como la forma de dar veredicto sobre lo
/// propio. Dos copias de este criterio serían dos auditorías distintas.
/// </para>
/// </summary>
public static class SweepReceipts
{
    /// <summary>Un <c>submit_finding</c>, con el ULID de lo que haya creado.</summary>
    public static SubmitFindingReceipt Of(ISweepCreations? creations, SubmitFindingResult result, int before)
    {
        int next = before;
        return Receipt(creations, result, ref next);
    }

    /// <summary>Un <c>submit_findings</c> entero, casando en orden.</summary>
    public static SubmitFindingsReceipt Of(
        ISweepCreations? creations, SubmitFindingsResult result, int before)
    {
        int next = before;
        var receipts = new List<SubmitFindingReceipt>(result.Results.Count);
        foreach (SubmitFindingResult one in result.Results)
        {
            receipts.Add(Receipt(creations, one, ref next));
        }

        return new SubmitFindingsReceipt(receipts);
    }

    /// <summary>Cuántos ULIDs había creados ANTES de la llamada. Es el punto de partida del casado.</summary>
    public static int Mark(ISweepCreations? creations) => creations?.CreatedInSweep.Count ?? 0;

    private static SubmitFindingReceipt Receipt(
        ISweepCreations? creations, SubmitFindingResult result, ref int next)
    {
        string? id = null;
        if (creations is not null && result.Accepted)
        {
            IReadOnlyList<string> created = creations.CreatedInSweep;
            if (next < created.Count)
            {
                id = created[next++];
            }
        }

        return new SubmitFindingReceipt(result.Accepted, result.DuplicateOf, result.Error, id);
    }
}
