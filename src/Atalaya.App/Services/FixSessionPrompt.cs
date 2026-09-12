using System.Text;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>El código de un símbolo afectado, leído del clon de hoy.</summary>
/// <param name="Path">Ruta relativa al clon.</param>
/// <param name="Caption">Miembro y ubicación, tal y como lo titula la ficha.</param>
/// <param name="FirstLine">Número de la primera línea del fragmento en el fichero.</param>
/// <param name="Text">El código. Vacío si no se pudo leer.</param>
/// <param name="Notice">Qué relación tiene con lo que se auditó, si hay algo que decir.</param>
public sealed record FixCodeExcerpt(string Path, string Caption, int FirstLine, string Text, string Notice);

/// <summary>
/// El encargo de una sesión de arreglo INTERACTIVA (F6.9 §2).
/// <para>
/// <b>Hereda F6.7 y F6.8 al completo.</b> Las piezas son las mismas que las del generador de
/// prompt old school —hallazgo íntegro, código actual del símbolo leído del clon, «quién usa este
/// código» del <see cref="ReferenceCollector"/> con sus límites y su honestidad— y la sección de
/// referencias es literalmente la misma función (<see cref="FixPromptBuilder.AppendReferences"/>),
/// no una copia. Lo que cambia son las REGLAS, porque cambia el medio: en el generador el agente
/// entrega un cambio y se acabó; aquí hay alguien delante al que se le puede —y se le debe—
/// preguntar.
/// </para>
/// <para>
/// <b>Y el generador no se toca.</b> El camino old school se conserva tal cual: son dos encargos
/// distintos para dos situaciones distintas, y fundirlos habría obligado a que el de siempre
/// hablara de tools que en su medio no existen.
/// </para>
/// </summary>
public static class FixSessionPrompt
{
    public static string Build(
        Finding finding,
        ReferenceReport? refs,
        IReadOnlyList<FixCodeExcerpt> code,
        string appName,
        int readBudget = FixToolbox.DefaultReadBudget,
        FixTestSituation? tests = null,
        DirectiveBundle? directives = null)
    {
        RuleDef? rule = RuleCatalog.Find(finding.RuleId);
        string alias = finding.DisplayId ?? finding.Id.ToString();
        var sb = new StringBuilder();

        sb.AppendLine($"# Arreglo asistido — {alias}: {finding.Title}");
        sb.AppendLine();
        sb.AppendLine(
            $"Estás arreglando UN hallazgo de la aplicación **{appName}** directamente sobre el clon "
            + "local del usuario, que te está mirando. No hay rama: editas el árbol de trabajo. El "
            + "usuario revisará y commiteará él mismo cuando esté conforme.");
        sb.AppendLine();

        AppendHowYouWork(sb, readBudget, tests ?? FixTestSituation.Unknown);

        sb.AppendLine($"## El hallazgo — {alias}");
        sb.AppendLine();
        sb.AppendLine($"- **Severidad**: {finding.Severity}");
        sb.AppendLine($"- **Regla**: `{finding.RuleId}`" + (rule is not null ? $" — {rule.Title}" : ""));
        if (rule is not null)
        {
            sb.AppendLine($"- **Qué mira la regla**: {rule.Look}");
        }

        sb.AppendLine();
        sb.AppendLine("### Descripción");
        sb.AppendLine(Text(finding.Description));
        sb.AppendLine();
        sb.AppendLine("### Impacto");
        sb.AppendLine(Text(finding.Impact));
        sb.AppendLine();
        sb.AppendLine("### Recomendación");
        sb.AppendLine(Text(finding.Recommendation));
        sb.AppendLine();

        sb.AppendLine("### Ubicaciones");
        foreach (Location loc in finding.Locations)
        {
            sb.AppendLine($"- `{loc.Path}:{loc.Line}`");
        }

        sb.AppendLine();
        AppendCode(sb, code);
        FixPromptBuilder.AppendReferences(sb, refs);

        // F7: las convenciones de la casa, antes de las reglas del arreglo. El orden importa: la
        // regla 8 le dice al agente que las respete, y una regla que apunta a algo que todavía no
        // ha leído es una regla que se cumple de memoria.
        sb.Append(DirectiveSection.Render(
            directives ?? DirectiveBundle.Empty, DirectivePurpose.ArregloInteractivo));

        AppendRules(sb, tests ?? FixTestSituation.Unknown, directives ?? DirectiveBundle.Empty);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ cómo se trabaja aquí

    /// <summary>
    /// Las herramientas y sus límites, ANTES del hallazgo. Va primero a propósito: un agente que
    /// lee el defecto antes de saber que no tiene shell empieza a planear con una shell.
    /// </summary>
    private static void AppendHowYouWork(StringBuilder sb, int readBudget, FixTestSituation tests)
    {
        sb.AppendLine("## Cómo trabajas aquí");
        sb.AppendLine();
        sb.AppendLine(
            $"- `read_file(path, startLine, endLine)` — lee ficheros del clon. Tienes "
            + $"**{readBudget} lecturas**; la respuesta te dice cuántas te quedan. Un fichero que "
            + "no cabe de una vez vuelve **por líneas completas diciéndote cuántas tiene y cuáles "
            + "van**: pide el resto por rango, tantas veces como haga falta. **Jamás le pidas al "
            + "usuario que te pegue código o líneas** — tienes la herramienta para leerlo.");
        sb.AppendLine(
            "- `apply_edit(path, reason, edits)` — la **única** forma de modificar código. Sobre "
            + "los ficheros de las ubicaciones de arriba (y sus ficheros de test) se aplica sin "
            + "más; sobre **cualquier otro** la aplicación le pedirá permiso al usuario, así que "
            + "el `reason` es lo que él va a leer para decidir: escríbelo para una persona.");
        sb.AppendLine(
            "- `run_build_and_tests()` — pides que se compile y se pasen los tests; lo ejecuta la "
            + "aplicación y te devuelve un resumen. Tarda: pídelo cuando el cambio esté completo. "
            + "Se compila el **proyecto** de los ficheros que hayas tocado y sus tests, no la "
            + "solución entera —eso lo decide el usuario, no tú—, y el resumen te dice cuántos "
            + "errores son **NUEVOS**: los preexistentes de la solución no son tuyos y no tienes "
            + "que arreglarlos ni mencionarlos como si lo fueran.");
        sb.AppendLine(
            "- `ask_user(...)` — para preguntar. Úsala en los momentos de decisión, no para pedir "
            + "permiso de cortesía.");
        sb.AppendLine(
            "- `fix_done(...)` — cierra la sesión con el resumen y la sugerencia de commit.");
        sb.AppendLine();
        sb.AppendLine(
            "**No tienes shell, ni git, ni red.** No puedes commitear ni empujar nada, y no debes "
            + "proponerlo: de eso se encarga el usuario después.");
        sb.AppendLine();
        // H9.1 §3: la situación de tests la ha resuelto la aplicación leyendo el clon. Va aquí,
        // arriba y afirmada, para que no se gaste ni un turno en averiguar lo que ya se sabe.
        sb.AppendLine(tests.PromptLine);
        sb.AppendLine();
    }

    // ------------------------------------------------------------------ el código de ahora

    private static void AppendCode(StringBuilder sb, IReadOnlyList<FixCodeExcerpt> code)
    {
        sb.AppendLine("## El código afectado, tal y como está HOY en el clon");
        sb.AppendLine();

        if (code.Count == 0)
        {
            sb.AppendLine(
                "No se ha podido leer el código de las ubicaciones desde aquí. Léelo tú con "
                + "`read_file` antes de tocar nada.");
            sb.AppendLine();
            return;
        }

        foreach (FixCodeExcerpt excerpt in code)
        {
            sb.AppendLine($"### {excerpt.Caption}");
            if (excerpt.Notice.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"> {excerpt.Notice}");
            }

            sb.AppendLine();
            if (excerpt.Text.Length == 0)
            {
                sb.AppendLine("_(no se pudo leer del clon; usa `read_file`)_");
                sb.AppendLine();
                continue;
            }

            sb.AppendLine($"```  // {excerpt.Path}, desde la línea {excerpt.FirstLine}");
            sb.AppendLine(excerpt.Text.TrimEnd());
            sb.AppendLine("```");
            sb.AppendLine();
        }
    }

    // ------------------------------------------------------------------ reglas del modo interactivo

    /// <summary>
    /// Las reglas de F6.8, adaptadas al medio (F6.9 §2). La diferencia de fondo está en la 2: allí
    /// el agente decidía y lo declaraba; aquí <b>no decide, PREGUNTA</b> — con las opciones y su
    /// consecuencia sobre los llamadores listados—. Es la razón de ser de este modo: un cambio de
    /// contrato es una decisión de producto, y hay una persona delante a la que preguntársela.
    /// </summary>
    private static void AppendRules(StringBuilder sb, FixTestSituation tests, DirectiveBundle directives)
    {
        sb.AppendLine("## Reglas del arreglo (obligatorias)");
        sb.AppendLine(
            "1. **Preserva el contrato observable** del código salvo que el defecto SEA el "
            + "contrato. Antes de cambiar la firma, las excepciones que lanza, los valores de "
            + "retorno en casos borde o los efectos, revisa TODOS los llamadores listados arriba. "
            + "Ejemplo de lo que no se puede hacer a ciegas: añadir una excepción a un método que "
            + "antes truncaba en silencio rompe a cualquier llamador que dependiera del truncado.");
        sb.AppendLine(
            "2. **Si el arreglo exige cambiar el contrato: NO decidas — PREGUNTA.** Usa "
            + "`ask_user` presentando las opciones con su consecuencia CONCRETA sobre los "
            + "llamadores listados, por ejemplo: «(A) lanzar excepción y adaptar los N "
            + "llamadores, (B) comportamiento compatible + aviso, (C) abortar». Espera la "
            + "elección y arregla lo que te haya dicho. Elegir tú y contarlo después no vale "
            + "aquí: hay alguien delante.");
        sb.AppendLine(
            "3. **No explores más allá de los llamadores listados.** Si sospechas impacto "
            + "transitivo —otros consumidores, otros repositorios, APIs públicas—, **decláralo y "
            + "pregunta si continuar** con `ask_user`. No lo persigas por tu cuenta.");
        sb.AppendLine(
            "4. **Explica cada paso en una o dos frases ANTES de darlo.** Qué vas a tocar y por "
            + "qué. La narración es parte del producto, no ruido: el usuario está decidiendo si "
            + "se fía de este cambio mientras lo lee.");
        sb.AppendLine(
            "5. **Arregla SOLO este hallazgo.** Nada de limpiezas de paso, renombrados ni mejoras "
            + "de camino: un arreglo que se expande por la solución es uno que ya no se puede "
            + "revisar.");
        sb.AppendLine(tests.HasTests
            ? "6. **Añade o ajusta un test** que cubra el defecto —el proyecto tiene tests— y "
              + "**compila y pásalos** con `run_build_and_tests` antes de cerrar. Si no se puede "
              + "compilar desde aquí, dilo en el resumen. Y si el resumen te devuelve errores "
              + "**preexistentes**, déjalos: son de la solución, no de tu cambio, y arreglarlos "
              + "sería exactamente la expansión que el punto 5 prohíbe."
            : "6. **Compila con `run_build_and_tests` antes de cerrar.** Aquí no hay tests y ya "
              + "está dicho arriba: no los busques ni los escribas, y **no lo declares como "
              + "riesgo** —es un hecho del proyecto, no una carencia de tu arreglo, y el informe "
              + "ya lo recoge—. Si no se puede compilar desde aquí, dilo en el resumen. Y si el "
              + "resumen te devuelve errores **preexistentes**, déjalos: son de la solución, no de "
              + "tu cambio, y arreglarlos sería exactamente la expansión que el punto 5 prohíbe.");
        sb.AppendLine(
            "7. **Cierra con `fix_done`**: resumen de qué cambió y por qué, ficheros tocados, "
            + "riesgos declarados si los hay, y la sugerencia de commit — título de ≤72 "
            + "caracteres, en imperativo y citando el identificador del hallazgo (por ejemplo "
            + "«Valida longitud par en HexStringToByteArray (BUG-0003)»), y descripción con el "
            + "qué y el porqué, incluyendo los llamadores adaptados si los hubo.");

        // F7: solo cuando hay convenciones que respetar. Una regla que remite a una sección que no
        // existe manda al agente a buscar un fichero que nadie le ha dado.
        if (!directives.IsEmpty)
        {
            sb.AppendLine(
                "8. **Respeta las convenciones del proyecto** de la sección de arriba: estilo, "
                + "patrones y librerías preferidas. Si el arreglo correcto contradice una, no la "
                + "atropelles — pregunta con `ask_user` (punto 2). Y si la contradicción está en "
                + "el código que estás mirando y no en tu arreglo, dilo en el resumen: es un "
                + "hallazgo, no algo que arreglar de paso.");
        }

        sb.AppendLine();
        sb.AppendLine(
            "El usuario puede interrumpirte en cualquier momento para dirigirte («no toques ese "
            + "fichero», «prefiero TryParse»). Cuando lo haga, hazle caso: manda él.");
    }

    private static string Text(string? value)
        => string.IsNullOrWhiteSpace(value) ? "_(no consta)_" : value!.Trim();
}
