using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Documents;
using Atalaya.App.Services;
using Atalaya.Domain.Ids;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>Una opción del combo de tipo. «Todos» vale null y es el arranque (patrón de V3).</summary>
public sealed record ReportKindOption(ReportKind? Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Una opción del combo de usuario. «Todos» vale null y es el arranque.</summary>
public sealed record AuthorOption(string? Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Un preset del filtro de fechas. <see cref="Days"/> null es «todo»; el rango a mano lo activa
/// <see cref="ReportsViewModel.UseCustomRange"/> y no vive aquí.
/// </summary>
public sealed record DateRangeOption(int? Days, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Una fila de la lista de informes, ya escrita para la vista.</summary>
public sealed class ReportRow
{
    public required ReportEntry Entry { get; init; }

    public string Slug => Entry.Slug;

    public string AppName => Entry.AppName;

    public string KindLabel => ReportKinds.Display(Entry.Kind);

    /// <summary>«21 ago 2026 · 10:32». Local, que es la hora en la que trabaja quien mira.</summary>
    public string When => Entry.When == DateTimeOffset.MinValue
        ? ReportsViewModel.Unknown
        : Entry.When.ToLocalTime().ToString("d MMM yyyy · HH:mm", CultureInfo.CurrentCulture);

    /// <summary>De dónde sale esa fecha, cuando no es la de una sesión (N-2).</summary>
    public string WhenTooltip => Entry.DateSource switch
    {
        ReportDateSource.Session => "Fecha de la sesión que lo generó.",
        ReportDateSource.Header => "Fecha declarada en la cabecera del informe: no tiene sesión asociada.",
        ReportDateSource.Ulid => "Fecha deducida del identificador del informe: no tiene sesión asociada.",
        _ => "Fecha del fichero en este clon: el informe no declara la suya.",
    };

    public string By => Entry.By ?? ReportsViewModel.Unknown;

    public string Mode => Entry.ModeLabel ?? ReportsViewModel.Unknown;

    /// <summary>«12 unidades», o «—» si el informe no tiene sesión que lo declare.</summary>
    public string Units => Entry.Units is { } u
        ? u == 1 ? "1 unidad" : $"{u} unidades"
        : ReportsViewModel.Unknown;

    /// <summary>«+4 / −1». Nunca un cero inventado: sin sesión detrás, «—».</summary>
    public string Findings => Entry.New is { } n && Entry.Resolved is { } r
        ? n == 0 && r == 0 ? "sin cambios" : $"+{n} / −{r}"
        : ReportsViewModel.Unknown;

    public string Cost => Entry.Cost is { } c
        ? $"{c.ToString("0.##", CultureInfo.CurrentCulture)} {Entry.CostUnit}"
        : ReportsViewModel.Unknown;

    public string Title => Entry.Title;
}

/// <summary>
/// V7 Informes (F6.3): buscar, leer y descargar los informes de sesión del hub.
/// <para>
/// <b>Lista y visor viven en el MISMO view-model, y a propósito.</b> «Volver» tiene que devolver
/// la lista con sus filtros y su scroll intactos, y eso solo es gratis si la lista nunca se
/// destruyó: el visor se enseña encima, no en lugar de. Separarlos en dos páginas habría obligado
/// a serializar el estado del filtro para restaurarlo, que es la clase de código que acaba
/// perdiendo un campo.
/// </para>
/// <para>
/// <b>Esta vista solo LEE.</b> No genera informes, no los edita y no los borra: son inmutables por
/// diseño (§7). Las dos únicas acciones son abrir y descargar.
/// </para>
/// </summary>
public sealed partial class ReportsViewModel : ViewModelBase
{
    /// <summary>Lo que se escribe donde no hay dato. Nunca un cero con formato (D-318).</summary>
    public const string Unknown = "—";

    /// <summary>La opción neutra del combo de aplicación. Es el valor inicial.</summary>
    public static readonly AppFilterOption AllApps = new(null, "Todas");

    /// <summary>La opción neutra del combo de usuario.</summary>
    public static readonly AuthorOption AllAuthors = new(null, "Todos");

    /// <summary>La opción neutra del combo de tipo.</summary>
    public static readonly ReportKindOption AllKinds = new(null, "Todos");

    private readonly ReportsQuery _reports;
    private readonly NavigationService _navigation;
    private readonly IFileSaver _saver;
    private readonly ToastCenter _toasts;

    private bool _suspendReload;
    private string? _pendingSlug;
    private string? _pendingReportId;

    public ReportsViewModel(
        ReportsQuery reports,
        NavigationService navigation,
        IFileSaver saver,
        ToastCenter toasts)
    {
        _reports = reports;
        _navigation = navigation;
        _saver = saver;
        _toasts = toasts;

        KindOptions = new List<ReportKindOption> { AllKinds }
            .Concat(Enum.GetValues<ReportKind>().Select(k => new ReportKindOption(k, ReportKinds.Display(k))))
            .ToList();

        RangeOptions = new List<DateRangeOption>
        {
            new(7, "Últimos 7 días"),
            new(30, "Últimos 30 días"),
            new(90, "Últimos 90 días"),
            new(null, "Todo"),
        };

        _suspendReload = true;
        AppOptions.Add(AllApps);
        AuthorOptions.Add(AllAuthors);
        SelectedApp = AllApps;
        SelectedAuthor = AllAuthors;
        SelectedKind = AllKinds;
        _selectedRange = RangeOptions[3];
        _suspendReload = false;
    }

    public override string Title => "Informes";

    // ---------- Filtros ----------

    public ObservableCollection<AppFilterOption> AppOptions { get; } = new();

    public ObservableCollection<AuthorOption> AuthorOptions { get; } = new();

    public IReadOnlyList<ReportKindOption> KindOptions { get; }

    public IReadOnlyList<DateRangeOption> RangeOptions { get; }

    [ObservableProperty] private AppFilterOption? _selectedApp;
    [ObservableProperty] private AuthorOption? _selectedAuthor;
    [ObservableProperty] private ReportKindOption? _selectedKind;
    [ObservableProperty] private DateRangeOption _selectedRange;
    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>
    /// El rango a mano. Mientras está apagado, los dos calendarios no filtran nada: un rango a
    /// medio escribir no puede vaciar la lista por su cuenta.
    /// </summary>
    [ObservableProperty] private bool _useCustomRange;

    [ObservableProperty] private DateTime? _customFrom;
    [ObservableProperty] private DateTime? _customTo;

    partial void OnSelectedAppChanged(AppFilterOption? value) => Reload();
    partial void OnSelectedAuthorChanged(AuthorOption? value) => Reload();
    partial void OnSelectedKindChanged(ReportKindOption? value) => Reload();
    partial void OnSelectedRangeChanged(DateRangeOption value) => Reload();
    partial void OnSearchTextChanged(string value) => Reload();
    partial void OnCustomFromChanged(DateTime? value) => Reload();
    partial void OnCustomToChanged(DateTime? value) => Reload();

    partial void OnUseCustomRangeChanged(bool value)
    {
        OnPropertyChanged(nameof(UsePresetRange));
        Reload();
    }

    /// <summary>Con el rango a mano encendido, el combo de presets no manda: se apaga y se ve.</summary>
    public bool UsePresetRange => !UseCustomRange;

    // ---------- La lista ----------

    public ObservableCollection<ReportRow> Rows { get; } = new();

    [ObservableProperty] private int _resultCount;
    [ObservableProperty] private string _resultsSummary = "0 informes";
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _hasActiveFilters;

    /// <summary>
    /// El estado vacío dice QUÉ pasa. No es lo mismo «el hub todavía no tiene informes» —que se
    /// arregla auditando— que «tus filtros no dejan pasar ninguno», que se arregla limpiándolos.
    /// </summary>
    [ObservableProperty] private string _emptyMessage = string.Empty;

    // ---------- El visor ----------

    /// <summary>El informe abierto, o null si se está viendo la lista.</summary>
    [ObservableProperty] private ReportRow? _openReport;

    /// <summary>Hay un informe abierto: la lista se queda debajo, viva y con su scroll.</summary>
    [ObservableProperty] private bool _isViewing;

    /// <summary>El markdown ya renderizado. Se construye al abrir, no al listar.</summary>
    [ObservableProperty] private FlowDocument? _document;

    [ObservableProperty] private string _viewerTitle = string.Empty;

    /// <summary>«21 ago 2026 · 10:32 · XBlast · alvaro».</summary>
    [ObservableProperty] private string _viewerSubtitle = string.Empty;

    /// <summary>
    /// El enlace a los hallazgos solo aparece en informes de SESIÓN: un consolidado de cierre o un
    /// reset no habla de una tanda concreta de hallazgos.
    /// </summary>
    [ObservableProperty] private bool _canOpenFindings;

    /// <summary>
    /// El informe abierto es de un arreglo asistido y se sabe de qué hallazgo (H9.1 §1): hay
    /// camino de vuelta a su ficha. Un informe de arreglo que nombra el hallazgo pero no deja
    /// llegar a él obliga a buscarlo a mano en V3, que es exactamente la fricción que se reportó.
    /// </summary>
    [ObservableProperty] private bool _canOpenFinding;

    /// <summary>«Ver el hallazgo (OPT-0002)» — el identificador va en el rótulo, no en un tooltip.</summary>
    [ObservableProperty] private string _openFindingLabel = "Ver el hallazgo";

    // ---------- Carga ----------

    /// <summary>
    /// Abre un informe concreto en cuanto la vista cargue. Lo usan los enlaces de Métricas y de
    /// «Última sesión»: en toda la aplicación hay UN camino para ver un informe, y es este.
    /// </summary>
    public void ShowReport(string slug, string reportId)
    {
        _pendingSlug = slug;
        _pendingReportId = reportId;
    }

    public override Task LoadAsync()
    {
        // El hub pudo cambiar por debajo (polling, push de un compañero): la lectura se tira y se
        // vuelve a hacer, que es lo que hace aparecer solos los informes de otros.
        _reports.Invalidate();
        RefreshOptions();
        Reload();
        ApplyPendingReport();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Los combos se rellenan con lo que HAY. Solo se reconstruyen si el conjunto cambió: rehacerlos
    /// en cada tick del polling tiraría la selección del usuario (misma regla que V3).
    /// </summary>
    private void RefreshOptions()
    {
        var apps = new List<AppFilterOption> { AllApps };
        apps.AddRange(_reports.Apps().Select(a => new AppFilterOption(a.Slug, a.Name)));
        Sync(AppOptions, apps, o => o.Slug, () => SelectedApp?.Slug, o => SelectedApp = o, AllApps);

        var authors = new List<AuthorOption> { AllAuthors };
        authors.AddRange(_reports.Authors().Select(a => new AuthorOption(a, a)));
        Sync(AuthorOptions, authors, o => o.Value, () => SelectedAuthor?.Value, o => SelectedAuthor = o, AllAuthors);
    }

    private void Sync<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> wanted,
        Func<T, string?> key,
        Func<string?> currentKey,
        Action<T> select,
        T fallback)
    {
        if (target.SequenceEqual(wanted))
        {
            return;
        }

        string? current = currentKey();
        bool previous = _suspendReload;
        _suspendReload = true;
        target.Clear();
        foreach (T option in wanted)
        {
            target.Add(option);
        }

        select(wanted.FirstOrDefault(o => key(o) == current) ?? fallback);
        _suspendReload = previous;
    }

    private void ApplyPendingReport()
    {
        if (_pendingSlug is null || _pendingReportId is null)
        {
            return;
        }

        (string slug, string id) = (_pendingSlug, _pendingReportId);
        _pendingSlug = null;
        _pendingReportId = null;

        if (_reports.Find(slug, id) is { } entry)
        {
            Open(new ReportRow { Entry = entry });
            return;
        }

        // El enlace llegó a un informe que no está. Se dice: quedarse en la lista sin explicar por
        // qué es indistinguible de que el botón no funcione.
        _toasts.Show("Ese informe no está en el hub. Puede que la sesión no llegara a generarlo.");
    }

    private void Reload()
    {
        if (_suspendReload)
        {
            return;
        }

        var filter = BuildFilter();
        IReadOnlyList<ReportEntry> matched = _reports.Filter(filter);

        Rows.Clear();
        foreach (ReportEntry entry in matched)
        {
            Rows.Add(new ReportRow { Entry = entry });
        }

        ResultCount = Rows.Count;
        ResultsSummary = ResultCount == 1 ? "1 informe" : $"{ResultCount} informes";
        HasActiveFilters = filter.IsActive;
        IsEmpty = Rows.Count == 0;
        EmptyMessage = !IsEmpty
            ? string.Empty
            : HasActiveFilters
                ? "Ningún informe con estos filtros."
                : "Todavía no hay informes. Cada auditoría deja el suyo al terminar.";
    }

    /// <summary>
    /// Traduce la barra de filtros a lo que entiende el agregador. Separado de <see cref="Reload"/>
    /// para poder comprobar el rango de fechas sin montar la lista.
    /// </summary>
    internal ReportsFilter BuildFilter()
    {
        (DateTimeOffset? from, DateTimeOffset? to) = Range();
        return new ReportsFilter(
            SelectedApp?.Slug,
            SelectedAuthor?.Value,
            SelectedKind?.Value,
            from,
            to,
            SearchText?.Trim() ?? string.Empty);
    }

    /// <summary>
    /// El rango efectivo. El personalizado gana al preset cuando está encendido; sus dos extremos
    /// son opcionales por separado, así que «desde el 1 de julio» sin fin es un rango válido. El
    /// «hasta» se lleva al final del día elegido: quien escribe 21 de agosto quiere ese día dentro.
    /// </summary>
    private (DateTimeOffset? From, DateTimeOffset? To) Range()
    {
        if (UseCustomRange)
        {
            DateTimeOffset? from = CustomFrom is { } f
                ? new DateTimeOffset(f.Date, TimeSpan.Zero)
                : null;
            DateTimeOffset? to = CustomTo is { } t
                ? new DateTimeOffset(t.Date, TimeSpan.Zero).AddDays(1)
                : null;
            return (from, to);
        }

        return SelectedRange?.Days is { } days
            ? (DateTimeOffset.UtcNow.AddDays(-days), null)
            : (null, null);
    }

    // ---------- Gestos ----------

    /// <summary>La fila entera es el enlace: abrir el informe es lo que hace esta lista.</summary>
    [RelayCommand]
    private void Open(ReportRow? row)
    {
        if (row is null)
        {
            return;
        }

        OpenReport = row;
        Document = MarkdownFlowDocument.Build(_reports.Read(row.Entry), OpenExternal);
        ViewerTitle = row.Title;
        ViewerSubtitle = string.Join(" · ", new[] { row.When, row.AppName, row.By }
            .Where(s => !string.IsNullOrWhiteSpace(s) && s != Unknown));
        CanOpenFindings = row.Entry.Kind == ReportKind.Sesion;
        CanOpenFinding = row.Entry.HasFinding;
        OpenFindingLabel = string.IsNullOrWhiteSpace(row.Entry.FindingAlias)
            ? "Ver el hallazgo"
            : $"Ver el hallazgo ({row.Entry.FindingAlias})";
        IsViewing = true;
    }

    /// <summary>Vuelve a la lista. No la recarga: sus filtros y su scroll siguen donde estaban.</summary>
    [RelayCommand]
    private void Back()
    {
        IsViewing = false;
        OpenReport = null;
        Document = null;
    }

    /// <summary>
    /// Descarga el <c>.md</c> donde el usuario elija, con un nombre que dice qué es sin abrirlo.
    /// Copia el fichero tal cual: esta vista no reescribe un informe ni para guardarlo.
    /// </summary>
    [RelayCommand]
    private void Download()
    {
        if (OpenReport is not { } row)
        {
            return;
        }

        string? target = _saver.Pick("Guardar informe", row.Entry.DownloadName, "Markdown (*.md)|*.md");
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        try
        {
            File.Copy(row.Entry.Path, target, overwrite: true);
            _toasts.Show($"Informe guardado en {target}");
        }
        catch (Exception ex)
        {
            _toasts.Show($"No se pudo guardar el informe: {ex.Message}");
        }
    }

    /// <summary>Los hallazgos de la aplicación de este informe, en V3.</summary>
    [RelayCommand]
    private Task OpenFindings()
        => OpenReport is not { } row
            ? Task.CompletedTask
            : _navigation.NavigateToAsync<FindingsViewModel>(vm => vm.SetApp(row.Slug));

    /// <summary>
    /// La ficha del hallazgo que arregló esta sesión (H9.1 §1). Mismo patrón que «Ver hallazgos de
    /// esta sesión», un escalón más fino: de un arreglo se vuelve a SU hallazgo, no a la lista.
    /// </summary>
    [RelayCommand]
    private Task OpenFinding()
        => OpenReport is { Entry.FindingId: { } id } row && Ulid.TryParse(id, out Ulid finding)
            ? _navigation.NavigateToAsync<FindingDetailViewModel>(vm => vm.Load(row.Slug, finding))
            : Task.CompletedTask;

    /// <summary>Devuelve toda la barra de filtros a su valor inicial.</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        _suspendReload = true;
        SearchText = string.Empty;
        SelectedApp = AppOptions.FirstOrDefault(o => o.Slug is null) ?? AllApps;
        SelectedAuthor = AuthorOptions.FirstOrDefault(o => o.Value is null) ?? AllAuthors;
        SelectedKind = AllKinds;
        SelectedRange = RangeOptions[^1];
        UseCustomRange = false;
        CustomFrom = null;
        CustomTo = null;
        _suspendReload = false;
        Reload();
    }

    /// <summary>
    /// Un enlace del informe sale FUERA, al navegador del sistema. Nunca dentro de la ventana:
    /// esta aplicación no es un navegador y un informe no es una página web.
    /// </summary>
    private void OpenExternal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _toasts.Show("Ese enlace no apunta a una dirección web.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _toasts.Show($"No se pudo abrir el enlace: {ex.Message}");
        }
    }
}
