using System.Globalization;
using System.Text;
using Atalaya.Domain;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>Builds the immutable per-session markdown report (§7).</summary>
public static class ReportBuilder
{
    /// <summary>
    /// La cultura de los informes (F8.1). <b>Explícita, no la ambiente.</b>
    /// <para>
    /// Un informe se escribe en el hub y lo lee todo el equipo, así que tiene que salir IGUAL
    /// desde cualquier máquina: con la cultura ambiente, la misma sesión escrita desde un Windows
    /// en inglés y desde uno en español producía dos textos distintos, y «1,234» significaba
    /// 1,234 en uno y 1234 en el otro. <c>AppCulture.Apply()</c> ya deja el proceso en es-ES, pero
    /// eso solo vale dentro de la aplicación: aquí se dice a mano para que un informe generado
    /// desde un test, un script o un hilo que nadie previó salga exactamente igual.
    /// </para>
    /// </summary>
    private static CultureInfo Culture => AppCulture.Display;

    public static string BuildSessionReport(
        AppConfig app,
        AuditSession session,
        IReadOnlyList<Finding> newFindings,
        int pendingUnits,
        int largeUnits,
        string? organization = null,
        ModelRateTable? rates = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Informe de sesión — {app.Name}");
        sb.AppendLine();
        sb.AppendLine($"- **Modo**: {session.Mode}");
        sb.AppendLine(Culture, $"- **Fecha**: {session.StartedUtc:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"- **Autor**: {session.By} ({session.Machine})");
        sb.AppendLine($"- **Commit auditado**: {session.Commit}");
        sb.AppendLine($"- **Modelo**: {session.Model ?? "n/d"}");
        // F5.1: el tope del barrido va en el informe porque sin él «cobertura posiblemente
        // incompleta» no se puede interpretar: no es lo mismo agotar 5 pasadas que agotar 1.
        // 0 = sesión anterior a F5.1, donde el tope no se registraba.
        if (session.MaxPassesPerUnit > 0)
        {
            sb.AppendLine($"- **Pasadas del barrido (tope)**: {session.MaxPassesPerUnit} por unidad");
        }

        sb.AppendLine($"- **Ciclo**: {session.CycleN}");
        // F15 — los TOKENS son el hecho primario y se escriben enteros; el coste es un derivado y
        // va detrás. Un informe es inmutable, así que dentro de un año alguien podrá recalcular ese
        // coste con otra tarifa a partir de estos mismos números.
        sb.AppendLine($"- **Tokens**: entrada {session.Usage.InputTokens}, salida {session.Usage.OutputTokens}"
            + (session.Usage.CacheReadTokens > 0 || session.Usage.CacheWriteTokens > 0
                ? $", caché lectura {session.Usage.CacheReadTokens}, escritura {session.Usage.CacheWriteTokens}"
                : ""));

        CostResult cost = CreditCalculator.Calculate(session, rates);
        sb.AppendLine(cost.HasValue
            ? $"- **Coste**: {CreditText.Of(cost.Credits)} ({CreditText.LabelFor(session.Provider)})"
            : $"- **Coste**: no calculable ({CreditText.Reason(cost.Why)})");
        sb.AppendLine();

        sb.AppendLine("## Cobertura");
        if (session.Interrupted)
        {
            sb.AppendLine("- ⚠ **Sesión detenida por el usuario**: no cubrió todas sus unidades. "
                + "Lo auditado hasta la parada sí está registrado aquí.");
        }

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
        // BUGFIX-REDONDEO: se guardan los ENTEROS, no el porcentaje ya calculado. 1 de 500 es
        // «0,2 %», no «0%» — y el informe es justo donde peor sienta un número redondeado a nada.
        int criterioShare = newFindings.Count(f => f.Tag == FindingTag.Criterio);
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

        // F5.12: lo que costó tener tipos de problema silenciados en esta app. No es un aviso —el
        // silencio es una decisión tomada a conciencia— pero tiene que verse: un número que no sale
        // es una decisión que nadie revisa. El desglose por patrón va en la misma línea, y la
        // sección de abajo dice qué patrón era cada uno.
        if (cn.SuppressedByPattern > 0)
        {
            string breakdown = session.SuppressionsByPattern.Count == 0
                ? string.Empty
                : " (" + string.Join(", ", session.SuppressionsByPattern
                    .Select(t => $"patrón {t.PatternId}: {t.Count}")) + ")";
            sb.AppendLine($"- Suprimidos por patrón: {cn.SuppressedByPattern}{breakdown}"
                + " — el auditor no los reportó por corresponder a un tipo silenciado");
        }

        // F5.1b: los dos números que impiden que un desacuerdo del modelo pase por resolución.
        // Nunca aparecen sin la sección que los detalla, más abajo.
        if (cn.ResolutionsRefused > 0)
        {
            sb.AppendLine($"- ⚠ «Arreglado» sin evidencia de cambio, degradados a presente: {cn.ResolutionsRefused}"
                + " (resolver exige que la unidad haya cambiado)");
        }

        if (cn.Disputed > 0)
        {
            sb.AppendLine($"- Disputados (el auditor sostiene que nunca fueron defecto): {cn.Disputed}"
                + " — no resueltos: esperan decisión humana");
        }

        var incompletas = session.Units.Where(u => u.MissingVerdicts > 0).ToList();
        if (incompletas.Count > 0)
        {
            sb.AppendLine($"- ⚠ Unidades incompletas: {incompletas.Count}"
                + $" ({incompletas.Sum(u => u.MissingVerdicts)} hallazgo(s) sin veredicto del auditor, intactos)");
        }
        sb.AppendLine($"- % criterio (informativo): {PercentText.Of(criterioShare, newFindings.Count)}");
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

        // F5.12: qué se suprimió, patrón por patrón. El contador de arriba dice cuánto; esto dice
        // qué tipo de problema era, que es lo único con lo que se puede decidir si el patrón sigue
        // teniendo sentido. El ejemplar se escribe aquí y no solo su id: el informe tiene que
        // seguir explicándose solo cuando el patrón se des-silencie.
        if (session.SuppressionsByPattern.Count > 0)
        {
            sb.AppendLine("## Detecciones suprimidas por patrón silenciado");
            sb.AppendLine();
            sb.AppendLine("El auditor encontró estos tipos de problema y NO los reportó, porque el equipo los");
            sb.AppendLine("silenció para esta aplicación. El juicio de que un hallazgo es «de este tipo» lo hace");
            sb.AppendLine("el auditor mirando el código: no es un filtro automático, y por eso se cuenta aquí.");
            sb.AppendLine();
            foreach (PatternSuppressionTally t in session.SuppressionsByPattern)
            {
                sb.AppendLine($"- **{t.PatternId}** · {t.Exemplar} — {t.Count} detección(es)");
            }

            sb.AppendLine();
            var porUnidad = session.Notes.Where(n => n.Contains(": suprimido por patrón · ")).ToList();
            if (porUnidad.Count > 0)
            {
                sb.AppendLine("### Por unidad");
                foreach (string n in porUnidad)
                {
                    sb.AppendLine($"- {n.Replace(": suprimido por patrón · ", " · ")}");
                }

                sb.AppendLine();
            }
        }

        AppendDirectives(sb, session);

        // F5.1b: qué veredictos no se aplicaron tal cual y por qué. Las notas los nombran por ULID;
        // sin esta sección, «1 degradado» sería otro número sin causa (D-060).
        var degradados = session.Notes.Where(n => n.Contains(": veredicto degradado · ")).ToList();
        if (degradados.Count > 0)
        {
            sb.AppendLine("## Veredictos que la app no aplicó tal cual");
            sb.AppendLine();
            sb.AppendLine("Un «arreglado» solo resuelve si la unidad cambió desde la última vez que se vio el");
            sb.AppendLine("hallazgo. Sin esa evidencia se degrada a presente. Y una discrepancia de criterio");
            sb.AppendLine("(«no es un defecto») marca el hallazgo como disputado, sin cerrarlo.");
            sb.AppendLine();
            foreach (string n in degradados)
            {
                sb.AppendLine($"- {n.Replace(": veredicto degradado · ", " · ")}");
            }

            sb.AppendLine();
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
                sb.AppendLine($"### [{f.Severity}] {Alias(f)}{f.Title}");
                sb.AppendLine($"- `{f.RuleId}` · {f.Pillar} · confianza {f.Confidence} · {loc}");
                sb.AppendLine($"- {f.Description}");
                if (!string.IsNullOrWhiteSpace(f.Recommendation))
                {
                    sb.AppendLine($"- **Recomendación**: {f.Recommendation}");
                }

                sb.AppendLine();
            }
        }

        Sign(sb, organization);
        return sb.ToString();
    }

    /// <summary>
    /// La firma del pie (F6.4 §2): quién generó esto y para quién. Es una LÍNEA, no un membrete —
    /// un informe de auditoría se lee, no se enmarca— y va en texto porque el markdown tiene que
    /// seguir siendo legible en cualquier visor, incluido un <c>cat</c> en una terminal.
    /// <para>
    /// Sin organización se firma solo con «Atalaya»: escribir «Atalaya ·» y nada detrás sería
    /// enseñar el hueco de un dato que el hub todavía no da.
    /// </para>
    /// </summary>
    private static void Sign(StringBuilder sb, string? organization)
    {
        sb.AppendLine("---");
        sb.AppendLine(string.IsNullOrWhiteSpace(organization)
            ? "Atalaya"
            : $"Atalaya · {organization.Trim()}");
    }

    /// <summary>
    /// El alias legible delante del título, cuando lo tiene (F5.6, D-229). Un informe que solo
    /// escribe el título obliga a volver a la aplicación para saber de qué hallazgo habla.
    /// </summary>
    private static string Alias(Finding f)
        => string.IsNullOrEmpty(f.DisplayId) ? string.Empty : $"{f.DisplayId} · ";

    /// <summary>
    /// El informe de una sesión de ARREGLO asistido (F6.9 §5).
    /// <para>
    /// Tiene forma propia porque cuenta otra cosa: no hay unidades auditadas ni veredictos, hay
    /// ficheros tocados, una compilación y una sugerencia de commit. Lo que comparte con los demás
    /// es la disciplina — quién, cuándo, con qué modelo, cuánto costó y qué quedó declarado como
    /// riesgo—, y sobre todo el recordatorio de que <b>nada se ha commiteado</b>: leer este informe
    /// no puede dejar entender que el arreglo ya está publicado.
    /// </para>
    /// </summary>
    /// <param name="files">Ruta, recuento de líneas y si el fichero era del hallazgo.</param>
    /// <param name="build">Resumen del último build/tests, o null si no se llegó a pedir.</param>
    public static string BuildFixReport(
        AppConfig? app,
        AuditSession session,
        Finding finding,
        IReadOnlyList<(string Path, string Tally, bool InScope)> files,
        string summary,
        string? risks,
        string commitTitle,
        string commitDescription,
        BuildVerdict? build,
        string? organization = null,
        FixTestSituation? tests = null,
        ModelRateTable? rates = null)
    {
        string appName = app?.Name ?? session.AppSlug;
        string alias = finding.DisplayId ?? finding.Id.ToString();
        var sb = new StringBuilder();

        sb.AppendLine($"# Arreglo asistido — {appName}");
        sb.AppendLine();
        sb.AppendLine($"- **Modo**: {session.Mode}");
        sb.AppendLine($"- **Hallazgo**: {alias} — {finding.Title}");
        sb.AppendLine($"- **Severidad**: {finding.Severity}");
        sb.AppendLine(Culture, $"- **Fecha**: {session.StartedUtc:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"- **Autor**: {session.By} ({session.Machine})");
        sb.AppendLine($"- **Commit del clon al empezar**: {session.Commit}");
        sb.AppendLine($"- **Modelo**: {session.Model ?? "n/d"}");
        sb.AppendLine($"- **Tokens**: entrada {session.Usage.InputTokens}, salida {session.Usage.OutputTokens}"
            + (session.Usage.CacheReadTokens > 0 || session.Usage.CacheWriteTokens > 0
                ? $", caché lectura {session.Usage.CacheReadTokens}, escritura {session.Usage.CacheWriteTokens}"
                : ""));

        CostResult fixCost = CreditCalculator.Calculate(session, rates);
        sb.AppendLine(fixCost.HasValue
            ? $"- **Coste**: {CreditText.Of(fixCost.Credits)} ({CreditText.LabelFor(session.Provider)})"
            : $"- **Coste**: no calculable ({CreditText.Reason(fixCost.Why)})");
        if (session.Interrupted)
        {
            sb.AppendLine("- ⚠ **Sesión detenida por el usuario**: el agente no llegó a cerrar el arreglo.");
        }

        sb.AppendLine();
        sb.AppendLine("> **Estos cambios NO están commiteados.** El arreglo asistido escribe en el clon "
            + "local de quien lo lanzó y ahí se queda: revisar, commitear y publicar sigue siendo suyo. "
            + "Y arreglar no resuelve el hallazgo — la resolución llega verificando, con evidencia.");
        sb.AppendLine();

        AppendDirectives(sb, session);

        sb.AppendLine("## Qué cambió y por qué");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(summary) ? "_(el agente no dejó resumen)_" : summary.Trim());
        sb.AppendLine();

        sb.AppendLine("## Ficheros tocados");
        if (files.Count == 0)
        {
            sb.AppendLine("- Ninguno.");
        }
        else
        {
            foreach ((string path, string tally, bool inScope) in files)
            {
                sb.AppendLine($"- `{path}` ({tally})"
                    + (inScope ? string.Empty : " — **fuera del hallazgo**, autorizado por el usuario"));
            }
        }

        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(risks))
        {
            sb.AppendLine("## Riesgos declarados");
            sb.AppendLine();
            sb.AppendLine(risks!.Trim());
            sb.AppendLine();
        }

        AppendBuildSection(sb, build, tests);

        sb.AppendLine();
        sb.AppendLine("## Sugerencia de commit");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(commitTitle.Trim());
        if (!string.IsNullOrWhiteSpace(commitDescription))
        {
            sb.AppendLine();
            sb.AppendLine(commitDescription.Trim());
        }

        sb.AppendLine("```");
        sb.AppendLine();
        Sign(sb, organization);
        return sb.ToString();
    }

    /// <summary>
    /// La compilación, contada con honestidad (H9.1 §2).
    /// <para>
    /// Qué se compiló, cuántos errores son del cambio y cuántos ya estaban. El desglose de lo
    /// preexistente va aparte y con su nombre: un informe que presenta 18 errores heredados como
    /// resultado de un arreglo de dos líneas convierte cada sesión en un susto, y a la tercera
    /// nadie lee la sección.
    /// </para>
    /// </summary>
    internal static void AppendBuildSection(
        StringBuilder sb, BuildVerdict? build, FixTestSituation? tests = null)
    {
        sb.AppendLine("## Compilación y tests");
        sb.AppendLine();

        // H9.1 §3: si el proyecto no tiene tests se dice UNA vez, como hecho del proyecto. No es
        // un riesgo del arreglo ni el resultado de una búsqueda infructuosa.
        if (tests is { HasTests: false })
        {
            sb.AppendLine(tests.AnyInClone
                ? $"> El proyecto afectado (`{tests.Project ?? "n/d"}`) **no tiene proyecto de tests** "
                  + "que lo cubra. Es un hecho del repositorio, conocido antes de empezar."
                : "> Esta solución **no tiene proyectos de tests**. Es un hecho del repositorio, "
                  + "conocido antes de empezar: el arreglo se verifica compilando y leyendo el código.");
            sb.AppendLine();
        }

        if (build is null)
        {
            sb.AppendLine("No se pidió compilar durante la sesión.");
            return;
        }

        sb.AppendLine($"- **Ámbito**: {(build.TargetLabel.Length > 0 ? build.TargetLabel : "n/d")}");
        sb.AppendLine($"- **Veredicto**: {build.Headline}");
        sb.AppendLine($"- **Tests**: {TestsLine(build, tests)}");
        sb.AppendLine(build.HasBaseline
            ? $"- **Línea base**: {build.BaselineNote}"
            : "- **Línea base**: no había ninguna para este commit, así que todo error contado como nuevo "
              + "podría ser anterior al cambio.");
        sb.AppendLine();

        if (build.New.Count > 0)
        {
            sb.AppendLine($"### Errores nuevos ({build.New.Count}) — los ha traído este cambio");
            sb.AppendLine();
            sb.AppendLine("```");
            foreach (string line in build.New)
            {
                sb.AppendLine(line);
            }

            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (build.Preexisting.Count > 0)
        {
            sb.AppendLine($"### Preexistentes ({build.Preexisting.Count}) — ya fallaban antes del arreglo");
            sb.AppendLine();
            sb.AppendLine("No son del cambio y no cuentan en el veredicto. Se listan porque quien lea "
                + "este informe verá esos errores al compilar, y tiene derecho a saber que ya estaban.");
            sb.AppendLine();
            sb.AppendLine("```");
            foreach (string line in build.Preexisting)
            {
                sb.AppendLine(line);
            }

            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (build.Excluded.Count > 0)
        {
            sb.AppendLine("### Fuera del alcance de la comprobación");
            sb.AppendLine();
            foreach (string project in build.Excluded)
            {
                sb.AppendLine($"- `{project}` requiere el toolset C++ de Visual Studio; "
                    + "`dotnet build` no puede compilarlo y no se cuenta como fallo.");
            }

            sb.AppendLine();
        }

        sb.AppendLine("<details><summary>Salida completa</summary>");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(build.Summary.TrimEnd());
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("</details>");
    }

    /// <summary>Lo que se dice de los tests: pasan, no pasan, o no hay — que no es lo mismo.</summary>
    private static string TestsLine(BuildVerdict build, FixTestSituation? tests)
    {
        if (build.TestsRun)
        {
            return build.TestsOk ? "pasan" : "**NO pasan**";
        }

        return tests is { HasTests: false } ? "no hay en este proyecto" : "no se ejecutaron";
    }

    /// <summary>Consolidated cycle-close report (§7): ascended confidences + top-10 priorities.</summary>
    /// <param name="aging">
    /// Lo que quedaba envejecido al cerrar (F9.2 §2). Cerrar no maquilla: si el código se movió
    /// mientras duraba el ciclo, el informe lo dice — es lo que el ciclo siguiente hereda.
    /// </param>
    public static string BuildCycleCloseReport(
        AppConfig app, int closedCycle, int promoted, IReadOnlyList<Finding> findings,
        string? organization = null, CycleAging? aging = null)
    {
        var active = findings.Where(f => f.Status == FindingStatus.Activo).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"# Cierre de ciclo {closedCycle} — {app.Name}");
        sb.AppendLine();

        // A cero no se dice nada: una frase que informa de que no hay nada que informar es ruido.
        if (aging?.Sentence is { } aged)
        {
            sb.AppendLine(aged);
            sb.AppendLine();
            sb.AppendLine("Las cambiadas las hereda el ciclo siguiente como **pendientes**; las arregladas");
            sb.AppendLine("conservan su estado y su acción **Verificar**, que es su cierre correcto.");
            sb.AppendLine();
        }

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
            sb.AppendLine($"- **[{f.Severity}/{f.Confidence}]** {Alias(f)}{f.Title} — `{f.RuleId}` {loc}");
        }

        sb.AppendLine();
        Sign(sb, organization);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ F7 · directivas

    /// <summary>
    /// Con qué convenciones del proyecto se hizo esta sesión (F7 §3).
    /// <para>
    /// Va con el hash del contenido íntegro porque las directivas viven en el repo de la
    /// aplicación y cambian con él: sin el hash, un informe de hace dos meses diría que hubo
    /// convenciones pero no cuáles, y volver al fichero de aquel día sería imposible. Se nombran
    /// también las truncadas y las omitidas por presupuesto — que es justo lo que explicaría por
    /// qué el auditor no vio algo.
    /// </para>
    /// </summary>
    private static void AppendDirectives(StringBuilder sb, AuditSession session)
    {
        if (session.Directives.Count == 0)
        {
            return;
        }

        sb.AppendLine("## Directivas del proyecto que viajaron");
        sb.AppendLine();
        sb.AppendLine("Las convenciones intencionales que el equipo mantiene en el repositorio de la");
        sb.AppendLine("aplicación y que se le enseñaron al modelo en esta sesión. Informan el criterio;");
        sb.AppendLine("nunca cambian las reglas de operación de Atalaya.");
        sb.AppendLine();
        foreach (DirectiveRecord d in session.Directives)
        {
            string state = d.Omitted
                ? " — **omitida por presupuesto** (no viajó)"
                : d.Truncated ? " — **truncada**: solo viajó su principio" : string.Empty;
            sb.AppendLine($"- `{d.Path}` · {d.ContentHash}{state}");
        }

        sb.AppendLine();
    }
}
