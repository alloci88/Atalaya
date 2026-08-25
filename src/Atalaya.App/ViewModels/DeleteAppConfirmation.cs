using Atalaya.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atalaya.App.ViewModels;

/// <summary>
/// La confirmación fuerte del borrado de una aplicación (F5.3 §4), al estilo de GitHub: el botón
/// rojo no se habilita hasta que se escribe el nombre de la app.
/// <para>
/// <b>Por qué escribir el nombre y no un «¿Seguro?».</b> Un sí/no se pulsa por inercia; teclear el
/// nombre obliga a leer CUÁL se está borrando. Es la diferencia entre confirmar una acción y
/// confirmar el objeto de la acción, y aquí el objeto es toda la traza de una app en el hub.
/// </para>
/// <para>
/// Vive separada del diálogo para que la regla sea comprobable sin abrir una ventana: la puerta
/// es <see cref="CanDelete"/>, y la vista solo la enlaza.
/// </para>
/// </summary>
public sealed partial class DeleteAppConfirmation : ObservableObject
{
    public DeleteAppConfirmation(AppDeletionImpact impact)
    {
        Impact = impact;
    }

    public AppDeletionImpact Impact { get; }

    /// <summary>Lo que hay que escribir, tal cual: el nombre de la aplicación.</summary>
    public string RequiredName => Impact.Name;

    /// <summary>Qué se va a borrar, con los números por delante.</summary>
    public string Warning => Impact.Describe();

    /// <summary>
    /// Y qué NO se pierde. Decirlo aquí evita las dos lecturas erróneas: que se borra el código
    /// auditado, y que no queda copia de nada.
    /// </summary>
    public string Reassurance =>
        "El historial git del hub conserva una copia recuperable por un administrador. "
        + "El repositorio auditado y tu clon local no se tocan.";

    public string Prompt => $"Escribe «{RequiredName}» para confirmar:";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private string _typedName = string.Empty;

    /// <summary>
    /// La puerta. Comparación EXACTA salvo espacios de sobra: aceptar mayúsculas distintas o un
    /// nombre parecido devolvería la confirmación a ser un sí/no con pasos extra.
    /// </summary>
    public bool CanDelete => string.Equals(TypedName?.Trim(), RequiredName, StringComparison.Ordinal);
}
