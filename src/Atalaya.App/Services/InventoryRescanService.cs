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

    public InventoryRescanService(HubContext hub, InventoryScanner scanner)
    {
        _hub = hub;
        _scanner = scanner;
    }

    /// <summary>
    /// Escanea <paramref name="clonePath"/> contra el ciclo vigente de la app y publica el
    /// inventario reconciliado. Devuelve cuántas unidades tiene el inventario resultante.
    /// </summary>
    public int Rescan(string slug, string clonePath)
    {
        AppConfig app = _hub.Store.TryReadApp(slug)
                        ?? throw new InvalidOperationException($"La aplicación «{slug}» ya no está en el hub.");

        ScanOutput scan = _scanner.Scan(clonePath, app, app.CurrentCycle);
        InventoryCycle? previous = _hub.Store.TryReadInventory(slug, app.CurrentCycle);
        InventoryCycle merged = previous is null
            ? scan.Inventory
            : Rescanner.Reconcile(previous, scan.Inventory).Merged;

        _hub.Store.WriteInventory(slug, merged);
        _hub.Sync?.CommitAndPush($"inventory: rescan {slug} cycle {app.CurrentCycle}");
        return merged.Units.Count;
    }
}
