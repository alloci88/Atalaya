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

    /// <summary>
    /// Desde cuándo la está auditando quien la reclamó (F31 §4). <c>null</c> cuando no hay
    /// reclamación viva.
    /// <para>
    /// Va con el nombre y no en un tooltip porque la pregunta que se hace quien mira el inventario
    /// no es «¿está cogida?» sino «¿la cojo yo?», y eso no se contesta sin saber si lleva dos
    /// minutos o dos horas.
    /// </para>
    /// </summary>
    public DateTimeOffset? ClaimedSince { get; init; }

    /// <summary>Reclamada y viva por OTRA persona: ni se selecciona ni se audita.</summary>
    public bool IsClaimedByOther => !string.IsNullOrWhiteSpace(ClaimedBy);

    /// <summary>
    /// «Daniel Rodríguez · auditando desde las 10:42». Es una pastilla de estado DEL SISTEMA, no
    /// del código: distinta de «Pendiente» y de «Auditada», que dicen en qué punto está la unidad,
    /// no quién la tiene ahora mismo.
    /// </summary>
    public string ClaimLabel => ClaimedBy is null
        ? string.Empty
        : ClaimedSince is { } desde
            ? $"{ClaimedBy} · auditando desde las {desde.ToLocalTime():HH:mm}"
            : $"{ClaimedBy} · auditando ahora";

    /// <summary>
    /// Se puede marcar para auditar. Lo que lo apaga es la reclamación viva de otra persona: dejar
    /// marcarla sería dejar que dos máquinas auditen la misma unidad, que es exactamente lo que
    /// las reclamaciones vienen a evitar.
    /// </summary>
    public bool IsSelectable => !IsClaimedByOther;

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
    /// Qué le ha pasado al código desde que se auditó (F9 §3). Es ORTOGONAL a
    /// <see cref="State"/> y por eso viaja aparte: una unidad puede estar «Auditada» Y «Cambiada»
    /// a la vez, y las dos cosas hacen falta para decidir qué mirar. <c>null</c> mientras el
    /// cálculo no ha llegado, o cuando la unidad nunca se auditó — que es cobertura, no deriva.
    /// </summary>
    public UnitDrift? Drift { get; init; }

    public bool HasDrift => Drift is not null && Drift.State != DriftState.SinCambios;

    public string DriftLabel => Drift?.Label ?? string.Empty;

    public string DriftTooltip => Drift?.Tooltip ?? string.Empty;

    /// <summary>
    /// El color del indicador de deriva. Ámbar lo que pide re-auditar, azul lo que pide verificar,
    /// gris lo que no se ha podido saber. Nunca es el único canal: al lado va siempre el texto.
    /// </summary>
    public string DriftInk => Drift?.State switch
    {
        DriftState.Modificada => "#D2B036",
        DriftState.ArregladaPendienteDeVerificar => "#4C8DD8",
        DriftState.Borrada => "#C4564E",
        DriftState.HistorialNoDisponible => "#8A8A8A",
        _ => "#00000000",
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
