using System.Text;
using Atalaya.Domain;
using Atalaya.Domain.Model;

namespace Atalaya.Copilot;

/// <summary>Builds the auditor brief per stack (§6.4) from the versioned rule catalog.</summary>
public static class PillarBrief
{
    private const string SeverityRubric =
        "RÚBRICA DE SEVERIDAD:\n" +
        "- critica: corrupción de datos, crash en producción, vulnerabilidad explotable, error de cálculo de negocio.\n" +
        "- alta: degradación seria de rendimiento, fuga de recursos, CVE, race probable.\n" +
        "- media: mantenibilidad, modernización, optimización notable.\n" +
        "- baja: estilo, micro-optimización, DX.\n";

    public static string For(TechStack stack)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"BRIEF DE AUDITOR — stack {stack}.");
        sb.AppendLine();
        sb.AppendLine(SeverityRubric);
        sb.AppendLine();

        foreach (Pillar pillar in new[] { Pillar.Errores, Pillar.Optimizacion, Pillar.Mejoras })
        {
            sb.AppendLine($"PILAR {pillar.ToString().ToUpperInvariant()} — mínimos a revisar:");
            foreach (RuleDef rule in RuleCatalog.Rules.Where(r => r.Pillar == pillar))
            {
                sb.AppendLine($"  [{rule.RuleId}] {rule.Title}: {rule.Look}");
            }

            sb.AppendLine();
        }

        sb.AppendLine("ÁREAS DE CRITERIO PROFESIONAL (usa un ruleId criterio.<área>):");
        sb.AppendLine("  " + string.Join(", ", RuleCatalog.CriterioAreas));
        sb.AppendLine();
        sb.AppendLine(StackNotes(stack));
        return sb.ToString();
    }

    private static string StackNotes(TechStack stack) => stack switch
    {
        TechStack.DotNet => "Notas .NET: revisa IDisposable/using, async/await, LINQ diferido, boxing, Task.Result.",
        TechStack.TypeScript or TechStack.JavaScript => "Notas JS/TS: promesas sin await, any implícito, mutación compartida, XSS/inyección.",
        TechStack.Python => "Notas Python: gestores de contexto, mutable default args, inyección, GIL en CPU-bound.",
        TechStack.Go => "Notas Go: goroutines fugadas, errores ignorados, defer en bucles, data races.",
        TechStack.Java => "Notas Java: try-with-resources, streams reevaluados, sincronización, deserialización.",
        TechStack.Rust => "Notas Rust: unwrap en producción, unsafe, clones innecesarios, bloqueos en async.",
        TechStack.CCpp => "Notas C/C++: gestión de memoria, desbordamientos, UB, RAII.",
        _ => "Notas: revisa gestión de recursos, concurrencia, entrada no confiable y rendimiento en caliente.",
    };
}

/// <summary>Composes the exact prompts sent to the agent (§5.1.3, §5.4, §6.4).</summary>
public static class PromptComposer
{
    private const string AuditorRules =
        "Eres un auditor de código. Reglas:\n" +
        "- Cubre ÍNTEGRAMENTE la unidad.\n" +
        "- Reporta CADA hallazgo llamando a la herramienta submit_finding, nunca en texto.\n" +
        "- Marca cada hallazgo con su ruleId de checklist o criterio.<área>, y cita fichero y línea.\n" +
        "- NO asignes IDs ni confianza (eso es de la app). NO filtres silenciados (lo hace la app).\n" +
        "- Puedes pedir firmas de dependencias con read_signatures(path); es tu única lectura extra.\n" +
        "- Cuando termines la unidad, llama a unit_done con un resumen.\n";

    public static string ComposeUnitPrompt(string unitPath, string unitContent, string brief, AuditMode mode)
    {
        var sb = new StringBuilder();
        sb.AppendLine(AuditorRules);
        sb.AppendLine($"MODO: {mode}. Los hallazgos nuevos nacen con la confianza que la app asigne.");
        sb.AppendLine();
        sb.AppendLine(brief);
        sb.AppendLine($"UNIDAD: {unitPath}");
        sb.AppendLine("CONTENIDO ÍNTEGRO DE LA UNIDAD (entre marcadores):");
        sb.AppendLine("<<<UNIT");
        sb.AppendLine(unitContent);
        sb.AppendLine("UNIT>>>");
        return sb.ToString();
    }

    public static string ComposeVerifyPrompt(IReadOnlyList<VerifyTarget> targets)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Eres un verificador. Para cada hallazgo, decide su veredicto y llama a submit_verdict(findingUlid, verdict, evidence).");
        sb.AppendLine("verdict ∈ {confirmado, resuelto, no-verificable}. Usa el ULID exacto que se te da.");
        sb.AppendLine();
        foreach (VerifyTarget t in targets)
        {
            sb.AppendLine($"- ULID {t.FindingUlid} · {t.Path}:{t.Line} · {t.Title}");
            sb.AppendLine($"    {t.Description}");
            if (!string.IsNullOrEmpty(t.Snippet))
            {
                sb.AppendLine("    snippet anclado:");
                sb.AppendLine("    " + t.Snippet.Replace("\n", "\n    "));
            }
        }

        return sb.ToString();
    }
}
