using Atalaya.Domain;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Ingestion;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// Crea hallazgos nuevos en el hub. El agente nunca escribe estado — esto sí (mejora 1).
/// <para>
/// F4: aquí ya NO hay deduplicación. Un payload que llega por <c>submit_finding(s)</c> es, por
/// contrato, un hallazgo genuinamente nuevo: si correspondía a uno existente el auditor tenía que
/// haberlo reconciliado por ULID en <c>report_verdicts</c> (la lista de existentes viaja en su
/// prompt). Se acabaron el fingerprint, el matching de 2ª pasada y la vía de recurrencia: tres
/// generaciones de heurísticas que trataban de recomputar una identidad semántica que solo el LLM
/// sabe decidir (ver D-077).
/// </para>
/// <para>
/// La única salvaguarda que queda es barata y local: rechazar el duplicado exacto DENTRO de la
/// misma sesión (mismo título normalizado y misma ubicación), que protege del agente que repite
/// un payload en dos tool calls sin mirar al histórico.
/// </para>
/// </summary>
public sealed class FindingIngestionService
{
    private readonly HubContext _hub;
    private readonly IUlidFactory _ulids;

    public FindingIngestionService(HubContext hub, IUlidFactory ulids)
    {
        _hub = hub;
        _ulids = ulids;
    }

    /// <summary>Crea y persiste el hallazgo. Devuelve el hallazgo creado.</summary>
    /// <param name="theme">La lupa del ciclo que lo detectó (F17): queda escrita en el hallazgo.</param>
    public Finding Create(
        SubmittedFinding submitted, string slug, AuditMode mode, DetectionStamp stamp,
        AuditTheme theme = AuditTheme.General)
    {
        Finding created = Finding.CreateNew(_ulids.NewUlid(), submitted, mode, stamp, theme);
        _hub.Store.WriteFinding(slug, created);
        return created;
    }
}
