using System.Collections.ObjectModel;
using Atalaya.App.Controls;
using Atalaya.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>
/// Una página que pertenece a UNA aplicación y sabe cuál (F26 Parte A, D-953).
/// <para>
/// Es el mínimo que la carcasa necesita para poner el nombre en la miga de pan y encender el grupo
/// del raíl. Se declara como interfaz y no como propiedad en <c>ViewModelBase</c> porque la mitad
/// de las páginas —Métricas, Informes, Ajustes, Cuenta— no son de ninguna aplicación, y una
/// propiedad que la mitad de las clases deja vacía no es un contrato, es un hueco.
/// </para>
/// </summary>
public interface IAppScoped
{
    /// <summary>El slug de la aplicación de esta página, o vacío mientras no se sepa.</summary>
    string AppSlug { get; }

    /// <summary>Su nombre para enseñar.</summary>
    string AppLabel { get; }
}

/// <summary>
/// LA CARCASA: raíl, miga de pan y vuelta atrás (F26 Parte A, D-952/D-954/D-955).
/// <para>
/// Está aparte del resto de <c>MainViewModel</c> —que es la sesión, el arreglo, la cuenta y los
/// avisos— porque es otra cosa: aquí no se decide nada del trabajo, solo dónde estás y cómo se
/// llega a los sitios. Mezclarlo con el estado de la auditoría haría el fichero de mil líneas que
/// nadie vuelve a abrir.
/// </para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Los grupos del raíl, tal cual se pintan. Se reconstruyen cuando cambia lo que hay.</summary>
    public ObservableCollection<NavGroup> NavGroups { get; } = new();

    /// <summary>Portafolio › XBLAST › Inventario. El último eslabón no es enlace.</summary>
    public ObservableCollection<Crumb> Crumbs { get; } = new();

    /// <summary>
    /// El raíl plegado a solo iconos. Lo decide el ANCHO de la ventana, no el usuario: por debajo
    /// de <c>Rail.CollapseBelow</c> los 232 px del raíl se los está quitando al contenido, que es
    /// donde está el trabajo. Se puede forzar a mano con el botón, y entonces manda lo que diga el
    /// usuario hasta que vuelva a tocarlo.
    /// </summary>
    [ObservableProperty]
    private bool _railCollapsed;

    private bool _railPinnedByUser;

    /// <summary>
    /// Lee del ajuste cómo estaba el raíl la última vez (D-963). Lo llama el arranque, después de
    /// construir la carcasa: si se leyera en el constructor, el primer `SizeChanged` de la ventana
    /// —que llega antes de que nadie haya podido tocar nada— lo pisaría.
    /// </summary>
    public void RestoreRail()
    {
        WindowPlacement saved = _settings.Current.Window;
        _railPinnedByUser = saved.RailPinned;
        RailCollapsed = saved.RailCollapsed;
    }

    /// <summary>
    /// Pliega y despliega. Los dos sentidos: el botón es un interruptor, no un «plegar» — un raíl
    /// que se pliega y no se puede volver a abrir sin redimensionar la ventana no es plegable, es
    /// un raíl roto.
    /// </summary>
    [RelayCommand]
    private void ToggleRail()
    {
        RailCollapsed = !RailCollapsed;
        _railPinnedByUser = true;
        SaveRail();
    }

    private void SaveRail()
    {
        AppSettings settings = _settings.Current;
        settings.Window.RailCollapsed = RailCollapsed;
        settings.Window.RailPinned = _railPinnedByUser;
        _settings.Save(settings);
    }

    /// <summary>
    /// La ventana ha cambiado de ancho. Mientras el usuario no toque el botón, el raíl se pliega y
    /// se despliega solo: es «reorganizar en vez de encoger» (principio 1) aplicado al menú.
    /// </summary>
    public void OnShellWidthChanged(double width, double collapseBelow)
    {
        if (_railPinnedByUser)
        {
            return;
        }

        RailCollapsed = width > 0 && width < collapseBelow;
    }

    /// <summary>
    /// ¿Hay algo corriendo que enseñar en el pie? Si no lo hay, el pie NO SE PINTA: una barra vacía
    /// de 33 px en el borde inferior de todas las pantallas es espacio que se cobra sin dar nada, y
    /// justo de eso iba la queja (principio 2).
    /// </summary>
    public bool HasFooter => SessionProgress.Length > 0 || FixProgress.Length > 0;

    /// <summary>
    /// EL PILOTO, DICHO CON PALABRAS (D-964). Antes la barra superior ponía «Green» al lado del
    /// punto — el nombre interno del estado, en inglés, en una aplicación en español y sin decir
    /// de qué. Un piloto no necesita etiqueta: necesita <b>significar algo cuando se pregunta por
    /// él</b>. El punto se queda (es lo que se ve de reojo) y la frase entera vive en su tooltip,
    /// que es donde se va a buscar cuando el color deje de ser verde.
    /// </summary>
    public string SyncTooltip => SyncPendingSuffix + SyncHealthTooltip;

    /// <summary>
    /// «2 commits pendientes de publicar · » delante de todo lo demás (F31 §2). Va PRIMERO porque
    /// es lo único del piloto que dice que hay trabajo tuyo que el equipo todavía no ve, y eso
    /// pesa más que el estado de la conexión que lo causó.
    /// </summary>
    private string SyncPendingSuffix
        => _hub.PendingLabel is { Length: > 0 } pending ? pending + " · " : string.Empty;

    private string SyncHealthTooltip => SyncHealth switch
    {
        Atalaya.Storage.Sync.SyncHealth.Green =>
            "Conectado a GitHub y al hub" + SyncStampSuffix,
        Atalaya.Storage.Sync.SyncHealth.Amber =>
            "Sin conexión con el hub, o con cambios tuyos sin publicar. Puedes seguir trabajando: "
            + "lo que escribas se publica en cuanto vuelva la conexión." + SyncStampSuffix,
        _ =>
            "La última sincronización con el hub falló"
            + (_hub.LastSyncError is { Length: > 0 } e ? $": {e}" : ".")
            + " Lo que escribas se queda en esta máquina hasta que se arregle." + SyncStampSuffix,
    };

    /// <summary>
    /// El tooltip de la cuenta. Lleva el nombre SIEMPRE, también con el raíl desplegado: ahí el
    /// nombre se ve, pero puede venir recortado si es largo, y el tooltip es donde se lee entero.
    /// Con el raíl plegado es lo único que hay.
    /// </summary>
    public string AccountTooltip => $"{AccountLabel} · abrir Cuenta";

    /// <summary>« · sincronizado 12:41», o nada si todavía no ha sincronizado nunca.</summary>
    private string SyncStampSuffix
        => _hub.LastSync is { } t ? $" · sincronizado {t:HH:mm}" : string.Empty;

    /// <summary>Deshace un paso. La página vuelve como la dejaste, no reconstruida (D-952).</summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task GoBack() => Navigation.GoBackAsync();

    public bool CanGoBack => Navigation.CanGoBack;

    /// <summary>
    /// El inventario de la aplicación activa, EN UN PASO desde donde estés (principio 6). Antes
    /// esto eran dos —Portafolio y luego la tarjeta—, que es la queja literal del usuario.
    /// </summary>
    [RelayCommand]
    private Task ShowInventory()
        => ActiveApplication.HasApp
            ? Navigation.NavigateOrResumeAsync<InventoryViewModel>(vm => vm.SetApp(ActiveApplication.Slug))
            : Navigation.NavigateToAsync<PortfolioViewModel>();

    /// <summary>
    /// Rehace el raíl y la miga. Lo llama la navegación y cada vez que aparece o desaparece algo
    /// que el raíl enseña —una sesión, un arreglo, un cambio de aplicación—.
    /// </summary>
    public void RefreshShell()
    {
        OnPropertyChanged(nameof(HasFooter));
        WatchScope(Navigation.Current);
        SyncActiveApp();
        BuildRail();
        BuildCrumbs();
        OnPropertyChanged(nameof(CanGoBack));
        GoBackCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// La aplicación activa sale de la PÁGINA que está delante, no de quien navegó. Así da igual
    /// por dónde hayas llegado —desde el portafolio, desde una métrica, desde un informe—: si estás
    /// mirando algo de XBLAST, el raíl dice XBLAST.
    /// <para>
    /// Una página que no es de ninguna aplicación NO borra la activa. Mirar las métricas de todas
    /// las aplicaciones no es «salir» de la tuya, y si lo borrara, el camino de vuelta al
    /// inventario se perdería justo cuando se necesita.
    /// </para>
    /// <para>
    /// <b>Y el portafolio TAMPOCO la borra</b> (UI-0017). Lo hacía a propósito —«el portafolio es
    /// literalmente el sitio donde eliges otra»— y el efecto medido era el contrario del que se
    /// buscaba: el bloque de la aplicación solo se pinta si hay una, así que en cuanto se pasaba
    /// por Portafolio el raíl de Métricas, Cuenta, Ajustes, Acerca de y Nueva aplicación se
    /// quedaba sin «Inventario» y volver costaba <b>dos pasos</b> — incumpliendo D-944.6 por su
    /// enunciado exacto: «el inventario es alcanzable en un paso desde cualquier sitio». Mirar el
    /// portafolio no es elegir otra aplicación; elegir otra es entrar en ella, y eso ya la cambia.
    /// La activa se conserva hasta que el usuario elija otra o borre la que había
    /// (<see cref="ForgetActiveApp"/>).
    /// </para>
    /// </summary>
    /// <summary>La página cuyo ámbito estamos escuchando. Solo una: la que está delante.</summary>
    private ViewModelBase? _watched;

    /// <summary>
    /// Escucha los cambios de ÁMBITO de la página que está delante (UI-0004).
    /// <para>
    /// La miga se rehacía solo al navegar, así que cambiar el filtro de aplicación de Hallazgos la
    /// dejaba diciendo «› XBLAST ›» con la lista enseñando el portafolio entero. Se engancha una
    /// sola página —y se suelta la anterior—: un manejador que se acumula por navegación es una
    /// fuga con un raíl repintado N veces dentro.
    /// </para>
    /// </summary>
    private void WatchScope(ViewModelBase? page)
    {
        if (ReferenceEquals(_watched, page))
        {
            return;
        }

        if (_watched is not null)
        {
            _watched.ScopeChanged -= OnScopeChanged;
        }

        _watched = page;

        if (_watched is not null)
        {
            _watched.ScopeChanged += OnScopeChanged;
        }
    }

    private void OnScopeChanged(object? sender, EventArgs e)
    {
        // Solo la página que está delante: una que quedó en el historial y se recarga sola no
        // tiene por qué mover la miga de la que se está mirando.
        if (ReferenceEquals(sender, Navigation.Current))
        {
            SyncActiveApp();
            BuildRail();
            BuildCrumbs();
        }
    }

    private void SyncActiveApp()
    {
        if (Navigation.Current is IAppScoped scoped && scoped.AppSlug.Length > 0)
        {
            ActiveApplication.Set(scoped.AppSlug, scoped.AppLabel);
        }
    }

    /// <summary>
    /// La aplicacion activa se OLVIDA cuando deja de existir, y solo entonces. La llama el
    /// borrado de una aplicacion.
    /// </summary>
    public void ForgetActiveApp(string slug)
    {
        if (string.Equals(ActiveApplication.Slug, slug, StringComparison.OrdinalIgnoreCase))
        {
            ActiveApplication.Clear();
            RefreshShell();
        }
    }

    private void BuildRail()
    {
        string active = Navigation.Current?.RailKey ?? string.Empty;

        var work = new List<NavItem>
        {
            new("portfolio", "Portafolio", Icons.Portfolio, ShowPortfolioCommand, WorkGroup),
            new("findings", "Hallazgos", Icons.Findings, ShowFindingsCommand, WorkGroup),
            new("reports", "Informes", Icons.Reports, ShowReportsCommand, WorkGroup),
            new("metrics", "Métricas", Icons.Metrics, ShowMetricsCommand, WorkGroup),
        };

        // El grupo de la aplicación activa. Solo existe cuando hay una: un rótulo vacío con un
        // «Inventario» que no sabe de qué sería peor que no tener grupo.
        var app = new List<NavItem>();
        if (ActiveApplication.HasApp)
        {
            app.Add(new NavItem("inventory", "Inventario", Icons.Inventory, ShowInventoryCommand, ActiveApplication.Name));
        }

        // Sesión y arreglo siguen siendo condicionales y siguen latiendo mientras corren (F5.2,
        // F6.9). Lo que cambia es dónde: ya no cuelgan del grupo de trabajo —no son un LUGAR al que
        // ir a diario— sino de la aplicación sobre la que están corriendo.
        if (HasSession)
        {
            app.Add(new NavItem("session", SessionNavLabel, Icons.Session, ShowSessionCommand, ActiveApplication.Name)
            {
                Pulsing = IsSessionRunning,
            });
        }

        if (HasFix)
        {
            app.Add(new NavItem("fix", FixNavLabel, Icons.Fix, ShowFixCommand, ActiveApplication.Name)
            {
                Pulsing = IsFixRunning,
            });
        }

        // «Acerca de» es una PÁGINA desde F26 §C, y va en Sistema debajo de Ajustes: no se edita
        // nada ahí dentro, así que no era un ajuste — estaba escondido al fondo de la única
        // sección donde nadie iba a buscarlo.
        var system = new List<NavItem>
        {
            new("account", "Cuenta", Icons.Account, ShowAccountCommand, SystemGroup),
            new("settings", "Ajustes", Icons.Settings, ShowSettingsCommand, SystemGroup),
            new("about", "Acerca de", Icons.Info, ShowAboutCommand, SystemGroup),
        };

        foreach (var item in work.Concat(app).Concat(system))
        {
            item.IsActive = item.Key == active;
        }

        NavGroups.Clear();
        NavGroups.Add(new NavGroup(WorkGroup, work) { HasSeparator = false });
        if (app.Count > 0)
        {
            // El nombre del grupo es el de la APLICACIÓN. Ya no se pinta —desde la tercera
            // revisión de §C los bloques se separan con una raya y sin texto—, pero sigue siendo
            // el nombre de automatización del bloque. Si la sesión o el arreglo están vivos sin
            // aplicación activa —puede pasar al arrancar con una sesión a medias—, se nombra
            // genérico en vez de quedarse en blanco.
            NavGroups.Add(new NavGroup(ActiveApplication.HasApp ? ActiveApplication.Name : "En curso", app));
        }

        // EL BLOQUE DE SISTEMA VA AQUÍ, CON LOS DEMÁS (F27, cierre). F27 lo sacó a una colección
        // propia para anclarlo al pie del raíl (UI-0043); el usuario lo vio en el dist y mandó
        // volver a la Parte C, con todas las entradas seguidas. N-6.
        NavGroups.Add(new NavGroup(SystemGroup, system));
    }

    private const string WorkGroup = "Trabajo";
    private const string SystemGroup = "Sistema";

    /// <summary>
    /// La miga: siempre empieza en Portafolio —es la raíz de todo lo que se audita—, mete la
    /// aplicación cuando la página es suya, y acaba en la página, sin enlace.
    /// </summary>
    private void BuildCrumbs()
    {
        Crumbs.Clear();

        var current = Navigation.Current;
        if (current is null)
        {
            return;
        }

        if (current is PortfolioViewModel)
        {
            Crumbs.Add(new Crumb("Portafolio") { IsLast = true });
            return;
        }

        Crumbs.Add(new Crumb("Portafolio", ShowPortfolioCommand));

        // EL ESLABÓN DE LA APLICACIÓN SALE CUANDO LA PÁGINA ESTÁ ENSEÑANDO ESA APLICACIÓN, no
        // cuando la ventana la recuerda (UI-0004). `BelongsToApp` es de la página y sabe la
        // diferencia —Hallazgos con «Aplicación: Todas» dice que no—, y desde UI-0004 la miga se
        // rehace también cuando la página cambia de filtro, que es lo que faltaba: se construía
        // solo al navegar, así que poner «Todas» dejaba el «› XBLAST ›» de antes.
        bool inApp = current.BelongsToApp && ActiveApplication.HasApp;
        if (inApp)
        {
            // Lleva a SU inventario, que es la portada de una aplicación en Atalaya. Cuando ya
            // estás en el inventario no es enlace: sería un enlace a donde estás.
            Crumbs.Add(current is InventoryViewModel
                ? new Crumb(ActiveApplication.Name)
                : new Crumb(ActiveApplication.Name, ShowInventoryCommand));
        }

        // LA MIGA ACABA SIEMPRE EN LA PÁGINA (UI-0044). El inventario se saltaba su último
        // eslabón y acababa en el nombre de la aplicación, así que era la única vista de las
        // dieciocho que no decía en qué página estabas.
        string sub = current.SubCrumbLabel;
        Crumbs.Add(sub.Length > 0
            ? new Crumb(current.CrumbLabel, current.SubCrumbParentCommand)
            : new Crumb(current.CrumbLabel));

        // Y CUANDO LA PÁGINA TIENE DOS NIVELES, el segundo también (UI-0044, UI-0058): el
        // hallazgo dentro de Hallazgos, el informe dentro de Informes, la sección dentro de
        // Ajustes. Es lo que hace que la ficha ofrezca la vuelta a su lista sin un segundo botón
        // de «volver» dentro del contenido.
        if (sub.Length > 0)
        {
            Crumbs.Add(new Crumb(sub));
        }

        // Y EL ÚLTIMO ES EL ÚLTIMO, lo diga su comando o no: es lo que decide quién lleva
        // separador detrás y quién se pinta como la página.
        Crumbs[^1].IsLast = true;
    }
}
