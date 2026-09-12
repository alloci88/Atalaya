using Atalaya.Domain.Ids;

namespace Atalaya.Domain.Model;

/// <summary>
/// <b>Con qué se pone precio a una sesión</b> (F29 §1): la tabla de tarifas de la organización,
/// para las sesiones que lo tuvieron la reconciliación que cerró su hueco, y —desde PROV-2 §3—
/// lo que cada casa declara de su forma de contar y de facturar.
/// <para>
/// Existe para que esas cosas viajen juntas. Cuando solo viajaban las tarifas, cada consulta
/// —Métricas, Informes, el Portafolio— tenía que acordarse de buscar la reconciliación por su
/// cuenta, y a la primera que se olvidara enseñaría «sin tarifa» una sesión ya reconciliada. Es el
/// mismo argumento de <see cref="CostCalculator"/>: una sola aritmética, un solo camino. Los
/// rasgos del proveedor entran por aquí por ese mismo argumento, y no por doce firmas.
/// </para>
/// </summary>
public sealed class CostLookup
{
    private readonly IReadOnlyDictionary<Ulid, CostReconciliation> _reconciled;
    private readonly Func<string?, ProviderCostTraits> _traits;

    /// <param name="traits">
    /// De dónde salen los rasgos de la casa que escribió cada sesión (PROV-2 §3). Lo contesta el
    /// registro de proveedores, que vive fuera del dominio. Null es el histórico: la entrada con
    /// la caché dentro y ninguna frase de «sin tarifa».
    /// </param>
    public CostLookup(
        ModelRateTable? rates,
        IReadOnlyDictionary<Ulid, CostReconciliation>? reconciled = null,
        Func<string?, ProviderCostTraits>? traits = null)
    {
        Rates = rates;
        _reconciled = reconciled ?? new Dictionary<Ulid, CostReconciliation>();
        _traits = traits ?? ProviderCostTraits.Default;
    }

    /// <summary>Sin tabla y sin reconciliaciones: lo que ve un hub recién clonado.</summary>
    public static readonly CostLookup Empty = new(null);

    public ModelRateTable? Rates { get; }

    public CostReconciliation? For(Ulid sessionId)
        => _reconciled.TryGetValue(sessionId, out CostReconciliation? r) ? r : null;

    /// <summary>Lo que declara la casa que escribió eso.</summary>
    public ProviderCostTraits TraitsOf(string? providerId) => _traits(providerId);

    /// <summary>El coste de una sesión, por el único camino que hay.</summary>
    public CostResult Of(AuditSession session)
        => CostCalculator.Calculate(session, Rates, For(session.Id), _traits(session.Provider));

    /// <summary>Los huecos de coste de estas sesiones, con lo ya reconciliado descontado.</summary>
    public IReadOnlyList<SessionCostGap> GapsOf(IEnumerable<AuditSession> sessions)
        => CostReconciler.GapsOf(sessions, Rates, s => For(s.Id), _traits);
}
