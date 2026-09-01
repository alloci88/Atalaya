using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// Finding governance (§5.6, §5.7): silence, un-silence, assign, comment, change severity, resolve
/// manually, reopen. Every action mutates a finding through its domain methods (audit trail),
/// persists, and pushes immediately. Silencing is always a human action with author and reason.
/// </summary>
public sealed class GovernanceService
{
    private readonly HubContext _hub;
    private readonly IUlidFactory _ulids;

    public GovernanceService(HubContext hub, IUlidFactory ulids)
    {
        _hub = hub;
        _ulids = ulids;
    }

    private string Me => _hub.ResolveIdentity().Name;

    /// <summary>
    /// Silencia ESTE hallazgo, y solo este. Es un hecho del hallazgo: alguien lo miró y decidió,
    /// así que sobrevive a que se retire cualquier patrón (F12 §F) — la procedencia de patrón, si
    /// la había, queda sustituida por esta decisión, que es más reciente y de una persona.
    /// </summary>
    public void Silence(string slug, Ulid findingId, SilenceReason reason, string? notes, DateTimeOffset? expiresUtc)
    {
        Finding f = Require(slug, findingId);
        _hub.Store.WriteSilence(slug, new Silence
        {
            FindingUlid = f.Id,
            Reason = reason,
            Notes = notes,
            By = Me,
            Utc = DateTimeOffset.UtcNow,
            ExpiresUtc = expiresUtc,
        });

        f.MarkSilenced(DateTimeOffset.UtcNow, Me, notes);
        _hub.Store.WriteFinding(slug, f);
        Push(slug, $"silence: {f.DisplayId ?? f.Id.ToString()}");
    }

    // ------------------------------------------------------------------ F5.12 · silencio por patrón

    /// <summary>
    /// Qué pasó al silenciar un tipo de problema: el patrón creado y si el hallazgo origen se
    /// silenció de paso. Se devuelve para poder contarlo, no para decidir nada.
    /// </summary>
    public sealed record PatternSilenceResult(PatternSilence Pattern, bool SilencedSource);

    /// <summary>
    /// Silencia un TIPO de problema en UNA aplicación (F5.12). Desde este momento el ejemplar viaja
    /// en el prompt de cada unidad auditada y el auditor deja de reportar lo que corresponda a él.
    /// <para>
    /// El hallazgo origen se silencia con el patrón, sin preguntar, y así queda escrito en su
    /// procedencia. No es una decisión aparte como lo era el silencio en masa de F5.10: aquel
    /// ofrecía «los N hallazgos de esta regla», una población que solo existía porque existía la
    /// taxonomía. Sin taxonomía, el único hallazgo del que se sabe con certeza que pertenece al
    /// patrón es el que acaba de mirarse para crearlo — silenciar el tipo y dejar activo el caso
    /// que lo motivó sería incoherente. Los demás se silencian uno a uno desde su ficha, o
    /// desaparecen solos en la siguiente auditoría.
    /// </para>
    /// </summary>
    public PatternSilenceResult SilencePattern(
        string slug, Ulid sourceFindingId, string exemplar, SilenceReason reason, string? notes,
        DateTimeOffset? expiresUtc)
    {
        string phrase = (exemplar ?? string.Empty).Trim();
        if (phrase.Length == 0)
        {
            throw new ArgumentException(
                "Un patrón sin ejemplar no le dice nada al auditor: la frase ES el alcance.", nameof(exemplar));
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var pattern = new PatternSilence
        {
            Id = _ulids.NewUlid(),
            ShortId = PatternShortId.Next(_hub.Store.ListPatternSilences(slug)),
            Exemplar = phrase,
            SourceFindingUlid = sourceFindingId,
            Reason = reason,
            Notes = notes,
            By = Me,
            Utc = now,
            ExpiresUtc = expiresUtc,
        };
        _hub.Store.WritePatternSilence(slug, pattern);

        bool silencedSource = false;
        Finding source = Require(slug, sourceFindingId);
        if (source.Status != FindingStatus.Silenciado)
        {
            _hub.Store.WriteSilence(slug, new Silence
            {
                FindingUlid = source.Id,
                Reason = reason,
                Notes = notes,
                By = Me,
                Utc = now,
                ExpiresUtc = expiresUtc,
                ByPatternExemplar = phrase,

                // F12 §F: la procedencia deja de ser solo un texto legible y pasa a ser el
                // enganche. Con el id, este silencio es DERIVADO — vale mientras el patrón viva.
                ByPatternId = pattern.Id,
            });

            source.MarkSilenced(now, Me, $"silenciado al silenciar el patrón «{phrase}»"
                + (string.IsNullOrWhiteSpace(notes) ? "" : $": {notes!.Trim()}"));
            _hub.Store.WriteFinding(slug, source);
            silencedSource = true;
        }

        // Un solo push para todo el gesto: silenciar el tipo y su caso origen son una sola decisión
        // del usuario, y partirla en dos commits contaría dos cosas donde hubo una.
        Push(slug, $"pattern-silence: {pattern.ShortId} en {slug}");
        return new PatternSilenceResult(pattern, silencedSource);
    }

    /// <summary>
    /// Retira el patrón: el ejemplar deja de viajar en el prompt, el auditor vuelve a reportar
    /// problemas de ese tipo, y <b>lo que solo él tapaba vuelve a activo al instante</b> (F12 §F).
    /// <para>
    /// <b>El silencio por patrón es DERIVADO.</b> Antes no lo era: el veredicto «silenciado» quedaba
    /// congelado en cada hallazgo, retirar el patrón no revivía nada, y la única forma de recuperar
    /// lo que había tapado era otra auditoría —pagada—. Un silencio que se pone gratis y solo se
    /// quita pagando no es reversible: es una puerta de un solo sentido con aspecto de interruptor.
    /// Mismo principio que la deriva: lo que se deriva de un hecho vigente no se guarda como
    /// veredicto.
    /// </para>
    /// <para>
    /// <b>El silencio individual SÍ es un hecho del hallazgo y se conserva.</b> Alguien miró ESE
    /// caso y decidió sobre él; retirar un patrón no deshace la decisión de nadie. La distinción la
    /// lleva <see cref="Silence.ByPatternId"/>, y quien silencia a mano un hallazgo que el patrón
    /// tapaba lo convierte en individual por el mismo gesto.
    /// </para>
    /// </summary>
    public bool UnsilencePattern(string slug, Ulid patternId)
    {
        PatternSilence? pattern = _hub.Store.TryReadPatternSilence(slug, patternId);
        if (!_hub.Store.DeletePatternSilence(slug, patternId))
        {
            return false;
        }

        int revived = ReconcilePatternSilences(slug);
        Push(slug, $"pattern-silence: retirado {pattern?.ShortId ?? patternId.ToString()} en {slug}"
            + (revived > 0 ? $" ({revived} hallazgo(s) de vuelta a activo)" : string.Empty));
        return true;
    }

    /// <summary>
    /// Vuelve a derivar el silencio POR PATRÓN de una aplicación (F12 §F), y devuelve cuántos
    /// hallazgos han vuelto a activo.
    /// <para>
    /// La regla, en una frase: <b>un hallazgo está silenciado por patrón si algún patrón vigente lo
    /// cubre</b>. Así que aquí, para cada silencio con procedencia de patrón:
    /// </para>
    /// <list type="bullet">
    /// <item>si su patrón ya no está, el silencio se retira y el hallazgo vuelve a activo — gratis,
    /// sin re-auditar, y con la razón escrita en el historial;</item>
    /// <item>si sigue estando, el silencio se mantiene AL DÍA con él: la frase y la caducidad son
    /// del patrón, no una copia que envejece por su cuenta.</item>
    /// </list>
    /// <para>
    /// <b>Los silencios anteriores a F12</b> no traen <see cref="Silence.ByPatternId"/>; se
    /// enganchan por el texto del ejemplar, que es el único dato que guardaban. Si ese texto ya no
    /// corresponde a ningún patrón, es que el patrón se retiró: mismo desenlace.
    /// </para>
    /// <para>
    /// No hace push: lo hace quien provoca el cambio, para que retirar un patrón siga siendo UN
    /// commit y no dos contando lo mismo.
    /// </para>
    /// </summary>
    public int ReconcilePatternSilences(string slug)
    {
        IReadOnlyList<PatternSilence> patterns = _hub.Store.ListPatternSilences(slug);
        var byId = patterns.ToDictionary(p => p.Id);
        var byExemplar = new Dictionary<string, PatternSilence>(StringComparer.OrdinalIgnoreCase);
        foreach (PatternSilence p in patterns)
        {
            byExemplar[p.Exemplar.Trim()] = p;
        }

        int revived = 0;

        foreach (Silence silence in _hub.Store.ListSilences(slug))
        {
            PatternSilence? owner = Owner(silence, byId, byExemplar);
            if (owner is null)
            {
                if (!FromPattern(silence))
                {
                    continue; // Individual: es un hecho del hallazgo y no se toca.
                }

                revived += Revive(slug, silence);
                continue;
            }

            // Vigente: el silencio derivado no puede desviarse de su patrón. Si el ejemplar se
            // reescribió o la caducidad se movió, aquí se pone al día — y solo se escribe si algo
            // cambió de verdad, para no ensuciar el hub con reescrituras idénticas.
            if (string.Equals(silence.ByPatternExemplar, owner.Exemplar, StringComparison.Ordinal)
                && silence.ExpiresUtc == owner.ExpiresUtc
                && silence.ByPatternId == owner.Id)
            {
                continue;
            }

            silence.ByPatternExemplar = owner.Exemplar;
            silence.ExpiresUtc = owner.ExpiresUtc;
            silence.ByPatternId = owner.Id;
            _hub.Store.WriteSilence(slug, silence);
        }

        return revived;
    }

    /// <summary>¿Este silencio lo puso un patrón, o una persona sobre este hallazgo?</summary>
    private static bool FromPattern(Silence silence)
        => silence.ByPatternId is not null || !string.IsNullOrWhiteSpace(silence.ByPatternExemplar);

    /// <summary>El patrón VIGENTE que respalda este silencio, o null si ya no hay ninguno.</summary>
    private static PatternSilence? Owner(
        Silence silence,
        IReadOnlyDictionary<Ulid, PatternSilence> byId,
        IReadOnlyDictionary<string, PatternSilence> byExemplar)
    {
        if (silence.ByPatternId is { } id)
        {
            return byId.TryGetValue(id, out PatternSilence? p) ? p : null;
        }

        // Migración: los silencios anteriores a F12 solo guardaban la frase.
        return silence.ByPatternExemplar is { Length: > 0 } phrase
               && byExemplar.TryGetValue(phrase.Trim(), out PatternSilence? legacy)
            ? legacy
            : null;
    }

    /// <summary>
    /// Devuelve a activo un hallazgo que solo tapaba un patrón ya retirado. El historial lo dice con
    /// todas las letras: nadie silenció ESTE hallazgo, así que nadie está deshaciendo la decisión de
    /// nadie.
    /// </summary>
    private int Revive(string slug, Silence silence)
    {
        _hub.Store.DeleteSilence(slug, silence.FindingUlid);

        Finding? f = _hub.Store.TryReadFinding(slug, silence.FindingUlid.ToString());
        if (f is null)
        {
            return 0;
        }

        string phrase = string.IsNullOrWhiteSpace(silence.ByPatternExemplar)
            ? "que lo silenciaba"
            : $"«{silence.ByPatternExemplar!.Trim()}»";
        f.Unsilence(DateTimeOffset.UtcNow, Me,
            $"se retiró el patrón {phrase}: vuelve a activo. Nadie había silenciado este hallazgo "
            + "en particular.");
        _hub.Store.WriteFinding(slug, f);
        return 1;
    }

    /// <summary>
    /// Reescribe el ejemplar de un patrón vivo o caducado. Es la operación central de la gestión:
    /// afinar el alcance es editar una frase, no mantener un catálogo. Quien la toca pasa a ser su
    /// autor —la frase nueva es suya— y el id corto NO cambia, para que un informe viejo siga
    /// nombrando lo mismo. Las supresiones acumuladas se conservan: siguen siendo el trabajo de
    /// este patrón.
    /// </summary>
    public bool EditPatternExemplar(string slug, Ulid patternId, string exemplar)
    {
        string phrase = (exemplar ?? string.Empty).Trim();
        if (phrase.Length == 0)
        {
            return false;
        }

        PatternSilence? pattern = _hub.Store.TryReadPatternSilence(slug, patternId);
        if (pattern is null)
        {
            return false;
        }

        pattern.Exemplar = phrase;
        pattern.By = Me;
        pattern.Utc = DateTimeOffset.UtcNow;
        _hub.Store.WritePatternSilence(slug, pattern);

        // Lo derivado sigue a su origen (F12 §F): si no, la ficha de un hallazgo tapado por este
        // patrón citaría la frase vieja para siempre.
        ReconcilePatternSilences(slug);
        Push(slug, $"pattern-silence: ejemplar de {pattern.ShortId} en {slug}");
        return true;
    }

    /// <summary>
    /// Cambia la caducidad de un patrón vivo o caducado, conservando motivo y notas. Quien la toca
    /// pasa a ser su autor: es una decisión nueva sobre cuánto más dura, y firmarla con el nombre
    /// de quien lo creó haría que el registro mintiera. Poner 0 días lo devuelve a permanente, que
    /// es además la forma de revivir uno caducado sin volver a escribirlo todo.
    /// </summary>
    public bool SetPatternExpiry(string slug, Ulid patternId, DateTimeOffset? expiresUtc)
    {
        PatternSilence? pattern = _hub.Store.TryReadPatternSilence(slug, patternId);
        if (pattern is null)
        {
            return false;
        }

        pattern.ExpiresUtc = expiresUtc;
        pattern.By = Me;
        pattern.Utc = DateTimeOffset.UtcNow;
        _hub.Store.WritePatternSilence(slug, pattern);

        // Igual que con el ejemplar: «vigente» lo decide el patrón, así que su caducidad manda
        // sobre la de los silencios que puso.
        ReconcilePatternSilences(slug);
        Push(slug, $"pattern-silence: caducidad de {pattern.ShortId} en {slug}");
        return true;
    }

    /// <summary>
    /// El patrón que nació de este hallazgo, si alguno sigue vivo o caducado en el hub. Es lo que
    /// permite que la ficha diga «origen del patrón silenciado …» en vez de dejar al hallazgo sin
    /// explicar por qué se silenció solo.
    /// </summary>
    public PatternSilence? PatternOriginatedBy(string slug, Ulid findingId)
        => _hub.Store.ListPatternSilences(slug)
            .FirstOrDefault(p => p.SourceFindingUlid == findingId);

    public void Unsilence(string slug, Ulid findingId)
    {
        Finding f = Require(slug, findingId);
        _hub.Store.DeleteSilence(slug, f.Id);
        f.Unsilence(DateTimeOffset.UtcNow, Me, "des-silenciado");
        _hub.Store.WriteFinding(slug, f);
        Push(slug, $"unsilence: {f.DisplayId ?? f.Id.ToString()}");
    }

    public void Assign(string slug, Ulid findingId, string? assignee)
    {
        Finding f = Require(slug, findingId);
        f.Assign(assignee, DateTimeOffset.UtcNow, Me);
        _hub.Store.WriteFinding(slug, f);
        Push(slug, $"assign: {f.DisplayId ?? f.Id.ToString()}");
    }

    public void ChangeSeverity(string slug, Ulid findingId, Severity severity)
    {
        Finding f = Require(slug, findingId);
        f.ChangeSeverity(severity, DateTimeOffset.UtcNow, Me);
        _hub.Store.WriteFinding(slug, f);
        Push(slug, $"severity: {f.DisplayId ?? f.Id.ToString()} → {severity}");
    }

    public void ResolveManually(string slug, Ulid findingId, string justification, string commit)
    {
        Finding f = Require(slug, findingId);
        f.Resolve(new ResolutionStamp(DateTimeOffset.UtcNow, ResolutionVia.Manual, AuditMode.Verify, commit, Me, justification));
        _hub.Store.WriteFinding(slug, f);
        Push(slug, $"resolve: {f.DisplayId ?? f.Id.ToString()}");
    }

    /// <summary>
    /// Resuelve un hallazgo porque el CÓDIGO QUE LO CONTENÍA YA NO EXISTE (F9 §4).
    /// <para>
    /// La ejecuta una persona, siempre, y nunca la aplicación: un fichero que no está donde estaba
    /// puede haberse movido, y «no está» no es «ya no existe». Lo que la aplicación aporta es la
    /// EVIDENCIA —el commit que borró el fichero, buscado en el historial— y la atribución de quien
    /// decide. Sin esta salida, los hallazgos de código borrado se quedan zombis para siempre:
    /// activos, incontables e imposibles de verificar, porque no hay nada que mirar.
    /// </para>
    /// </summary>
    /// <param name="deletedCommit">
    /// El commit del borrado, o <c>null</c> si no se localizó. Se registra lo que hay: una
    /// resolución sin evidencia de commit lo DICE, en vez de inventarse una.
    /// </param>
    public void ResolveAsDeletedCode(string slug, Ulid findingId, string unitPath, string? deletedCommit)
    {
        Finding f = Require(slug, findingId);
        string evidence = deletedCommit is { Length: > 0 }
            ? $"«{unitPath}» ya no existe en el repositorio: la borró el commit {deletedCommit}."
            : $"«{unitPath}» ya no existe en el repositorio; no se ha podido localizar el commit "
              + "que la borró, así que esta resolución se apoya solo en que hoy no está.";

        f.Resolve(new ResolutionStamp(
            DateTimeOffset.UtcNow, ResolutionVia.CodigoEliminado, AuditMode.Verify,
            deletedCommit ?? "unknown", Me, evidence));

        _hub.Store.WriteFinding(slug, f);
        Push(slug, $"resolve (código eliminado): {f.DisplayId ?? f.Id.ToString()}");
    }

    /// <summary>
    /// Cierra una disputa dando la razón al auditor que discrepó (F5.1b): el hallazgo NO se
    /// resuelve —nunca hubo nada que arreglar— sino que se silencia con motivo
    /// <see cref="SilenceReason.FalsoPositivo"/>, que es el cajón que §2 ya tenía para esto, con
    /// autor y fecha. La marca de disputa se retira porque la decisión ya está tomada; el
    /// historial la conserva.
    /// </summary>
    public void ResolveDisputeAsFalsePositive(string slug, Ulid findingId, string? notes)
    {
        Finding f = Require(slug, findingId);
        string justification = notes ?? DescribeDisputes(f);

        _hub.Store.WriteSilence(slug, new Silence
        {
            FindingUlid = f.Id,
            Reason = SilenceReason.FalsoPositivo,
            Notes = justification,
            By = Me,
            Utc = DateTimeOffset.UtcNow,
            ExpiresUtc = null,
        });

        f.ClearDisputes(DateTimeOffset.UtcNow, Me, $"disputa aceptada como falso positivo: {justification}");
        f.MarkSilenced(DateTimeOffset.UtcNow, Me, justification);
        _hub.Store.WriteFinding(slug, f);
        Push(slug, $"dispute: falso-positivo {f.DisplayId ?? f.Id.ToString()}");
    }

    /// <summary>
    /// Cierra una disputa dando la razón a quien lo reportó (F5.1b): sigue siendo un defecto. Se
    /// retira la marca y el hallazgo continúa exactamente como estaba.
    /// </summary>
    public void DismissDispute(string slug, Ulid findingId, string? notes)
    {
        Finding f = Require(slug, findingId);
        f.ClearDisputes(DateTimeOffset.UtcNow, Me,
            notes is null ? null : $"sigue siendo un defecto: {notes}");
        _hub.Store.WriteFinding(slug, f);
        Push(slug, $"dispute: mantenido {f.DisplayId ?? f.Id.ToString()}");
    }

    /// <summary>Resume quién discrepó y por qué, para dejarlo escrito en el silencio.</summary>
    private static string DescribeDisputes(Finding f)
        => f.Disputes.Count == 0
            ? "falso positivo (sin disputa registrada)"
            : string.Join(" · ", f.Disputes.Select(d => $"{d.Model ?? "auditor"}: {d.Justification}"));

    public void Reopen(string slug, Ulid findingId, string? detail)
    {
        Finding f = Require(slug, findingId);
        f.Reopen(DateTimeOffset.UtcNow, Me, detail);
        _hub.Store.WriteFinding(slug, f);
        Push(slug, $"reopen: {f.DisplayId ?? f.Id.ToString()}");
    }

    public Comment AddComment(string slug, Ulid findingId, string body, string? kind = null)
    {
        var comment = new Comment
        {
            Id = _ulids.NewUlid(),
            FindingUlid = findingId,
            By = Me,
            Utc = DateTimeOffset.UtcNow,
            Body = body,
            Kind = kind,
        };
        _hub.Store.WriteComment(slug, comment);
        Push(slug, $"comment: {findingId}");
        return comment;
    }

    private Finding Require(string slug, Ulid findingId)
        => _hub.Store.TryReadFinding(slug, findingId.ToString())
           ?? throw new InvalidOperationException($"Hallazgo {findingId} no encontrado.");

    private void Push(string slug, string message) => _hub.Sync?.CommitAndPush(message);
}
