using System.Text;
using Atalaya.Copilot;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// Builds the self-contained "fix prompt" (§5.7): everything a developer needs to fix EXACTLY one
/// finding in their IDE Copilot, with fixed acceptance criteria. Pure — the VM copies it to the
/// clipboard and records it under comments/ as kind "fix-prompt".
/// </summary>
public static class FixPromptBuilder
{
    public static string Build(Finding f)
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
        sb.AppendLine("## Criterios de aceptación (obligatorios)");
        sb.AppendLine("1. Arregla SOLO este hallazgo; no toques nada más.");
        sb.AppendLine("2. No cambies el comportamiento observable salvo el defecto descrito.");
        sb.AppendLine("3. Añade o ajusta un test que cubra el defecto si el stack lo permite.");
        sb.AppendLine("4. Lista al final los ficheros tocados.");
        return sb.ToString();
    }
}
