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

    /// <summary>
    /// El eslabón que cuelga de esta página cuando la página tiene DOS niveles (UI-0044, UI-0058):
    /// el hallazgo que estás leyendo dentro de Hallazgos, el informe dentro de Informes, la
    /// sección dentro de Ajustes. Vacío cuando la página es una sola cosa.
    /// <para>
    /// <b>Por qué existe.</b> La miga tenía tres granos distintos: el Inventario acababa en el
    /// nombre de la aplicación y no decía «Inventario»; las cinco secciones de Ajustes tenían la
    /// MISMA miga, aunque D-985 hizo de la sección un destino al que se aterriza desde Métricas; y
    /// el informe abierto llevaba la miga de la lista, así que leer un informe y mirar la lista se
    /// escribían igual. Con esto la miga acaba siempre en la página que estás mirando.
    /// </para>
    /// </summary>
    public virtual string SubCrumbLabel => string.Empty;

    /// <summary>
    /// Qué hace el eslabón de la PÁGINA cuando hay un <see cref="SubCrumbLabel"/> debajo: cerrar el
    /// informe abierto, volver a la lista de hallazgos. Nulo cuando no lleva a ningún sitio.
    /// </summary>
    public virtual System.Windows.Input.ICommand? SubCrumbParentCommand => null;

    /// <summary>
    /// La página ha cambiado de ÁMBITO sin que nadie haya navegado: otro filtro de aplicación,
    /// otra sección, otro informe abierto.
    /// <para>
    /// <b>Lo que arregla</b> (UI-0004): la miga se construía solo al navegar, así que entrar en
    /// Hallazgos desde el inventario de XBLAST y luego poner «Aplicación: Todas» dejaba la miga en
    /// «Portafolio › XBLAST › Hallazgos» con la lista enseñando el portafolio entero. La miga
    /// miente sobre dónde estás, que es lo único que la miga hace.
    /// </para>
    /// </summary>
    public event EventHandler? ScopeChanged;

    /// <summary>Avisa a la carcasa de que hay que rehacer la miga y el raíl.</summary>
    protected void RaiseScopeChanged() => ScopeChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Loads/refreshes the page's data. Called on navigation and after hub changes.</summary>
    public virtual Task LoadAsync() => Task.CompletedTask;
}
