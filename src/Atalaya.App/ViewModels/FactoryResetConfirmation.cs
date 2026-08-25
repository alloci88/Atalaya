using Atalaya.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atalaya.App.ViewModels;

/// <summary>
/// La confirmación del reset de fábrica (F5.7 §5). Mismo patrón que el borrado de una aplicación
/// —teclear para abrir la puerta— pero con una diferencia que cambia el texto entero: aquí lo que
/// se destruye <b>no es tuyo</b>. Es de todo el equipo, y los demás no se enteran hasta que su
/// siguiente sincronización lo haga desaparecer de su pantalla.
/// <para>
/// <b>Por qué la palabra RESET y no el nombre de algo.</b> En el borrado de una app se escribe el
/// nombre de la app porque hay que confirmar CUÁL. Aquí no hay cuál: son todas. Lo que hay que
/// confirmar es la naturaleza de la acción, y para eso la palabra tiene que ser la que nadie
/// teclea por inercia.
/// </para>
/// <para>
/// Vive separada del diálogo para que la regla sea comprobable sin abrir una ventana: la puerta es
/// <see cref="CanReset"/>, y la vista solo la enlaza.
/// </para>
/// </summary>
public sealed partial class FactoryResetConfirmation : ObservableObject
{
    /// <summary>Lo que hay que escribir, en mayúsculas y sin margen de interpretación.</summary>
    public const string RequiredWord = "RESET";

    public FactoryResetConfirmation(FactoryResetImpact impact) => Impact = impact;

    public FactoryResetImpact Impact { get; }

    /// <summary>Qué se borra del hub, con los números por delante.</summary>
    public string Warning => Impact.Describe();

    /// <summary>A quién más le pasa. Es la línea que este diálogo existe para decir.</summary>
    public string TeamWarning => Impact.TeamWarning;

    /// <summary>Qué se borra en esta máquina.</summary>
    public string LocalWarning => Impact.LocalWarning;

    /// <summary>Y qué no se pierde: evita las dos lecturas erróneas del pánico.</summary>
    public string Reassurance => Impact.Reassurance;

    public string Prompt => $"Escribe «{RequiredWord}» para confirmar:";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReset))]
    private string _typedWord = string.Empty;

    /// <summary>
    /// La puerta. Comparación EXACTA salvo espacios de sobra: aceptar «reset» en minúsculas
    /// devolvería la confirmación a ser un sí/no con pasos extra.
    /// </summary>
    public bool CanReset => string.Equals(TypedWord?.Trim(), RequiredWord, StringComparison.Ordinal);
}
