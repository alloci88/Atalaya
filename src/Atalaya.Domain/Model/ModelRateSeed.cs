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
        "Sembrado el 2026-09-01 de docs.github.com (Copilot · models-and-pricing) y, para "
        + "Claude Code, de las tarifas de API de Anthropic comprobadas contra el coste que el "
        + "propio CLI calcula. Revisa cuando venzan los promocionales.";

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

        // ---- Claude Code · las MISMAS familias, pero con SU tarifa de caché ----------------
        //
        // Atadas al proveedor a propósito, y esto es el hallazgo que lo justifica: Claude Code usa
        // caché de UNA HORA, que Anthropic cobra al DOBLE de la entrada, mientras que la tabla de
        // GitHub publica la de cinco minutos (1,25 ×). Con la tarifa de GitHub, el coste de una
        // sesión real de Claude Code salía 0,035545 $ cuando el propio CLI calculaba 0,051106 $;
        // con la de una hora sale 0,051106 $ EXACTO. Un 44 % de desviación por una tarifa de caché.
        //
        // El CLI resuelve los alias a nombres concretos —`opus` → `claude-opus-5`— y es ese nombre
        // resuelto el que queda registrado en la sesión, así que es el que hay que casar.
        AddForClaudeCode(table, "claude-opus-5", 5.00m, 25.00m, 0.50m, 10.00m);
        AddForClaudeCode(table, "claude-sonnet-5", 2.00m, 10.00m, 0.20m, 4.00m);
        AddForClaudeCode(table, "claude-haiku-4-5-20251001", 1.00m, 5.00m, 0.10m, 2.00m);
        AddForClaudeCode(table, "claude-fable-5", 10.00m, 50.00m, 1.00m, 20.00m);

        return table;
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

    private static void AddForClaudeCode(
        ModelRateTable table, string model, decimal input, decimal output, decimal cachedInput, decimal cacheWrite1h)
        => table.Rates.Add(new ModelRate(
            model, input, output, cachedInput, cacheWrite1h,
            Provider: "claude-code",
            EffectiveFrom: VerifiedOn,
            Note: "Escritura de caché a 1 h (2 × la entrada), que es la que usa el CLI de Claude Code."));
}
