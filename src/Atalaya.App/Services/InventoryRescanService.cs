using Atalaya.Domain.Model;
using Atalaya.Inventory;

namespace Atalaya.App.Services;

/// <summary>
/// Re-escanea el clon local y reconcilia el inventario del ciclo vigente.
/// <para>
/// Vivía dentro de <c>InventoryViewModel.Rescan</c>. Salió en F5.8 porque el flujo de vincular un
/// clon ofrece re-escanear cuando detecta que el clon está en otro commit (§2), y un segundo
/// re-escaneo escrito aparte sería el que se olvidaría de reconciliar —arrastrar el estado
/// auditado— o de publicar.
/// </para>
/// </summary>
public sealed class InventoryRescanService
{
    private readonly HubContext _hub;
    private readonly InventoryScanner _scanner;
    private readonly MeasuredFindingService? _measured;

    /// <param name="measured">
    /// Quien pone al día los hallazgos que la app MIDE (F5.16). Opcional para no romper a quien
    /// construya el servicio a mano; en la aplicación va siempre puesto — sin él, re-escanear
    /// vuelve a dejar hallazgos de tamaño describiendo un tamaño que ya no existe.
    /// </param>
    public InventoryRescanService(
        HubContext hub, InventoryScanner scanner, MeasuredFindingService? measured = null)
    {
        _hub = hub;
        _scanner = scanner;
        _measured = measured;
    }

    /// <summary>
    /// Escanea <paramref name="clonePath"/> contra el ciclo vigente de la app, publica el
    /// inventario reconciliado y pone al día los hallazgos medidos.
    /// <para>
    /// Las dos cosas van juntas y en este orden a propósito: el inventario dice qué unidades son
    /// grandes AHORA, y los hallazgos de tamaño no son más que ese hecho contado en la otra lista.
    /// Actualizar una sin la otra es lo que dejó a MEJ-0037 activo describiendo 2.983 LOC de un
    /// fichero que ya tenía 978.
    /// </para>
    /// </summary>
    public RescanOutcome Rescan(string slug, string clonePath)
    {
        AppConfig app = _hub.Store.TryReadApp(slug)
                        ?? throw new InvalidOperationException($"La aplicación «{slug}» ya no está en el hub.");

        ScanOutput scan = _scanner.Scan(clonePath, app, app.CurrentCycle);
        InventoryCycle? previous = _hub.Store.TryReadInventory(slug, app.CurrentCycle);
        InventoryCycle merged = previous is null
            ? scan.Inventory
            : Rescanner.Reconcile(previous, scan.Inventory).Merged;

        _hub.Store.WriteInventory(slug, merged);

        MeasuredReconciliation measured = _measured?.Reconcile(slug, merged, clonePath)
                                          ?? MeasuredReconciliation.Empty;

        // Un solo push para el gesto entero: el inventario y sus hallazgos son la misma verdad.
        _hub.Sync?.CommitAndPush($"inventory: rescan {slug} cycle {app.CurrentCycle}"
            + (measured.Total > 0 ? $" (+{measured.Total} hallazgo(s) medidos)" : ""));

        return new RescanOutcome(merged.Units.Count, measured);
    }
}

/// <summary>
/// Lo que hizo un re-escaneo: cuántas unidades quedaron y qué pasó con los hallazgos medidos
/// (F5.16). El segundo dato no es decoración — una resolución que no se narra es indistinguible de
/// un borrado, y ese fue el susto que abrió esta tanda.
/// </summary>
public sealed record RescanOutcome(int Units, MeasuredReconciliation Measured)
{
    /// <summary>Permite seguir leyendo el resultado como el número de unidades de siempre.</summary>
    public static implicit operator int(RescanOutcome outcome) => outcome.Units;
}
