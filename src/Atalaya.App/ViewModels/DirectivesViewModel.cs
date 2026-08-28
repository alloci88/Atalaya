using System.Collections.ObjectModel;
using Atalaya.App.Services;
using Atalaya.Copilot;
using Atalaya.Domain.Ids;
using Atalaya.Domain.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>
/// Una directiva —o un candidato a serlo— tal y como se lee en el panel (F7). Es un modelo de
/// fila: la entidad del hub no sabe decir «no encontrada» ni cuánto ocupa en tokens, porque el
/// contenido no vive en el hub.
/// </summary>
public sealed partial class DirectiveRow : ObservableObject
{
    /// <summary>Los ámbitos, en el orden en que se ofrecen. El vacío es no activarla.</summary>
    public static IReadOnlyList<string> ScopeOptions { get; } = new[]
    {
        DirectiveScopeNames.Display(DirectiveScope.Auditoria),
        DirectiveScopeNames.Display(DirectiveScope.Arreglo),
        DirectiveScopeNames.Display(DirectiveScope.Ambos),
    };

    public DirectiveRow(DirectiveEntry entry, int tokens)
    {
        Id = entry.Registered?.Id;
        Path = entry.Path;
        Kind = entry.Kind;
        Why = entry.Candidate?.Why ?? "Añadida a mano: el catálogo no conoce esta ruta.";
        Missing = entry.Missing;
        IsNew = entry.IsNew;
        Bytes = entry.Candidate?.Bytes ?? 0;
        Tokens = tokens;
        _isActive = entry.Scope != DirectiveScope.Ninguno;
        _selectedScope = DirectiveScopeNames.Display(
            entry.Scope == DirectiveScope.Ninguno ? DirectiveScope.Ambos : entry.Scope);
        _order = entry.Order;
    }

    /// <summary>Null mientras solo sea un candidato: todavía no hay entrada en el hub.</summary>
    public Ulid? Id { get; }

    public string Path { get; }

    public string Kind { get; }

    /// <summary>Por qué el catálogo la propone. Es lo que permite decidir sin abrir el fichero.</summary>
    public string Why { get; }

    /// <summary>Registrada pero sin fichero en el clon. Ni es un error ni rompe nada.</summary>
    public bool Missing { get; }

    /// <summary>Detectada y nunca curada. Aparece sin marcar, siempre.</summary>
    public bool IsNew { get; }

    public long Bytes { get; }

    /// <summary>Lo que ocuparía en un prompt, estimado. 0 si no se pudo leer.</summary>
    public int Tokens { get; }

    /// <summary>Marcarla es activarla; desmarcarla la deja registrada con ámbito «sin activar».</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State))]
    private bool _isActive;

    [ObservableProperty] private string _selectedScope;

    [ObservableProperty] private int _order;

    /// <summary>El contenido, cuando se pide verlo. No se lee de antemano: son ficheros del clon.</summary>
    [ObservableProperty] private string _preview = string.Empty;

    [ObservableProperty] private bool _isPreviewOpen;

    public string Size => Missing
        ? "no encontrada"
        : Bytes < 1024 ? $"{Bytes} B" : $"{Bytes / 1024.0:0.#} kB";

    /// <summary>El coste, dicho como se decide: en tokens del presupuesto.</summary>
    public string Cost => Missing ? "—" : $"~{Tokens} tokens";

    public string State => Missing
        ? "no encontrada — el fichero ya no está en el clon"
        : IsNew ? "candidato nuevo — sin activar"
        : IsActive ? "activa" : "sin activar";

    /// <summary>El ámbito elegido, traducido de vuelta a lo que se persiste.</summary>
    public DirectiveScope Scope => !IsActive
        ? DirectiveScope.Ninguno
        : SelectedScope == DirectiveScopeNames.Display(DirectiveScope.Auditoria) ? DirectiveScope.Auditoria
        : SelectedScope == DirectiveScopeNames.Display(DirectiveScope.Arreglo) ? DirectiveScope.Arreglo
        : DirectiveScope.Ambos;

    public bool CountsForAudit => IsActive && Scope is DirectiveScope.Auditoria or DirectiveScope.Ambos;

    public bool CountsForFix => IsActive && Scope is DirectiveScope.Arreglo or DirectiveScope.Ambos;
}

/// <summary>
/// La gestión de directivas del proyecto de UNA aplicación (F7 §1): ver qué propone el catálogo,
/// activar lo que de verdad son convenciones, decidir a qué flujo aplica cada una, ordenarlas y
/// mirar cuánto presupuesto consumen.
/// <para>
/// Vive separada del diálogo por la misma razón que la de patrones silenciados: las cuatro cosas
/// que importan —que un candidato nunca se active solo, que el ámbito decida en qué prompt viaja,
/// que el presupuesto se vea antes de gastarlo y que un fichero desaparecido no rompa nada— se
/// comprueban sin abrir una ventana.
/// </para>
/// </summary>
public sealed partial class DirectivesViewModel : ObservableObject
{
    private readonly DirectiveService _directives;
    private readonly HubContext _hub;
    private readonly ToastCenter _toasts;
    private string? _clonePath;

    public DirectivesViewModel(DirectiveService directives, HubContext hub, ToastCenter toasts)
    {
        _directives = directives;
        _hub = hub;
        _toasts = toasts;
    }

    public ObservableCollection<DirectiveRow> Rows { get; } = new();

    [ObservableProperty] private string _slug = string.Empty;

    [ObservableProperty] private string _appName = string.Empty;

    /// <summary>La ruta que se va a añadir a mano, relativa a la raíz del clon.</summary>
    [ObservableProperty] private string _manualPath = string.Empty;

    public string Title => $"Directivas del proyecto · {AppName}";

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>
    /// Que no haya clon vinculado NO se dice con una lista vacía: son dos cosas distintas y la
    /// diferencia importa —«el catálogo no encontró nada» invita a añadir a mano; «no se ha podido
    /// mirar» invita a vincular el clon—.
    /// </summary>
    public bool Scanned { get; private set; }

    public string ScanNotice { get; private set; } = string.Empty;

    public bool HasScanNotice => ScanNotice.Length > 0;

    /// <summary>
    /// El presupuesto de esta app, tal y como se aplica en cada prompt. Es editable aquí, que es
    /// donde se ve su consecuencia: el consumo de lo activado se cuenta contra este número.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BudgetLabel))]
    private int _budget;

    public int AuditTokens { get; private set; }

    public int FixTokens { get; private set; }

    /// <summary>
    /// Lo que se está gastando, flujo a flujo. Se dice por separado porque son prompts distintos:
    /// una directiva de ámbito «Arreglo» no le quita presupuesto al auditor.
    /// </summary>
    public string BudgetLabel => Budget == 0
        ? "Presupuesto 0: las directivas están apagadas en esta aplicación y no viaja ninguna."
        : $"Presupuesto por prompt: {Budget} tokens · auditoría {AuditTokens} · arreglo {FixTokens}.";

    /// <summary>
    /// El aviso de pasarse. No bloquea nada —el presupuesto ya recorta por prioridad y el prompt
    /// declara lo omitido—, pero se dice aquí, que es donde se puede arreglar sin gastar una
    /// sesión en descubrirlo.
    /// </summary>
    public string OverBudgetNotice { get; private set; } = string.Empty;

    public bool IsOverBudget => OverBudgetNotice.Length > 0;

    public void Load(string slug, string? clonePath)
    {
        Slug = slug;
        _clonePath = clonePath;
        AppName = _hub.Store.TryReadApp(slug)?.Name ?? slug;
        Reload();
    }

    private void Reload()
    {
        DirectiveInventory inventory = _directives.Inventory(Slug, _clonePath);
        Scanned = inventory.Scanned;
        Budget = Math.Max(0, _hub.Store.TryReadApp(Slug)?.Thresholds.DirectiveTokenBudget ?? 0);

        Rows.Clear();
        foreach (DirectiveEntry entry in inventory.Entries)
        {
            Rows.Add(new DirectiveRow(entry, EstimateTokens(entry)));
        }

        ScanNotice = !Scanned
            ? "No hay clon local vinculado: no se ha podido buscar nada en el repositorio. Lo que se "
              + "lista es solo lo que ya estaba registrado en el hub."
            : inventory.Truncated
                ? $"El escaneo se cortó en {Atalaya.Inventory.DirectiveScanner.MaxCandidates} candidatos. "
                  + "Si falta alguno, añádelo a mano por su ruta."
                : string.Empty;

        Recount();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Scanned));
        OnPropertyChanged(nameof(ScanNotice));
        OnPropertyChanged(nameof(HasScanNotice));
    }

    /// <summary>Rehace las cuentas del presupuesto sin volver a tocar el disco.</summary>
    private void Recount()
    {
        AuditTokens = Rows.Where(r => r.CountsForAudit && !r.Missing).Sum(r => r.Tokens);
        FixTokens = Rows.Where(r => r.CountsForFix && !r.Missing).Sum(r => r.Tokens);

        var over = new List<string>();
        if (Budget > 0 && AuditTokens > Budget)
        {
            over.Add($"auditoría ({AuditTokens})");
        }

        if (Budget > 0 && FixTokens > Budget)
        {
            over.Add($"arreglo ({FixTokens})");
        }

        OverBudgetNotice = over.Count == 0
            ? string.Empty
            : $"Lo activado supera el presupuesto de {Budget} tokens en {string.Join(" y ", over)}. "
              + "Se incluirán por prioridad —el número de orden, menor primero— y el prompt declarará "
              + "cuáles quedaron fuera. Nada se incluye a medias en silencio.";

        OnPropertyChanged(nameof(AuditTokens));
        OnPropertyChanged(nameof(FixTokens));
        OnPropertyChanged(nameof(BudgetLabel));
        OnPropertyChanged(nameof(OverBudgetNotice));
        OnPropertyChanged(nameof(IsOverBudget));
    }

    private int EstimateTokens(DirectiveEntry entry)
    {
        if (entry.Missing || string.IsNullOrWhiteSpace(_clonePath))
        {
            return 0;
        }

        try
        {
            string abs = Path.Combine(_clonePath!, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            return PromptTokens.Estimate(File.ReadAllText(abs));
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Guarda el presupuesto de la aplicación. 0 apaga las directivas del todo: es el interruptor
    /// de quien no las quiera, sin tener que desactivarlas una a una.
    /// </summary>
    [RelayCommand]
    private void ApplyBudget()
    {
        if (Budget < 0)
        {
            _toasts.Show("El presupuesto no puede ser negativo: 0 apaga las directivas.");
            Reload();
            return;
        }

        if (_directives.SetBudget(Slug, Budget))
        {
            _toasts.Show(Budget == 0
                ? $"Directivas desactivadas en {AppName}: no viajará ninguna."
                : $"Presupuesto de directivas de {AppName}: {Budget} tokens por prompt.");
        }

        Reload();
    }

    // ------------------------------------------------------------------ curación

    /// <summary>
    /// Aplica el ámbito de la fila —marcada o no— y lo persiste. Marcar y elegir ámbito son el
    /// mismo gesto porque son la misma decisión: una directiva activa siempre aplica a algo.
    /// </summary>
    [RelayCommand]
    private void ApplyScope(DirectiveRow? row)
    {
        if (row is null)
        {
            return;
        }

        _directives.SetScope(Slug, row.Path, row.Kind, row.Scope);
        _toasts.Show(row.Scope == DirectiveScope.Ninguno
            ? $"«{row.Path}» queda registrada sin activar: no viaja en ningún prompt."
            : $"«{row.Path}» activa para {DirectiveScopeNames.Display(row.Scope)}.");
        Reload();
    }

    /// <summary>Cambia la prioridad de inclusión. Solo tiene sentido sobre algo ya registrado.</summary>
    [RelayCommand]
    private void ApplyOrder(DirectiveRow? row)
    {
        if (row?.Id is not { } id)
        {
            _toasts.Show("Marca primero la directiva: la prioridad ordena lo que va a viajar.");
            return;
        }

        if (_directives.SetOrder(Slug, id, row.Order))
        {
            _toasts.Show($"«{row.Path}» pasa a prioridad {row.Order}.");
        }

        Reload();
    }

    /// <summary>Abre o cierra la vista previa, leyendo el fichero del clon en ese momento.</summary>
    [RelayCommand]
    private void TogglePreview(DirectiveRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (row.IsPreviewOpen)
        {
            row.IsPreviewOpen = false;
            row.Preview = string.Empty;
            return;
        }

        row.Preview = ReadPreview(row);
        row.IsPreviewOpen = true;
    }

    /// <summary>
    /// El principio del fichero. Es una vista previa, no un visor: lo que se está decidiendo es si
    /// esto son las convenciones del proyecto, y para eso basta con las primeras líneas.
    /// </summary>
    private string ReadPreview(DirectiveRow row)
    {
        if (row.Missing || string.IsNullOrWhiteSpace(_clonePath))
        {
            return "El fichero no está en el clon.";
        }

        try
        {
            string abs = Path.Combine(_clonePath!, row.Path.Replace('/', Path.DirectorySeparatorChar));
            string text = File.ReadAllText(abs);
            const int max = 4000;
            return text.Length <= max
                ? text
                : text[..max] + $"\n\n… (vista previa recortada; el fichero tiene ~{row.Tokens} tokens)";
        }
        catch (Exception ex)
        {
            return $"No se pudo leer: {ex.Message}";
        }
    }

    /// <summary>
    /// Añade a mano un fichero del repo que el catálogo no conoce, ya activado para los dos
    /// flujos: quien se molesta en escribir una ruta ya ha decidido que eso es una directiva. El
    /// ámbito se afina después, en su fila.
    /// </summary>
    [RelayCommand]
    private void AddManual()
    {
        string path = ManualPath.Trim();
        if (path.Length == 0)
        {
            _toasts.Show("Escribe la ruta del fichero, relativa a la raíz del repositorio.");
            return;
        }

        ProjectDirective? added = _directives.AddManual(Slug, _clonePath, path, DirectiveScope.Ambos);
        if (added is null)
        {
            _toasts.Show(string.IsNullOrWhiteSpace(_clonePath)
                ? "Sin clon local vinculado no se puede comprobar que la ruta exista."
                : $"No se ha añadido «{path}»: o no existe en el clon, o ya estaba registrada.");
            return;
        }

        ManualPath = string.Empty;
        _toasts.Show($"«{added.Path}» añadida a mano y activa para auditoría y arreglo.");
        Reload();
    }

    /// <summary>
    /// Retira la entrada del registro. Es lo que se hace con una «no encontrada» que ya no va a
    /// volver; si el fichero sigue en el repo, el catálogo lo volverá a proponer como candidato.
    /// </summary>
    [RelayCommand]
    private void Forget(DirectiveRow? row)
    {
        if (row?.Id is not { } id)
        {
            return;
        }

        if (_directives.Forget(Slug, id))
        {
            _toasts.Show($"«{row.Path}» retirada del registro de directivas.");
        }

        Reload();
    }
}
