using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>Lo que el diálogo de configurar necesita saber del ciclo ANTES de proponer nada.</summary>
/// <param name="AuditedUnits">
/// Cuántas unidades están auditadas bajo la lupa actual. Es el número del aviso: si se cambia la
/// temática, todas ellas vuelven a pendientes, porque auditada bajo otra lupa no es auditada bajo
/// esta (F17 §4).
/// </param>
public sealed record CycleConfigPreview(
    string Slug, string AppName, int CycleN, CycleConfig Current, int AuditedUnits);

/// <summary>El resultado de aplicar una configuración: qué se tocó y cómo se cuenta.</summary>
public sealed record CycleConfigResult(bool Applied, int Reseeded, string Message)
{
    public static CycleConfigResult Unchanged { get; } =
        new(false, 0, "La configuración del ciclo no ha cambiado.");
}

/// <summary>
/// Configurar un ciclo (F17 §4): su temática y su juez preferido, escritos en el <c>cycle{N}.json</c>
/// del hub y publicados — es política compartida por la regla de F13 (D-769): lo que decide se
/// escribe en el hub y cambia lo que «auditada» significa para todo el equipo.
/// <para>
/// <b>Una sola mecánica para cambiar de temática, se cambie cuando se cambie.</b> A mitad de
/// ciclo o al configurar el siguiente recién heredado, la operación es la misma: se avisa de
/// cuántas auditadas pasarán a pendientes, y se re-siembra con <see cref="CycleSeeding.Reseed"/>.
/// Los hallazgos existentes no se tocan —siguen activos, con su temática— y las grandes siguen
/// siendo grandes. Cambiar solo el modelo preferido no re-siembra nada: no cambia la lupa.
/// </para>
/// </summary>
public sealed class CycleConfigService
{
    private readonly HubContext _hub;
    private readonly DriftQuery? _drift;

    public CycleConfigService(HubContext hub, DriftQuery? drift = null)
    {
        _hub = hub;
        _drift = drift;
    }

    /// <summary>La configuración vigente y cuánto trabajo hecho hay bajo ella. Null sin app o sin inventario.</summary>
    public CycleConfigPreview? Preview(string slug)
    {
        AppConfig? app = _hub.Store.TryReadApp(slug);
        InventoryCycle? inv = app is null ? null : _hub.Store.TryReadInventory(slug, app.CurrentCycle);
        if (app is null || inv is null)
        {
            return null;
        }

        return new CycleConfigPreview(
            slug, app.Name, inv.CycleN, inv.Config, inv.Units.Count(u => u.State == UnitState.Auditada));
    }

    /// <summary>
    /// El aviso que se enseña ANTES de cambiar de temática con trabajo hecho. Lo redacta el
    /// servicio y no la ventana, para que el diálogo, el toast y el test digan la misma frase.
    /// Vacío a cero: cambiar de lupa sin nada auditado no cuesta nada, y no hay que avisar.
    /// </summary>
    public static string ChangeWarning(int auditedUnits) => auditedUnits switch
    {
        <= 0 => string.Empty,
        1 => "1 unidad auditada pasará a pendiente; los hallazgos existentes no se tocan.",
        _ => $"{auditedUnits} unidades auditadas pasarán a pendientes; los hallazgos existentes no se tocan.",
    };

    /// <summary>
    /// Escribe la configuración en el ciclo vigente y la publica. Si cambia la temática, re-siembra:
    /// todas las auditables a pendiente, las grandes siguen grandes, los hallazgos intactos.
    /// </summary>
    public CycleConfigResult Apply(string slug, CycleConfig config)
    {
        AppConfig? app = _hub.Store.TryReadApp(slug);
        InventoryCycle? inv = app is null ? null : _hub.Store.TryReadInventory(slug, app.CurrentCycle);
        if (app is null || inv is null)
        {
            return new CycleConfigResult(false, 0, "No hay ciclo que configurar en esta aplicación.");
        }

        if (inv.Config == config)
        {
            return CycleConfigResult.Unchanged;
        }

        bool themeChanged = inv.Theme != config.Theme;
        int reseeded = themeChanged ? inv.Units.Count(u => u.State == UnitState.Auditada) : 0;

        InventoryCycle written = CycleSeeding.Reseed(inv, config, app.Thresholds.LargeUnitLoc);
        _hub.Store.WriteInventory(slug, written);

        // El inventario vigente ha cambiado bajo la caché de deriva: una auditada que vuelve a
        // pendiente pierde su ancla, y el panel no puede seguir enseñando la deriva de antes.
        if (themeChanged)
        {
            _drift?.Invalidate();
        }

        string theme = ThemeCatalog.Display(config.Theme);
        _hub.Sync?.CommitAndPush($"config: {slug} ciclo {inv.CycleN} · {theme}");

        string message = themeChanged
            ? reseeded == 0
                ? $"Ciclo {inv.CycleN} configurado como {theme}."
                : $"Ciclo {inv.CycleN} configurado como {theme}: " + Past(reseeded)
            : $"Ciclo {inv.CycleN}: modelo preferido actualizado.";
        return new CycleConfigResult(true, reseeded, message);
    }

    private static string Past(int reseeded) => reseeded == 1
        ? "1 unidad auditada ha pasado a pendiente; los hallazgos existentes no se han tocado."
        : $"{reseeded} unidades auditadas han pasado a pendientes; los hallazgos existentes no se han tocado.";
}

/// <summary>
/// La preferencia de juez de un ciclo, contrastada con el juez con el que se va a lanzar (F17 §5).
/// Es preferencia, no imposición: se avisa en una línea y se deja continuar.
/// </summary>
public static class CyclePreference
{
    /// <summary>
    /// La línea del aviso, o null si no hay nada que avisar: sin preferencia declarada, o con el
    /// mismo proveedor y el mismo modelo. Se compara por identificador y sin distinguir mayúsculas,
    /// que es como los guardan los ajustes.
    /// </summary>
    public static string? Notice(CycleConfig config, string? providerId, string? modelId, string providerName)
    {
        if (!config.HasPreferredModel)
        {
            return null;
        }

        bool sameProvider = string.IsNullOrWhiteSpace(config.PreferredProvider)
            || string.Equals(config.PreferredProvider, providerId, StringComparison.OrdinalIgnoreCase);
        bool sameModel = string.Equals(config.PreferredModel, modelId, StringComparison.OrdinalIgnoreCase);
        if (sameProvider && sameModel)
        {
            return null;
        }

        string preferred = string.IsNullOrWhiteSpace(config.PreferredProvider)
            ? config.PreferredModel!
            : $"{config.PreferredModel} ({ProviderNames.Display(config.PreferredProvider)})";
        string actual = string.IsNullOrWhiteSpace(modelId) ? providerName : $"{modelId} ({providerName})";
        return $"Este ciclo prefiere {preferred}; vas a auditar con {actual}. "
               + "Auditar con otro juez puede producir disputas.";
    }
}
