using System.Collections.ObjectModel;
using Atalaya.App.Services;
using Atalaya.Domain.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>Una fila editable de la tabla de tarifas.</summary>
public sealed partial class RateRow : ObservableObject
{
    public RateRow()
    {
    }

    public RateRow(ModelRate rate)
    {
        _model = rate.Model;
        _provider = rate.Provider ?? string.Empty;
        _input = rate.InputPerMillion;
        _output = rate.OutputPerMillion;
        _cachedInput = rate.CachedInputPerMillion;
        _cacheWrite = rate.CacheWritePerMillion?.ToString(AppCulture.Display) ?? string.Empty;
        _note = rate.Note ?? string.Empty;
        _effectiveFrom = rate.EffectiveFrom;
    }

    [ObservableProperty] private string _model = string.Empty;
    [ObservableProperty] private string _provider = string.Empty;
    [ObservableProperty] private decimal _input;
    [ObservableProperty] private decimal _output;
    [ObservableProperty] private decimal _cachedInput;

    /// <summary>
    /// La escritura de caché, como TEXTO y no como número. Vacío significa «este modelo no la cobra
    /// aparte», que no es lo mismo que cero —cero afirmaría que escribir en caché es gratis—, y un
    /// <c>decimal</c> no sabe expresar esa diferencia.
    /// </summary>
    [ObservableProperty] private string _cacheWrite = string.Empty;

    [ObservableProperty] private string _note = string.Empty;
    [ObservableProperty] private DateOnly? _effectiveFrom;

    /// <summary>La fila como tarifa, o null si el modelo está en blanco (fila a medio escribir).</summary>
    public ModelRate? ToRate()
    {
        if (string.IsNullOrWhiteSpace(Model))
        {
            return null;
        }

        decimal? write = decimal.TryParse(CacheWrite, System.Globalization.NumberStyles.Number, AppCulture.Display, out decimal w)
            ? w
            : null;

        return new ModelRate(
            Model.Trim(),
            Provider.Trim(),
            Input,
            Output,
            CachedInput,
            write,
            EffectiveFrom ?? DateOnly.FromDateTime(DateTime.UtcNow),
            string.IsNullOrWhiteSpace(Note) ? null : Note.Trim());
    }
}

/// <summary>
/// La pantalla de tarifas por modelo (F15).
/// <para>
/// <b>Vive en Ajustes desde R2</b>, y antes en Métricas. El argumento de D-770 —se edita donde se
/// ve la consecuencia— valía cuando activar las tarifas era parte del trabajo de Métricas; desde que
/// se aplican solas, corregir un precio es mantenimiento de la configuración y no una lectura del
/// panel. Métricas conserva lo que sí es suyo: el aviso de «parcial» con su recuento, que es lo que
/// hace accionable el hueco, y un enlace hasta aquí.
/// </para>
/// <para>
/// <b>Ajustes es dónde se EDITA, no dónde se guarda.</b> El fichero sigue en la raíz del hub
/// (D-786): un precio es del contrato de la organización con su proveedor, no de la aplicación ni
/// del puesto. Es de la ORGANIZACIÓN —lo ve todo el equipo—, no una preferencia de máquina.
/// </para>
/// <para>
/// El commit del hub es la atribución de quién cambió qué tarifa y cuándo, así que no hay ningún
/// campo «modificado por» que mantener.
/// </para>
/// <para>
/// <b>Y es la tabla de lo que FACTURA</b> (F16-RETOQUE §1). Los modelos que solo se usan contra una
/// suscripción personal —Claude Code— ni se listan ni se admiten: no hay factura que calcular, y
/// una tarifa que no gobierna nada es una que alguien mantendrá para siempre sin saberlo.
/// </para>
/// </summary>
public sealed partial class ModelRatesViewModel : ObservableObject
{
    private readonly ModelRatesService _rates;

    /// <summary>
    /// Las sesiones sin coste, para la línea neutra (F29 §1). Opcional por lo mismo que el resto:
    /// los tests que solo ejercitan la tabla no montan un hub con sesiones.
    /// </summary>
    private readonly CostReconciliationService? _gaps;

    public ModelRatesViewModel(ModelRatesService rates, CostReconciliationService? gaps = null)
    {
        _rates = rates;
        _gaps = gaps;
        Load();
    }

    public ObservableCollection<RateRow> Rows { get; } = new();

    [ObservableProperty] private string _status = string.Empty;

    [ObservableProperty] private string _source = string.Empty;

    [ObservableProperty] private bool _saved;

    /// <summary>Hay sesiones sin coste en algún sitio del hub. Lo dice la línea neutra.</summary>
    public bool HasMissing => _sessionsWithoutCost > 0;

    private int _sessionsWithoutCost;
    private int _appsWithoutCost;

    /// <summary>
    /// <b>La línea neutra</b> (F29 §1): «Sesiones sin coste: 3 en 1 aplicación».
    /// <para>
    /// Es un recuento y un camino, no un aviso: desde aquí no se puede reconciliar nada, y pintar de
    /// ámbar algo sobre lo que no se puede actuar solo enseña a ignorar el color. La acción vive en
    /// el inventario de cada aplicación, que es de quien es el hueco.
    /// </para>
    /// </summary>
    public string MissingLine
        => $"Sesiones sin coste: {_sessionsWithoutCost} en "
           + (_appsWithoutCost == 1 ? "1 aplicación" : $"{_appsWithoutCost} aplicaciones");

    /// <summary>
    /// LA MISMA REGLA QUE LAS OTRAS CUATRO SECCIONES DE AJUSTES (P-27, UI-0038).
    /// <para>
    /// «Guardar tarifas» estaba encendido sin que se hubiera tocado nada, mientras el «Guardar» de
    /// las otras cuatro secciones se apagaba hasta que había cambios. Cuatro sitios y cuatro
    /// criterios no es una regla: es lo que cada uno hizo el día que lo escribió.
    /// </para>
    /// <para>
    /// La huella se toma de la tabla ENTERA, así que deshacer un cambio a mano vuelve a apagar el
    /// botón — que es lo que «no hay nada que guardar» significa.
    /// </para>
    /// </summary>
    [ObservableProperty] private bool _isDirty;

    /// <summary>La tabla tal cual está, en una cadena. Dos huellas iguales son dos tablas iguales.</summary>
    private string Fingerprint() => string.Join(
        "|",
        Rows.Select(r => string.Join(
            ";",
            r.Model, r.Provider, r.Input, r.Output, r.CachedInput, r.CacheWrite, r.Note, r.EffectiveFrom))
            .Append(Source));

    /// <summary>Vuelve a comparar con lo guardado. La llama cualquier cambio de la tabla.</summary>
    private void Recheck() => IsDirty = _savedFingerprint is not null && Fingerprint() != _savedFingerprint;

    private string? _savedFingerprint;

    private void Watch()
    {
        Rows.CollectionChanged -= OnRowsChanged;
        Rows.CollectionChanged += OnRowsChanged;
        foreach (RateRow row in Rows)
        {
            row.PropertyChanged -= OnRowChanged;
            row.PropertyChanged += OnRowChanged;
        }
    }

    private void OnRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        Watch();
        Recheck();
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Recheck();

    private void Load()
    {
        Rows.Clear();

        // R2 §2 — aquí había un `EnsureSeeded()`: abrir esta pantalla era lo ÚNICO que llegaba a
        // escribir `model-rates.json`, así que hasta que alguien la visitaba el hub no tenía tabla y
        // las sesiones salían con «tarifa no configurada». La siembra la hace ahora la aplicación al
        // abrir el hub, sin preguntar; esta pantalla solo LEE lo que hay y lo deja corregir.
        ModelRateTable? table = _rates.Current;
        if (table is null)
        {
            // Sin hub todavía —o con el fichero ilegible— no se inventa una tabla en memoria que
            // guardar pisaría el día que el hub aparezca.
            Status = "Todavía no hay tabla de tarifas en el hub. Se siembra sola al conectar con él.";
            RefreshMissing();
            return;
        }

        Source = table.Source ?? string.Empty;

        // TODAS las tarifas de la tabla (PROV-2 §3). Aquí se filtraban las de la casa que no
        // facturaba, porque una tarifa suya no gobernaba nada: con el coste tarifado por
        // proveedor+modelo, la de cualquier casa gobierna lo suyo, y esconder la que alguien haya
        // escrito sería justo lo contrario de lo que hace falta.
        foreach (ModelRate rate in table.Rates
                     .OrderBy(r => r.Provider, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(r => r.Model, StringComparer.OrdinalIgnoreCase))
        {
            Rows.Add(new RateRow(rate));
        }

        RefreshMissing();
        _savedFingerprint = Fingerprint();
        Watch();
        Recheck();
    }

    /// <summary>
    /// F29 §1 — aquí se montaba <c>MissingModels</c>, la lista ámbar de «modelos usados sin
    /// tarifa». Se retira con su bloque: desde esta pantalla no se puede reconciliar nada, y un
    /// aviso sobre el que no se puede actuar se aprende a ignorar. Lo que queda es el recuento, en
    /// una línea neutra, y el camino hasta donde sí se actúa.
    /// </summary>
    private void RefreshMissing()
    {
        IReadOnlyList<AppCostGap> gaps = _gaps?.Gaps() ?? Array.Empty<AppCostGap>();
        _appsWithoutCost = gaps.Count;
        _sessionsWithoutCost = gaps.Sum(g => g.Sessions);

        OnPropertyChanged(nameof(HasMissing));
        OnPropertyChanged(nameof(MissingLine));
    }

    [RelayCommand]
    private void AddRow() => Rows.Add(new RateRow
    {
        // Nace con la casa de fábrica escrita (PROV-2 §3): es de quien son casi todas las tarifas
        // de esta tabla, y la columna es obligatoria. Se puede cambiar; lo que no se puede es
        // dejarla en blanco.
        Provider = _rates.FactoryProviderId,
        EffectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow),
    });

    [RelayCommand]
    private void RemoveRow(RateRow? row)
    {
        if (row is not null)
        {
            Rows.Remove(row);
        }
    }

    /// <summary>
    /// Guarda, validando antes y DICIENDO qué se rechaza. Una tarifa sin sentido es peor que
    /// ninguna: se colaría en los agregados como si fuera un dato.
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        Saved = false;

        var rates = new List<ModelRate>();
        foreach (RateRow row in Rows)
        {
            if (row.ToRate() is not { } rate)
            {
                continue;   // fila en blanco: se ignora, no es un error
            }

            if (rate.InputPerMillion < 0 || rate.OutputPerMillion < 0
                || rate.CachedInputPerMillion < 0 || rate.CacheWritePerMillion < 0)
            {
                Status = $"«{rate.Model}»: una tarifa no puede ser negativa. No se ha guardado nada.";
                return;
            }

            // PROV-2 §3 — LA PUERTA QUE SE CIERRA ES LA OTRA. Aquí se rechazaba una tarifa cuya
            // casa no facturaba; ahora la de cualquier casa es legítima y lo que no se puede es
            // dejarla sin dueño: el coste se tarifa por proveedor + modelo, y una tarifa sin
            // proveedor es una que no se sabe a quién le cobra.
            if (!rate.IsProviderSpecific)
            {
                Status = $"«{rate.Model}»: falta el proveedor. Una tarifa dice a qué casa le cobra "
                    + "—el coste se calcula por proveedor y modelo—, así que la columna no puede "
                    + "quedar en blanco. No se ha guardado nada.";
                return;
            }

            if (rates.Any(r => r.Matches(rate.Model, rate.Provider) && r.IsProviderSpecific == rate.IsProviderSpecific))
            {
                Status = $"«{rate.Model}» está repetido para el mismo proveedor. No se ha guardado nada.";
                return;
            }

            rates.Add(rate);
        }

        if (rates.Count == 0)
        {
            Status = "La tabla se quedaría vacía: sin tarifas, ningún coste se puede calcular.";
            return;
        }

        _rates.Save(new ModelRateTable
        {
            Source = string.IsNullOrWhiteSpace(Source) ? null : Source.Trim(),
            ReviewedOn = DateOnly.FromDateTime(DateTime.UtcNow),
            Rates = rates,
        });

        Saved = true;
        Status = $"Guardadas {rates.Count} tarifas. Se aplican al recalcular: los costes que ya se "
            + "enseñan salen de estos números, así que cambian en cuanto se recarga Métricas.";
        _savedFingerprint = Fingerprint();
        Recheck();
        RefreshMissing();
    }
}
