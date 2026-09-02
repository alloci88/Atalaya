using Atalaya.App.Services;

namespace Atalaya.App.ViewModels;

/// <summary>
/// Lo que se pregunta antes de gastar (F5.6 §4): cuántas unidades, con qué tope de pasadas y
/// cuánto se estima que va a costar, con la procedencia del número.
/// <para>
/// <b>Por qué existe separada del diálogo.</b> Igual que <see cref="DeleteAppConfirmation"/>: la
/// regla —qué se enseña y cuándo se avisa de que el dato es flojo— se prueba sin abrir una
/// ventana, y el view-model de V2 no depende de WPF para poder lanzar.
/// </para>
/// </summary>
public sealed class AuditLaunchConfirmation
{
    /// <param name="providerName">
    /// Con QUIÉN se va a auditar (F14). Va en el titular, no en la letra pequeña: desde que hay dos
    /// proveedores, el juez de la sesión es la decisión más consecuente del lanzamiento —cambia el
    /// criterio, la cuota que se gasta y la unidad en la que se mide— y no puede ser una sorpresa
    /// que se descubra leyendo el informe.
    /// </param>
    /// <param name="modelName">
    /// El modelo, cuando ya se sabe. Vacío significa que todavía no se ha resuelto y entonces NO se
    /// nombra: prometer un modelo concreto y usar otro sería peor que no decirlo.
    /// </param>
    public AuditLaunchConfirmation(
        string appName,
        CostEstimate estimate,
        string providerName = "",
        string? modelName = null,
        string? costCaveat = null)
    {
        AppName = appName;
        Estimate = estimate;
        ProviderName = providerName;
        ModelName = modelName;
        CostCaveat = costCaveat;
    }

    public string AppName { get; }

    public CostEstimate Estimate { get; }

    /// <summary>El proveedor que va a juzgar esta sesión.</summary>
    public string ProviderName { get; }

    /// <summary>El modelo, si ya se conoce.</summary>
    public string? ModelName { get; }

    /// <summary>
    /// La advertencia de la unidad de coste, cuando la casa la necesita (F14). Desde F16-RETOQUE §1
    /// ninguna la necesita: la que no facturaba dejó de producir cifra, así que ya no hay ningún
    /// número que se pueda leer como dinero sin serlo. Se mantiene el canal por si mañana lo hay.
    /// </summary>
    public string? CostCaveat { get; }

    /// <summary>
    /// Qué se va a auditar, con quién, y con qué modelo. El número de unidades va primero —es lo
    /// que se multiplica— y el auditor justo detrás.
    /// </summary>
    public string Headline
    {
        get
        {
            string what = AppName.Length > 0
                ? $"Vas a auditar {Estimate.UnitsLabel} de {AppName}"
                : $"Vas a auditar {Estimate.UnitsLabel}";

            if (ProviderName.Length == 0)
            {
                return what + ".";
            }

            return string.IsNullOrWhiteSpace(ModelName)
                ? $"{what} con {ProviderName}."
                : $"{what} con {ProviderName} (modelo {ModelName}).";
        }
    }

    /// <summary>El tope vigente. Es un ajuste de la máquina, así que se recuerda aquí y no se supone.</summary>
    public string PassesLine => Estimate.MaxPasses == 1
        ? "Tope del barrido: 1 pasada por unidad."
        : $"Tope del barrido: {Estimate.MaxPasses} pasadas por unidad.";

    /// <inheritdoc cref="CostEstimate.Breakdown"/>
    public string Breakdown => Estimate.Breakdown;

    /// <inheritdoc cref="CostEstimate.Provenance"/>
    public string Provenance => Estimate.Provenance;

    /// <summary>
    /// El número no se sostiene solo: o hay poco historial, o no hay ninguno. Se marca en el
    /// diálogo para que nadie lo lea como una medida. Con una casa que no factura no hay número
    /// que marcar, así que tampoco hay aviso (F16-RETOQUE §1).
    /// </summary>
    public bool IsWeakEstimate => Estimate.IsWeak;

    /// <summary>La estimación informa, no bloquea: el botón de confirmar nunca se deshabilita.</summary>
    public string Reassurance =>
        "La estimación es informativa: sale del gasto ya medido, no de una tarifa. "
        + (CostCaveat is { Length: > 0 } caveat ? caveat + " " : string.Empty)
        + "Puedes detener la sesión en cualquier momento desde «Sesión en vivo».";
}

/// <summary>
/// Quién pregunta antes de lanzar. Existe para que <see cref="InventoryViewModel"/> no dependa de
/// una ventana: los tests sustituyen esta pieza y ejercitan el flujo entero —incluido «cancelar»—
/// sin interfaz gráfica.
/// </summary>
public interface IAuditLaunchConfirmer
{
    /// <summary>True si el usuario confirmó el gasto.</summary>
    bool Confirm(AuditLaunchConfirmation confirmation);
}
