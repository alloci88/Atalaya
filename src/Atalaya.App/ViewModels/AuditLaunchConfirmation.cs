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
    public AuditLaunchConfirmation(string appName, CostEstimate estimate)
    {
        AppName = appName;
        Estimate = estimate;
    }

    public string AppName { get; }

    public CostEstimate Estimate { get; }

    /// <summary>Qué se va a auditar. El número de unidades va primero: es lo que se multiplica.</summary>
    public string Headline => AppName.Length > 0
        ? $"Vas a auditar {Estimate.UnitsLabel} de {AppName}."
        : $"Vas a auditar {Estimate.UnitsLabel}.";

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
    /// diálogo para que nadie lo lea como una medida.
    /// </summary>
    public bool IsWeakEstimate => Estimate.Evidence != CostEvidence.Suficiente;

    /// <summary>La estimación informa, no bloquea: el botón de confirmar nunca se deshabilita.</summary>
    public string Reassurance =>
        "La estimación es informativa: sale del gasto ya medido, no de una tarifa. "
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
