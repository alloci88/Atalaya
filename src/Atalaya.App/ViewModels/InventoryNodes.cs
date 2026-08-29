using System.Collections.ObjectModel;
using System.Windows.Media;
using Atalaya.App.Services;
using Atalaya.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atalaya.App.ViewModels;

/// <summary>A selectable unit row in the V2 tree.</summary>
public sealed partial class UnitNode : ObservableObject
{
    public required string Path { get; init; }
    public required string Module { get; init; }
    public int Loc { get; init; }
    public UnitState State { get; init; }
    public string? ClaimedBy { get; init; }

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// Quién se entera de que esta casilla cambió. La marca la puede poner el usuario, la cabecera
    /// del módulo o un botón de la barra, y en los tres casos hay que actualizar lo mismo: el
    /// conjunto de seleccionadas del view-model, el contador y el tri-estado del módulo. Un solo
    /// punto de entrada para los tres caminos evita que uno se quede sin actualizar.
    /// </summary>
    internal Action<UnitNode>? SelectionChanged { get; set; }

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this);

    /// <summary>Marca sin avisar a nadie: para reconstruir la vista desde el estado ya conocido.</summary>
    internal void SetSelectedQuietly(bool selected)
    {
        Action<UnitNode>? hook = SelectionChanged;
        SelectionChanged = null;
        IsSelected = selected;
        SelectionChanged = hook;
    }

    public string FileName => Path.Contains('/') ? Path[(Path.LastIndexOf('/') + 1)..] : Path;

    public string StateLabel => State switch
    {
        UnitState.Auditada => "Auditada",
        UnitState.Grande => "Grande",
        _ => "Pendiente",
    };

    /// <summary>
    /// La franja de densidad de deuda, con la MISMA rampa que el mapa de calor (F10.1 §1). El
    /// inventario es donde se decide qué auditar, así que es donde más falta hace saber cuánto
    /// arde ya lo que hay — y traer aquí un color propio habría dado dos escalas para el mismo
    /// dato en dos pantallas que se visitan seguidas.
    /// </summary>
    [ObservableProperty]
    private Brush? _densityBrush;

    /// <summary>Qué dice esa franja, escrito. El color nunca es el único canal.</summary>
    [ObservableProperty]
    private string _densityTooltip = string.Empty;
}

/// <summary>
/// A module group in the V2 tree. Se pliega como los grupos de V3 (F5.6 §1) y lleva la casilla
/// tri-estado que selecciona el módulo entero (F5.6 §3).
/// </summary>
public sealed partial class ModuleNode : ObservableObject, ICollapsibleGroup
{
    private bool _suspend;

    public required string Name { get; init; }

    /// <summary>La app a la que pertenece. Entra en la clave de plegado: los módulos se repiten.</summary>
    public required string Slug { get; init; }

    public ObservableCollection<UnitNode> Units { get; } = new();

    public int Total => Units.Count;

    public int Audited => Units.Count(u => u.State == UnitState.Auditada);

    public string Header => $"{Name}  ({Audited}/{Total})";

    /// <inheritdoc/>
    /// <remarks>El prefijo separa el espacio de claves del de V3, que comparte la misma memoria.</remarks>
    public string Key => $"inv {Slug} {Name}";

    [ObservableProperty]
    private bool _isExpanded = true;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandGlyph));

    public string ExpandGlyph => IsExpanded ? "▾" : "▸";

    /// <summary>
    /// El tri-estado del módulo: marcado, sin marcar, o indeterminado con selección parcial. Lo
    /// escribe SIEMPRE <see cref="RefreshCheckState"/> a partir de las unidades — es un reflejo de
    /// la selección, nunca su origen.
    /// </summary>
    [ObservableProperty]
    private bool? _isChecked = false;

    /// <summary>
    /// Quién ejecuta el gesto de la casilla del módulo. El nodo NO toca sus unidades por su cuenta:
    /// avisa, y el view-model —que es el dueño del conjunto de seleccionadas— aplica el cambio y
    /// vuelve a refrescar el tri-estado. Un solo dueño de la selección es lo que impide que el
    /// árbol y el contador digan cosas distintas.
    /// </summary>
    /// <remarks>
    /// El <c>bool</c> es «marcar todas»; false es «limpiar todas».
    /// </remarks>
    internal Action<ModuleNode, bool>? SelectionRequested { get; set; }

    /// <summary>
    /// Un clic sobre la casilla del módulo, o el mismo gesto ejecutado desde código.
    /// <para>
    /// La regla es la de cualquier casilla de dos estados: si no está todo marcado, marca el módulo
    /// entero; si lo está, lo limpia. Puede leerse directamente del dibujo, que es lo que hace que
    /// no sorprenda — y lo que no ocurría cuando la casilla mentía sobre su estado (ver
    /// <see cref="IsAllSelected"/>).
    /// </para>
    /// </summary>
    partial void OnIsCheckedChanged(bool? value)
    {
        if (_suspend)
        {
            return;   // lo puso RefreshCheckState: reflejar la selección no puede cambiarla
        }

        RequestToggle();
    }

    /// <summary>El gesto, sea cual sea la vía por la que llegue. Una sola regla, un solo sitio.</summary>
    internal void RequestToggle() => SelectionRequested?.Invoke(this, !IsAllSelected);

    /// <summary>
    /// <b>Lo que pinta la casilla del módulo</b>: marcada si y solo si TODAS sus unidades lo están.
    /// <para>
    /// La casilla NO puede recibir el tri-estado. La plantilla de WPF-UI 3.0.5 resuelve
    /// <c>IsChecked = null</c> y <c>IsChecked = true</c> con el <b>mismo</b> fondo
    /// (<c>CheckBoxCheckBackgroundFillChecked</c>, el relleno de acento); lo único que cambia es el
    /// glifo de dentro — un guion (<c>Subtract16</c>) en vez de la marca (<c>Checkmark48</c>)—. En
    /// una lista densa eso es una casilla azul maciza idéntica a una marcada, y por eso marcar UNA
    /// clase parecía marcar el módulo entero. La selección parcial se dice ahora con palabras
    /// (<see cref="SelectionNote"/>), que no se pueden confundir con un relleno.
    /// </para>
    /// </summary>
    public bool IsAllSelected => Units.Count > 0 && Units.All(u => u.IsSelected);

    /// <summary>Cuántas unidades del módulo están marcadas ahora mismo.</summary>
    public int SelectedUnits => Units.Count(u => u.IsSelected);

    /// <summary>
    /// La selección parcial, escrita: «3 de 12 seleccionadas». Vacía cuando no hay nada marcado o
    /// cuando está todo —ahí la casilla ya lo dice sin ambigüedad—. Es la mitad de información que
    /// el guion del tri-estado pretendía dar y nunca daba: cuántas.
    /// </summary>
    public string SelectionNote
    {
        get
        {
            int selected = SelectedUnits;
            return selected == 0 || selected == Units.Count
                ? string.Empty
                : $"{selected} de {Units.Count} seleccionadas";
        }
    }

    public bool HasSelectionNote => SelectionNote.Length > 0;

    /// <summary>
    /// Recalcula el estado del módulo a partir de sus unidades. Silencioso: reflejar la selección
    /// no puede volver a marcar ni desmarcar nada, o el módulo entero se seleccionaría solo.
    /// <para>
    /// <see cref="IsChecked"/> conserva el tri-estado porque es la SEMÁNTICA correcta y es lo que
    /// se puede interrogar; lo que ya no hace es llegar a la casilla, que solo entiende de marcado
    /// y sin marcar.
    /// </para>
    /// </summary>
    internal void RefreshCheckState()
    {
        int selected = Units.Count(u => u.IsSelected);
        bool? state = selected == 0 ? false : selected == Units.Count ? true : null;

        // Lo derivado se anuncia SIEMPRE, cambie o no el tri-estado: pasar de «1 de 12» a «2 de 12»
        // deja IsChecked en null las dos veces, y la nota tiene que moverse igualmente.
        OnPropertyChanged(nameof(IsAllSelected));
        OnPropertyChanged(nameof(SelectedUnits));
        OnPropertyChanged(nameof(SelectionNote));
        OnPropertyChanged(nameof(HasSelectionNote));

        if (state == IsChecked)
        {
            return;
        }

        _suspend = true;
        IsChecked = state;
        _suspend = false;
    }
}
