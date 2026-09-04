using CommunityToolkit.Mvvm.ComponentModel;

namespace Atalaya.App.ViewModels;

/// <summary>Base for page view-models. Provides a busy flag and an async load hook.</summary>
public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>A short human title shown in the shell header.</summary>
    public virtual string Title => GetType().Name;

    /// <summary>
    /// Qué entrada del raíl se resalta mientras esta página está delante (F26 Parte A, D-954).
    /// <para>
    /// No es lo mismo que la página: la ficha de un hallazgo y el detalle de un arreglo no tienen
    /// entrada propia —serían un raíl de quince cosas— pero SÍ pertenecen a una. Sin esto, entrar
    /// en un hallazgo apagaba el raíl entero y dejaba de haber respuesta a «dónde estoy», que es la
    /// mitad de la queja sobre el menú lateral.
    /// </para>
    /// </summary>
    public virtual string RailKey => string.Empty;

    /// <summary>
    /// Cómo se llama esta página en la miga de pan. Por defecto, su título; las que llevan el
    /// nombre de la aplicación en el título lo acortan, porque la aplicación ya es el eslabón
    /// anterior de la miga y repetirla la haría ilegible.
    /// </summary>
    public virtual string CrumbLabel => Title;

    /// <summary>
    /// True si esta página vive DENTRO de una aplicación. Decide si la miga enseña el eslabón de la
    /// aplicación (Portafolio › XBLAST › Inventario) o se queda en dos (Portafolio › Métricas).
    /// </summary>
    public virtual bool BelongsToApp => false;

    /// <summary>Loads/refreshes the page's data. Called on navigation and after hub changes.</summary>
    public virtual Task LoadAsync() => Task.CompletedTask;
}
