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
    /// El tri-estado del módulo: marcado, sin marcar, o indeterminado con selección parcial.
    /// <para>
    /// La casilla se declara con <c>IsThreeState="False"</c> a propósito: así el clic solo alterna
    /// entre marcar y desmarcar —el gesto que se espera—, mientras que un valor <c>null</c> puesto
    /// desde aquí se sigue PINTANDO como indeterminado. Con <c>IsThreeState="True"</c> el usuario
    /// tendría que pasar por el estado intermedio en cada vuelta, que no significa nada cuando lo
    /// pulsa una persona.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool? _isChecked = false;

    partial void OnIsCheckedChanged(bool? value)
    {
        if (_suspend || value is null)
        {
            return;
        }

        foreach (UnitNode unit in Units)
        {
            unit.IsSelected = value.Value;
        }
    }

    /// <summary>
    /// Recalcula el tri-estado a partir de las unidades. Silencioso: refrescar la casilla no puede
    /// volver a marcar ni desmarcar nada, o el módulo entero se seleccionaría solo.
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
