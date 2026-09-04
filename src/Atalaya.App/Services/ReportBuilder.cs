using System.Globalization;
using System.Text;
using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;

using Atalaya.Copilot;

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

    /// <summary>
    /// Con qué CASA se hizo, junto al modelo (F16 §C).
    /// <para>
    /// El modelo solo no basta: «claude-opus-4.6» no dice si detrás hubo un CLI local o el asiento
    /// de la organización, y eso cambia qué cuota se gastó, qué superficie tuvo el agente y con
    /// quién hay que hablar cuando algo no cuadra. Sobre todo, es lo que hace legible una
    /// discrepancia: dos casas distintas coincidiendo es una segunda opinión, y tres modelos de la
    /// misma pueden compartir el mismo punto ciego (D-781).
    /// </para>
    /// <para>
    /// Las sesiones anteriores a F14 no lo traen y eso NO es un dato que falte: era Copilot, porque
    /// no había otro. Se nombra así, sin marcarlas de «desconocido».
    /// </para>
    /// </summary>
    internal static string ProviderLine(AuditSession session)
        => $"- **Proveedor**: {ProviderNames.Display(session.Provider)}";

    /// <summary>
    /// Los tokens por tipo y las llamadas al modelo. <b>Los tokens son el hecho</b> y por eso van
    /// enteros: dentro de un año alguien puede recalcular el coste con otra tarifa a partir de
    /// estos mismos números (D-788). Las llamadas van al lado porque son la otra magnitud que se
    /// puede comparar entre sesiones sin saber nada de precios.
    /// <para>
    /// La caché solo se nombra cuando la hay: con Copilot en una sesión corta puede no haberla, y
    /// un «caché 0» se lee como «no usó caché» cuando lo que pasa es que el proveedor no la
    /// informó.
    /// </para>
    /// </summary>
    private static string UsageLine(AuditSession session)
        => $"- **Tokens**: entrada {session.Usage.InputTokens}, salida {session.Usage.OutputTokens}"
        + (session.Usage.CacheReadTokens > 0 || session.Usage.CacheWriteTokens > 0
            ? $", caché lectura {session.Usage.CacheReadTokens}, escritura {session.Usage.CacheWriteTokens}"
            : string.Empty)
        + (session.Usage.Calls > 0 ? $" · **{session.Usage.Calls} llamada(s) al modelo**" : string.Empty);

    /// <summary>
    /// Lo que el CLI del proveedor DECLARÓ que costó, tal cual y con su unidad (F16-RETOQUE §1).
    /// <para>
    /// <b>Es un dato del proveedor, no el coste de la sesión.</b> El CLI de Claude Code publica un
    /// <c>total_cost_usd</c> que él mismo etiqueta <c>"costBasis": "list"</c>: lo que habrían
    /// costado esos tokens pagando la API. La suscripción no factura por tokens, así que ese número
    /// no le llega a nadie en ninguna factura — y por eso ni se presenta como coste ni entra en
    /// ninguna métrica.
    /// </para>
    /// <para>
    /// <b>Y aun así se escribe</b>, en una línea aparte y diciendo lo que es. Viene gratis —el CLI
    /// lo manda solo—, no obliga a mantener ninguna tabla de precios y es una medida independiente
    /// de la nuestra: el día que alguien quiera comparar el peso de dos sesiones, o comprobar si
    /// nuestros tokens cuadran con los suyos, está ahí. Solo aparece cuando llegó.
    /// </para>
    /// </summary>
    private static void AppendDeclaredCost(StringBuilder sb, AuditSession session)
    {
        if (CreditCalculator.IsBilled(session.Provider) || session.Usage.Cost is not { } declared)
        {
            return;
        }

        string unit = string.IsNullOrWhiteSpace(session.Usage.Currency)
            ? "USD (tarifa de lista)"
            : session.Usage.Currency!;

        string amount = declared.ToString("0.######", Culture);
        sb.AppendLine($"- **Lo que declaró el CLI**: {amount} {unit} — dato del proveedor, no el "
            + "coste de esta sesión y no entra en ninguna métrica.");
    }

    /// <summary>
    /// <b>La línea que hasta F18 había que calcular a mano</b> (§1): en qué se reparte una llamada.
    /// <para>
    /// Los tokens totales de una sesión no dicen dónde actuar. Lo que sí lo dice es que el código
    /// auditado sea el 2 % de lo que se manda, o que hagan falta once llamadas para una clase de
    /// cuarenta líneas — y las dos cosas caben en un renglón. Va justo debajo de los tokens porque
    /// es su lectura, no un apartado nuevo.
    /// </para>
    /// <para>
    /// No aparece cuando no hay con qué escribirla: en las sesiones anteriores a F18 no hay
    /// desglose por pasada, y una línea con un 0 % afirmaría que no viajó código.
    /// </para>
    /// </summary>
    /// <summary>
    /// <b>En qué se reparte el coste</b> (F20 §1). Va pegada al coste porque es su lectura: el
    /// total dice cuánto y esto dice de qué. Con tarifas que difieren doce veces entre leer caché y
    /// escribirla, el desglose en tokens que ya estaba arriba no permite decidir nada — dos cifras
    /// parecidas pueden costar trece veces distinto.
    /// </summary>
    private static void AppendCostSplit(StringBuilder sb, CostResult cost)
    {
        string line = CreditText.CostSplitLine(cost);
        if (line.Length > 0)
        {
            sb.AppendLine($"- **Reparto del coste**: {line}");
        }
    }

    private static void AppendBudgetLine(StringBuilder sb, AuditSession session)
    {
        PromptBudget budget = PromptBudget.From(session);
        if (budget.Line.Length == 0)
        {
            return;
        }

        sb.AppendLine($"- **Composición**: {budget.Line}");

        // El termómetro del prefijo inestable (F18 §2). Solo cuando hay escritura de caché que
        // interpretar: sin ella no hay nada que diagnosticar y la frase sobraría.
        if (budget.CacheWriteTokens > 0)
        {
            sb.AppendLine(
                $"- **Caché**: {budget.CacheWriteTokens} escritos, {budget.CacheReadTokens} leídos · "
                + $"suelo inevitable ≈ {budget.CacheWriteFloor} (prefijo estable {budget.StablePrefixTokens} "
                + $"× 1 + parte variable de {budget.Prompts} prompt(s)) · "
                + $"re-escrituras ≈ {budget.CacheRewrites}");
        }
    }

    /// <summary>
    /// <b>Pasada a pasada, y de qué estaba hecho cada prompt</b> (F18 §1). Es el nivel que faltaba:
    /// una unidad son N pasadas y cada una manda su prompt entero, así que sin esto no se puede
    /// distinguir «abrir la unidad cuesta» de «insistir sobre ella cuesta».
    /// <para>
    /// Las columnas de composición son ESTIMACIONES (la regla de siempre, ~4 caracteres por token),
    /// y por eso van marcadas con ~. No hacen falta exactas: lo que se decide con ellas —dónde está
    /// el peso— no cambia porque la cuenta se desvíe.
    /// </para>
    /// </summary>
    private static void AppendPassBreakdown(StringBuilder sb, AuditSession session)
    {
        if (!session.UsageBreakdown.Any(u => u.Passes.Count > 0))
        {
            return;
        }

        sb.AppendLine("### Por pasada, y de qué se compone el prompt");
        sb.AppendLine();
        sb.AppendLine("Los tokens de composición son estimados (~4 caracteres por token); los de consumo, medidos.");
        sb.AppendLine();
        sb.AppendLine("| Unidad | Pasada | Llamadas | In | Out | CacheRead | CacheWrite | Duración "
            + "| Estable~ | Existentes~ | Unidad~ | Código % |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (UnitUsageBreakdown b in session.UsageBreakdown)
        {
            foreach (PassUsage p in b.Passes)
            {
                PromptComposition c = p.Composition ?? new PromptComposition();
                string share = c.Total > 0
                    ? (100.0 * c.Unidad / c.Total).ToString("0.#", Culture) + " %"
                    : "—";
                sb.AppendLine($"| {b.Unit} | {p.Pass} | {p.Calls} | {p.InputTokens} | {p.OutputTokens} "
                    + $"| {p.CacheReadTokens} | {p.CacheWriteTokens} | {Seconds(p.DurationMs)} "
                    + $"| {c.Estable} | {c.Existentes} | {c.Unidad} | {share} |");
            }
        }

        sb.AppendLine();
    }

    /// <summary>Milisegundos leídos como segundos con un decimal. «—» cuando no se midió.</summary>
    private static string Seconds(long ms)
        => ms <= 0 ? "—" : (ms / 1000.0).ToString("0.#", Culture) + " s";

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

        // LA CABECERA: qué, cuándo, quién, sobre qué, con qué y cuánto. Nada más (F23 §2). Todo lo
        // que había aquí de tokens, caché y composición vive ahora en el anexo — sigue registrado
        // y sigue escrito, pero no delante de quien viene a arreglar su código.
        sb.AppendLine(LocalStampLine(session));
        sb.AppendLine($"- **Autor**: {session.By} ({session.Machine})");
        sb.AppendLine($"- **Commit auditado**: {session.Commit}");
        sb.AppendLine($"{ProviderLine(session)} · **Modelo**: {session.Model ?? "n/d"}");
        // R2 §1 — el modo dice también CÓMO se barrió: «Lotes · exhaustivo» cuando cada pasada fue
        // una petición nueva. Va en la cabecera y no en el anexo porque cambia lo que el informe
        // cuesta y lo que puede duplicar, y eso se lee antes de nada.
        sb.AppendLine($"- **Ciclo**: {session.CycleN} · **Temática**: {ThemeCatalog.Display(session.Theme)}"
            + $" · **Modo**: {AuditModes.Describe(session.Mode, session.Exhaustive)}");

        CostResult cost = CreditCalculator.Calculate(session, rates);
        sb.AppendLine(CostHeadline(session, cost));
        sb.AppendLine();

        // EL RESUMEN VA PRIMERO, y contesta lo que se pregunta primero (F23 §3). Antes había que
        // atravesar cinco líneas de caché y dos tablas de tokens para llegar a «cuántos y de qué
        // gravedad» — que además no estaba: había que contarlos a mano.
        SessionCounters cn = session.Counters;
        sb.AppendLine("## Resumen");
        if (session.Interrupted)
        {
            sb.AppendLine("- ⚠ **Sesión detenida por el usuario**: no cubrió todas sus unidades. "
                + "Lo auditado hasta la parada sí está registrado aquí.");
        }

        if (SeverityLine(newFindings) is { Length: > 0 } gravedad)
        {
            sb.AppendLine($"- **Gravedad**: {gravedad}");
        }

        sb.AppendLine($"- Nuevos: {cn.New} · Confirmados: {cn.Confirmed} · Resueltos: {cn.Resolved}"
            + $" · Silenciados respetados: {cn.SilencedRespected}");
        sb.AppendLine($"- Unidades: {UnitsLine(session, pendingUnits)}");

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
        // es una decisión que nadie revisa.
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

        // Antes se llamaba «% criterio (informativo)», que no se entiende sin que alguien te lo
        // explique. Dice de dónde salen los hallazgos: de una regla del catálogo, o del juicio del
        // auditor sin regla detrás. Escrito así se lee sin nota al pie (F23 §3).
        if (newFindings.Count > 0)
        {
            int criterio = newFindings.Count(f => f.Tag == FindingTag.Criterio);
            sb.AppendLine($"- Origen: {newFindings.Count - criterio} del catálogo de reglas · "
                + $"{criterio} del criterio del auditor ({PercentText.Of(criterio, newFindings.Count)})");
        }

        if (cn.Rejected > 0)
        {
            sb.AppendLine($"- ⚠ Payloads rechazados por validación: {cn.Rejected}");
        }

        sb.AppendLine();

        // COBERTURA: UNA LÍNEA POR UNIDAD, no una por pasada (F23 §4). La narrativa pasada a pasada
        // es trazabilidad —repetía siete veces la misma lista de símbolos— y se ha ido al anexo.
        sb.AppendLine("## Cobertura");
        foreach (UnitVerdictRecord u in session.Units)
        {
            sb.AppendLine($"- **{Path.GetFileName(u.Unit)}**{Folder(u.Unit)} — {CoverageLine(u, session)}");
            IReadOnlyList<string> reviewed = ReviewedMembers.From(
                (u.Passes ?? new List<UnitPassRecord>()).Select(p => AuditorText.Clean(p.Summary)));
            if (reviewed.Count > 0)
            {
                sb.AppendLine($"  - Revisados: {string.Join(", ", reviewed)}");
            }
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

        if (newFindings.Count > 0)
        {
            AppendFindings(sb, session, newFindings);
        }

        AppendAnnex(sb, session, cost, pendingUnits, largeUnits);

        Sign(sb, organization);
        return sb.ToString();
    }

    /// <summary>
    /// <b>La fecha, en la hora de quien lee</b> (F23 §6). El informe la escribía en UTC y sin
    /// decirlo: «14:14» para una sesión de las 16:14 en España. Una hora que no es la del reloj de
    /// nadie y que además no avisa es peor que ninguna — se lee como local y son dos horas menos.
    /// Va con la zona puesta para que también se entienda leyéndolo desde otro sitio.
    /// </summary>
    private static string LocalStampLine(AuditSession session)
    {
        DateTimeOffset local = session.StartedUtc.ToLocalTime();

        // El DESPLAZAMIENTO, no el nombre de la zona: Windows en español lo llama «Hora de verano
        // romance», que no ayuda a nadie y además cambia con el idioma de la máquina. «UTC+02:00»
        // lo entiende quien lo lee aquí y quien lo lee desde otro huso.
        string zone = local.Offset == TimeSpan.Zero
            ? "UTC"
            : "UTC" + (local.Offset < TimeSpan.Zero ? "-" : "+")
              + local.Offset.ToString(@"hh\:mm", Culture);
        return string.Create(Culture, $"- **Fecha**: {local:yyyy-MM-dd HH:mm} ({zone})");
    }

    /// <summary>
    /// <b>El coste en UNA línea</b> (F23 §2): total, por unidad y cuánto duró. El reparto por
    /// conceptos, los tokens y las llamadas son diagnóstico y viven en el anexo.
    /// <para>
    /// El coste por unidad es lo que hace comparables dos sesiones de tamaños distintos, y es la
    /// cifra con la que se decide si una auditoría sale a cuenta. Con una casa que no factura no
    /// hay importe que repartir y la línea dice lo que hay: la duración.
    /// </para>
    /// </summary>
    private static string CostHeadline(AuditSession session, CostResult cost)
    {
        var parts = new List<string> { CreditText.OfSession(cost, session.Provider) };

        int units = session.Units.Count;
        if (cost.Credits is { } credits && units > 0)
        {
            parts.Add(string.Create(Culture, $"{credits / units:0.#} por unidad"));
        }

        if (Elapsed(session) is { Length: > 0 } elapsed)
        {
            parts.Add(elapsed);
        }

        return $"- **Coste**: {string.Join(" · ", parts)}";
    }

    /// <summary>
    /// «1 pasada» y «6 pasadas». Se escribe en español y no con «(s)»: un informe que alguien va a
    /// leer entero no puede estar salpicado de plantillas sin resolver.
    /// </summary>
    private static string Plural(int n, string one, string many)
        => string.Create(Culture, $"{n} {(n == 1 ? one : many)}");

    /// <summary>Lo que duró la sesión, de reloj de pared. Vacío si no se registró el final.</summary>
    private static string Elapsed(AuditSession session)
    {
        if (session.EndedUtc is not { } ended || ended <= session.StartedUtc)
        {
            return string.Empty;
        }

        TimeSpan span = ended - session.StartedUtc;
        return span.TotalMinutes >= 1
            ? string.Create(Culture, $"{(int)span.TotalMinutes} min {span.Seconds} s")
            : string.Create(Culture, $"{span.Seconds} s");
    }

    /// <summary>
    /// <b>Cuántos hallazgos y de qué gravedad</b> (F23 §3). Es la primera pregunta de quien tiene
    /// que actuar y no estaba en ningún sitio: había que contar a mano las cabeceras de la lista.
    /// Solo se nombran las gravedades que existen — «0 Críticas» ocupa sitio para no decir nada.
    /// </summary>
    internal static string SeverityLine(IReadOnlyList<Finding> findings)
    {
        (Severity Severity, string One, string Many)[] names =
        {
            (Severity.Critica, "Crítica", "Críticas"),
            (Severity.Alta, "Alta", "Altas"),
            (Severity.Media, "Media", "Medias"),
            (Severity.Baja, "Baja", "Bajas"),
        };

        var parts = new List<string>();
        foreach ((Severity severity, string one, string many) in names)
        {
            int n = findings.Count(f => f.Severity == severity);
            if (n > 0)
            {
                parts.Add($"{n} {(n == 1 ? one : many)}");
            }
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Cuántas unidades se auditaron y <b>cómo cerraron</b> (F23 §3). «2 auditadas» no dice si el
    /// barrido convergió o se quedó a medias, que es lo que decide si hay que volver.
    /// </summary>
    private static string UnitsLine(AuditSession session, int pendingUnits)
    {
        int total = session.Units.Count;
        int incompletas = session.Units.Count(u => u.CoverageIncomplete);
        int cortadas = session.Units.Count(u => u.Verdict == "presupuesto-superado");
        int completas = total - incompletas - cortadas;

        var parts = new List<string>();
        if (completas > 0)
        {
            parts.Add(Plural(completas, "completa", "completas"));
        }

        if (incompletas > 0)
        {
            parts.Add($"{incompletas} con cobertura posiblemente incompleta");
        }

        if (cortadas > 0)
        {
            parts.Add(Plural(cortadas, "cortada por presupuesto", "cortadas por presupuesto"));
        }

        string detail = parts.Count > 0 ? ": " + string.Join(", ", parts) : string.Empty;
        string pending = pendingUnits > 0
            ? " · " + Plural(pendingUnits, "pendiente", "pendientes") + " en el inventario"
            : string.Empty;
        return $"{Plural(total, "auditada", "auditadas")}{detail}{pending}";
    }

    /// <summary>
    /// La línea de una unidad: estado, pasadas, <b>por qué dejó de barrerse</b> y qué aportó
    /// (F23 §4).
    /// <para>
    /// El motivo de cierre se deduce de lo que la sesión ya registra: un veredicto de presupuesto
    /// es un corte; <c>CoverageIncomplete</c> es haberse quedado sin pasadas; y lo demás es haber
    /// convergido. <b>Importa distinguirlos</b>: una unidad que agotó el tope <i>seguía
    /// encontrando</i>, y decir solo «6 pasadas» deja al lector creyendo que se miró entera.
    /// </para>
    /// </summary>
    private static string CoverageLine(UnitVerdictRecord u, AuditSession session)
    {
        List<UnitPassRecord> passes = u.Passes ?? new List<UnitPassRecord>();
        var parts = new List<string> { u.Verdict };

        if (passes.Count > 0)
        {
            string why;
            if (u.Verdict == "presupuesto-superado")
            {
                why = "cortada por presupuesto";
            }
            else if (u.CoverageIncomplete)
            {
                UnitPassRecord last = passes[^1];
                why = last.New > 0
                    ? $"cerrada por tope: seguía encontrando, {last.New} en la última"
                    : "cerrada por tope sin converger";
            }
            else
            {
                why = "cerrada por dos pasadas secas";
            }

            parts.Add($"{Plural(passes.Count, "pasada", "pasadas")}, {why}");

            // Una pasada en la que el auditor no llamó a NINGUNA herramienta se gastó sin entregar
            // nada. No cierra la unidad —desde F20 ni siquiera cuenta como seca—, pero es gasto sin
            // trabajo y en el informe tiene que verse.
            int mudas = session.Notes.Count(n =>
                n.StartsWith(u.Unit, StringComparison.Ordinal)
                && n.Contains("no llamó a ninguna herramienta", StringComparison.Ordinal));
            if (mudas > 0)
            {
                parts.Add(Plural(mudas, "pasada muda", "pasadas mudas"));
            }
        }

        int nuevos = passes.Sum(p => p.New);
        if (nuevos > 0)
        {
            parts.Add(Plural(nuevos, "nuevo", "nuevos"));
        }

        // Los confirmados NO se suman entre pasadas: cada pasada reconcilia los MISMOS hallazgos
        // existentes, así que sumarlos daba «70 confirmados» sobre 18 hallazgos. El recuento que
        // significa algo es el de la sesión, y está en el resumen.
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// La carpeta de una unidad, entre paréntesis. El nombre del fichero es lo que se lee, pero sin
    /// la carpeta dos <c>Common.cs</c> de módulos distintos son la misma línea — y una unidad que
    /// no aportó hallazgos no aparece en ningún otro sitio del cuerpo donde mirar la ruta.
    /// </summary>
    private static string Folder(string unit)
    {
        string folder = Path.GetDirectoryName(unit)?.Replace('\\', '/') ?? string.Empty;
        return folder.Length == 0 ? string.Empty : $" ({folder}/)";
    }

    /// <summary>
    /// <b>Los hallazgos, agrupados por unidad y ordenados por gravedad</b> (F23 §5). La ruta iba
    /// repetida en los 25 hallazgos del caso de referencia; ahora es el título del grupo y en cada
    /// hallazgo queda la línea, que es lo que cambia entre uno y otro.
    /// </summary>
    private static void AppendFindings(
        StringBuilder sb, AuditSession session, IReadOnlyList<Finding> findings)
    {
        sb.AppendLine("## Hallazgos nuevos");
        sb.AppendLine();

        // La confianza NO se escribe cuando es la que el modo reparte a todo lo que nace en esta
        // sesión: en el caso de referencia salía «confianza Media» en los 25, que es tanto como no
        // decir nada. El prompt le prohíbe al auditor asignarla —«eso es de la app»— así que es
        // función del modo, no un juicio. Se sigue guardando; se enseña solo cuando dice algo.
        Confidence usual = Domain.Rules.ConfidenceMachine.ForNew(session.Mode);

        List<Finding> ordered = findings
            .OrderBy(f => UnitOf(f), StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Severity)
            .ToList();

        IReadOnlyDictionary<Ulid, Finding> duplicates = DuplicateHints.Of(ordered);

        string? unit = null;
        foreach (Finding f in ordered)
        {
            string current = UnitOf(f);
            if (!string.Equals(current, unit, StringComparison.Ordinal))
            {
                unit = current;
                sb.AppendLine($"### {current}");
                sb.AppendLine();
            }

            string line = f.Locations.Count > 0 ? $" — línea {f.Locations[0].Line}" : string.Empty;
            sb.AppendLine($"#### [{f.Severity}] {Alias(f)}{f.Title}{line}");
            sb.AppendLine($"- `{f.RuleId}` · {f.Pillar}"
                + (f.Confidence == usual ? string.Empty : $" · confianza {f.Confidence}"));
            sb.AppendLine($"- {f.Description}");
            if (!string.IsNullOrWhiteSpace(f.Recommendation))
            {
                sb.AppendLine($"- **Recomendación**: {f.Recommendation}");
            }

            // La marca de posible duplicado. La aplicación NO fusiona: dice a qué se parece y
            // decide quien conoce el código (F23 §5).
            if (duplicates.TryGetValue(f.Id, out Finding? twin))
            {
                sb.AppendLine($"- ⚠ Posible duplicado de {Name(twin)} — mismo sitio y misma regla, "
                    + "descrito con otras palabras. No se han fusionado.");
            }

            sb.AppendLine();
        }
    }

    /// <summary>La unidad de un hallazgo: su primera ubicación, que es por donde se agrupa.</summary>
    private static string UnitOf(Finding f)
        => f.Locations.Count > 0 ? f.Locations[0].Path : "(sin ubicación)";

    /// <summary>Cómo nombrar a otro hallazgo dentro del informe: su alias si lo tiene.</summary>
    private static string Name(Finding f)
        => string.IsNullOrEmpty(f.DisplayId) ? $"«{f.Title}»" : f.DisplayId!;

    /// <summary>
    /// <b>El anexo técnico</b> (F23 §1): todo lo que sirve para diagnosticar el COSTE de Atalaya,
    /// junto y al final.
    /// <para>
    /// <b>No se ha borrado nada</b>, y ése es el punto. F18-F21 llenaron la cabecera de telemetría
    /// que sirvió para tres fases de ahorro y que hay que poder seguir leyendo; lo que no tiene
    /// sentido es ponérsela delante a quien viene a arreglar su código y no sabe —ni tiene por qué—
    /// qué es una re-escritura de caché. Dos lectores, un documento, y cada uno con su parte.
    /// </para>
    /// </summary>
    private static void AppendAnnex(
        StringBuilder sb, AuditSession session, CostResult cost, int pendingUnits, int largeUnits)
    {
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Anexo técnico — diagnóstico");
        sb.AppendLine();
        sb.AppendLine("Instrumentación del coste de la propia auditoría. No hace falta para actuar sobre los");
        sb.AppendLine("hallazgos: está aquí para quien mantiene Atalaya.");
        sb.AppendLine();

        sb.AppendLine(UsageLine(session));
        AppendCostSplit(sb, cost);
        AppendDeclaredCost(sb, session);
        AppendBudgetLine(sb, session);
        if (session.MaxPassesPerUnit > 0)
        {
            sb.AppendLine($"- **Pasadas del barrido (tope)**: {session.MaxPassesPerUnit} por unidad");
        }

        sb.AppendLine($"- **Inventario**: {pendingUnits} pendiente(s) · {largeUnits} grande(s)");
        sb.AppendLine();

        if (session.UsageBreakdown.Count > 0)
        {
            sb.AppendLine("### Consumo por unidad");
            sb.AppendLine();
            sb.AppendLine("| Unidad | Prompt~ | Llamadas | ToolCalls | In | Out | CacheRead | CacheWrite | Duración |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (UnitUsageBreakdown b in session.UsageBreakdown)
            {
                sb.AppendLine($"| {b.Unit} | {b.PromptTokensEstimate} | {b.Calls} | {b.ToolCalls} "
                    + $"| {b.InputTokens} | {b.OutputTokens} | {b.CacheReadTokens} | {b.CacheWriteTokens} "
                    + $"| {Seconds(b.DurationMs)} |");
            }

            sb.AppendLine();
            AppendPassBreakdown(sb, session);
            AppendThreadBreakdown(sb, session);
        }

        AppendPassNarrative(sb, session);
    }

    /// <summary>
    /// <b>El hilo, unidad a unidad</b> (F25 §5): cuántos turnos y cuántas veces hubo que empezar
    /// otra conversación, con el motivo.
    /// <para>
    /// Va aquí y no en el cuerpo porque es instrumentación —el criterio de D-886, y no ha cambiado—,
    /// pero tiene que estar: la diferencia entre un barrido de cuatro turnos y uno de cuatro
    /// conversaciones es toda la factura, y sin esta línea las dos se parecen en cualquier otra
    /// tabla del anexo.
    /// </para>
    /// </summary>
    private static void AppendThreadBreakdown(StringBuilder sb, AuditSession session)
    {
        if (!session.UsageBreakdown.Any(u => u.ThreadTurns > 0))
        {
            return;
        }

        sb.AppendLine("### El hilo, unidad a unidad");
        sb.AppendLine();
        sb.AppendLine("Cada unidad se audita como una conversación y cada pasada es un turno suyo. Una");
        sb.AppendLine("conversación que se reabre vuelve a mandar el prompt entero, y por eso se cuenta.");
        sb.AppendLine();
        foreach (UnitUsageBreakdown b in session.UsageBreakdown)
        {
            sb.AppendLine($"- **{b.Unit}** — {ThreadLine(b)}");
        }

        sb.AppendLine();
    }

    /// <summary>«hilo: 4 turnos · 1 reinicio (la pasada se cortó en unit_done)», y sus casos raros.</summary>
    private static string ThreadLine(UnitUsageBreakdown b)
    {
        if (b.ThreadTurns == 0)
        {
            return "sin hilo: una petición por pasada";
        }

        string line = $"hilo: {b.ThreadTurns} turno" + (b.ThreadTurns == 1 ? "" : "s");
        if (b.ThreadRestarts == 0)
        {
            return line;
        }

        string why = string.Join(", ", b.ThreadRestartReasons.Distinct(StringComparer.Ordinal));
        return line + $" · {b.ThreadRestarts} reinicio" + (b.ThreadRestarts == 1 ? "" : "s")
            + (why.Length > 0 ? $" ({why})" : "");
    }

    /// <summary>
    /// Lo que el auditor declaró haber revisado en CADA pasada. Es trazabilidad —permite ver qué
    /// zonas revisita— y no lectura: en el caso de referencia repetía siete veces la misma lista de
    /// símbolos delante de quien solo quería la lista de defectos.
    /// </summary>
    private static void AppendPassNarrative(StringBuilder sb, AuditSession session)
    {
        bool any = session.Units.Any(u =>
            (u.Passes ?? new List<UnitPassRecord>()).Any(p => !string.IsNullOrWhiteSpace(p.Summary)));
        if (!any)
        {
            return;
        }

        sb.AppendLine("### Cobertura declarada, pasada a pasada");
        sb.AppendLine();
        sb.AppendLine("Lo que el auditor dice haber mirado. Es una afirmación suya, no una prueba.");
        sb.AppendLine();
        foreach (UnitVerdictRecord u in session.Units)
        {
            List<UnitPassRecord> passes = (u.Passes ?? new List<UnitPassRecord>())
                .Where(p => !string.IsNullOrWhiteSpace(p.Summary))
                .ToList();
            if (passes.Count == 0)
            {
                continue;
            }

            sb.AppendLine($"- **{u.Unit}**");
            string trace = string.Join(" · ", (u.Passes ?? new List<UnitPassRecord>()).Select(pp =>
                pp.Dry
                    ? $"pasada {pp.Index}: seca"
                    : $"pasada {pp.Index}: {pp.New} nuevos"
                      + (pp.LocationsAdded > 0 ? $", {pp.LocationsAdded} ubicaciones" : "")));
            sb.AppendLine($"  - Barrido: {trace}");
            foreach (UnitPassRecord pp in passes)
            {
                // También al ESCRIBIR, y no solo al ingerir (F23 §6): el arreglo de origen impide
                // que vuelva a pasar, pero lo que ya está guardado sigue llevando el escape dentro
                // y un informe de una sesión vieja se sigue regenerando desde el hub.
                sb.AppendLine($"  - Pasada {pp.Index}: {AuditorText.Clean(pp.Summary)}");
            }
        }

        sb.AppendLine();
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
    /// Qué se le enseñó a un hallazgo en una verificación y qué contestó (F16 §F).
    /// </summary>
    /// <param name="Basis">
    /// El eslabón de la cadena con el que se le juzgó: el ancla exacta, el símbolo o la unidad
    /// entera. Sin esto un veredicto no se puede pesar — «no concluyente» con el método delante y
    /// «no concluyente» con la unidad entera delante no significan lo mismo, y el segundo es el que
    /// pide re-auditar.
    /// </param>
    /// <param name="Verdict">El desenlace tal y como lo aplicó la aplicación.</param>
    /// <param name="Evidence">El razonamiento del modelo, literal.</param>
    public sealed record VerifyLine(
        string Alias,
        string Title,
        Severity Severity,
        string Path,
        int Line,
        VerifyBasis Basis,
        string? Member,
        string Verdict,
        string Evidence);

    /// <summary>
    /// El informe de una VERIFICACIÓN (F16 §F).
    /// <para>
    /// <b>Por qué existe.</b> Hasta aquí verificar era una acción fantasma: gastaba dinero, decidía
    /// estados —resolvía hallazgos, abría disputas, ponía marcas de revisión— y no dejaba más rastro
    /// que unas líneas en el historial de cada ficha. Auditar y arreglar sí dejan informe, y por
    /// eso se pueden auditar a sí mismos meses después; verificar no, y era justo la acción cuyo
    /// veredicto más cuesta reconstruir.
    /// </para>
    /// <para>
    /// <b>Qué lleva, y por qué cada cosa.</b> Qué hallazgo, <b>qué código se le enseñó</b> —el
    /// ancla, el símbolo o la unidad entera—, el veredicto textual del modelo, y los tokens con su
    /// coste derivado. El «qué se le enseñó» es la pieza que nadie más guarda: sin ella, releer un
    /// «no concluyente» no permite saber si al instrumento le faltó contexto o le faltó criterio.
    /// </para>
    /// </summary>
    public static string BuildVerifyReport(
        AppConfig? app,
        AuditSession session,
        IReadOnlyList<VerifyLine> lines,
        IReadOnlyList<string> notes,
        string? organization = null,
        ModelRateTable? rates = null)
    {
        string appName = app?.Name ?? session.AppSlug;
        var sb = new StringBuilder();

        sb.AppendLine($"# Verificación — {appName}");
        sb.AppendLine();
        sb.AppendLine($"- **Modo**: {session.Mode}");
        sb.AppendLine(Culture, $"- **Fecha**: {session.StartedUtc:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"- **Autor**: {session.By} ({session.Machine})");
        sb.AppendLine($"- **Commit del clon**: {session.Commit}");
        sb.AppendLine(ProviderLine(session));
        sb.AppendLine($"- **Modelo**: {session.Model ?? "n/d"}");
        sb.AppendLine($"- **Hallazgos verificados**: {lines.Count}");
        sb.AppendLine(UsageLine(session));

        CostResult cost = CreditCalculator.Calculate(session, rates);
        sb.AppendLine($"- **Coste**: {CreditText.OfSession(cost, session.Provider)}");
        AppendDeclaredCost(sb, session);
        sb.AppendLine();

        sb.AppendLine("> Verificar **juzga el código que hay ahora**. Que el código anclado haya "
            + "desaparecido es precisamente lo que hace un arreglo, así que se le enseña al "
            + "instrumento lo que quede —el método, su margen, o la unidad entera si cambió— y se "
            + "le pide un veredicto sobre eso.");
        sb.AppendLine();

        AppendDirectives(sb, session);

        sb.AppendLine("## Veredictos");
        sb.AppendLine();

        if (lines.Count == 0)
        {
            sb.AppendLine("_(ningún hallazgo llegó al instrumento; ver las notas de abajo)_");
            sb.AppendLine();
        }

        foreach (VerifyLine line in lines)
        {
            sb.AppendLine($"### {line.Alias} — {line.Title}");
            sb.AppendLine();
            sb.AppendLine($"- **Severidad**: {line.Severity}");
            sb.AppendLine($"- **Ubicación**: `{line.Path}:{line.Line}`");
            sb.AppendLine($"- **Código que se le enseñó**: {BasisText(line.Basis, line.Member)}");
            sb.AppendLine($"- **Veredicto**: {line.Verdict}");
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrWhiteSpace(line.Evidence)
                ? "_(sin evidencia aportada)_"
                : "> " + line.Evidence.Trim().Replace("\n", "\n> "));
            sb.AppendLine();
        }

        if (notes.Count > 0)
        {
            sb.AppendLine("## Notas de la sesión");
            sb.AppendLine();
            foreach (string note in notes)
            {
                sb.AppendLine($"- {note}");
            }

            sb.AppendLine();
        }

        Sign(sb, organization);
        return sb.ToString();
    }

    /// <summary>
    /// Cómo se escribe el eslabón de la cadena con el que se juzgó. Se DICE, y con el nombre del
    /// miembro cuando lo hay: «el método `Get`» pesa distinto que «la unidad entera».
    /// </summary>
    private static string BasisText(VerifyBasis basis, string? member) => basis switch
    {
        VerifyBasis.Simbolo => member is { Length: > 0 }
            ? $"el símbolo del hallazgo, re-anclado a «{member}»"
            : "el símbolo del hallazgo, re-anclado",
        VerifyBasis.Unidad => "**la unidad entera**, porque ni el código anclado ni el símbolo "
            + "seguían ahí y la unidad había cambiado",
        _ => member is { Length: > 0 }
            ? $"el código anclado, dentro de «{member}»"
            : "el código anclado",
    };

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
        sb.AppendLine(ProviderLine(session));
        sb.AppendLine($"- **Modelo**: {session.Model ?? "n/d"}");
        sb.AppendLine(UsageLine(session));

        CostResult fixCost = CreditCalculator.Calculate(session, rates);
        sb.AppendLine($"- **Coste**: {CreditText.OfSession(fixCost, session.Provider)}");
        AppendDeclaredCost(sb, session);
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
    /// <param name="config">
    /// La configuración del ciclo que se cierra (F17): su temática y su juez preferido. El ciclo
    /// siguiente la hereda, y el informe lo dice para que la foto del cierre lleve la lupa con la
    /// que se hizo.
    /// </param>
    /// <param name="periods">
    /// El historial de temáticas del ciclo (F17.1). Con más de una, el informe dice con qué lupas se
    /// trabajó y quién cambió cuándo — no solo con la última.
    /// </param>
    public static string BuildCycleCloseReport(
        AppConfig app, int closedCycle, int promoted, IReadOnlyList<Finding> findings,
        string? organization = null, CycleAging? aging = null, CycleConfig? config = null,
        IReadOnlyList<ThemePeriod>? periods = null)
    {
        var active = findings.Where(f => f.Status == FindingStatus.Activo).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"# Cierre de ciclo {closedCycle} — {app.Name}");
        sb.AppendLine();

        CycleConfig cfg = config ?? CycleConfig.Default;
        if (periods is { Count: > 1 })
        {
            sb.AppendLine($"- **Temáticas del ciclo**: {ThemeHistoryText.Chain(periods)}");
            foreach (string line in ThemeHistoryText.Lines(periods))
            {
                sb.AppendLine($"  - {line}");
            }
        }
        else
        {
            sb.AppendLine($"- **Temática del ciclo**: {ThemeCatalog.Display(cfg.Theme)}");
        }

        if (cfg.HasPreferredModel)
        {
            string house = string.IsNullOrWhiteSpace(cfg.PreferredProvider)
                ? string.Empty
                : $" ({ProviderNames.Display(cfg.PreferredProvider)})";
            sb.AppendLine($"- **Modelo preferido**: {cfg.PreferredModel}{house}");
        }

        sb.AppendLine($"- El ciclo {closedCycle + 1} hereda esta configuración; se puede cambiar desde «Configurar ciclo».");
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
