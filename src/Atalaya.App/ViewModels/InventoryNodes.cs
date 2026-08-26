using System.Collections.ObjectModel;
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
    /// Un clic del usuario sobre la casilla del módulo.
    /// <para>
    /// <b>Desde indeterminado, un clic LIMPIA.</b> Es la corrección del incidente: la casilla se
    /// declaraba <c>IsThreeState="False"</c> mientras el view-model le empujaba <c>null</c>, y
    /// <c>ToggleButton.OnToggle</c> de WPF, con tres estados desactivados, manda un clic desde
    /// indeterminado directo a <b>marcado</b>. Es decir: marcabas una clase, el módulo se pintaba
    /// como indeterminado —que a ojo se lee «marcado»—, pulsabas para deshacerlo y te llevabas el
    /// módulo ENTERO a la selección. Un gesto de corrección que multiplicaba el gasto.
    /// </para>
    /// <para>
    /// La regla ahora se lee sola: si hay algo marcado en el módulo, el clic lo quita; si no hay
    /// nada, lo marca entero. La dirección segura es la de quitar, y además es la que espera quien
    /// pulsa para deshacer.
    /// </para>
    /// </summary>
    partial void OnIsCheckedChanged(bool? value)
    {
        if (_suspend)
        {
            return;   // lo puso RefreshCheckState: reflejar la selección no puede cambiarla
        }

        SelectionRequested?.Invoke(this, Units.All(u => !u.IsSelected));
    }

    /// <summary>
    /// Recalcula el tri-estado a partir de las unidades. Silencioso: refrescar la casilla no puede
    /// volver a marcar ni desmarcar nada, o el módulo entero se seleccionaría solo.
    /// <para>
    /// Con selección parcial el estado es <c>null</c> — <b>nunca</b> <c>true</c>. Un grupo marcado
    /// significa «todas sus unidades están marcadas» y nada más; que lo pareciera con una sola
    /// hija marcada es lo que hacía creer al usuario que había seleccionado un módulo entero.
    /// </para>
    /// </summary>
    internal void RefreshCheckState()
    {
        int selected = Units.Count(u => u.IsSelected);
        bool? state = selected == 0 ? false : selected == Units.Count ? true : null;
        if (state == IsChecked)
        {
            return;
        }

        _suspend = true;
        IsChecked = state;
        _suspend = false;
    }
}
