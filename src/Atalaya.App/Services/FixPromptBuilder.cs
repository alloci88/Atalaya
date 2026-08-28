using System.Text;
using Atalaya.Copilot;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// Builds the self-contained "fix prompt" (§5.7): everything a developer needs to fix EXACTLY one
/// finding in their IDE Copilot, with fixed acceptance criteria. Pure — the VM copies it to the
/// clipboard and records it under comments/ as kind "fix-prompt".
/// <para>
/// <b>F6.8: el prompt viaja con sus REFERENCIAS.</b> El encargo anterior enseñaba el método
/// afectado y nada más, así que el agente aplicaba un arreglo localmente correcto sin saber quién
/// llamaba a ese método —y rompía procesos aguas arriba: añadir una excepción a un método que
/// antes truncaba en silencio rompe a todo llamador que dependiera del truncado—. Ahora lleva dos
/// secciones nuevas: <b>quién usa este código</b> (lo que encuentra
/// <see cref="ReferenceCollector"/>) y unas <b>reglas del arreglo</b> endurecidas alrededor del
/// contrato observable. La segunda sin la primera sería una regla imposible de cumplir: no se
/// puede «revisar todos los llamadores» sin la lista delante.
/// </para>
/// </summary>
public static class FixPromptBuilder
{
    /// <summary>
    /// El prompt SIN referencias. Se conserva porque la sección es opcional por diseño: si la
    /// recolección falla, el prompt sale igual con su aviso (anti-objetivo declarado).
    /// </summary>
    public static string Build(Finding f) => Build(f, refs: null);

    /// <param name="directives">
    /// Las convenciones del proyecto de ámbito Arreglo, ya recortadas al presupuesto (F7).
    /// Opcional por la misma razón que las referencias: si no se pueden leer, el prompt sale igual
    /// —sin la sección— en vez de no salir.
    /// </param>
    public static string Build(Finding f, ReferenceReport? refs, DirectiveBundle? directives = null)
    {
        RuleDef? rule = RuleCatalog.Find(f.RuleId);
        var sb = new StringBuilder();

        sb.AppendLine($"# Arreglo dirigido — {f.DisplayId ?? f.Id.ToString()}: {f.Title}");
        sb.AppendLine();
        sb.AppendLine($"Regla: `{f.RuleId}`" + (rule is not null ? $" — {rule.Title}" : ""));
        if (rule is not null)
        {
            sb.AppendLine($"Qué mira la regla: {rule.Look}");
        }

        sb.AppendLine();
        sb.AppendLine("## Descripción");
        sb.AppendLine(f.Description);
        sb.AppendLine();
        sb.AppendLine("## Impacto");
        sb.AppendLine(f.Impact);
        sb.AppendLine();
        sb.AppendLine("## Recomendación");
        sb.AppendLine(f.Recommendation);
        sb.AppendLine();

        sb.AppendLine("## Ubicaciones");
        foreach (Location loc in f.Locations)
        {
            sb.AppendLine($"- `{loc.Path}:{loc.Line}`");
        }

        sb.AppendLine();
        AppendReferences(sb, refs);
        sb.Append(DirectiveSection.Render(
            directives ?? DirectiveBundle.Empty, DirectivePurpose.ArregloPrompt));
        AppendRules(sb, directives ?? DirectiveBundle.Empty);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ quién usa este código

    /// <summary>
    /// La sección de referencias. Tiene cuatro formas y ninguna se calla: la lista, «no se
    /// encontraron llamadores», la lista etiquetada como aproximada, y «va sin referencias» con
    /// el motivo. La cuarta es la importante: un prompt sin la sección se leería como un método
    /// sin usos, que es justo el permiso para cambiar el contrato a ciegas.
    /// </summary>
    /// <remarks>
    /// <b>internal</b> desde F6.9: el arreglo asistido interactivo escribe EXACTAMENTE esta misma
    /// sección. Duplicarla habría sido garantizar que las dos se separaran — y la que se quedara
    /// atrás sería la que le miente al agente sobre quién usa el código.
    /// </remarks>
    internal static void AppendReferences(StringBuilder sb, ReferenceReport? refs)
    {
        sb.AppendLine("## Quién usa este código");
        sb.AppendLine();

        if (refs is null || !refs.Collected)
        {
            string reason = refs?.Unavailable ?? "la recolección de referencias no llegó a ejecutarse";
            sb.AppendLine($"**Este prompt va SIN la lista de llamadores**: {reason}.");
            sb.AppendLine();
            sb.AppendLine(
                "No supongas que el código no se usa en ningún sitio: no se ha podido mirar. "
                + "Antes de cambiar la firma, las excepciones o los valores de retorno, comprueba "
                + "tú mismo quién llama a este código y dilo en tu respuesta.");
            sb.AppendLine();
            return;
        }

        string what = string.Join(", ", refs.Symbols.Select(s => $"`{s}`"));
        sb.AppendLine($"Usos de {what} en la solución del clon local. Son llamadores **DIRECTOS**");
        sb.AppendLine("(un solo nivel): estos llamadores tienen a su vez sus propios consumidores, y");
        sb.AppendLine("ese radio de impacto de segundo orden NO está calculado aquí.");
        sb.AppendLine();

        if (refs.Precision == ReferencePrecision.Texto)
        {
            sb.AppendLine(
                "> ⚠ Referencias obtenidas por **búsqueda de texto** del nombre del símbolo, no por "
                + "análisis de código: puede haber falsos positivos (otro tipo con un miembro del "
                + "mismo nombre, menciones en comentarios) y falsos negativos (usos indirectos, "
                + "reflexión).");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine(
                "> Referencias obtenidas con el analizador de C# sobre los fuentes del clon: los "
                + "comentarios, la documentación y las cadenas quedan fuera. No resuelve tipos, así "
                + "que un miembro del mismo nombre en otro tipo podría colarse.");
            sb.AppendLine();
        }

        if (refs.TimedOut)
        {
            sb.AppendLine(
                "> ⚠ Se agotó el presupuesto de tiempo de la recolección: la lista puede estar "
                + "incompleta.");
            sb.AppendLine();
        }

        if (refs.Total == 0)
        {
            sb.AppendLine(
                "**No se encontraron llamadores en esta solución.** Si es una API pública consumida "
                + "desde fuera —otra solución, otro repositorio, un paquete publicado—, considera la "
                + "compatibilidad antes de cambiar el contrato: desde aquí no se puede ver.");
            sb.AppendLine();
            return;
        }

        foreach (ReferenceSite site in refs.Sites)
        {
            string who = site.Member is null ? string.Empty : $" — `{site.Member}`";
            sb.AppendLine($"- `{site.Path}:{site.Line}`{who}");
            sb.AppendLine($"  `{site.Text}`");
        }

        if (refs.Hidden > 0)
        {
            string where = refs.OverflowAreas.Count > 0
                ? $" en {string.Join(", ", refs.OverflowAreas)}"
                : string.Empty;
            sb.AppendLine();
            sb.AppendLine($"…y {refs.Hidden} más{where} (de {refs.Total} en total).");
        }

        sb.AppendLine();
    }

    // ------------------------------------------------------------------ reglas del arreglo

    /// <summary>
    /// Las reglas, endurecidas alrededor del contrato observable (F6.8 §2). Las tres primeras son
    /// nuevas y salen del defecto que abrió el parte; las tres últimas son los criterios de
    /// aceptación de §5.7, que no se pierden.
    /// <para>
    /// Y los <b>límites</b> van aquí y no en la app: el tope de la recolección impide que Atalaya
    /// se vuelva loca, pero nada impedía que se volviera loco el agente que arregla. Un arreglo
    /// que se expande por la solución no es un arreglo mejor: es uno que ya no se puede revisar.
    /// </para>
    /// </summary>
    private static void AppendRules(StringBuilder sb, DirectiveBundle directives)
    {
        sb.AppendLine("## Reglas del arreglo (obligatorias)");
        sb.AppendLine(
            "1. **Preserva el contrato observable** del código salvo que el defecto SEA el "
            + "contrato. Antes de cambiar la firma, las excepciones que lanza, los valores de "
            + "retorno en casos borde o los efectos, revisa TODOS los llamadores listados arriba. "
            + "Ejemplo de lo que no se puede hacer a ciegas: añadir una excepción a un método que "
            + "antes truncaba en silencio rompe a cualquier llamador que dependiera del truncado.");
        sb.AppendLine(
            "2. **Si el arreglo exige cambiar el contrato**, adapta cada llamador afectado en el "
            + "mismo cambio y lista en tu respuesta qué llamador tocaste y por qué. Un arreglo que "
            + "rompe llamadores no es un arreglo.");
        sb.AppendLine(
            "3. **Compila la solución y ejecuta los tests** antes de dar por bueno el cambio. Si "
            + "algún llamador queda en un proyecto que no compila o que no tiene tests, dilo "
            + "explícitamente en tu respuesta.");
        sb.AppendLine("4. Arregla SOLO este hallazgo; no toques nada más.");
        sb.AppendLine("5. Añade o ajusta un test que cubra el defecto si el stack lo permite.");
        sb.AppendLine("6. Lista al final los ficheros tocados.");

        // F7: aquí no hay a quién preguntar —este prompt se pega en otro sitio—, así que el
        // conflicto con una convención se DECLARA como riesgo. Es la misma regla del modo
        // interactivo con la única salida que tiene este medio.
        if (!directives.IsEmpty)
        {
            sb.AppendLine(
                "7. **Respeta las convenciones del proyecto** de la sección de arriba. Si el "
                + "arreglo correcto contradice una, aplica lo que manda la convención y declara "
                + "el conflicto como riesgo al final: qué directiva es, qué manda y qué habrías "
                + "hecho si no existiera. No la atropelles en silencio.");
        }

        sb.AppendLine();
        sb.AppendLine("### Límites de la exploración");
        sb.AppendLine(
            "- Revisa los llamadores **LISTADOS arriba**; NO explores el código base más allá de "
            + "ellos.");
        sb.AppendLine(
            "- Si al revisarlos sospechas un impacto más profundo —transitivo, en otros "
            + "repositorios, en consumidores externos—, **NO lo persigas**: decláralo en tu "
            + "respuesta como riesgo pendiente de revisión humana.");
        sb.AppendLine(
            "- El arreglo es mínimo y quirúrgico: este hallazgo, sus ficheros, y los llamadores "
            + "listados si el contrato cambia. Un arreglo que se expande por la solución es un "
            + "arreglo fallido: entrega el cambio mínimo más la lista de riesgos.");
    }
}
