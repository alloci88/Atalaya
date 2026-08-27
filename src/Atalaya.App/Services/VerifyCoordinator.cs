using Atalaya.Copilot;
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

    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;
    private readonly IUlidFactory _ulids;
    private readonly ICopilotAgent _agent;
    private readonly MeasuredFindingService? _measured;

    public VerifyCoordinator(
        HubContext hub, MachineConfigStore machines, IUlidFactory ulids, ICopilotAgent agent,
        MeasuredFindingService? measured = null)
    {
        _hub = hub;
        _machines = machines;
        _ulids = ulids;
        _agent = agent;
        _measured = measured;
    }

    public async Task<VerifyOutcome> RunAsync(string slug, IReadOnlyList<Ulid> findingIds, CancellationToken ct)
    {
        string? clone = _machines.Load().ClonePathFor(slug);
        string commit = GitInfo.HeadSha(clone);
        string by = _hub.ResolveIdentity().Name;
        DateTimeOffset utc = DateTimeOffset.UtcNow;

        var measuredMessages = new List<string>();
        var notes = new List<string>();
        int measuredApplied = 0;
        int written = 0;

        var targets = new List<VerifyTarget>();
        var stamps = new Dictionary<string, DetectionStamp>(StringComparer.Ordinal);
        var aimed = new Dictionary<string, (Finding Finding, VerifyAim Aim)>(StringComparer.Ordinal);

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
                utc, AuditMode.Verify, commit, by, TryHashUnit(clone, loc.Path), _agent.ModelName);

            VerifyAim aim = Aim(clone, f, loc, stamp);
            if (!aim.Judgeable)
            {
                // Fase (a) agotada y sin nada que enseñar: AQUÍ sí es «no localizado». Y el evento
                // lo dice con esas palabras: un hallazgo activo no puede «reabrirse» (F6.6).
                f.NeedsReview = true;
                f.Record(new HistoryEntry(utc, FindingEvent.NotLocated, by, aim.Reason));
                _hub.Store.WriteFinding(slug, f);
                written++;
                notes.Add($"{Alias(f)}: {aim.Reason}");
                continue;
            }

            string key = f.Id.ToString();
            stamps[key] = stamp;
            aimed[key] = (f, aim);
            targets.Add(new VerifyTarget(
                key, loc.Path, aim.Line, aim.Snippet, f.Title, f.Description,
                aim.Basis, aim.Member, f.Recommendation));
        }

        if (targets.Count == 0)
        {
            Push(slug, written);
            return new VerifyOutcome(measuredApplied, measuredMessages, notes);
        }

        var toolbox = new VerifyToolbox(_hub, slug, stamps);
        string prompt = PromptComposer.ComposeVerifyPrompt(targets);
        await _agent.VerifyAsync(new VerifyRequest(prompt, targets), toolbox, ct);
        notes.AddRange(toolbox.Notes);

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
            f.Record(new HistoryEntry(utc, kind, by, detail));
            _hub.Store.WriteFinding(slug, f);
            written++;
            notes.Add(note);
        }

        _hub.Store.WriteSession(new AuditSession
        {
            Id = _ulids.NewUlid(),
            AppSlug = slug,
            Mode = AuditMode.Verify,
            By = by,
            Machine = Environment.MachineName,
            StartedUtc = utc,
            EndedUtc = DateTimeOffset.UtcNow,
            Commit = commit,
            CycleN = _hub.Store.TryReadApp(slug)?.CurrentCycle ?? 1,
            Model = _agent.ModelName,
        });
        Push(slug, written + toolbox.Applied);
        return new VerifyOutcome(toolbox.Applied + measuredApplied, measuredMessages, notes);
    }

    /// <summary>Se empuja cuando se ha escrito algo, y no por haber preguntado.</summary>
    private void Push(string slug, int changed)
    {
        if (changed > 0)
        {
            _hub.Sync?.CommitAndPush($"verify: {slug} {changed} hallazgos");
        }
    }

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
        (bool anchored, int line, string? snippet) =
            SnippetAnchor.TryAnchor(clone, loc.Path, loc.Line, loc.SnippetHash);
        if (anchored)
        {
            return VerifyAim.Judge(VerifyBasis.Anclado, line, snippet, null);
        }

        // (a2) El símbolo. AQUÍ estaba el fallo: esto ya funcionaba para pintar la ficha —el banner
        // decía «re-anclado» y enseñaba el método arreglado— pero el verify no lo usaba y se rendía
        // un paso antes de preguntar. Si el símbolo existe, hay código que juzgar.
        SymbolHit hit = SymbolAnchor.FindMember(lines, loc.Path, SymbolAnchor.Candidates(f.Symbol, f.Title));
        if (hit.Found)
        {
            CodeSpanLines span = MethodBoundary.ForLine(lines, hit.Line, loc.Path);
            string text = string.Join("\n", lines[(span.StartLine - 1)..span.EndLine]);
            return VerifyAim.Judge(VerifyBasis.Simbolo, hit.Line, text, hit.Member);
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
        private readonly HashSet<string> _judged = new(StringComparer.Ordinal);
        private readonly List<string> _notes = new();

        public VerifyToolbox(HubContext hub, string slug, IReadOnlyDictionary<string, DetectionStamp> stamps)
        {
            _hub = hub;
            _slug = slug;
            _stamps = stamps;
        }

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
            switch (Normalize(verdict))
            {
                case "confirmado":
                    f.Confirm(AuditMode.Verify, stamp); // refreshes lastConfirmed, no confidence change
                    note = $"{Alias(f)}: confirmado — el defecto sigue ahí.";
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
                    f.Dispute(stamp.Utc, stamp.By, stamp.Model, evidence);
                    note = $"{Alias(f)}: el auditor sostiene que nunca fue un defecto. Queda disputado.";
                    break;

                default: // no-verificable
                    f.NeedsReview = true;
                    f.Record(new HistoryEntry(stamp.Utc, FindingEvent.Confirmed, stamp.By,
                        $"verify: no verificable — {Detail(evidence)}"));
                    note = $"{Alias(f)}: no verificable — {Detail(evidence)}";
                    break;
            }

            _judged.Add(key);
            _hub.Store.WriteFinding(_slug, f);
            _notes.Add(note);
            Applied++;
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
