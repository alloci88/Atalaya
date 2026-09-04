namespace Atalaya.Domain.Model;

/// <summary>
/// Las tarifas con las que nace la tabla de una organización (F15).
/// <para>
/// <b>Verificadas el 2026-09-01</b> contra las dos fuentes publicadas, que son las que hay que
/// volver a mirar cuando alguien sospeche de una cifra:
/// </para>
/// <list type="bullet">
/// <item>
/// <c>docs.github.com/en/copilot/reference/copilot-billing/models-and-pricing</c> — la tabla de
/// tarifas por modelo de Copilot, en $/millón, y el valor del credit (1 credit = 0,01 $).
/// </item>
/// <item>
/// <c>github.blog</c> · «GitHub Copilot is moving to usage-based billing» — el 1 de junio de 2026
/// como fecha de corte, y que los credits se consumen «based on token usage, including input,
/// output, and cached tokens, according to the published API rates for each model».
/// </item>
/// </list>
/// <para>
/// <b>Solo lo que factura a la organización</b> (F16-RETOQUE §1). Hasta aquí la siembra traía
/// también las tarifas de Claude Code, atadas a su proveedor. Se retiraron: ese consumo va contra
/// la suscripción personal de quien lo usa, así que no hay factura que calcular y mantener a mano
/// una copia de la lista de precios de Anthropic solo servía para tener un dato que caduca solo.
/// </para>
/// <para>
/// <b>Esto es una SIEMBRA, no la verdad permanente.</b> Se escribe una vez en el hub y a partir de
/// ahí manda lo que haya allí, que es lo que la organización puede corregir sin esperar a una
/// release. Varias de estas tarifas son promocionales con fecha de caducidad y están anotadas como
/// tales: cuando venzan, el precio sube y hay que editarlas.
/// </para>
/// </summary>
public static class ModelRateSeed
{
    /// <summary>El día en que se leyeron las dos páginas y se copiaron estos números.</summary>
    public static readonly DateOnly VerifiedOn = new(2026, 9, 1);

    public const string SourceNote =
        "Sembrado el 2026-09-01 de docs.github.com (Copilot · models-and-pricing). Solo lo que "
        + "factura a la organización. Revisa cuando venzan los promocionales.";

    /// <summary>La tabla inicial. Nombres de modelo tal y como los registra cada proveedor.</summary>
    public static ModelRateTable Create()
    {
        var table = new ModelRateTable
        {
            Source = SourceNote,
            ReviewedOn = VerifiedOn,
        };

        // ---- Copilot · OpenAI -------------------------------------------------------------
        // Ninguno cobra la escritura de caché aparte (la tabla de GitHub la deja en blanco), así
        // que esos tokens son entrada normal: CacheWritePerMillion se queda en null.
        Add(table, "gpt-5-mini", 0.25m, 2.00m, 0.025m);
        Add(table, "gpt-5.3-codex", 1.75m, 14.00m, 0.175m);
        Add(table, "gpt-5.4", 2.50m, 15.00m, 0.25m);
        Add(table, "gpt-5.4-mini", 0.75m, 4.50m, 0.075m);
        Add(table, "gpt-5.4-nano", 0.20m, 1.25m, 0.02m);
        Add(table, "gpt-5.5", 5.00m, 30.00m, 0.50m);
        Add(table, "gpt-5.6-luna", 0.20m, 1.20m, 0.02m, 0.25m);
        Add(table, "gpt-5.6-sol", 2.00m, 10.00m, 0.20m, 2.50m,
            note: "Promocional: 50 % de descuento hasta el 2026-09-03. Después sube.");
        Add(table, "gpt-5.6-terra", 2.00m, 12.00m, 0.20m, 2.50m);

        // ---- Copilot · Anthropic ----------------------------------------------------------
        // Aquí la escritura de caché SÍ se cobra aparte, y la tabla de GitHub publica la tarifa de
        // caché de cinco minutos (1,25 × la entrada).
        Add(table, "claude-haiku-4.5", 1.00m, 5.00m, 0.10m, 1.25m);
        Add(table, "claude-sonnet-4", 3.00m, 15.00m, 0.30m, 3.75m);
        Add(table, "claude-sonnet-4.5", 3.00m, 15.00m, 0.30m, 3.75m);
        Add(table, "claude-sonnet-4.6", 3.00m, 15.00m, 0.30m, 3.75m);
        Add(table, "claude-sonnet-5", 2.00m, 10.00m, 0.20m, 2.50m);
        Add(table, "claude-opus-4.5", 5.00m, 25.00m, 0.50m, 6.25m);
        Add(table, "claude-opus-4.6", 5.00m, 25.00m, 0.50m, 6.25m);
        Add(table, "claude-opus-4.7", 5.00m, 25.00m, 0.50m, 6.25m);
        Add(table, "claude-opus-4.8", 5.00m, 25.00m, 0.50m, 6.25m);
        Add(table, "claude-opus-5", 5.00m, 25.00m, 0.50m, 6.25m);
        Add(table, "claude-fable-5", 10.00m, 50.00m, 1.00m, 12.50m);

        // ---- Copilot · Google, Microsoft, xAI, Moonshot ------------------------------------
        Add(table, "gemini-3.1-pro", 2.00m, 12.00m, 0.20m);
        Add(table, "gemini-3.5-flash", 1.50m, 9.00m, 0.15m);
        Add(table, "gemini-3.6-flash", 0.75m, 3.75m, 0.075m,
            note: "Promocional hasta el 2026-12-31.");
        Add(table, "gemini-3.7-flash", 0.75m, 3.75m, 0.075m,
            note: "Promocional hasta el 2026-12-31.");
        Add(table, "mai-code-1-flash", 0.75m, 4.50m, 0.075m);
        Add(table, "mai-code-1.1-flash", 0.20m, 1.20m, 0.02m);
        Add(table, "grok-4.5", 2.00m, 6.00m, 0.50m);
        Add(table, "grok-4.6", 2.00m, 6.00m, 0.50m);
        Add(table, "kimi-k2.7-code", 0.95m, 4.00m, 0.19m);
        Add(table, "kimi-k3", 3.00m, 15.00m, 0.30m);
        Add(table, "raptor-mini", 0.25m, 2.00m, 0.025m);

        // ---- Claude Code: NO va en esta tabla (F16-RETOQUE §1) -----------------------------
        //
        // Aquí hubo cuatro tarifas atadas al proveedor `claude-code`, con su caché de una hora al
        // doble de la entrada. Estaban bien medidas —reproducían al sexto decimal el coste que el
        // propio CLI calcula— y aun así se retiran, porque la pregunta no era si el número salía:
        // era quién paga. El consumo de Claude Code va contra la SUSCRIPCIÓN PERSONAL de quien lo
        // usa y no le llega a la organización en ninguna factura, así que tarifarlo obligaba a
        // mantener a mano una copia de la lista de precios de Anthropic: ruido el día que se
        // escribe y desinformación el día que cambia sin avisar.
        //
        // Esta tabla es la de lo que FACTURA. Los modelos de Anthropic que sí están arriba son los
        // que Copilot revende, y ésos sí los paga la organización, con la tarifa que publica
        // GitHub. La medida de la caché de una hora queda escrita en DECISIONS (D-785) por si
        // alguna vez vuelve a hacer falta.

        return table;
    }

    /// <summary>
    /// Lo que una siembra automática hizo con la tabla del hub (R2 §2).
    /// </summary>
    /// <param name="Table">La tabla resultante. Es la misma instancia que entró, si entró alguna.</param>
    /// <param name="Added">
    /// Las tarifas que faltaban y se han añadido. Vacía significa «no hay nada que escribir», y es
    /// lo que evita un commit por arranque en un hub que ya está al día.
    /// </param>
    public sealed record SeedFill(ModelRateTable Table, IReadOnlyList<ModelRate> Added);

    /// <summary>
    /// <b>Rellena lo que falte, sin pisar nada</b> (R2 §2). Es la operación que la aplicación hace
    /// sola al abrir el hub: un precio publicado es un dato, no una decisión del usuario, así que no
    /// puede depender de que alguien visite una pantalla.
    /// <para>
    /// <b>Una tarifa escrita manda sobre la sembrada, siempre.</b> El criterio es por MODELO y no
    /// por tabla: basta con que la tabla nombre ese modelo —con proveedor o sin él— para que la
    /// siembra lo deje en paz. Así, quien corrigió un precio lo conserva, y un hub que ya tenía
    /// tabla sí recibe los modelos que esa tabla todavía no conocía — que es lo que la regla por
    /// tabla de D-786 no podía hacer.
    /// </para>
    /// <para>
    /// La contrapartida, dicha: una tarifa sembrada que alguien BORRE vuelve en el arranque
    /// siguiente. Borrar una tarifa no es una preferencia que la aplicación pueda respetar sin
    /// inventarse una lápida por modelo; corregir el número sí, y es lo que la pantalla ofrece.
    /// </para>
    /// </summary>
    /// <param name="existing">La tabla del hub, o null si todavía no hay ninguna.</param>
    public static SeedFill Fill(ModelRateTable? existing)
    {
        ModelRateTable seed = Create();
        if (existing is null)
        {
            return new SeedFill(seed, seed.Rates.ToList());
        }

        var known = new HashSet<string>(
            existing.Rates.Select(r => r.Model), StringComparer.OrdinalIgnoreCase);

        var added = new List<ModelRate>();
        foreach (ModelRate rate in seed.Rates)
        {
            if (known.Add(rate.Model))
            {
                existing.Rates.Add(rate);
                added.Add(rate);
            }
        }

        // La procedencia se apunta solo cuando de verdad se ha escrito algo: sellar «revisado hoy»
        // en una tabla que no se ha tocado sería fechar una revisión que nadie hizo.
        if (added.Count > 0 && string.IsNullOrWhiteSpace(existing.Source))
        {
            existing.Source = SourceNote;
        }

        return new SeedFill(existing, added);
    }

    private static void Add(
        ModelRateTable table,
        string model,
        decimal input,
        decimal output,
        decimal cachedInput,
        decimal? cacheWrite = null,
        string? note = null)
        => table.Rates.Add(new ModelRate(
            model, input, output, cachedInput, cacheWrite,
            Provider: null, EffectiveFrom: VerifiedOn, Note: note));

}
