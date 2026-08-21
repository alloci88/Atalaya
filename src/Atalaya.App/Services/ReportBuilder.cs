using System.Text;
using Atalaya.Domain;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>Builds the immutable per-session markdown report (§7).</summary>
public static class ReportBuilder
{
    public static string BuildSessionReport(
        AppConfig app,
        AuditSession session,
        IReadOnlyList<Finding> newFindings,
        int pendingUnits,
        int largeUnits)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Informe de sesión — {app.Name}");
        sb.AppendLine();
        sb.AppendLine($"- **Modo**: {session.Mode}");
        sb.AppendLine($"- **Fecha**: {session.StartedUtc:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"- **Autor**: {session.By} ({session.Machine})");
        sb.AppendLine($"- **Commit auditado**: {session.Commit}");
        sb.AppendLine($"- **Modelo**: {session.Model ?? "n/d"}");
        sb.AppendLine($"- **Ciclo**: {session.CycleN}");
        sb.AppendLine($"- **Tokens**: entrada {session.Usage.InputTokens}, salida {session.Usage.OutputTokens}"
            + (session.Usage.Cost is { } c ? $", coste {c:0.####} {session.Usage.Currency}" : ""));
        sb.AppendLine();

        sb.AppendLine("## Cobertura");
        sb.AppendLine($"- Unidades procesadas: {session.Units.Count}");
        sb.AppendLine($"- Pendientes tras la sesión: {pendingUnits}");
        sb.AppendLine($"- Grandes: {largeUnits}");
        sb.AppendLine();

        SessionCounters cn = session.Counters;
        int total = cn.New + cn.Confirmed + cn.Resolved + cn.SilencedRespected;
        double criterioPct = total == 0 ? 0 : 100.0 * newFindings.Count(f => f.Tag == FindingTag.Criterio) / Math.Max(1, newFindings.Count);
        sb.AppendLine("## Resumen de hallazgos");
        sb.AppendLine($"- Nuevos: {cn.New}  · Confirmados: {cn.Confirmed}  · Resueltos: {cn.Resolved}"
            + $"  · Silenciados respetados: {cn.SilencedRespected}  · Reincidencias: {cn.Recurrences}");
        sb.AppendLine($"- % criterio (informativo): {criterioPct:0}%");
        sb.AppendLine();

        if (newFindings.Count > 0)
        {
            sb.AppendLine("## Hallazgos nuevos");
            foreach (Finding f in newFindings.OrderBy(f => f.Severity))
            {
                string loc = f.Locations.Count > 0 ? $"{f.Locations[0].Path}:{f.Locations[0].Line}" : "";
                sb.AppendLine($"### [{f.Severity}] {f.Title}");
                sb.AppendLine($"- `{f.RuleId}` · {f.Pillar} · confianza {f.Confidence} · {loc}");
                sb.AppendLine($"- {f.Description}");
                if (!string.IsNullOrWhiteSpace(f.Recommendation))
                {
                    sb.AppendLine($"- **Recomendación**: {f.Recommendation}");
                }

                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>Consolidated cycle-close report (§7): ascended confidences + top-10 priorities.</summary>
    public static string BuildCycleCloseReport(AppConfig app, int closedCycle, int promoted, IReadOnlyList<Finding> findings)
    {
        var active = findings.Where(f => f.Status == FindingStatus.Activo).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"# Cierre de ciclo {closedCycle} — {app.Name}");
        sb.AppendLine();
        sb.AppendLine($"- Confianzas ascendidas media→alta: {promoted}");
        sb.AppendLine($"- Hallazgos activos: {active.Count}");
        foreach (Severity sev in Enum.GetValues<Severity>())
        {
            sb.AppendLine($"  - {sev}: {active.Count(f => f.Severity == sev)}");
        }

        sb.AppendLine();
        sb.AppendLine("## Top 10 prioridades");
        foreach (Finding f in active.OrderBy(f => f.Severity).ThenBy(f => f.Confidence).Take(10))
        {
            string loc = f.Locations.Count > 0 ? $"{f.Locations[0].Path}:{f.Locations[0].Line}" : "";
            sb.AppendLine($"- **[{f.Severity}/{f.Confidence}]** {f.Title} — `{f.RuleId}` {loc}");
        }

        return sb.ToString();
    }
}
