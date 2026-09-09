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

    /// <summary>
    /// TODAS las unidades del proyecto que están a la vista, en plano. Es la lista con la que se
    /// cuenta, se marca y se refleja el tri-estado, y por eso NO se reparte por carpetas: el árbol
    /// de carpetas (F37) es una forma de enseñarlas, no otra manera de tenerlas. Dos listas serían
    /// dos contabilidades, y el incidente del 2026-08-26 ya enseñó lo que cuesta eso.
    /// </summary>
    public ObservableCollection<UnitNode> Units { get; } = new();

    /// <summary>
    /// Lo que se DIBUJA colgando del proyecto (F37 §1): sus carpetas de primer nivel y, detrás,
    /// las unidades que no están en ninguna. Mezcla <see cref="FolderNode"/> y
    /// <see cref="UnitNode"/> porque son las dos cosas que puede haber en un nivel, y cada una
    /// trae su plantilla.
    /// </summary>
    public ObservableCollection<object> Children { get; } = new();

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

/// <summary>
/// <b>Una carpeta del proyecto</b> (F37 §1): la fila que va entre el proyecto y sus unidades.
/// <para>
/// <b>La carpeta agrupa y se marca; no se audita.</b> No tiene acciones propias y nunca llega a
/// una lista de lanzamiento: lo que se audita son las unidades que se marcan a través de ella, con
/// «Auditar selección», que no cambia. Por eso la carpeta no tiene ruta de unidad ni estado — lo
/// único que sabe hacer es contar lo que lleva dentro y reflejar si está marcado.
/// </para>
/// <para>
/// Se pliega con la MISMA lógica que el proyecto (<see cref="GroupCollapse"/>) porque es el mismo
/// gesto: dos implementaciones del plegado divergen a la primera corrección.
/// </para>
/// </summary>
public sealed partial class FolderNode : ObservableObject, ICollapsibleGroup
{
    private bool _suspend;

    /// <summary>
    /// Lo que se lee. Con la cadena plegada lleva varios tramos —«Class/Objects3D»—, porque esa
    /// cadena es UNA fila.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// La carpeta en canónico, relativa a su proyecto. Es la identidad estable de la fila: el
    /// nombre cambia cuando la cadena se pliega o deja de plegarse, la ruta no.
    /// </summary>
    public required string RelativePath { get; init; }

    /// <summary>La app. Entra en la clave de plegado: dos apps repiten nombres de carpeta.</summary>
    public required string Slug { get; init; }

    /// <summary>El proyecto al que cuelga. También entra en la clave: «Forms» se repite en once.</summary>
    public required string Module { get; init; }

    /// <summary>Subcarpetas primero y unidades después, en canónico (§1.8).</summary>
    public ObservableCollection<object> Children { get; } = new();

    /// <summary>
    /// TODAS las unidades de dentro, también las de sus subcarpetas. Es lo que cuenta la cabecera
    /// y lo que marca la casilla: «marcar la carpeta marca todo lo de dentro».
    /// </summary>
    public ObservableCollection<UnitNode> Units { get; } = new();

    public int Total => Units.Count;

    public int Audited => Units.Count(u => u.State == UnitState.Auditada);

    /// <summary>
    /// «Class  (3/12)», el mismo formato que el proyecto. Con un filtro de deriva puesto cuenta
    /// sobre las que pasan el filtro, porque el árbol se construye con las que se enseñan (§1.6).
    /// </summary>
    public string Header => $"{Name}  ({Audited}/{Total})";

    /// <inheritdoc/>
    /// <remarks>
    /// El proyecto usa «inv {slug} {nombre}»; la carpeta añade su ruta detrás de un separador que
    /// no puede aparecer en un nombre de proyecto, así que los dos espacios de claves no se pisan.
    /// </remarks>
    public string Key => $"inv {Slug} {Module} · {RelativePath}";

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandGlyph));

    public string ExpandGlyph => IsExpanded ? "▾" : "▸";

    /// <summary>
    /// El tri-estado de la carpeta: marcada, sin marcar, o indeterminada con selección parcial.
    /// Lo escribe SIEMPRE <see cref="RefreshCheckState"/> a partir de sus unidades — es un reflejo
    /// de la selección, nunca su origen.
    /// </summary>
    [ObservableProperty]
    private bool? _isChecked = false;

    /// <inheritdoc cref="ModuleNode.SelectionRequested"/>
    internal Action<FolderNode, bool>? SelectionRequested { get; set; }

    partial void OnIsCheckedChanged(bool? value)
    {
        if (_suspend)
        {
            return;   // lo puso RefreshCheckState: reflejar la selección no puede cambiarla
        }

        RequestToggle();
    }

    /// <inheritdoc cref="ModuleNode.RequestToggle"/>
    internal void RequestToggle() => SelectionRequested?.Invoke(this, !IsAllSelected);

    /// <summary>
    /// Lo que PINTA la casilla, por la misma razón que en el proyecto (F5.13): la plantilla de
    /// WPF-UI 3.0.5 resuelve el indeterminado y el marcado con el mismo relleno de acento, así que
    /// una casilla «a medias» se ve idéntica a una marcada. El tri-estado sigue estando —es la
    /// semántica correcta y es lo que se interroga—, pero lo que se ve a medias se dice con
    /// palabras (<see cref="SelectionNote"/>), que no se pueden confundir con un relleno.
    /// </summary>
    public bool IsAllSelected => Units.Count > 0 && Units.All(u => u.IsSelected);

    public int SelectedUnits => Units.Count(u => u.IsSelected);

    /// <inheritdoc cref="ModuleNode.SelectionNote"/>
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

    /// <inheritdoc cref="ModuleNode.RefreshCheckState"/>
    internal void RefreshCheckState()
    {
        int selected = Units.Count(u => u.IsSelected);
        bool? state = selected == 0 ? false : selected == Units.Count ? true : null;

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
