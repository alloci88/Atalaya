using CommunityToolkit.Mvvm.ComponentModel;

namespace Atalaya.App.Services;

/// <summary>
/// En qué aplicación estás trabajando (F26 Parte A, D-953).
/// <para>
/// <b>Por qué hace falta.</b> Hasta aquí, «la aplicación» era un dato que cada vista se guardaba
/// para sí: el inventario tenía su <c>Slug</c>, los hallazgos el suyo, y la carcasa no sabía
/// ninguno de los dos. Consecuencia directa, y es la queja del usuario: para salir del inventario y
/// volver había que pasar por Portafolio —dos pasos— porque el raíl no tenía forma de saber a qué
/// inventario llevar. Con esto, el raíl enseña un grupo con el nombre de la aplicación activa y su
/// «Inventario» dentro, y la vuelta es UN clic desde cualquier sitio (principio 6).
/// </para>
/// <para>
/// <b>Qué NO es.</b> No es estado de negocio ni se guarda en ningún sitio: es la memoria de la
/// sesión de ventana, y muere con ella. Lo pone quien navega a algo que pertenece a una aplicación
/// —el inventario, los hallazgos de una app, una sesión, un arreglo—; nadie lo lee para decidir
/// nada que se escriba.
/// </para>
/// </summary>
public sealed partial class ActiveApp : ObservableObject
{
    /// <summary>El slug de la aplicación activa, o vacío si estás en una vista de portafolio.</summary>
    [ObservableProperty]
    private string _slug = string.Empty;

    /// <summary>Su nombre para enseñar. Cuando no se conoce todavía, vale el slug.</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>True cuando hay una aplicación en la que estar.</summary>
    public bool HasApp => Slug.Length > 0;

    partial void OnSlugChanged(string value) => OnPropertyChanged(nameof(HasApp));

    /// <summary>
    /// Apunta la aplicación. El nombre es opcional porque no todas las vistas lo tienen a mano al
    /// navegar: quien lo sepa después lo completa, y mientras tanto se enseña el slug — que es
    /// reconocible— en vez de un hueco.
    /// </summary>
    public void Set(string? slug, string? name = null)
    {
        Slug = slug ?? string.Empty;
        Name = name is { Length: > 0 } ? name : Slug;
    }

    /// <summary>Sales de la aplicación: vuelves a una vista que es de todas.</summary>
    public void Clear()
    {
        Slug = string.Empty;
        Name = string.Empty;
    }
}
