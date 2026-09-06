using Atalaya.Domain.Ids;

namespace Atalaya.Domain.Model;

/// <summary>
/// <b>Con qué se pone precio a una sesión</b> (F29 §1): la tabla de tarifas de la organización y,
/// para las sesiones que lo tuvieron, la reconciliación que cerró su hueco.
/// <para>
/// Existe para que las dos cosas viajen juntas. Cuando solo viajaban las tarifas, cada consulta
/// —Métricas, Informes, el Portafolio— tenía que acordarse de buscar la reconciliación por su
/// cuenta, y a la primera que se olvidara enseñaría «sin tarifa» una sesión ya reconciliada. Es el
/// mismo argumento de <see cref="CreditCalculator"/>: una sola aritmética, un solo camino.
/// </para>
/// </summary>
public sealed class CostLookup
{
    private readonly IReadOnlyDictionary<Ulid, CostReconciliation> _reconciled;

    public CostLookup(
        ModelRateTable? rates,
        IReadOnlyDictionary<Ulid, CostReconciliation>? reconciled = null)
    {
        Rates = rates;
        _reconciled = reconciled ?? new Dictionary<Ulid, CostReconciliation>();
    }

    /// <summary>Sin tabla y sin reconciliaciones: lo que ve un hub recién clonado.</summary>
    public static readonly CostLookup Empty = new(null);

    public ModelRateTable? Rates { get; }

    public CostReconciliation? For(Ulid sessionId)
        => _reconciled.TryGetValue(sessionId, out CostReconciliation? r) ? r : null;

    /// <summary>El coste de una sesión, por el único camino que hay.</summary>
    public CostResult Of(AuditSession session)
        => CreditCalculator.Calculate(session, Rates, For(session.Id));

    /// <summary>Los huecos de coste de estas sesiones, con lo ya reconciliado descontado.</summary>
    public IReadOnlyList<SessionCostGap> GapsOf(IEnumerable<AuditSession> sessions)
        => CostReconciler.GapsOf(sessions, Rates, s => For(s.Id));
}
