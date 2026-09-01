using Atalaya.Domain.Model;
using Atalaya.Inventory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly DirectiveService? _directives;
    private readonly ILogger _log;

    /// <param name="measured">
    /// Quien pone al día los hallazgos que la app MIDE (F5.16). Opcional para no romper a quien
    /// construya el servicio a mano; en la aplicación va siempre puesto — sin él, re-escanear
    /// vuelve a dejar hallazgos de tamaño describiendo un tamaño que ya no existe.
    /// </param>
    /// <param name="directives">
    /// Quien sabe qué ficheros de convenciones propone el catálogo (F7 §1). Opcional igual que
    /// <paramref name="measured"/>; sin él, un re-escaneo simplemente no anuncia candidatos.
    /// </param>
    public InventoryRescanService(
        HubContext hub, InventoryScanner scanner,
        MeasuredFindingService? measured = null, DirectiveService? directives = null,
        ILogger<InventoryRescanService>? log = null)
    {
        _hub = hub;
        _scanner = scanner;
        _measured = measured;
        _directives = directives;
        _log = log ?? NullLogger<InventoryRescanService>.Instance;
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

        // Queda escrito el número con el que se clasificó, y de dónde salió. Diagnosticar el
        // defecto que trajo aquí costó mirar tres ficheros porque el log contaba el resultado y no
        // el criterio.
        _log.LogInformation(
            "Re-escaneo de {Slug} (ciclo {Cycle}): umbral de unidad grande {Loc} LOC / {Chars} "
            + "caracteres (política de la aplicación).",
            slug, app.CurrentCycle, app.Thresholds.LargeUnitLoc, app.Thresholds.LargeUnitChars);

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

        // F7 §1: el re-escaneo PROPONE directivas nuevas y no activa ninguna. Se cuentan aquí,
        // dentro del mismo gesto, porque el fichero de convenciones que alguien acaba de añadir al
        // repositorio llega al clon por el mismo camino que el código.
        IReadOnlyList<string> newDirectives =
            _directives?.NewCandidates(slug, clonePath) ?? Array.Empty<string>();

        return new RescanOutcome(merged.Units.Count, measured, newDirectives);
    }
}

/// <summary>
/// Lo que hizo un re-escaneo: cuántas unidades quedaron y qué pasó con los hallazgos medidos
/// (F5.16). El segundo dato no es decoración — una resolución que no se narra es indistinguible de
/// un borrado, y ese fue el susto que abrió esta tanda.
/// </summary>
/// <param name="NewDirectives">
/// Rutas de ficheros de convenciones que el catálogo propone y que nadie ha curado todavía (F7).
/// Se anuncian; <b>no se activan</b>. Que una directiva empiece a informar al auditor sin que nadie
/// lo haya decidido sería cambiar el criterio de la auditoría en silencio, que es exactamente lo
/// contrario de para lo que existe esta funcionalidad.
/// </param>
public sealed record RescanOutcome(
    int Units,
    MeasuredReconciliation Measured,
    IReadOnlyList<string>? NewDirectives = null)
{
    /// <summary>Los candidatos nuevos, nunca null: el aviso los cuenta sin comprobar nada antes.</summary>
    public IReadOnlyList<string> Candidates => NewDirectives ?? Array.Empty<string>();

    /// <summary>Permite seguir leyendo el resultado como el número de unidades de siempre.</summary>
    public static implicit operator int(RescanOutcome outcome) => outcome.Units;
}
