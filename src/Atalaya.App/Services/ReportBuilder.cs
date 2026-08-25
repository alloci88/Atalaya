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
            + (session.Usage.CacheReadTokens > 0 || session.Usage.CacheWriteTokens > 0
                ? $", caché lectura {session.Usage.CacheReadTokens}, escritura {session.Usage.CacheWriteTokens}"
                : "")
            + (session.Usage.Cost is { } c
                ? $", coste {c:0.####} {(string.IsNullOrWhiteSpace(session.Usage.Currency) ? "(unidad SDK)" : session.Usage.Currency)}"
                : ""));
        sb.AppendLine();

        sb.AppendLine("## Cobertura");
        sb.AppendLine($"- Unidades procesadas: {session.Units.Count}");
        sb.AppendLine($"- Pendientes tras la sesión: {pendingUnits}");
        sb.AppendLine($"- Grandes: {largeUnits}");
        // Lo que el auditor declara haber revisado, unidad por unidad. Sin esto la cobertura
        // solo existia dentro del JSON de la sesion y no habia forma de juzgarla de un vistazo.
        foreach (UnitVerdictRecord u in session.Units.Where(u => !string.IsNullOrWhiteSpace(u.Summary) || u.Passes is { Count: > 0 }))
        {
            sb.AppendLine($"  - **{u.Unit}** ({u.Verdict})");
            // Desglose del barrido (F4.1). Las pasadas son internas para el usuario, pero tienen
            // que ser auditables: son la prueba de si la unidad llego a cubrirse o no.
            if (u.Passes is { Count: > 0 })
            {
                string trace = string.Join(" · ", u.Passes.Select(pp =>
                    pp.Dry
                        ? $"pasada {pp.Index}: seca"
                        : $"pasada {pp.Index}: {pp.New} nuevos"
                          + (pp.LocationsAdded > 0 ? $", {pp.LocationsAdded} ubicaciones" : "")));
                sb.AppendLine($"    - Barrido: {trace}");
            }

            if (!string.IsNullOrWhiteSpace(u.Summary))
            {
                sb.AppendLine($"    - {u.Summary}");
            }

            // La cobertura declarada por pasada: es una afirmacion del modelo, no una prueba
            // (2026-08-25), pero comparada entre pasadas ensena que zonas revisita.
            foreach (UnitPassRecord pp in (u.Passes ?? new List<UnitPassRecord>()).Where(pp => !string.IsNullOrWhiteSpace(pp.Summary)))
            {
                sb.AppendLine($"    - Pasada {pp.Index}: {pp.Summary}");
            }
        }

        sb.AppendLine();

        SessionCounters cn = session.Counters;
        int total = cn.New + cn.Confirmed + cn.Resolved + cn.SilencedRespected + cn.NoVerificables;
        double criterioPct = total == 0 ? 0 : 100.0 * newFindings.Count(f => f.Tag == FindingTag.Criterio) / Math.Max(1, newFindings.Count);
        sb.AppendLine("## Resumen de hallazgos");
        sb.AppendLine($"- Nuevos: {cn.New}  · Confirmados: {cn.Confirmed}  · Resueltos: {cn.Resolved}"
            + $"  · Silenciados respetados: {cn.SilencedRespected}");
        // F4: números con causa. Solo aparecen si los hay, y siempre acompañados del detalle de
        // qué unidades y qué hallazgos los produjeron (secciones de abajo).
        if (cn.LocationsAdded > 0)
        {
            sb.AppendLine($"- Ubicaciones añadidas a hallazgos existentes: {cn.LocationsAdded}"
                + " (un defecto sistémico es un hallazgo con varias ubicaciones)");
        }

        if (cn.NoVerificables > 0)
        {
            sb.AppendLine($"- No verificables (marcados para revisión): {cn.NoVerificables}");
        }

        var incompletas = session.Units.Where(u => u.MissingVerdicts > 0).ToList();
        if (incompletas.Count > 0)
        {
            sb.AppendLine($"- ⚠ Unidades incompletas: {incompletas.Count}"
                + $" ({incompletas.Sum(u => u.MissingVerdicts)} hallazgo(s) sin veredicto del auditor, intactos)");
        }
        sb.AppendLine($"- % criterio (informativo): {criterioPct:0}%");
        if (cn.Rejected > 0)
        {
            sb.AppendLine($"- ⚠ Payloads rechazados por validación: {cn.Rejected}");
        }

        sb.AppendLine();

        // F3.1 Bloque 0: cualquier unidad cortada por presupuesto (o con rechazos) se narra explícitamente
        // para que un “Nuevos 0” nunca vuelva a aparecer sin causa visible en el informe.
        var incidencias = session.Units
            .Where(u => u.Verdict is "presupuesto-superado" or "incompleta" || u.RejectedPayloads > 0)
            .ToList();
        if (incidencias.Count > 0)
        {
            sb.AppendLine("## Incidencias por unidad");
            foreach (UnitVerdictRecord u in incidencias)
            {
                sb.AppendLine($"- **{u.Unit}** — {u.Verdict}: {u.Summary}");
            }

            sb.AppendLine();
            // Qué hallazgos concretos quedaron sin veredicto: las notas de la sesión los nombran
            // por ULID. Sin esto, "1 incompleta" sería un número sin causa.
            var sinVeredicto = session.Notes.Where(n => n.Contains(": sin veredicto · ")).ToList();
            if (sinVeredicto.Count > 0)
            {
                sb.AppendLine("### Hallazgos sin veredicto (no modificados)");
                foreach (string n in sinVeredicto)
                {
                    sb.AppendLine($"- {n.Replace(": sin veredicto · ", " · ")}");
                }

                sb.AppendLine();
            }
        }

        if (session.UsageBreakdown.Count > 0)
        {
            sb.AppendLine("## Desglose por unidad (instrumentación Hito 1a)");
            sb.AppendLine("| Unidad | Prompt~ | Llamadas | ToolCalls | In | Out | CacheRead | CacheWrite |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
            foreach (UnitUsageBreakdown b in session.UsageBreakdown)
            {
                sb.AppendLine($"| {b.Unit} | {b.PromptTokensEstimate} | {b.Calls} | {b.ToolCalls} "
                    + $"| {b.InputTokens} | {b.OutputTokens} | {b.CacheReadTokens} | {b.CacheWriteTokens} |");
            }

            sb.AppendLine();
        }

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
