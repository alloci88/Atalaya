using Atalaya.Domain.Model;

namespace Atalaya.Inventory;

/// <summary>
/// El resultado de volver a MEDIR una unidad contra los umbrales de su aplicación (F5.16).
/// </summary>
/// <param name="Measured">
/// Se pudo medir. False = no hay clon, o el fichero ya no está: incertidumbre declarada, nunca un
/// veredicto inventado.
/// </param>
/// <param name="Exceeds">La medida supera el umbral: la unidad sigue siendo grande.</param>
/// <param name="Problem">Por qué no se pudo medir, cuando <paramref name="Measured"/> es false.</param>
public sealed record UnitMeasurement(
    bool Measured, int Loc, int Chars, bool Exceeds, string? Problem = null)
{
    /// <summary>
    /// La medida escrita como se lee. Nombra el criterio que DE VERDAD decide: el umbral es
    /// «LOC o caracteres», así que una unidad de 1.269 líneas puede seguir siendo grande por peso,
    /// y decir «1269 LOC ≥ 1500» sería mentir con un número correcto.
    /// </summary>
    public string Describe(MeasureThresholds t)
        => !Measured
            ? Problem ?? "no se pudo medir"
            : Loc > t.LargeUnitLoc
                ? $"{Loc} LOC ≥ umbral {t.LargeUnitLoc}"
                : Chars > t.LargeUnitChars
                    ? $"{Chars} caracteres ≥ umbral {t.LargeUnitChars} ({Loc} LOC)"
                    : $"{Loc} LOC < umbral {t.LargeUnitLoc}";
}

/// <summary>
/// Mide unidades y reconoce los hallazgos que MIDE la aplicación (F5.16).
/// <para>
/// <b>La regla de la casa:</b> cada hallazgo se verifica con el instrumento que lo detectó. Los
/// hallazgos de «unidad demasiado grande» no los encuentra el auditor: los calcula la aplicación
/// comparando LOC y caracteres contra el umbral de la app (§4, mejora 7). Pedirle a un LLM que
/// verifique una cuenta de líneas mirando un fragmento anclado en la línea 1 es usar el
/// instrumento equivocado, y responde lo único que puede responder con honradez: «no verificable».
/// Eso es exactamente lo que le pasó a MEJ-0037 dos veces (ver DECISIONS, F5.16 §0).
/// </para>
/// <para>
/// Está aquí, junto a <see cref="InventoryScanner"/>, porque la condición de «grande» tiene que ser
/// LA MISMA en los dos sitios: si el escáner y la re-medición discreparan, una unidad podría salir
/// de «Grandes» en el inventario y seguir con su hallazgo activo — que es la mitad del parte que
/// abrió esta tanda.
/// </para>
/// </summary>
public static class UnitMeasure
{
    /// <summary>
    /// Las reglas cuyo veredicto es una MEDIDA de la aplicación, no un juicio del auditor. Hoy solo
    /// hay una; el mecanismo es de la clase, no de la regla, para que añadir la siguiente sea
    /// añadirla a esta lista y nada más.
    /// </summary>
    public static IReadOnlyCollection<string> MeasuredRuleIds { get; } =
        new[] { InventoryScanner.LargeUnitRuleId };

    /// <summary>¿Lo mide la aplicación? Entonces se verifica midiendo, nunca preguntando.</summary>
    public static bool IsMeasured(string? ruleId)
        => ruleId is not null && MeasuredRuleIds.Contains(ruleId, StringComparer.Ordinal);

    /// <summary>
    /// Mide la unidad en el clon. Sin clon o sin fichero devuelve <c>Measured=false</c> con el
    /// motivo: la incertidumbre se declara, no se resuelve a ojo. En particular, un fichero que ya
    /// no está NO es un hallazgo resuelto — puede haberse movido o renombrado, y «no está donde
    /// estaba» no es «ya no es grande».
    /// </summary>
    public static UnitMeasurement Measure(string? clonePath, string unitPath, MeasureThresholds thresholds)
    {
        if (string.IsNullOrWhiteSpace(clonePath) || !Directory.Exists(clonePath))
        {
            return new UnitMeasurement(false, 0, 0, false,
                "no se puede medir sin el clon local — vincúlalo desde el Inventario");
        }

        string abs = Path.Combine(clonePath, unitPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(abs))
        {
            return new UnitMeasurement(false, 0, 0, false,
                $"«{unitPath}» ya no está en el clon: puede haberse movido o renombrado, "
                + "así que no se da por resuelto");
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(abs);
            int loc = InventoryScanner.CountLinesOf(bytes);
            int chars = bytes.Length;
            bool exceeds = loc > thresholds.LargeUnitLoc || chars > thresholds.LargeUnitChars;
            return new UnitMeasurement(true, loc, chars, exceeds);
        }
        catch (IOException ex)
        {
            return new UnitMeasurement(false, 0, 0, false, $"no se pudo leer «{unitPath}»: {ex.Message}");
        }
    }
}
