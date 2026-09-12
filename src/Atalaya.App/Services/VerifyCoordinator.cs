using Atalaya.Domain;
using Atalaya.Domain.Anchoring;
using Atalaya.Domain.Hashing;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using Atalaya.Inventory;

namespace Atalaya.App.Services;

/// <summary>
/// Runs a verify session (§5.4): re-anchors each finding's location, asks the agent for a verdict,
/// and applies it — confirmado refreshes, resuelto resolves (via verify, con la guarda de evidencia
/// de cambio), no-verificable marca <c>needsReview</c>.
/// <para>
/// <b>F5.16 — cada hallazgo se verifica con el instrumento que lo detectó.</b> Los hallazgos que
/// MIDE la aplicación (hoy «unidad demasiado grande») no llegan al agente: se vuelven a medir. Pedir
/// a un LLM que verifique una cuenta de líneas desde un fragmento anclado en la línea 1 es usar el
/// instrumento equivocado, y responde lo único honrado que puede responder — «no verificable»—,
/// que además ensucia el hallazgo con <c>needsReview</c>. Es exactamente lo que le pasó dos veces a
/// MEJ-0037 antes de que su unidad se troceara.
/// </para>
/// <para>
/// <b>F6.6 — verificar es JUZGAR EL CÓDIGO DE AHORA, no buscar el de antes.</b> Hasta aquí el
/// verify se paraba en el anclaje: si el fragmento auditado no aparecía, se rendía con «no
/// localizado» y no llegaba a preguntar nada. Pero que el código malo haya desaparecido es
/// precisamente el aspecto de un arreglo — el 2026-08-27 un hallazgo arreglado, con commit y push
/// hechos, se «reabrió» tres veces seguidas por esto. Ahora hay DOS fases: (a) anclar y (b) juzgar.
/// El anclaje solo decide QUÉ código se le enseña al auditor —el fragmento exacto, el miembro
/// entero o la unidad—, nunca si se pregunta o no. «No localizado» queda para cuando no hay nada
/// que enseñar: ni ancla, ni símbolo, ni una unidad que haya cambiado.
/// </para>
/// </summary>
public sealed class VerifyCoordinator
{
    /// <summary>
    /// Tope de líneas cuando se juzga la unidad entera. Una unidad de Atalaya es un fichero, y un
    /// fichero entero en el prompt es lo normal en una auditoría; pero un verify puede llevar
    /// varios objetivos, así que se recorta — y el recorte se dice, no se disimula.
    /// </summary>
    private const int MaxUnitLines = 400;

    // Los identificadores de los pasos (F30 §4).
    public const string Preparar = "preparar";
    public const string Enviar = "enviar";
    public const string Juzgar = "juzgar";
    public const string Escribir = "escribir";
    public const string Publicar = "publicar";

    /// <summary>
    /// <b>Los pasos de una verificación</b>, y <b>ninguno se puede cancelar</b>.
    /// <para>
    /// No es un olvido: verificar <b>escribe desde el primer paso</b>. Lo que la aplicación mide se
    /// mide y se aplica ahí mismo (F5.16), y un hallazgo que ya no se localiza deja su evento antes
    /// de que nadie llame a ningún agente (F6.6). No hay, por tanto, ningún punto en el que
    /// cancelar deje el hub como estaba — y donde no se puede cumplir, no se ofrece.
    /// </para>
    /// </summary>
    public static IReadOnlyList<StepSpec> Plan { get; } = new[]
    {
        new StepSpec(Preparar, "Preparar los hallazgos"),
        new StepSpec(Enviar, "Componer el encargo del verificador"),
        new StepSpec(Juzgar, "Juzgar con el agente"),
        new StepSpec(Escribir, "Escribir el resultado y su informe"),
        new StepSpec(Publicar, "Publicar en el hub"),
    };

    /// <summary>La lista lista para colgarla del sitio desde el que se lanzó.</summary>
    public static StepList NewSteps(StepFlow flow = StepFlow.Vertical) => new(Plan, flow);

    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly IUlidFactory _ulids;
    private readonly IAuditorProvider _agent;
    private readonly MeasuredFindingService? _measured;
    private readonly DirectiveService? _directives;

    /// <param name="directives">
    /// Las convenciones del proyecto (F7). Opcional igual que <paramref name="measured"/>; sin
    /// ella el verify se comporta como antes de F7.
    /// </param>
    public VerifyCoordinator(
        HubContext hub, MachineConfigStore machines, IUlidFactory ulids, IAuditorProvider agent,
        MeasuredFindingService? measured = null, DirectiveService? directives = null)
    {
        _hub = hub;
        _machines = machines;
        _ulids = ulids;
        _agent = agent;
        _measured = measured;
        _directives = directives;
    }

    /// <param name="steps">
    /// Los pasos que se están enseñando (F30 §4). Si no llega uno se crea aquí: el camino que
    /// ejecuta es el mismo se enseñe o no.
    /// </param>
    public async Task<VerifyOutcome> RunAsync(
        string slug, IReadOnlyList<Ulid> findingIds, CancellationToken ct, StepList? steps = null)
    {
        steps ??= NewSteps();
        string? clone = _machines.Load().ClonePathFor(slug);
        string commit = GitInfo.HeadSha(clone);
        string by = _hub.ResolveIdentity().Name;
        DateTimeOffset utc = DateTimeOffset.UtcNow;

        // F16 §F — el identificador de la sesión se saca AQUÍ y no al final, porque cada evento
        // que se escriba en el historial de un hallazgo tiene que poder apuntar a ella. Sin eso,
        // el rastro de una verificación en la ficha es una línea de texto que no lleva a ninguna
        // parte: la única acción de las tres que no dejaba camino de vuelta a su informe.
        Ulid sessionId = _ulids.NewUlid();
        string sessionKey = sessionId.ToString();

        // Con quién se está verificando, para escribirlo en cada evento. Un veredicto sin la casa
        // que lo emitió no se puede pesar: dos casas coincidiendo valen más que dos modelos de la
        // misma (D-781).
        string judge = Judge(_agent);

        var measuredMessages = new List<string>();
        var notes = new List<string>();

        // Los que ni llegaron al instrumento, para que el informe los cuente igual: un desenlace
        // frustrante es un desenlace, y esconderlo haría que el informe pareciera más limpio de lo
        // que fue la sesión.
        var lost = new List<ReportBuilder.VerifyLine>();
        int measuredApplied = 0;
        int written = 0;

        var targets = new List<VerifyTarget>();
        var stamps = new Dictionary<string, DetectionStamp>(StringComparer.Ordinal);
        var aimed = new Dictionary<string, (Finding Finding, VerifyAim Aim)>(StringComparer.Ordinal);

        // El paso siguiente de cada objetivo, por si el instrumento acaba diciendo que no puede
        // decidir (F12 §A). Se calcula AQUÍ porque aquí es donde se sabe qué código se le enseñó:
        // proponerlo desde la caja de herramientas obligaría a adivinarlo.
        var nextSteps = new Dictionary<string, string>(StringComparer.Ordinal);

        // ---------------------------------------------------------------- (1) preparar
        // Leer cada hallazgo, medir lo que se mide y re-anclar lo que hay que preguntar. Escribe:
        // por eso ni éste ni ninguno de los que siguen se pueden cancelar (ver `Plan`).
        steps.Run(Preparar, () =>
        {
            foreach (Ulid id in findingIds)
            {
                Finding? f = _hub.Store.TryReadFinding(slug, id.ToString());
                if (f is null)
                {
                    notes.Add("Ese hallazgo ya no está en el hub.");
                    continue;
                }

                if (f.Locations.Count == 0)
                {
                    notes.Add($"{Alias(f)}: no tiene ninguna ubicación en el código que verificar.");
                    continue;
                }

                // El desvío de F5.16: lo medido se mide, y no gasta ni un token.
                if (_measured is not null && UnitMeasure.IsMeasured(f.RuleId))
                {
                    MeasuredVerdict verdict = _measured.Verify(slug, f);
                    measuredMessages.Add(verdict.Message);
                    notes.Add(verdict.Message);
                    if (verdict.Applied)
                    {
                        measuredApplied++;
                    }

                    continue;
                }

                Location loc = f.Locations[0];
                var stamp = new DetectionStamp(
                    utc, AuditMode.Verify, commit, by, TryHashUnit(clone, loc.Path),
                    _agent.ModelName, _agent.ProviderId);

                VerifyAim aim = Aim(clone, f, loc, stamp);
                if (!aim.Judgeable)
                {
                    // Fase (a) agotada y sin nada que enseñar: AQUÍ sí es «no localizado». Y el evento
                    // lo dice con esas palabras: un hallazgo activo no puede «reabrirse» (F6.6).
                    f.NeedsReview = true;
                    f.Record(new HistoryEntry(utc, FindingEvent.NotLocated, by, $"{aim.Reason} · {judge}")
                    {
                        SessionId = sessionKey,
                    });
                    _hub.Store.WriteFinding(slug, f);
                    written++;
                    notes.Add($"{Alias(f)}: {aim.Reason}");
                    lost.Add(new ReportBuilder.VerifyLine(
                        Alias(f), f.Title, f.Severity, loc.Path, loc.Line,
                        VerifyBasis.Anclado, null, "no localizado", aim.Reason));
                    continue;
                }

                string key = f.Id.ToString();
                stamps[key] = stamp;
                aimed[key] = (f, aim);
                nextSteps[key] = NextStep(aim, loc.Path);
                targets.Add(new VerifyTarget(
                    key, loc.Path, aim.Line, aim.Snippet, f.Title, f.Description,
                    aim.Basis, aim.Member, f.Recommendation, aim.AnchoredSnippet));
            }
        });

        // ---------------------------------------------------------------- (2) enviar al agente
        // Componer el encargo: la caja de herramientas, las directivas y el prompt. Con cero
        // objetivos —todo medido, o nada que localizar— no hay encargo, pero el paso se ejecuta
        // igual: una lista cuyos pasos aparecen y desaparecen deja de ser una lista.
        VerifyToolbox? toolbox = null;
        DirectiveBundle directives = DirectiveBundle.Empty;
        string prompt = string.Empty;

        steps.Run(Enviar, () =>
        {
            if (targets.Count == 0)
            {
                return;
            }

            toolbox = new VerifyToolbox(_hub, slug, stamps, nextSteps, sessionKey, judge, aimed);

            // F7 §3: el verificador juzga el mismo código que el auditor y necesita el mismo
            // criterio. Sin las directivas de ámbito Auditoría confirmaría como defecto justo lo
            // que la auditoría había aprendido a no reportar, y el hallazgo iría y vendría entre
            // las dos.
            directives = _directives?.Bundle(slug, clone, DirectiveScope.Auditoria)
                         ?? DirectiveBundle.Empty;
            prompt = PromptComposer.ComposeVerifyPrompt(targets, directives);
        });

        // Lo que la verificación consume se REGISTRA, igual que en una auditoría o en un arreglo.
        // Hasta aquí no se anotaba: la sesión quedaba escrita con `usage` a cero, así que en las
        // métricas cada verify parecía gratis y el coste del periodo se quedaba corto por todo lo
        // que verificar cuesta. La sesión ya se guardaba; lo que faltaba era su factura.
        var usage = new UsageTotals();
        void OnUsage(UsageSample sample)
        {
            usage.Add(sample.InputTokens, sample.OutputTokens, sample.CacheReadTokens,
                sample.CacheWriteTokens, sample.Cost, sample.Calls);
            if (sample.CostUnit is not null && string.IsNullOrEmpty(usage.Currency))
            {
                usage.Currency = sample.CostUnit;
            }
        }

        // ---------------------------------------------------------------- (3) juzgar
        // El tiempo del agente es UN PASO EN CURSO con su reloj subiendo, como los demás: es lo que
        // más dura y es justo lo que la pantalla se callaba (F30 §2e).
        await steps.RunAsync(Juzgar, async () =>
        {
            if (toolbox is null)
            {
                return;
            }

            _agent.UsageReported += OnUsage;
            try
            {
                await _agent.VerifyAsync(new VerifyRequest(prompt, targets), toolbox, ct);
            }
            finally
            {
                _agent.UsageReported -= OnUsage;
            }
        });

        if (toolbox is null)
        {
            // Sin objetivos no hay nada que escribir ni informe que redactar, pero los dos pasos
            // que quedan se ejecutan igual: lo hecho hasta aquí también se publica.
            steps.Run(Escribir, () => { });
            PushStep(steps, slug, written);
            return new VerifyOutcome(measuredApplied, measuredMessages, notes);
        }

        notes.AddRange(toolbox.Notes);

        // ---------------------------------------------------------------- (4) escribir el resultado
        steps.Run(Escribir, () =>
        {
            // Lo que el auditor no contestó no se queda mudo: se anota lo que SÍ se pudo hacer —el
            // re-anclaje— y se dice en el aviso. Un «no se pudo verificar» sin causa era el tercero de
            // los tres defectos de este parte.
            foreach ((string key, (Finding f, VerifyAim aim)) in aimed)
            {
                if (toolbox.Judged(key))
                {
                    continue;
                }

                (FindingEvent kind, string detail, string note) = Silent(f, aim);
                f.Record(new HistoryEntry(utc, kind, by, $"{detail} · {judge}") { SessionId = sessionKey });
                _hub.Store.WriteFinding(slug, f);
                written++;
                notes.Add(note);
                Location silentLoc = f.Locations.Count > 0 ? f.Locations[0] : new Location { Path = "?", Line = 0 };
                lost.Add(new ReportBuilder.VerifyLine(
                    Alias(f), f.Title, f.Severity, silentLoc.Path, silentLoc.Line,
                    aim.Basis, aim.Member, "sin veredicto", detail));
            }

            AppConfig? app = _hub.Store.TryReadApp(slug);
            var session = new AuditSession
            {
                Id = sessionId,
                AppSlug = slug,
                Mode = AuditMode.Verify,
                By = by,
                Machine = Environment.MachineName,
                StartedUtc = utc,
                EndedUtc = DateTimeOffset.UtcNow,
                Commit = commit,
                CycleN = app?.CurrentCycle ?? 1,
                Model = _agent.ModelName,
                Provider = _agent.ProviderId,
                Usage = usage,
                Directives = directives.Records.ToList(),
            };
            _hub.Store.WriteSession(session);

            // F16 §F — y su INFORME. Verificar cuesta dinero y decide estados; que fuera la única de
            // las tres acciones sin informe era lo que la convertía en un fantasma.
            WriteReport(slug, app, session, toolbox.Lines.Concat(lost).ToList(), notes);
        });

        // ---------------------------------------------------------------- (5) publicar
        PushStep(steps, slug, written + toolbox.Applied);
        return new VerifyOutcome(toolbox.Applied + measuredApplied, measuredMessages, notes);
    }

    /// <summary>
    /// El paso de publicar, con lo que ya sabía <see cref="Push"/>: se empuja cuando se ha escrito
    /// algo, y si el hub no lo acepta la línea lo dice —el trabajo está en el clon y sale con lo
    /// pendiente (F31)—, sin tumbar una verificación que ya está aplicada.
    /// </summary>
    private void PushStep(StepList steps, string slug, int changed)
    {
        bool published = steps.Run(Publicar, () => Push(slug, changed));
        if (!published)
        {
            steps.Fail(Publicar, StepList.PendingPublish);
        }
    }

    /// <summary>
    /// Con qué casa y qué modelo se está verificando, en una línea. Va a cada evento del historial
    /// porque un veredicto sin la casa que lo emitió no se puede pesar: dos casas coincidiendo son
    /// una segunda opinión de verdad, y dos modelos de la misma pueden compartir el punto ciego.
    /// </summary>
    private static string Judge(IAuditorProvider agent)
        => agent.ModelName is { Length: > 0 } model
            ? $"verificado con {agent.ProviderName} (modelo {model})"
            : $"verificado con {agent.ProviderName}";

    /// <summary>
    /// Escribe el informe de la verificación. Un fallo aquí NO tumba la sesión: los veredictos ya
    /// están aplicados y son el hecho; el informe es la narración.
    /// </summary>
    private void WriteReport(
        string slug, AppConfig? app, AuditSession session,
        IReadOnlyList<ReportBuilder.VerifyLine> lines, IReadOnlyList<string> notes)
    {
        try
        {
            string report = ReportBuilder.BuildVerifyReport(
                app, session, lines, notes, _hub.OrganizationName, ModelRates());
            _hub.Store.WriteReport(slug, session.Id.ToString(), report);
        }
        catch (Exception)
        {
            // Sin informe, pero con los veredictos escritos. Lo contrario sería perder el trabajo
            // pagado por no poder contarlo.
        }
    }

    /// <summary>Las tarifas del hub, para derivar el coste del informe. Null si no hay.</summary>
    private ModelRateTable? ModelRates()
    {
        try
        {
            return _hub.Store.TryReadModelRates();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Se empuja cuando se ha escrito algo, y no por haber preguntado. Devuelve <c>false</c> solo
    /// cuando había algo que publicar y el hub no lo aceptó.
    /// </summary>
    private bool Push(string slug, int changed)
        => changed <= 0 || (_hub.Sync?.CommitAndPush($"verify: {slug} {changed} hallazgos") ?? true);

    /// <summary>
    /// Lo que se propone cuando no hay nada mejor que proponer: el objetivo ni siquiera llegó a
    /// registrarse (no debería pasar, pero un «no concluyente» sin salida es peor que un genérico).
    /// </summary>
    private const string DefaultNextStep = "El siguiente paso es re-auditar la unidad.";

    /// <summary>
    /// El paso siguiente de un «no concluyente» (F12 §A). Depende de CUÁNTO código llegó a ver el
    /// instrumento: si vio menos que la unidad, lo que falta es contexto; si ya vio la unidad
    /// entera y sigue sin poder decidir, lo que falta es una auditoría con el criterio completo.
    /// </summary>
    private static string NextStep(VerifyAim aim, string path) => aim.Basis switch
    {
        VerifyBasis.Unidad =>
            $"se juzgó con {path} entera delante y aun así no se pudo decidir: el siguiente paso es "
            + "re-auditar la unidad.",
        _ =>
            $"el siguiente paso es ampliar el contexto — re-audita {path} para juzgarlo con la "
            + "unidad entera delante.",
    };

    /// <summary>Qué anotar de un objetivo sobre el que el auditor no llegó a pronunciarse.</summary>
    private static (FindingEvent Kind, string Detail, string Note) Silent(Finding f, VerifyAim aim)
    {
        if (aim.Basis == VerifyBasis.Simbolo)
        {
            string detail = $"re-anclado a «{aim.Member}»; el auditor no emitió veredicto";
            return (FindingEvent.Reanchored, detail, $"{Alias(f)}: {detail}.");
        }

        const string other = "el auditor no emitió veredicto sobre este hallazgo";
        return (FindingEvent.NotLocated, other, $"{Alias(f)}: {other}.");
    }

    /// <summary>
    /// La fase (a): decidir QUÉ código se le enseña al auditor. Por orden de fiabilidad — el ancla
    /// exacta, el símbolo que el hallazgo nombra y la unidad entera cuando esa unidad ha cambiado
    /// desde el último avistamiento. Solo cuando ninguna de las tres da nada se abandona.
    /// </summary>
    private static VerifyAim Aim(string? clone, Finding f, Location loc, DetectionStamp stamp)
    {
        if (string.IsNullOrWhiteSpace(clone))
        {
            // Sin clon no hay contra qué anclar; se pregunta igual con lo que se guardó, que es lo
            // que este camino ha hecho siempre.
            return VerifyAim.Judge(VerifyBasis.Anclado, loc.Line, null, null);
        }

        string abs = Path.Combine(clone, loc.Path.Replace('/', Path.DirectorySeparatorChar));
        string[] lines;
        try
        {
            if (!File.Exists(abs))
            {
                return Abandon(f, stamp, $"el fichero {loc.Path} ya no está en el clon");
            }

            lines = File.ReadAllLines(abs);
        }
        catch (IOException ex)
        {
            return VerifyAim.Lost($"no se pudo leer {loc.Path}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return VerifyAim.Lost($"no se pudo leer {loc.Path}: {ex.Message}");
        }

        // (a1) El ancla exacta: el fragmento sigue siendo, letra por letra, el que se auditó.
        //
        // F12 §B — PERO NO SE ENSEÑA LA LÍNEA SUELTA. Se enseñaba, y era inútil: a un hallazgo cuya
        // recomendación es estructural («acumula en una sola pasada») se le enseñaba `foreach (…)`
        // y nada más, con lo que el verificador contestaba lo único honrado que podía contestar —
        // que sin el cuerpo del método no se puede decidir—, y eso acababa en el «Confirmado» que
        // arregla §A. El ancla dice DÓNDE mirar; lo que se juzga es el SÍMBOLO que la contiene.
        (bool anchored, int line, string? snippet) =
            SnippetAnchor.TryAnchor(clone, loc.Path, loc.Line, loc.SnippetHash);
        if (anchored)
        {
            return VerifyAim.Judge(VerifyBasis.Anclado, line, Around(lines, line, loc.Path, out string? member), member)
                with { AnchoredSnippet = snippet };
        }

        // (a2) El símbolo. AQUÍ estaba el fallo: esto ya funcionaba para pintar la ficha —el banner
        // decía «re-anclado» y enseñaba el método arreglado— pero el verify no lo usaba y se rendía
        // un paso antes de preguntar. Si el símbolo existe, hay código que juzgar.
        SymbolHit hit = SymbolAnchor.FindMember(lines, loc.Path, SymbolAnchor.Candidates(f.Symbol, f.Title));
        if (hit.Found)
        {
            CodeSpanLines span = MethodBoundary.ForLine(lines, hit.Line, loc.Path);
            string text = string.Join("\n", lines[(span.StartLine - 1)..span.EndLine]);
            return VerifyAim.Judge(VerifyBasis.Simbolo, hit.Line, text, hit.Member)
                with { LineText = lines[hit.Line - 1] };
        }

        // (a3) Ni ancla ni símbolo. Aun así, si la unidad cambió hay algo que juzgar: la unidad.
        if (ReconciliationService.UnchangedSinceLastSighting(f, stamp, out string? why))
        {
            return VerifyAim.Lost(
                $"no localizado: ni el código anclado en {loc.Path}:{loc.Line} ni el símbolo del "
                + $"hallazgo aparecen ya, y {why}");
        }

        int end = Math.Min(lines.Length, MaxUnitLines);
        string unit = string.Join("\n", lines[..end]);
        if (end < lines.Length)
        {
            unit += $"\n… (recortado; la unidad tiene {lines.Length} líneas)";
        }

        return VerifyAim.Judge(VerifyBasis.Unidad, 1, unit, null);
    }

    /// <summary>
    /// El código que rodea a <paramref name="line"/>: el <b>miembro completo</b> que la contiene,
    /// vía Roslyn, y cuando no hay ninguno que resolver —no es C#, o la línea cae fuera de todo
    /// miembro— un margen de ±<see cref="MethodBoundary.FallbackRadius"/> líneas (F12 §B).
    /// <para>
    /// Es el mismo recorte que la ficha lleva enseñando desde F5.5, y por el mismo motivo: un
    /// fragmento de una línea no permite juzgar nada que no quepa en esa línea.
    /// </para>
    /// </summary>
    private static string Around(string[] lines, int line, string path, out string? member)
    {
        CodeSpanLines span = MethodBoundary.ForLine(lines, line, path);
        member = span.Member;
        return string.Join("\n", lines[(span.StartLine - 1)..span.EndLine]);
    }

    /// <summary>
    /// El fichero entero desapareció. Sigue habiendo una pregunta que hacer solo si hay código, y
    /// aquí no lo hay: se abandona, pero diciendo por qué y no «no se pudo».
    /// </summary>
    private static VerifyAim Abandon(Finding f, DetectionStamp stamp, string what)
    {
        ReconciliationService.UnchangedSinceLastSighting(f, stamp, out string? why);
        return VerifyAim.Lost(why is null ? $"no localizado: {what}" : $"no localizado: {what} ({why})");
    }

    private static string? TryHashUnit(string? clone, string path)
    {
        if (string.IsNullOrWhiteSpace(clone))
        {
            return null;
        }

        try
        {
            string abs = Path.Combine(clone, path.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(abs) ? HashUtil.Sha256Hex(File.ReadAllBytes(abs)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Alias(Finding f) => f.DisplayId ?? f.Id.ToString();

    /// <summary>Lo que la fase de anclaje decidió: qué código se juzga, o por qué no hay ninguno.</summary>
    private sealed record VerifyAim(
        bool Judgeable, VerifyBasis Basis, int Line, string? Snippet, string? Member, string Reason)
    {
        /// <summary>
        /// La línea que el ancla casó, cuando la casó. <see cref="Snippet"/> es ahora el símbolo
        /// entero (F12 §B), así que este es el dato que dice CUÁL de esas líneas es la anclada —y
        /// sin él el prompt no podría señalarla.
        /// </summary>
        public string? AnchoredSnippet { get; init; }

        /// <summary>
        /// El texto CRUDO de <see cref="Line"/>, cuando el objetivo salió del símbolo. Es lo
        /// único que hace falta para re-anclar en disco si el veredicto confirma
        /// (BUGFIX-ANCLA): con él se recalcula el <c>snippetHash</c> sin volver a abrir el
        /// fichero — y sobre todo, sin volver a leerlo, que para entonces ya podría haber
        /// cambiado debajo.
        /// </summary>
        public string? LineText { get; init; }

        public static VerifyAim Judge(VerifyBasis basis, int line, string? snippet, string? member)
            => new(true, basis, line, snippet, member, string.Empty);

        public static VerifyAim Lost(string reason)
            => new(false, VerifyBasis.Anclado, 0, null, null, reason);
    }

    /// <summary>Applies verify verdicts to findings (§5.4). Uses the ULID, never the alias.</summary>
    private sealed class VerifyToolbox : IVerifyToolbox
    {
        private readonly HubContext _hub;
        private readonly string _slug;
        private readonly IReadOnlyDictionary<string, DetectionStamp> _stamps;

        /// <summary>Qué proponerle al usuario cuando el veredicto sea «no concluyente» (F12 §A).</summary>
        private readonly IReadOnlyDictionary<string, string> _nextSteps;
        private readonly string _sessionKey;
        private readonly string _judge;
        private readonly IReadOnlyDictionary<string, (Finding Finding, VerifyAim Aim)> _aimed;

        private readonly HashSet<string> _judged = new(StringComparer.Ordinal);
        private readonly List<string> _notes = new();

        /// <param name="sessionKey">
        /// La sesión a la que apunta cada evento (F16 §F). Es lo que convierte una línea del
        /// historial en un camino: del veredicto al informe que lo explica.
        /// </param>
        /// <param name="judge">Con qué casa y qué modelo se juzgó, para escribirlo en el evento.</param>
        /// <param name="aimed">Qué código se le enseñó a cada objetivo, para el informe.</param>
        public VerifyToolbox(
            HubContext hub, string slug, IReadOnlyDictionary<string, DetectionStamp> stamps,
            IReadOnlyDictionary<string, string> nextSteps, string sessionKey, string judge,
            IReadOnlyDictionary<string, (Finding Finding, VerifyAim Aim)> aimed)
        {
            _hub = hub;
            _slug = slug;
            _stamps = stamps;
            _nextSteps = nextSteps;
            _sessionKey = sessionKey;
            _judge = judge;
            _aimed = aimed;
        }

        /// <summary>Lo que contestó el instrumento sobre cada hallazgo, para el informe.</summary>
        public List<ReportBuilder.VerifyLine> Lines { get; } = new();

        public int Applied { get; private set; }

        /// <summary>Las frases del aviso, una por veredicto aplicado.</summary>
        public IReadOnlyList<string> Notes => _notes;

        public bool Judged(string findingUlid) => _judged.Contains(findingUlid);

        public void SubmitVerdict(string findingUlid, string verdict, string evidence)
        {
            if (!Ulid.TryParse(findingUlid, out Ulid id))
            {
                return;
            }

            string key = id.ToString();
            Finding? f = _hub.Store.TryReadFinding(_slug, key);
            if (f is null)
            {
                return;
            }

            DetectionStamp stamp = _stamps.TryGetValue(key, out DetectionStamp? s)
                ? s
                : new DetectionStamp(DateTimeOffset.UtcNow, AuditMode.Verify, "unknown", _hub.ResolveIdentity().Name);

            string note;
            string verdictName = Normalize(verdict);
            switch (verdictName)
            {
                case "confirmado":
                    f.Confirm(AuditMode.Verify, stamp); // refreshes lastConfirmed, no confidence change

                    // BUGFIX-ANCLA — Y AQUÍ SÍ SE RE-ANCLA EN DISCO. D-226 reservaba esto para
                    // cuando hubiera una persona o una evidencia detrás, y esto es exactamente
                    // eso: el agente acaba de mirar el código de HOY y ha dicho que el defecto
                    // sigue en ese miembro. La ficha no puede escribirlo —allí no hay más que
                    // una coincidencia de nombre— pero un veredicto sí.
                    note = $"{Alias(f)}: confirmado — el defecto sigue ahí.";
                    if (Reanchor(f, key) is { Length: > 0 } moved)
                    {
                        note += $" {moved}.";
                    }

                    break;

                case "resuelto":
                    // LA GUARDA DE EVIDENCIA DE CAMBIO NO SE RELAJA (F5.1b): se REUTILIZA tal cual.
                    // Resolver sigue exigiendo que la unidad haya cambiado desde el último
                    // avistamiento; lo que F6.6 cambia es que ahora se LLEGA hasta aquí.
                    if (ReconciliationService.UnchangedSinceLastSighting(f, stamp, out string? why))
                    {
                        f.Confirm(AuditMode.Verify, stamp);
                        f.Record(new HistoryEntry(stamp.Utc, FindingEvent.Confirmed, stamp.By,
                            $"«arreglado» degradado a presente: {why}"));
                        note = $"{Alias(f)}: «arreglado» degradado a presente — {why}.";
                    }
                    else
                    {
                        // Un hallazgo que se resuelve deja de estar «por revisar»: la duda que esa
                        // marca representaba acaba de contestarse. Y se retira SIN evento propio —
                        // el del resultado es «Resuelto», y una segunda línea diciendo lo mismo con
                        // otras palabras es el eco que este parte vino a quitar.
                        f.NeedsReview = false;
                        f.Resolve(new ResolutionStamp(
                            stamp.Utc, ResolutionVia.Verify, AuditMode.Verify, stamp.Commit, stamp.By,
                            Detail(evidence)));
                        note = $"{Alias(f)}: resuelto — {Detail(evidence)}";
                    }

                    break;

                case "no-es-defecto":
                    f.Dispute(stamp.Utc, stamp.By, stamp.Model, evidence, stamp.Provider);
                    note = $"{Alias(f)}: el auditor sostiene que nunca fue un defecto. Queda disputado.";
                    break;

                default: // no-verificable → NO CONCLUYENTE (F12 §A)
                    verdictName = "no concluyente";
                    // Una no-respuesta tiene desenlace propio. Se anotaba como «Confirmado», y con
                    // eso el hallazgo se leía más sólido cuantas más veces NO se hubiera podido
                    // verificar: la métrica se alimentaba justo de la ausencia de evidencia. Aquí
                    // no se toca ni TimesConfirmed, ni la confianza, ni lastConfirmed — solo queda
                    // la marca de revisión, la causa, y qué hacer a continuación.
                    f.NeedsReview = true;
                    string step = _nextSteps.TryGetValue(key, out string? next) ? next : DefaultNextStep;
                    f.Record(new HistoryEntry(stamp.Utc, FindingEvent.Inconclusive, stamp.By,
                        $"no concluyente — {Detail(evidence)} · {step}"));
                    note = $"{Alias(f)}: no concluyente — {Detail(evidence)} · {step}";
                    break;
            }

            // EL SELLO (F16 §F). El evento lo escribe quien hace la transición —Confirm, Resolve,
            // Dispute o el Record de arriba—, y quién lo juzgó y con qué sesión lo pone aquí, sobre
            // la última entrada que acaba de nacer. Se hace así, y no metiendo la sesión en la
            // firma de cada transición del dominio, porque esto es una circunstancia de ESTA
            // acción y no una propiedad del hallazgo: el dominio no tiene por qué saber que existen
            // los informes.
            //
            // Y el COSTE no se escribe aquí a propósito. Es un derivado (D-788): se calcula de los
            // tokens de la sesión con la tarifa de su modelo, y guardarlo en un texto del hub sería
            // congelar un número que mañana se recalcula. El evento apunta a su sesión; el coste lo
            // deriva quien lo pinte.
            Seal(f, verdictName, evidence);

            _judged.Add(key);
            _hub.Store.WriteFinding(_slug, f);
            _notes.Add(note);
            Applied++;
        }

        /// <summary>
        /// <b>Deja el ancla donde el veredicto acaba de mirar</b> (BUGFIX-ANCLA), y lo dice en el
        /// mismo evento de la verificación.
        /// <para>
        /// <b>Solo con el objetivo sacado del SÍMBOLO</b> y solo con veredicto que confirma. Con
        /// el ancla exacta no hay nada que mover; con la unidad entera (D-813) no hay miembro al
        /// que apuntar, así que inventarse una línea sería justo lo que D-225 prohíbe. Y un «no
        /// concluyente» no toca nada: una no-respuesta no es evidencia de dónde está el código.
        /// </para>
        /// <para>
        /// <b>Idempotente</b>, como el re-anclaje de D-226: si la línea ya era la buena no
        /// escribe, así que verificar dos veces no ensucia el historial con un movimiento que no
        /// hubo.
        /// </para>
        /// </summary>
        /// <returns>La frase para el aviso y el historial, o vacío si no se movió nada.</returns>
        private string Reanchor(Finding f, string key)
        {
            if (!_aimed.TryGetValue(key, out (Finding Finding, VerifyAim Aim) aimed)
                || aimed.Aim.Basis != VerifyBasis.Simbolo
                || aimed.Aim.Line < 1
                || aimed.Aim.LineText is not { } text
                || f.Locations.Count == 0)
            {
                return string.Empty;
            }

            Location loc = f.Locations[0];
            if (loc.Line == aimed.Aim.Line)
            {
                return string.Empty;   // Ya estaba donde tiene que estar.
            }

            string moved = $"re-anclado {loc.Line} → {aimed.Aim.Line}";
            loc.Line = aimed.Aim.Line;
            loc.SnippetHash = CodeAnchor.ComputeSnippetHash(text);

            // Va en el MISMO evento que la verificación: son el mismo hecho —se miró el código
            // de hoy y se dijo que el defecto sigue ahí—, y una segunda línea diciendo que
            // además se movió el número se lee como si hubiera pasado otra cosa.
            if (f.History.Count > 0)
            {
                HistoryEntry last = f.History[^1];
                f.History[^1] = last with
                {
                    Detail = string.IsNullOrWhiteSpace(last.Detail) ? moved : $"{last.Detail} · {moved}",
                };
            }

            return moved;
        }

        /// <summary>
        /// Sella el último evento del hallazgo con quién lo juzgó y con la sesión que lo produjo, y
        /// anota la línea del informe. Es lo que convierte una verificación en algo que se puede
        /// releer: en la ficha, un camino al informe; en Informes, una fila con lo que se le enseñó
        /// y lo que contestó.
        /// </summary>
        private void Seal(Finding f, string verdictName, string evidence)
        {
            if (f.History.Count > 0)
            {
                HistoryEntry last = f.History[^1];
                f.History[^1] = last with
                {
                    Detail = string.IsNullOrWhiteSpace(last.Detail) ? _judge : $"{last.Detail} · {_judge}",
                    SessionId = _sessionKey,
                };
            }

            if (_aimed.TryGetValue(f.Id.ToString(), out (Finding Finding, VerifyAim Aim) aimed))
            {
                Location loc = f.Locations.Count > 0 ? f.Locations[0] : new Location { Path = "?", Line = 0 };
                Lines.Add(new ReportBuilder.VerifyLine(
                    Alias(f), f.Title, f.Severity, loc.Path, loc.Line,
                    aimed.Aim.Basis, aimed.Aim.Member, verdictName, Detail(evidence)));
            }
        }

        /// <summary>
        /// El vocabulario del verify y el de la reconciliación dicen lo mismo con otras palabras.
        /// Se aceptan los dos: un modelo que conteste «arreglado» en un verify está siendo claro,
        /// no incumpliendo el contrato.
        /// </summary>
        private static string Normalize(string verdict) => verdict.Trim().ToLowerInvariant() switch
        {
            "confirmado" or "presente" => "confirmado",
            "resuelto" or "arreglado" => "resuelto",
            "no-es-defecto" or "no es defecto" => "no-es-defecto",
            _ => "no-verificable",
        };

        private static string Detail(string evidence)
            => string.IsNullOrWhiteSpace(evidence) ? "sin evidencia aportada" : evidence.Trim();

        private static string Alias(Finding f) => f.DisplayId ?? f.Id.ToString();
    }
}

/// <summary>
/// Lo que hizo un «Verificar ahora» (F5.16): cuántos veredictos se aplicaron y qué decir.
/// </summary>
/// <param name="Messages">
/// Las frases con el número, para los hallazgos medidos. Vacía cuando todo fue al auditor: ahí el
/// veredicto vive en el historial, que es donde siempre ha vivido.
/// </param>
/// <param name="Notes">
/// Una frase por hallazgo con lo que REALMENTE pasó (F6.6): el veredicto, la degradación, el
/// re-anclaje o la causa concreta por la que no se pudo verificar. Es lo que sustituye al «no se
/// pudo verificar» a secas, que no decía ni qué había fallado ni qué hacer con ello.
/// </param>
public sealed record VerifyOutcome(
    int Applied, IReadOnlyList<string> Messages, IReadOnlyList<string>? Notes = null)
{
    /// <summary>La frase para el usuario, o null si no hay ninguna medida que contar.</summary>
    public string? Measured => Messages.Count == 0 ? null : string.Join(" · ", Messages);

    /// <summary>
    /// El aviso: el resultado real, siempre con causa. Nunca se queda en «no se pudo verificar».
    /// </summary>
    public string Toast => Notes is { Count: > 0 }
        ? string.Join(" · ", Notes)
        : "No había nada que verificar en este hallazgo.";
}

/// <summary>Re-anchors a stored location by its snippet hash (§5.4).</summary>
public static class SnippetAnchor
{
    public static (bool Anchored, int Line, string? Snippet) TryAnchor(string? clonePath, string path, int line, string? snippetHash)
    {
        if (string.IsNullOrWhiteSpace(clonePath))
        {
            return (true, line, null); // no clone to check against — trust the stored line
        }

        string abs = Path.Combine(clonePath, path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(abs))
        {
            return (false, line, null);
        }

        string[] lines = File.ReadAllLines(abs);
        if (snippetHash is null)
        {
            return (line >= 1 && line <= lines.Length, line, line >= 1 && line <= lines.Length ? lines[line - 1] : null);
        }

        // Still at the recorded line?
        if (line >= 1 && line <= lines.Length && CodeAnchor.ComputeSnippetHash(lines[line - 1]) == snippetHash)
        {
            return (true, line, lines[line - 1]);
        }

        // Moved — search the file for the matching snippet. La candidata más cercana a la línea
        // guardada, no la primera del fichero: desde F5.6 el hash ignora la sangría (D-219) y dos
        // líneas idénticas con sangrías distintas casan las dos.
        int moved = LocationAnchor.FindByHash(lines, snippetHash, line);
        return moved > 0 ? (true, moved, lines[moved - 1]) : (false, line, null);
    }
}
