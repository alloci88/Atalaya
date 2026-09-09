using CommunityToolkit.Mvvm.ComponentModel;

namespace Atalaya.App.Services;

/// <summary>
/// Lo que la lógica compartida de plegado necesita saber de un grupo: cómo se llama —de forma
/// estable entre recargas— y si está abierto. Lo implementan la cabecera de unidad de V3
/// (<c>FindingGroupHeader</c>) y el módulo de V2 (<c>ModuleNode</c>).
/// </summary>
public interface ICollapsibleGroup
{
    /// <summary>Identidad estable del grupo, la clave con la que se recuerda su estado.</summary>
    string Key { get; }

    bool IsExpanded { get; set; }
}

/// <summary>
/// Plegar y desplegar una lista agrupada, con memoria de sesión (F5.4 §5, extraído en F5.6 §1).
/// <para>
/// <b>Por qué existe.</b> La regla nació dentro de <c>FindingsViewModel</c>: la lista corta abre,
/// la larga pliega, lo que el usuario decide manda, y un solo botón alterna entre «Colapsar todo»
/// y «Expandir todo». V2 necesita exactamente eso mismo sobre sus módulos. Duplicarlo habría
/// dejado dos reglas que se separan a la primera corrección —el patrón de D-239—, así que la
/// lógica vive aquí y las dos vistas la consultan.
/// </para>
/// <para>
/// El estado de expansión recordado sigue viviendo en <see cref="GroupExpansionMemory"/>, que es
/// un singleton de la sesión de la aplicación: las dos vistas son transitorias en el contenedor.
/// </para>
/// </summary>
public sealed partial class GroupCollapse : ObservableObject
{
    private readonly GroupExpansionMemory _memory;
    private IReadOnlyList<ICollapsibleGroup> _groups = Array.Empty<ICollapsibleGroup>();

    public GroupCollapse(GroupExpansionMemory memory) => _memory = memory;

    /// <summary>No hay ningún grupo desplegado: el botón de la cabecera ofrece desplegarlos.</summary>
    [ObservableProperty] private bool _allCollapsed;

    /// <summary>Un solo botón que alterna. Su texto ES su estado, así que no hace falta explicarlo.</summary>
    [ObservableProperty] private string _toggleAllLabel = "Colapsar todo";

    /// <summary>Sin grupos no hay nada que plegar: el control se retira.</summary>
    [ObservableProperty] private bool _hasGroups;

    /// <summary>
    /// Adopta los grupos recién construidos y les fija su estado inicial: lo que el usuario decidió
    /// a mano si lo decidió, y si no la regla de la lista corta. La regla mira el total de grupos
    /// DE ESTE filtro: al filtrar, lo que era ilegible pasa a ser legible.
    /// </summary>
    public void Adopt(IReadOnlyList<ICollapsibleGroup> groups)
    {
        bool openByDefault = groups.Count <= GroupExpansionMemory.SmallListGroups;
        Adopt(groups, _ => null, _ => openByDefault);
    }

    /// <summary>
    /// La misma adopción, con las dos reglas que el inventario necesita distintas (F37 §1.4-§1.5)
    /// y que en una lista de un solo tipo de grupo no hacen falta:
    /// <list type="bullet">
    /// <item><paramref name="forced"/> — un estado que se impone POR ENCIMA de lo que el usuario
    /// decidió, sin borrar su decisión. Es lo que hace que buscar abra las carpetas con
    /// coincidencias aunque estuvieran cerradas a mano, y que al vaciar la búsqueda vuelvan a
    /// estar como estaban: lo forzado no se recuerda, así que al dejar de forzarse no queda
    /// rastro. <c>null</c> para un grupo es «aquí no me meto».</item>
    /// <item><paramref name="byDefault"/> — qué se abre cuando el usuario no ha dicho nada, POR
    /// GRUPO. La regla de la lista corta vale cuando todos los grupos son iguales; con proyectos y
    /// carpetas mezclados no, porque el estado por defecto de cada uno es distinto.</item>
    /// </list>
    /// </summary>
    public void Adopt(
        IReadOnlyList<ICollapsibleGroup> groups,
        Func<ICollapsibleGroup, bool?> forced,
        Func<ICollapsibleGroup, bool> byDefault)
    {
        _groups = groups;
        foreach (ICollapsibleGroup group in groups)
        {
            group.IsExpanded = forced(group) ?? _memory.Remembered(group.Key) ?? byDefault(group);
        }

        Refresh();
    }

    /// <summary>Alterna UN grupo. Es una decisión explícita, así que se recuerda.</summary>
    public void Toggle(ICollapsibleGroup? group)
    {
        if (group is null)
        {
            return;
        }

        group.IsExpanded = !group.IsExpanded;
        _memory.Remember(group.Key, group.IsExpanded);
        Refresh();
    }

    /// <summary>
    /// Pliega todo, o lo despliega si ya estaba todo plegado. Un botón, no dos: con la mitad de los
    /// grupos abiertos, «colapsar todo» es la única acción que cambia algo para todos.
    /// </summary>
    public void ToggleAll()
    {
        bool expand = AllCollapsed;
        foreach (ICollapsibleGroup group in _groups)
        {
            group.IsExpanded = expand;
            _memory.Remember(group.Key, expand);
        }

        Refresh();
    }

    /// <summary>Recalcula lo que lee la cabecera. Público porque el estado puede cambiar fuera.</summary>
    public void Refresh()
    {
        HasGroups = _groups.Count > 0;
        AllCollapsed = _groups.Count > 0 && _groups.All(g => !g.IsExpanded);
        ToggleAllLabel = AllCollapsed ? "Expandir todo" : "Colapsar todo";
    }
}
