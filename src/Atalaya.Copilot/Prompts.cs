using System.Text;
using Atalaya.Domain;
using Atalaya.Domain.Model;

namespace Atalaya.Copilot;

/// <summary>Builds the auditor brief per stack (§6.4) from the versioned rule catalog.</summary>
public static class PillarBrief
{
    /// <summary>
    /// El brief del stack, con el catálogo ENTERO.
    /// <para>
    /// F5.12 devolvió el catálogo a su sitio: es metadato informativo (búsqueda, métricas, «qué
    /// busca» en la ficha) y ya no una superficie de gobernanza. Lo que esta aplicación ha decidido
    /// no ver no se recorta de aquí sino que se le dice al auditor en el prompt de la unidad, con
    /// la frase que lo describe — porque «¿esto es del mismo tipo que aquello?» es una pregunta
    /// semántica, y las semánticas las contesta el modelo, no un diccionario mantenido a mano.
    /// </para>
    /// </summary>
    public static string For(TechStack stack)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"BRIEF DE AUDITOR — stack {stack}.");
        sb.AppendLine();
        // F12 §D — la rúbrica vive en su propio fichero versionado y se CITA, no se copia. Ver
        // SeverityRubric: la de antes cabía en cuatro líneas, no daba un solo ejemplo, y su renglón
        // de crítica acababa en «error de cálculo de negocio», que es por donde entraron siete
        // críticas donde había una.
        sb.AppendLine(SeverityRubric.Text);
        sb.AppendLine();

        foreach (Pillar pillar in new[] { Pillar.Errores, Pillar.Optimizacion, Pillar.Mejoras })
        {
            var rules = RuleCatalog.Rules.Where(r => r.Pillar == pillar).ToList();
            if (rules.Count == 0)
            {
                continue;
            }

            sb.AppendLine($"PILAR {pillar.ToString().ToUpperInvariant()} — mínimos a revisar:");
            foreach (RuleDef rule in rules)
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
    /// <summary>
    /// Reglas del auditor. El ORDEN importa: la cobertura va primera porque es la obligación que
    /// define el modo lotes ("auditoría íntegra de las unidades seleccionadas").
    /// <para>
    /// Histórico: el prompt llevaba desde D-055 presión explícita de coste ("cada tool call
    /// multiplica el coste") y ninguna contrapartida hacia la exhaustividad; F4 además degradó
    /// "cubre íntegramente la unidad" a viñeta secundaria. Resultado medido el 2026-08-25 sobre
    /// CommonStatics.cs: 2 turnos, 1.943 tokens de salida, el 0,6 % del presupuesto de la unidad,
    /// y cada re-auditoría descubría 2 defectos más — la primera pasada nunca era íntegra. Ahora
    /// se exige un barrido por miembros y se declara en voz alta que el presupuesto está para
    /// gastarlo.
    /// </para>
    /// </summary>
    private const string AuditorRules =
        """
        Eres un auditor de código. Tu obligación PRINCIPAL es la COBERTURA ÍNTEGRA de la unidad:
        este es un modo de auditoría exhaustiva, no un vistazo. Una segunda auditoría de la misma
        unidad sin cambios en el código no debería encontrar nada que tú no hayas encontrado ya.

        MÉTODO DE BARRIDO — síguelo en este orden, no lo abrevies:
        1. Enumera TODOS los miembros de la unidad: cada método, constructor, propiedad, campo y
           bloque de nivel superior. Trabaja sobre esa lista; es tu lista de comprobación.
        2. Recorre los miembros UNO A UNO. Para cada uno, contrasta los tres pilares del brief
           (errores, optimizacion, mejoras) y las áreas de criterio. Un miembro puede tener varios
           defectos independientes, o ninguno.
        3. Presta atención a lo que es fácil pasar por alto: argumentos sin validar (nulos, vacíos,
           longitudes), casos límite (cero, uno, impar, desbordamiento), valores de retorno y
           excepciones sin controlar, recursos sin liberar, materialización innecesaria de
           colecciones, dependencias de cultura o de endianness, y comentarios o contratos que ya
           no describen lo que hace el código.
        4. Antes de cerrar, repasa tu lista del paso 1 y comprueba que ningún miembro quedó sin
           revisar. Si alguno quedó, revísalo ahora.

        TÓMATE LOS TURNOS QUE NECESITES. Tienes un presupuesto amplio de tokens por unidad y está
        para gastarlo: es preferible una auditoría profunda y cara que una barata e incompleta. Lo
        que se agrupa en una sola llamada son los HALLAZGOS (ver abajo), no tu razonamiento.

        UN DEFECTO SISTÉMICO ES UN SOLO HALLAZGO. Si el mismo problema aparece en varios miembros
        (p. ej. "no valida argumentos nulos" en cinco métodos), NO reportes cinco hallazgos:
        reporta UNO con las cinco ubicaciones en su array `locations`. Y si ese hallazgo YA existe
        —está en la lista de abajo, o lo acabas de reportar en esta unidad— no crees otro: llama a
        add_locations(findingId, locations) para extenderlo con los sitios nuevos. Fragmentar un
        defecto por miembro infla el baseline y no aporta información.

        Tienes dos cosas que entregar en cada unidad:

        1) RECONCILIAR los hallazgos que ya existen en esta unidad (se te listan abajo). Llama UNA vez a
           report_verdicts con un ARRAY que contenga un veredicto por CADA hallazgo de la lista. Cada
           veredicto es {findingId, verdict, evidence}:
             * findingId: el ULID EXACTO tal cual aparece en la lista. No lo inventes ni lo abrevies.
             * verdict: exactamente uno de {presente, arreglado, no-verificable, no-es-defecto}.
                 - presente: el problema sigue en el código que estás viendo.
                 - arreglado: SOLO si el código CAMBIÓ y por eso el problema ya no está. Es una
                   afirmación sobre un cambio, no sobre tu criterio.
                 - no-es-defecto: crees que esto NUNCA fue un defecto — el código es el mismo y
                   discrepas de quien lo reportó. Explica tu razonamiento en evidence. No cierra el
                   hallazgo: lo marca como disputado y lo decide una persona.
                 - no-verificable: no puedes determinarlo desde esta unidad (p. ej. depende de otro fichero).
             * evidence: una frase con la razón concreta (línea, construcción, qué cambió). Obligatoria.
           Si NO te pronuncias sobre alguno, la unidad queda marcada INCOMPLETA y ese hallazgo no se toca.
           Nada se resuelve por omisión: un hallazgo solo se cierra si dices 'arreglado' explícitamente.
           NO uses 'arreglado' para expresar desacuerdo: si el código no ha cambiado, nada se ha
           arreglado, y la app lo degradará a 'presente'. Para discrepar está 'no-es-defecto'.

        2) REPORTAR los hallazgos NUEVOS con submit_findings, un ARRAY con todos los de la unidad en UNA
           sola llamada. IMPORTANTE: si el problema que has encontrado se corresponde con uno de la lista
           de existentes, NO lo reportes como nuevo — referéncialo en report_verdicts como 'presente'.
           submit_findings es SOLO para problemas que no están en la lista.

        Reglas de forma:
        - Agrupa los hallazgos en UNA llamada a submit_findings. La versión singular es solo un
          fallback; no la uses para ir soltándolos de uno en uno.
        - Campos obligatorios de cada hallazgo nuevo:
            * ruleId: un id EXACTO del catálogo (los listados en el brief como [rule.id]) o, si no encaja
              ninguno, uno de la forma criterio.<área> con las áreas listadas en el brief.
            * pillar: exactamente uno de {optimizacion, mejoras, errores}.
            * severity: exactamente uno de {critica, alta, media, baja}, aplicando la RÚBRICA DE
              SEVERIDAD del brief — incluidas sus reglas de desempate.
            * locations: al menos una con {path, line} y opcionalmente snippet.
        - NO envíes tag: la app lo deriva de ruleId (criterio.* → criterio; resto → checklist).
        - NO asignes IDs ni confianza (eso es de la app). NO filtres silenciados: los verás en la lista
          con estado 'silenciado' y debes pronunciarte sobre ellos igual; decir 'presente' NO los reactiva.
        - Nunca reportes hallazgos en texto: solo por tool.
        - Puedes pedir firmas de dependencias con read_signatures(path); es tu única lectura extra.
        - add_locations solo acepta ULIDs de la lista de existentes o de hallazgos que hayas
          reportado en ESTA unidad, y ubicaciones dentro de la unidad que estás auditando.
        - Termina con unit_done. Su resumen DEBE empezar por la lista de miembros que has revisado,
          con el formato: "Revisados: A, B, C." Es la prueba de tu cobertura y queda en el informe.
          Si la unidad trae PATRONES SILENCIADOS y te has callado algo por uno de ellos, declara
          cuántos en el argumento suppressedByPattern de unit_done.
        """;

    /// <param name="directives">
    /// Las convenciones intencionales del proyecto, ya recortadas al presupuesto (F7). Van
    /// DESPUÉS del brief y antes de todo lo demás: el brief dice qué se busca, y las directivas
    /// dicen qué de lo que se busca ya está decidido en esta casa. Null o vacío = no se escribe
    /// nada.
    /// </param>
    public static string ComposeUnitPrompt(
        string unitPath, string unitContent, string brief, AuditMode mode,
        IReadOnlyList<ExistingFinding>? existing = null,
        PatternSilenceSet? patterns = null,
        DirectiveBundle? directives = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(AuditorRules);
        sb.AppendLine($"MODO: {mode}. Los hallazgos nuevos nacen con la confianza que la app asigne.");
        sb.AppendLine();
        sb.AppendLine(brief);
        sb.AppendLine(DirectiveSection.Render(directives ?? DirectiveBundle.Empty, DirectivePurpose.Auditoria));
        sb.AppendLine(PatternBlock(patterns));
        sb.AppendLine(ExistingBlock(unitPath, existing));
        sb.AppendLine($"UNIDAD: {unitPath}");
        sb.AppendLine("CONTENIDO ÍNTEGRO DE LA UNIDAD (entre marcadores):");
        sb.AppendLine("<<<UNIT");
        sb.AppendLine(unitContent);
        sb.AppendLine("UNIT>>>");
        return sb.ToString();
    }

    /// <summary>
    /// Los TIPOS de problema que esta aplicación ha silenciado (F5.12). Una línea por patrón, con
    /// su id corto: con decenas de patrones sigue siendo despreciable frente al contenido de la
    /// unidad.
    /// <para>
    /// Aquí es donde vive la supresión. No hay filtro programático detrás —ni hashes, ni parecidos
    /// calculados—: el juicio de «esto es de ese tipo» lo hace el auditor mientras mira el código,
    /// que es el único momento en que se tiene delante el contexto necesario. Se le pide que lo
    /// DECLARE en <c>unit_done</c> porque una supresión invisible es un dato sin causa, y ningún
    /// número de esta aplicación aparece sin causa.
    /// </para>
    /// <para>
    /// Cuando no hay patrones no se escribe nada: un bloque vacío solo gasta tokens y sugiere que
    /// el modelo debería buscarse algo que callar.
    /// </para>
    /// </summary>
    private static string PatternBlock(PatternSilenceSet? patterns)
    {
        if (patterns is null || patterns.IsEmpty)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("TIPOS DE PROBLEMA SILENCIADOS EN ESTA APLICACIÓN (decisión del equipo):");
        foreach (PatternSilence p in patterns.Patterns)
        {
            sb.AppendLine($"  - [{p.ShortId}] {p.Exemplar}");
        }

        sb.AppendLine();
        sb.AppendLine("Si un hallazgo que ibas a reportar CORRESPONDE a uno de estos tipos, NO lo reportes:");
        sb.AppendLine("ni con submit_findings ni con add_locations. El juicio de si corresponde es tuyo y lo");
        sb.AppendLine("haces mirando el código; no busques coincidencias literales de palabras.");
        sb.AppendLine("Regístralo en unit_done: suppressedByPattern = [{patternId, count}], con el id EXACTO");
        sb.AppendLine("del patrón (p. ej. P-2) y cuántas detecciones te has callado por él en esta unidad.");
        sb.AppendLine("Un tipo silenciado NO te exime de reconciliar: si uno de los hallazgos existentes de");
        sb.AppendLine("la lista de abajo es de ese tipo, sigue necesitando su veredicto en report_verdicts.");
        return sb.ToString();
    }

    /// <summary>
    /// La lista de hallazgos existentes de la unidad (F4). Es barata en tokens — son pocos por
    /// unidad — y es lo que sustituye a toda la maquinaria de fingerprints: el auditor ve qué se
    /// sabe ya y se pronuncia. Cuando no hay ninguno se dice explícitamente, para que el modelo no
    /// invente veredictos sobre una lista vacía.
    /// </summary>
    private static string ExistingBlock(string unitPath, IReadOnlyList<ExistingFinding>? existing)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"HALLAZGOS YA EXISTENTES EN {unitPath} (reconcilia TODOS con report_verdicts):");
        if (existing is null || existing.Count == 0)
        {
            sb.AppendLine("  (ninguno — no llames a report_verdicts en esta unidad)");
            return sb.ToString();
        }

        foreach (ExistingFinding f in existing)
        {
            string alias = string.IsNullOrWhiteSpace(f.DisplayId) ? "" : $" [{f.DisplayId}]";
            sb.AppendLine($"  - findingId: {f.FindingId}{alias}");
            sb.AppendLine($"      titulo: {f.Title}");
            sb.AppendLine($"      severidad: {f.Severity} · ubicacion: {f.Location} · estado: {f.State}");
        }

        return sb.ToString();
    }

    /// <param name="directives">
    /// Las mismas directivas de ámbito Auditoría que vio el auditor (F7 §3). El verificador juzga
    /// el mismo código con el mismo criterio: sin ellas confirmaría como defecto justo lo que la
    /// auditoría había aprendido a no reportar.
    /// </param>
    public static string ComposeVerifyPrompt(
        IReadOnlyList<VerifyTarget> targets, DirectiveBundle? directives = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Eres un verificador. Para cada hallazgo, decide su veredicto y llama a submit_verdict(findingUlid, verdict, evidence).");
        sb.AppendLine("verdict ∈ {confirmado, resuelto, no-verificable, no-es-defecto}. Usa el ULID exacto que se te da.");
        sb.AppendLine();
        sb.Append(DirectiveSection.Render(directives ?? DirectiveBundle.Empty, DirectivePurpose.Verificacion));

        // F6.6 — LA REGLA QUE FALTABA. Se juzga el código que hay AHORA; no se busca el de antes.
        // Que el fragmento auditado haya desaparecido es el aspecto NORMAL de un arreglo, y leerlo
        // como «no se puede verificar» era justo lo que impedía cerrar un hallazgo ya arreglado.
        sb.AppendLine("Juzga SIEMPRE el código que se te muestra, que es el que hay ahora en el clon.");
        sb.AppendLine("Si el fragmento que se auditó ya no aparece, eso NO es motivo de «no-verificable»:");
        sb.AppendLine("es lo que pasa cuando algo se arregla. Compara el código actual con lo que el");
        sb.AppendLine("hallazgo describe y con lo que recomendaba, y decide:");
        sb.AppendLine("  · confirmado     — el defecto sigue en el código que ves.");
        sb.AppendLine("  · resuelto       — el código que ves ya no tiene el defecto.");
        sb.AppendLine("  · no-es-defecto  — nunca lo fue.");
        sb.AppendLine("  · no-verificable — solo si el código que ves no permite decidirlo.");
        sb.AppendLine("La evidencia es obligatoria: cita lo que ves y por qué te lleva a ese veredicto.");
        sb.AppendLine();
        foreach (VerifyTarget t in targets)
        {
            sb.AppendLine($"- ULID {t.FindingUlid} · {t.Path}:{t.Line} · {t.Title}");
            sb.AppendLine($"    {t.Description}");
            if (!string.IsNullOrWhiteSpace(t.Recommendation))
            {
                sb.AppendLine($"    lo que se recomendó: {t.Recommendation.Trim()}");
            }

            // El ancla, señalada dentro del bloque: lo de abajo es el símbolo ENTERO (F12 §B),
            // así que sin esto el verificador tendría el contexto pero no el punto.
            if (!string.IsNullOrEmpty(t.AnchoredSnippet))
            {
                sb.AppendLine($"    la línea anclada, dentro de lo de abajo: {t.AnchoredSnippet.Trim()}");
            }

            if (!string.IsNullOrEmpty(t.Snippet))
            {
                sb.AppendLine("    " + BasisCaption(t) + ":");
                sb.AppendLine("    " + t.Snippet.Replace("\n", "\n    "));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Qué es el fragmento que va debajo, dicho con todas las letras (F6.6), y desde F12 §B también
    /// CUÁNTO se enseña: el símbolo entero, o el margen de líneas cuando no se pudo resolver
    /// ninguno. Un verificador que no sabe si está viendo un método completo o un recorte no puede
    /// saber si su «no se puede decidir» es honesto o es falta de contexto.
    /// </summary>
    private static string BasisCaption(VerifyTarget t) => t.Basis switch
    {
        VerifyBasis.Simbolo =>
            $"el código anclado YA NO ESTÁ; este es el código ACTUAL de «{t.Member ?? "el miembro"}», entero",
        VerifyBasis.Unidad =>
            "ni el código anclado ni el símbolo aparecen ya; esta es la unidad tal y como está AHORA",
        _ when t.Member is { Length: > 0 } m =>
            $"el ancla sigue casando en la línea {t.Line}; este es «{m}» ENTERO, que es el símbolo que la contiene",
        _ =>
            $"el ancla sigue casando en la línea {t.Line}; no se pudo resolver el símbolo que la "
            + "contiene, así que van las líneas de alrededor",
    };
}
