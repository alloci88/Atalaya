using System.Collections.ObjectModel;
using Atalaya.App.Services;
using Atalaya.Domain.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>Un grupo del diálogo: las sesiones que están paradas por lo mismo (F29 §1).</summary>
public sealed partial class ReconcileGroupRow : ObservableObject
{
    public ReconcileGroupRow(CostGapGroup group)
    {
        Group = group;
    }

    public CostGapGroup Group { get; }

    /// <summary>El título del grupo: qué las para.</summary>
    public string Title => Group.Reason == CostGapReason.Desconocido
        ? Group.Model.Length > 0
            ? $"Modelo desconocido («{Group.Model}»)"
            : "Modelo desconocido (sin registrar)"
        : $"Modelo sin tarifa: {Group.Model}";

    /// <summary>Cuántas son.</summary>
    public string Count => Group.Count == 1 ? "1 sesión" : $"{Group.Count} sesiones";

    /// <summary>
    /// <b>Qué pasa con este grupo</b>, en una línea. Es lo que decide qué controles tiene delante:
    /// un enlace a las tarifas, un desplegable, o nada porque ya está listo.
    /// </summary>
    public string State
    {
        get
        {
            if (Group.ResolvableByCall)
            {
                return "Listo para calcular: las llamadas guardaron con qué modelo contestó cada una, "
                    + $"así que se valora llamada a llamada — {string.Join(", ", Group.CallModels)}.";
            }

            if (Group.Reason == CostGapReason.SinTarifa)
            {
                return RateAdded
                    ? "Listo para calcular: la tarifa ya está en la tabla."
                    : $"Falta la tarifa de «{Group.Model}» en la tabla de la organización.";
            }

            return "Nadie registró con qué modelo corrieron y sus llamadas tampoco lo dicen. "
                + "Elige con qué tarifa valorarlas: el coste quedará marcado como estimado, y la "
                + "marca no se quita.";
        }
    }

    /// <summary>La tarifa que faltaba ya está: el grupo pasa a «listo para calcular».</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State))]
    [NotifyPropertyChangedFor(nameof(NeedsRate))]
    private bool _rateAdded;

    /// <summary>Hace falta ir a añadir la tarifa: el enlace a Ajustes → Tarifas.</summary>
    public bool NeedsRate
        => Group.Reason == CostGapReason.SinTarifa && !Group.ResolvableByCall && !RateAdded;

    /// <summary>Hace falta elegir con qué tarifa valorarlas: el desplegable.</summary>
    public bool NeedsAssignment
        => Group.Reason == CostGapReason.Desconocido && !Group.ResolvableByCall;

    /// <summary>Este grupo se cierra al pulsar el botón.</summary>
    public bool IsReady => Group.ResolvableByCall || (Group.Reason == CostGapReason.SinTarifa && RateAdded);
}

/// <summary>
/// <b>Reconciliar los costes de una aplicación</b> (F29 §1).
/// <para>
/// <b>Un diálogo, no una página</b>: se abre desde el resumen del ciclo, se hace una cosa y se
/// cierra. Los diálogos siguen siendo diálogos.
/// </para>
/// <para>
/// <b>Lo que se pulsa aquí no escribe un coste.</b> Escribe lo que faltaba para poder calcularlo
/// —que estas sesiones se valoran por sus llamadas, o con qué tarifa se valoran las que nadie
/// midió— y lo publica con su commit. El coste se sigue derivando en cada lectura (D-788).
/// </para>
/// </summary>
public sealed partial class ReconcileCostsViewModel : ObservableObject
{
    private readonly CostReconciliationService _reconciler;
    private readonly ModelRatesService _rates;

    public ReconcileCostsViewModel(CostReconciliationService reconciler, ModelRatesService rates)
    {
        _reconciler = reconciler;
        _rates = rates;
    }

    /// <summary>El diálogo se cierra. Lo pide el enlace a las tarifas, que lleva a otra pantalla.</summary>
    public event Action? CloseRequested;

    /// <summary>
    /// Al cerrarse, quien lo abrió tiene que llevar a Ajustes → Tarifas. La navegación NO se hace
    /// desde aquí: mover la ventana de detrás con un modal encima deja al usuario mirando una
    /// pantalla que no puede tocar.
    /// </summary>
    public bool GoToRates { get; private set; }

    public string Slug { get; private set; } = string.Empty;

    [ObservableProperty] private string _appName = string.Empty;

    /// <summary>Los grupos, uno por motivo.</summary>
    public ObservableCollection<ReconcileGroupRow> Groups { get; } = new();

    /// <summary>Los modelos CON tarifa, para el desplegable de asignación.</summary>
    public ObservableCollection<string> RatedModels { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReconcile))]
    [NotifyPropertyChangedFor(nameof(ReconcileLabel))]
    [NotifyPropertyChangedFor(nameof(DisabledReason))]
    private string? _assignedModel;

    [ObservableProperty] private string _status = string.Empty;

    /// <summary>Se ha reconciliado algo: quien abrió el diálogo tiene que recargar su vista.</summary>
    [ObservableProperty] private bool _changed;

    /// <summary>Cuántas sesiones cerraría el botón AHORA MISMO.</summary>
    public int Reconcilable
        => Groups.Sum(g => g.IsReady || (g.NeedsAssignment && HasAssignment) ? g.Group.Count : 0);

    private bool HasAssignment => !string.IsNullOrWhiteSpace(AssignedModel);

    public bool CanReconcile => Reconcilable > 0;

    /// <summary>«Reconciliar 3 sesiones». El número es el que se va a cerrar, no el total.</summary>
    public string ReconcileLabel => Reconcilable == 1
        ? "Reconciliar 1 sesión"
        : $"Reconciliar {Reconcilable} sesiones";

    /// <summary>
    /// Por qué está apagado. Un primario apagado sin motivo es un callejón: aquí siempre hay uno,
    /// y siempre dice qué falta.
    /// </summary>
    public string DisabledReason
    {
        get
        {
            if (CanReconcile)
            {
                return string.Empty;
            }

            if (Groups.Count == 0)
            {
                return "No hay ninguna sesión sin coste en esta aplicación.";
            }

            return Groups.Any(g => g.NeedsRate)
                ? "Añade la tarifa que falta en Ajustes → Tarifas y vuelve: el grupo pasará a «listo para calcular»."
                : "Elige con qué tarifa valorar las sesiones cuyo modelo no se sabe.";
        }
    }

    /// <summary>
    /// <b>Las sesiones que este diálogo se ha comprometido a cerrar</b> (F29 §1). Se fijan al
    /// abrirlo y no cambian mientras esté abierto: añadir la tarifa que faltaba cierra el hueco por
    /// sí sola —eso es D-788—, y sin esta lista el grupo desaparecería en vez de pasar a «listo
    /// para calcular», dejando además sus informes sin la línea de «calculado a posteriori».
    /// </summary>
    public IReadOnlyList<Domain.Ids.Ulid> Scope { get; private set; } = Array.Empty<Domain.Ids.Ulid>();

    /// <summary>Lee el hueco de la aplicación y monta los grupos.</summary>
    /// <param name="resume">
    /// Las sesiones de un diálogo anterior que fue a Ajustes → Tarifas y vuelve. Sin ellas, las que
    /// la tarifa recién añadida ya cerró no volverían a listarse.
    /// </param>
    public void Load(string slug, string appName, IReadOnlyList<Domain.Ids.Ulid>? resume = null)
    {
        Slug = slug;
        AppName = appName;
        Scope = resume ?? Array.Empty<Domain.Ids.Ulid>();
        Refresh();

        // El compromiso se fija con lo que hay al abrir: a partir de aquí el diálogo cierra estas
        // sesiones, no las que aparezcan o desaparezcan por debajo.
        Scope = Groups.SelectMany(g => g.Group.Sessions).Select(s => s.Id).Distinct().ToList();
    }

    /// <summary>
    /// Vuelve a mirar el hub. La llama volver de Ajustes → Tarifas: si la tarifa ya está, el grupo
    /// pasa a «listo para calcular» sin que haya que cerrar y reabrir el diálogo.
    /// </summary>
    public void Refresh()
    {
        AppCostGap gap = _reconciler.GapOf(Slug, Scope);
        ModelRateTable? table = _rates.Current;

        Groups.Clear();
        foreach (CostGapGroup group in gap.Groups)
        {
            Groups.Add(new ReconcileGroupRow(group)
            {
                RateAdded = group.Reason == CostGapReason.SinTarifa
                    && table?.Find(group.Model, null) is not null,
            });
        }

        RatedModels.Clear();
        if (table is not null)
        {
            foreach (string model in ModelRatesService.Billable(table)
                         .Select(r => r.Model)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
            {
                RatedModels.Add(model);
            }
        }

        OnPropertyChanged(nameof(Reconcilable));
        OnPropertyChanged(nameof(CanReconcile));
        OnPropertyChanged(nameof(ReconcileLabel));
        OnPropertyChanged(nameof(DisabledReason));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public bool IsEmpty => Groups.Count == 0;

    /// <summary>
    /// Cierra y pide que lleven a Ajustes → Tarifas. Al volver y reabrir el diálogo, el grupo sale
    /// ya como «listo para calcular» si la tarifa está.
    /// </summary>
    [RelayCommand]
    private void AddRate()
    {
        GoToRates = true;
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// <b>Los pasos de reconciliar</b> (F30 §4), dentro del diálogo y bajo el botón. Uno solo: el
    /// plan no depende de nada, y reintentar vuelve a ponerlo todo en pendiente.
    /// </summary>
    public StepList Steps { get; } = CostReconciliationService.NewSteps();

    /// <summary>
    /// Escribe las reconciliaciones y las publica con su commit. Al terminar vuelve a leer: lo que
    /// se ha cerrado desaparece de la lista, que es la confirmación que hace falta.
    /// <para>
    /// <b>El diálogo no se cierra solo</b> (F30 §4): al terminar enseña lo que cerró y CUÁNTO
    /// suma —«3 sesiones reconciliadas · 0,91 $»—, que es la pregunta por la que se abrió. Se
    /// cierra con «Cerrar».
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task Reconcile()
    {
        Steps.Start();
        try
        {
            // Fuera del hilo de interfaz: si no, el diálogo se congela y sus propios pasos no se
            // pintarían — que es exactamente el defecto que esta pieza viene a cerrar.
            ReconciliationOutcome done = await Task.Run(
                () => _reconciler.Reconcile(Slug, AssignedModel, Scope, Steps));

            Changed |= done.Sessions > 0;
            Status = done.Sessions == 0
                ? "No se ha reconciliado ninguna sesión."
                : $"{Sesiones(done.Sessions)} · {CostFormat.Of(done.Credits)}";
        }
        catch (Exception ex)
        {
            Status = $"La reconciliación se ha parado: {ex.Message}";
        }
        finally
        {
            Steps.Finish();
            Refresh();
        }
    }

    /// <summary>«1 sesión reconciliada» / «3 sesiones reconciliadas». Singular y plural, una vez.</summary>
    private static string Sesiones(int n)
        => n == 1 ? "1 sesión reconciliada" : $"{n} sesiones reconciliadas";
}
