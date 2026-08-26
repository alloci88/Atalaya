namespace Atalaya.App.ViewModels;

/// <summary>
/// Qué hacer con los hallazgos que YA existen de la regla que se está excluyendo (F5.10).
/// <para>
/// Son tres salidas y no dos porque «cancelar» no es «no silenciarlos»: quien abre la pregunta y
/// descubre que hay 40 hallazgos activos de esa regla puede querer echarse atrás de la exclusión
/// entera, y un diálogo de dos botones le obligaría a excluir para luego des-excluir.
/// </para>
/// </summary>
public enum ExcludeRuleChoice
{
    /// <summary>No se excluye nada: el gesto se abandona entero.</summary>
    Cancel,

    /// <summary>Se excluye la regla; los hallazgos que ya existen siguen activos.</summary>
    ExcludeOnly,

    /// <summary>Se excluye la regla y se silencian los existentes con el mismo motivo.</summary>
    ExcludeAndSilence,
}

/// <summary>
/// Lo que el diálogo le enseña al usuario antes de excluir una regla en toda una aplicación
/// (F5.10). Toda la redacción vive aquí para que se pueda comprobar sin abrir una ventana.
/// </summary>
public sealed class ExcludeRuleConfirmation
{
    public ExcludeRuleConfirmation(string ruleId, string appName, int activeFindings)
    {
        RuleId = ruleId;
        AppName = appName;
        ActiveFindings = activeFindings;
    }

    public string RuleId { get; }

    public string AppName { get; }

    /// <summary>Cuántos hallazgos activos de esta regla hay en la app. La N de la pregunta.</summary>
    public int ActiveFindings { get; }

    public string Question => ActiveFindings == 1
        ? $"Hay 1 hallazgo activo de esta regla en {AppName}. ¿Silenciarlo también?"
        : $"Hay {ActiveFindings} hallazgos activos de esta regla en {AppName}. ¿Silenciarlos también?";

    /// <summary>Lo que la exclusión hace por sí sola, esté marcada o no la casilla.</summary>
    public string Consequence =>
        $"Ninguna auditoría de {AppName} volverá a reportar {RuleId}.";

    /// <summary>Qué pasa si se dice que sí: se cierran, con el mismo motivo y cada uno con su traza.</summary>
    public string YesExplanation => ActiveFindings == 1
        ? "Se silencia con el mismo motivo y notas, y queda registrado en su historial."
        : "Se silencian con el mismo motivo y notas, y cada uno queda registrado en su historial.";

    /// <summary>Y qué pasa si se dice que no: siguen contando como deuda abierta.</summary>
    public string NoExplanation => ActiveFindings == 1
        ? "El hallazgo sigue activo y contando en informes; solo se previenen los futuros."
        : "Los hallazgos siguen activos y contando en informes; solo se previenen los futuros.";
}
