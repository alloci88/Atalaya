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
    /// Retira el patrón: el ejemplar deja de viajar en el prompt y el auditor vuelve a reportar
    /// problemas de ese tipo en la auditoría siguiente.
    /// <para>
    /// NO des-silencia el hallazgo que lo originó ni ningún otro. Cada uno de esos silencios fue
    /// una decisión registrada con su autor y su motivo; deshacerla en cascada tiraría también las
    /// que se hubieran revisado a mano desde entonces. Se levantan desde su ficha.
    /// </para>
    /// </summary>
    public bool UnsilencePattern(string slug, Ulid patternId)
    {
        PatternSilence? pattern = _hub.Store.TryReadPatternSilence(slug, patternId);
        if (!_hub.Store.DeletePatternSilence(slug, patternId))
        {
            return false;
        }

        Push(slug, $"pattern-silence: retirado {pattern?.ShortId ?? patternId.ToString()} en {slug}");
        return true;
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
