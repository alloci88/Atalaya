using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atalaya.App.ViewModels;

/// <summary>
/// Una entrada del raíl (F26 Parte A, D-954).
/// <para>
/// <b>Por qué son datos y no ocho botones escritos a mano en el XAML.</b> Porque el raíl cambia:
/// «Sesión en vivo» y «Arreglo asistido» aparecen y desaparecen, el grupo de la aplicación activa
/// lleva su nombre, y algunas entradas traen un contador. Con ocho botones sueltos, cada una de
/// esas reglas se escribe en el XAML y ninguna se puede probar; como lista, el view-model dice qué
/// hay y la carcasa solo lo pinta.
/// </para>
/// </summary>
public sealed partial class NavItem : ObservableObject
{
    public NavItem(string key, string label, Geometry icon, ICommand command, string group)
    {
        Key = key;
        Label = label;
        Icon = icon;
        Command = command;
        Group = group;
    }

    /// <summary>La clave con la que una página dice que ESTA es su entrada (<c>ViewModelBase.RailKey</c>).</summary>
    public string Key { get; }

    public string Label { get; }

    public Geometry Icon { get; }

    public ICommand Command { get; }

    /// <summary>«Trabajo», el nombre de la aplicación activa, o «Sistema».</summary>
    public string Group { get; }

    /// <summary>Un contador a la derecha (hallazgos abiertos, unidades auditadas). Vacío = no hay.</summary>
    [ObservableProperty]
    private string _badge = string.Empty;

    /// <summary>Un punto de color a la izquierda del texto, para lo que está VIVO (sesión, arreglo).</summary>
    [ObservableProperty]
    private bool _pulsing;

    /// <summary>Resaltada: es donde estás.</summary>
    [ObservableProperty]
    private bool _isActive;

    public bool HasBadge => Badge.Length > 0;

    partial void OnBadgeChanged(string value) => OnPropertyChanged(nameof(HasBadge));
}

/// <summary>Un grupo del raíl, con su rótulo y sus entradas.</summary>
public sealed partial class NavGroup : ObservableObject
{
    public NavGroup(string title, IEnumerable<NavItem> items)
    {
        Title = title;
        Items = items.ToList();
    }

    /// <summary>
    /// El rótulo del grupo. En el grupo de la aplicación activa es el NOMBRE de la aplicación, que
    /// es la forma más corta de decir «esto de aquí es de XBLAST» sin repetirlo en cada entrada.
    /// </summary>
    public string Title { get; }

    public IReadOnlyList<NavItem> Items { get; }

    /// <summary>
    /// Si el bloque va precedido de una raya. Lo lleva todo el mundo menos el primero: una línea
    /// antes de la primera entrada no separa nada, solo cuelga del botón de plegar.
    /// </summary>
    public bool HasSeparator { get; init; } = true;
}

/// <summary>Un eslabón de la miga de pan. El último no lleva comando: es donde estás.</summary>
public sealed class Crumb
{
    public Crumb(string label, ICommand? command = null)
    {
        Label = label;
        Command = command;
    }

    public string Label { get; }

    public ICommand? Command { get; }

    public bool IsLink => Command is not null;

    public bool IsCurrent => Command is null;
}
