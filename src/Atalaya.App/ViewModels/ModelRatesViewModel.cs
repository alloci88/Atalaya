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
            Input,
            Output,
            CachedInput,
            write,
            string.IsNullOrWhiteSpace(Provider) ? null : Provider.Trim(),
            EffectiveFrom ?? DateOnly.FromDateTime(DateTime.UtcNow),
            string.IsNullOrWhiteSpace(Note) ? null : Note.Trim());
    }
}

/// <summary>
/// La pantalla de tarifas por modelo (F15).
/// <para>
/// <b>Vive en Métricas y no en Ajustes</b>, por el mismo argumento que llevó los umbrales al
/// Inventario (D-770): se edita donde se ve la consecuencia. Y es de la ORGANIZACIÓN —se escribe en
/// el hub y la ve todo el equipo—, no una preferencia de máquina: por la regla de F13, lo que
/// gobierna un resultado compartido se gobierna con ajuste compartido.
/// </para>
/// <para>
/// El commit del hub es la atribución de quién cambió qué tarifa y cuándo, así que no hay ningún
/// campo «modificado por» que mantener.
/// </para>
/// </summary>
public sealed partial class ModelRatesViewModel : ObservableObject
{
    private readonly ModelRatesService _rates;

    public ModelRatesViewModel(ModelRatesService rates)
    {
        _rates = rates;
        Load();
    }

    public ObservableCollection<RateRow> Rows { get; } = new();

    /// <summary>
    /// Los modelos que APARECEN en las sesiones del hub y no tienen tarifa. Es lo que convierte
    /// esta pantalla en accionable: sin la lista, un modelo nuevo se traduce en agregados parciales
    /// y nadie sabe qué añadir.
    /// </summary>
    public ObservableCollection<string> MissingModels { get; } = new();

    [ObservableProperty] private string _status = string.Empty;

    [ObservableProperty] private string _source = string.Empty;

    [ObservableProperty] private bool _saved;

    /// <summary>Hay modelos usados sin tarifa: los agregados que los incluyan son parciales.</summary>
    public bool HasMissing => MissingModels.Count > 0;

    private void Load()
    {
        Rows.Clear();
        ModelRateTable table = _rates.EnsureSeeded();
        Source = table.Source ?? string.Empty;

        foreach (ModelRate rate in table.Rates.OrderBy(r => r.Model, StringComparer.OrdinalIgnoreCase))
        {
            Rows.Add(new RateRow(rate));
        }

        RefreshMissing();
    }

    private void RefreshMissing()
    {
        MissingModels.Clear();
        foreach ((string model, string? provider, int sessions) in _rates.ModelsWithoutRate())
        {
            MissingModels.Add(provider is { Length: > 0 }
                ? $"{model} ({provider}) · {sessions} sesión(es)"
                : $"{model} · {sessions} sesión(es)");
        }

        OnPropertyChanged(nameof(HasMissing));
    }

    [RelayCommand]
    private void AddRow() => Rows.Add(new RateRow { EffectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow) });

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
        RefreshMissing();
    }
}
