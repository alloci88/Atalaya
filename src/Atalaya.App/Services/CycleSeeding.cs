using Atalaya.Domain;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// Lo que queda ENVEJECIDO en el momento de cerrar un ciclo (F9.2 §2): las unidades cuya auditoría
/// ya no describe el código que hay.
/// <para>
/// <b>Los dos números van separados y no se suman</b>, por lo mismo que en el panel (D-687): piden
/// acciones distintas —auditar y verificar—, con coste distinto y con instrumento distinto.
/// </para>
/// </summary>
/// <param name="Changed">Auditadas cuyo código cambió por mano ajena. El siguiente ciclo las hereda pendientes.</param>
/// <param name="FixedPendingVerify">Arregladas desde Atalaya y aún sin verificar. Conservan su estado y su acción.</param>
public sealed record CycleAging(int Changed, int FixedPendingVerify)
{
    /// <summary>Nada envejecido: ni se ha podido mirar, o se miró y estaba todo limpio.</summary>
    public static CycleAging None { get; } = new(0, 0);

    public bool Any => Changed > 0 || FixedPendingVerify > 0;

    /// <summary>
    /// «Cerrado con 4 cambiadas desde su auditoría y 1 sin verificar». <c>null</c> cuando no hay
    /// nada que contar: cerrar limpio no estrena una frase que diga «cero y cero» (F9.2 §2).
    /// </summary>
    public string? Sentence
    {
        get
        {
            if (!Any)
            {
                return null;
            }

            var parts = new List<string>(2);
            if (Changed > 0)
            {
                parts.Add(Changed == 1
                    ? "1 cambiada desde su auditoría"
                    : $"{Changed} cambiadas desde su auditoría");
            }

            if (FixedPendingVerify > 0)
            {
                parts.Add(FixedPendingVerify == 1 ? "1 sin verificar" : $"{FixedPendingVerify} sin verificar");
            }

            return $"Cerrado con {string.Join(" y ", parts)}.";
        }
    }
}

/// <summary>
/// Qué significa EMPEZAR un ciclo (F9.2 §1): con qué estado nace cada unidad del inventario nuevo.
/// <para>
/// <b>La deriva se cobra aquí, y solo aquí.</b> Durante el ciclo la deriva es ortogonal y no reabre
/// nada: un ciclo tiene que poder cerrarse aunque el código siga vivo, o no se cerraría nunca. Pero
/// al cambiar de ciclo esa deuda de mirada sí se cobra — arrastrar «auditada» sobre código que ya no
/// es el que se miró convertiría la cobertura del ciclo siguiente en una cifra falsa.
/// </para>
/// <para>
/// <b>Y solo se conserva lo que se puede demostrar.</b> Sigue auditada la unidad cuyo commit de
/// auditoría se puede comparar con HEAD y sale idéntica; todo lo demás —cambiada, sin historial, sin
/// clon con el que comparar— nace pendiente, porque sin evidencia no hay estado (N-2). Re-auditar
/// código que no ha cambiado sería quemar cuota sin causa; darlo por bueno sin poder mirarlo, mentir.
/// </para>
/// </summary>
public static class CycleSeeding
{
    /// <summary>
    /// El inventario del ciclo <paramref name="nextCycle"/> a partir del que se cierra.
    /// <paramref name="drift"/> es la deriva medida sobre el ciclo que TERMINA; <c>null</c> —o con
    /// problema, que es lo mismo: no se ha podido mirar— siembra todo pendiente.
    /// </summary>
    public static InventoryCycle Seed(
        InventoryCycle closing, int nextCycle, int largeUnitLoc, AppDrift? drift)
    {
        IReadOnlyDictionary<string, DriftState> byPath = StatesByPath(drift);
        var fresh = new InventoryCycle { CycleN = nextCycle };

        foreach (InventoryUnit u in closing.Units)
        {
            bool keepsAudit = u.State == UnitState.Auditada
                && byPath.TryGetValue(u.Path, out DriftState state)
                && Survives(state);

            fresh.Units.Add(new InventoryUnit
            {
                Path = u.Path,
                Module = u.Module,
                Loc = u.Loc,
                ContentHash = u.ContentHash,

                // Las grandes se re-evalúan contra el umbral (§5.5) — pero solo las que vuelven a
                // entrar en la cola. Degradar a «Grande» una unidad auditada y sin deriva sería
                // pedir que se vuelva a mirar lo que ya se miró y no ha cambiado.
                State = keepsAudit
                    ? UnitState.Auditada
                    : u.Loc > largeUnitLoc ? UnitState.Grande : UnitState.Pendiente,

                // El ancla de la auditoría viaja con el estado, o no viaja. La unidad que nace
                // pendiente PIERDE la marca: su próxima auditoría estrenará commit, y conservar el
                // viejo solo serviría para que la deriva siguiera contando un rango ya cobrado.
                AuditedInSession = keepsAudit ? u.AuditedInSession : null,
            });
        }

        return fresh;
    }

    /// <summary>Lo que queda envejecido del ciclo que se cierra. Sin deriva medible, nada que decir.</summary>
    public static CycleAging AgingOf(AppDrift? drift)
        => drift is null || drift.Problem is not null
            ? CycleAging.None
            : new CycleAging(drift.Changed, drift.FixedPendingVerify);

    /// <summary>
    /// Los dos estados de deriva que NO cobran la auditoría al cambiar de ciclo.
    /// <list type="bullet">
    /// <item><see cref="DriftState.SinCambios"/> — el código es el que se miró. Su commit de
    /// auditoría sigue valiendo, y con él la deriva del ciclo nuevo se sigue pudiendo medir.</item>
    /// <item><see cref="DriftState.ArregladaPendienteDeVerificar"/> — el alcance está acotado por la
    /// huella del arreglo. Verificar sigue siendo su cierre correcto y es más barato que re-auditar;
    /// degradarla a pendiente cambiaría un verify por una auditoría entera sin ganar nada.</item>
    /// </list>
    /// <see cref="DriftState.Borrada"/> no sobrevive: el fichero no está, no hay nada que dar por
    /// auditado, y de sus hallazgos ya se ocupa el flujo de «unidades que ya no existen». La retira
    /// el re-escaneo, como siempre.
    /// </summary>
    private static bool Survives(DriftState state)
        => state is DriftState.SinCambios or DriftState.ArregladaPendienteDeVerificar;

    private static IReadOnlyDictionary<string, DriftState> StatesByPath(AppDrift? drift)
    {
        if (drift is null || drift.Problem is not null || drift.Units.Count == 0)
        {
            return new Dictionary<string, DriftState>(StringComparer.Ordinal);
        }

        var map = new Dictionary<string, DriftState>(StringComparer.Ordinal);
        foreach (UnitDrift u in drift.Units)
        {
            map[u.Path] = u.State;
        }

        return map;
    }
}
