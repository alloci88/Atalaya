using System.Text;
using Atalaya.Domain;
using Atalaya.Domain.Model;

namespace Atalaya.Copilot;

/// <summary>
/// El brief del auditor, partido por donde se mide (F18 §1). Las dos piezas siempre viajan
/// juntas y en este orden; se separan para poder decir cuánto cuesta cada una sin volver a
/// componer el texto por otro camino — que es como se acaba teniendo dos versiones del prompt.
/// </summary>
/// <param name="Rubric">La cabecera y la rúbrica de severidad citada del fichero versionado.</param>
/// <param name="Catalog">Los pilares del catálogo, las áreas de criterio y las notas del stack.</param>
public sealed record AuditorBrief(string Rubric, string Catalog)
{
    public string Text => Rubric + Catalog;

    public override string ToString() => Text;

    /// <summary>
    /// Un brief que llega como texto suelto —los tests, y cualquier llamada que no distinga las
    /// piezas— se cuenta entero como catálogo. No se parte adivinando dónde acaba la rúbrica:
    /// una heurística sobre el propio prompt sería un dato inventado (N-2).
    /// </summary>
    public static implicit operator AuditorBrief(string text) => new(string.Empty, text);
}

/// <summary>Builds the auditor brief per stack (§6.4) from the versioned rule catalog.</summary>
public static class PillarBrief
{
    /// <inheritdoc cref="Parts"/>
    public static string For(TechStack stack) => Parts(stack).Text;

    /// <summary>
    /// El brief del stack, con el catálogo ENTERO, en sus dos piezas.
    /// <para>
    /// F5.12 devolvió el catálogo a su sitio: es metadato informativo (búsqueda, métricas, «qué
    /// busca» en la ficha) y ya no una superficie de gobernanza. Lo que esta aplicación ha decidido
    /// no ver no se recorta de aquí sino que se le dice al auditor en el prompt de la unidad, con
    /// la frase que lo describe — porque «¿esto es del mismo tipo que aquello?» es una pregunta
    /// semántica, y las semánticas las contesta el modelo, no un diccionario mantenido a mano.
    /// </para>
    /// </summary>
    public static AuditorBrief Parts(TechStack stack)
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
        string rubric = sb.ToString();

        sb = new StringBuilder();
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
        return new AuditorBrief(rubric, sb.ToString());
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

/// <summary>
/// Un prompt de unidad ya compuesto, con la costura entre lo que se puede cachear y lo que no
/// (F18 §2), y con la cuenta de lo que aporta cada bloque.
/// </summary>
/// <param name="StablePrefix">
/// Idéntico byte a byte en todas las unidades y en todas las pasadas de una sesión. Si algo que
/// varía se colara aquí, cada unidad invalidaría la caché entera — y la factura no lo diría.
/// </param>
/// <param name="UnitPart">Lo que cambia: los hallazgos conocidos de la unidad y su código.</param>
public sealed record ComposedUnitPrompt(
    string StablePrefix, string UnitPart, PromptComposition Composition)
{
    /// <summary>El prompt entero, tal y como se manda cuando el proveedor no sabe partirlo.</summary>
    public string Text => StablePrefix + UnitPart;
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

        ECONOMÍA DE TURNOS — importa tanto como lo anterior, y no contradice nada de lo anterior.
        Cada vuelta tuya reenvía el prompt ENTERO otra vez, así que un turno de más cuesta lo mismo
        que auditar la unidad entera. Tómate los turnos de RAZONAMIENTO que necesites —eso no se
        discute—; lo que no puede ser es gastar una vuelta por cada cosa que entregas.
        Cuando ya sepas lo que vas a reportar, ENTREGA TODO EN UN SOLO TURNO: report_verdicts,
        submit_findings, add_locations y unit_done, las cuatro en la misma vuelta, y unit_done la
        última de las cuatro. No hace falta que esperes la respuesta de una para emitir la
        siguiente: son independientes, y la aplicación las procesa todas.
        Después de unit_done NO digas nada más. Ni un resumen, ni una despedida: has terminado.

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
            * symbol: SIEMPRE, el miembro que contiene el defecto. Es lo que distingue dos defectos
              parecidos en miembros distintos; si no está dentro de ninguno, pon el tipo.
        - NO envíes tag: la app lo deriva de ruleId (criterio.* → criterio; resto → checklist).
        - NO asignes IDs ni confianza (eso es de la app). NO filtres silenciados: los verás en la lista
          con estado 'silenciado' y debes pronunciarte sobre ellos igual; decir 'presente' NO los reactiva.
        - Nunca reportes hallazgos en texto: solo por tool.
        - Puedes pedir firmas de dependencias con read_signatures(path); es tu única lectura extra.
        - add_locations solo acepta ULIDs de la lista de existentes o de hallazgos que hayas
          reportado en ESTA unidad, y ubicaciones dentro de la unidad que estás auditando.
        - Cierra con unit_done, en el mismo turno que lo demás. Su resumen DEBE empezar por la lista de miembros que has revisado,
          con el formato: "Revisados: A, B, C." Es la prueba de tu cobertura y queda en el informe.
          Si la unidad trae PATRONES SILENCIADOS y te has callado algo por uno de ellos, declara
          cuántos en el argumento suppressedByPattern de unit_done.
        """;

    /// <summary>
    /// <b>El contrato de las variantes</b> (F24 §1). Va en la zona estable, entre el defecto
    /// sistémico y las dos entregas, porque es la continuación del mismo argumento: aquél dice que
    /// un defecto en N sitios es UN hallazgo, y éste que un defecto contado de N maneras también.
    /// <para>
    /// <b>Por qué hace falta decirlo.</b> El contrato de F4 ya decía «si es el mismo problema, no lo
    /// reportes como nuevo». Lo que no decía es qué es el mismo problema <b>cuando se le pide más y
    /// no hay más</b>: en el informe de referencia el modelo marcaba «presente» todo lo que había
    /// reportado antes —la reconciliación funcionaba— y aun así volvía a emitir el mismo defecto
    /// cortado por otro sitio, con otro título y a veces bajo otra regla. Cuatro de los nueve
    /// hallazgos tardíos de ClienteRemoto son eso.
    /// </para>
    /// <para>
    /// <b>Y por qué la segunda mitad.</b> Un modelo al que se le pide «más» cuando no queda más,
    /// reformula. Lo que se le pide en cada vuelta es lo que FALTA, y que una pasada vacía sea una
    /// respuesta correcta tiene que estar escrito: es la que termina el barrido.
    /// </para>
    /// <para>
    /// <b>Es una de las dos capas y no fusiona nada.</b> La otra es el filtro de
    /// <c>submit_findings</c>, que rebota lo que se parece demasiado; ninguna decide que dos
    /// hallazgos sean el mismo, eso lo decide el auditor (D-077). Dos de los cinco pares del caso de
    /// referencia —reglas distintas, y símbolos a alturas distintas— el filtro no los puede ver, y
    /// quedan a cargo SOLO de este texto.
    /// </para>
    /// </summary>
    /// <param name="directives">
    /// Las convenciones intencionales del proyecto, ya recortadas al presupuesto (F7). Van
    /// DESPUÉS del brief y antes de todo lo demás: el brief dice qué se busca, y las directivas
    /// dicen qué de lo que se busca ya está decidido en esta casa. Null o vacío = no se escribe
    /// nada.
    /// </param>
    /// <param name="theme">
    /// La lupa del ciclo (F17). Con General no cambia ni un byte del prompt; con otra, el bloque
    /// de enfoque va JUSTO detrás del brief —el brief dice cómo se clasifica, el enfoque dice qué
    /// se busca— y la lista de existentes se parte en dos: los de la temática, que se reconcilian,
    /// y los de otras, que se enseñan para no re-reportarlos y NO se juzgan.
    /// </param>
    /// <param name="offTheme">
    /// Los hallazgos existentes de OTRAS temáticas en la unidad (F17 §3). Solo tiene sentido con
    /// una temática concreta; en un ciclo General todo se reconcilia y esta lista va vacía.
    /// </param>
    public static string ComposeUnitPrompt(
        string unitPath, string unitContent, AuditorBrief brief, AuditMode mode,
        IReadOnlyList<ExistingFinding>? existing = null,
        PatternSilenceSet? patterns = null,
        DirectiveBundle? directives = null,
        AuditTheme theme = AuditTheme.General,
        IReadOnlyList<ExistingFinding>? offTheme = null)
        => Compose(unitPath, unitContent, brief, mode, existing, patterns, directives, theme, offTheme).Text;

    /// <summary>
    /// El mismo prompt, <b>partido por donde la caché lo parte</b> y con la cuenta de lo que aporta
    /// cada bloque (F18 §§1–2).
    /// <para>
    /// <b>El orden ya era el correcto y esto lo fija.</b> Lo estable —reglas, rúbrica, catálogo,
    /// temática, directivas y patrones silenciados— va primero y no cambia ni entre unidades ni
    /// entre pasadas de una sesión; lo variable —los hallazgos conocidos de la unidad y su código—
    /// va después. Un proveedor cuya caché se dirija por prefijo puede así reutilizar el primer
    /// tramo entero; uno que sepa marcarlo explícitamente sabe dónde poner la marca.
    /// </para>
    /// <para>
    /// <b>Concatenar las dos piezas da byte a byte el prompt de siempre.</b> Eso no es una
    /// casualidad que haya que cuidar a mano: hay un test que lo fija, y otro que comprueba que en
    /// la pieza estable no se cuela nada que varíe por unidad ni por pasada. Un prefijo que se
    /// contamina no falla: gasta, en silencio.
    /// </para>
    /// </summary>
    public static ComposedUnitPrompt Compose(
        string unitPath, string unitContent, AuditorBrief brief, AuditMode mode,
        IReadOnlyList<ExistingFinding>? existing = null,
        PatternSilenceSet? patterns = null,
        DirectiveBundle? directives = null,
        AuditTheme theme = AuditTheme.General,
        IReadOnlyList<ExistingFinding>? offTheme = null)
    {
        // ------------------------------ ESTABLE (cacheable) ------------------------------
        var reglas = new StringBuilder();
        reglas.AppendLine(AuditorRules);
        reglas.AppendLine($"MODO: {mode}. Los hallazgos nuevos nacen con la confianza que la app asigne.");
        reglas.AppendLine();

        // El brief entra en dos trozos por el mismo AppendLine de siempre: la rúbrica arrastra el
        // salto que separaba las piezas, así que el texto resultante no cambia ni un byte.
        var rubrica = new StringBuilder();
        rubrica.Append(brief.Rubric);
        var catalogo = new StringBuilder();
        catalogo.AppendLine(brief.Catalog);

        var tematica = new StringBuilder();
        if (theme != AuditTheme.General)
        {
            tematica.AppendLine(ThemeSection.Render(theme));
        }

        var directivas = new StringBuilder();
        directivas.AppendLine(DirectiveSection.Render(directives ?? DirectiveBundle.Empty, DirectivePurpose.Auditoria));

        var patrones = new StringBuilder();
        patrones.AppendLine(PatternBlock(patterns));

        // ------------------------------ VARIABLE (por unidad) ----------------------------
        var existentes = new StringBuilder();
        existentes.AppendLine(ExistingBlock(unitPath, existing, theme));
        if (theme != AuditTheme.General)
        {
            existentes.Append(OffThemeBlock(unitPath, offTheme));
        }

        var unidad = new StringBuilder();
        unidad.AppendLine($"UNIDAD: {unitPath}");
        unidad.AppendLine("CONTENIDO ÍNTEGRO DE LA UNIDAD (entre marcadores):");
        unidad.AppendLine("<<<UNIT");
        unidad.AppendLine(unitContent);
        unidad.AppendLine("UNIT>>>");

        string stable = reglas.ToString() + rubrica + catalogo + tematica + directivas + patrones;
        string variable = existentes.ToString() + unidad;

        return new ComposedUnitPrompt(
            stable,
            variable,
            new PromptComposition(
                Reglas: PromptTokens.Estimate(reglas.ToString()),
                Rubrica: PromptTokens.Estimate(rubrica.ToString()),
                Catalogo: PromptTokens.Estimate(catalogo.ToString()),
                Tematica: PromptTokens.Estimate(tematica.ToString()),
                Directivas: PromptTokens.Estimate(directivas.ToString()),
                Patrones: PromptTokens.Estimate(patrones.ToString()),
                Existentes: PromptTokens.Estimate(existentes.ToString()),
                Unidad: PromptTokens.Estimate(unidad.ToString())));
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
    private static string ExistingBlock(string unitPath, IReadOnlyList<ExistingFinding>? existing, AuditTheme theme)
    {
        var sb = new StringBuilder();
        // Con General la cabecera es la de siempre, byte a byte. Con una lupa, dice de qué
        // temática son los de la lista: es la única que el auditor tiene que reconciliar.
        sb.AppendLine(theme == AuditTheme.General
            ? $"HALLAZGOS YA EXISTENTES EN {unitPath} (reconcilia TODOS con report_verdicts):"
            : $"HALLAZGOS YA EXISTENTES DE TU TEMÁTICA ({ThemeCatalog.Display(theme)}) EN {unitPath} "
              + "(reconcilia TODOS con report_verdicts):");
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

    /// <summary>
    /// Los hallazgos de OTRAS temáticas que hay en la unidad (F17 §3). Se enseñan por una sola
    /// razón: que el auditor no los re-reporte como nuevos si su familia roza la suya (un
    /// <c>.Result</c> puede ser de Rendimiento y de Concurrencia a la vez). Y se dice, con el
    /// mismo peso, que NO se juzgan: un veredicto sobre uno de estos lo rechaza la aplicación.
    /// Cuando no hay ninguno no se escribe nada — un bloque vacío solo invita a buscar qué callar.
    /// </summary>
    private static string OffThemeBlock(string unitPath, IReadOnlyList<ExistingFinding>? offTheme)
    {
        if (offTheme is null || offTheme.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"HALLAZGOS DE OTRAS TEMÁTICAS EN {unitPath} (NO los juzgues ni los re-reportes; "
                      + "no llames a report_verdicts con estos ULID):");
        foreach (ExistingFinding f in offTheme)
        {
            string alias = string.IsNullOrWhiteSpace(f.DisplayId) ? "" : $" [{f.DisplayId}]";
            string theme = string.IsNullOrWhiteSpace(f.Theme) ? "" : $" · temática: {f.Theme}";
            sb.AppendLine($"  - {f.FindingId}{alias} «{f.Title}»{theme} · ubicacion: {f.Location}");
        }

        sb.AppendLine();
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
