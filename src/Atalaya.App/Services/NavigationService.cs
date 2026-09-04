using Atalaya.App.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace Atalaya.App.Services;

/// <summary>
/// Minimal VM-first navigation: resolves a page view-model from DI, runs its optional init,
/// loads it, and exposes it as <see cref="Current"/>. The shell binds a ContentControl to it,
/// with DataTemplates mapping VM → View.
/// <para>
/// <b>F26 Parte A — y ahora recuerda por dónde has pasado (D-952).</b> Cada navegación apila la
/// página que dejas, y <see cref="GoBackAsync"/> la devuelve <b>tal cual estaba</b>: la MISMA
/// instancia, con su filtro, su selección y su desplazamiento puestos. No se resuelve otra del
/// contenedor, que es justo lo que hacía que volver a Hallazgos enseñara todos los hallazgos de
/// todas las aplicaciones —una página nueva no tiene por qué parecerse a la que dejaste—.
/// </para>
/// <para>
/// La pila tiene tope (<see cref="HistoryLimit"/>). Un historial sin límite en una aplicación que
/// se deja abierta toda la semana mantiene vivos view-models que ya nadie va a mirar, con sus
/// suscripciones al hub dentro.
/// </para>
/// </summary>
public sealed partial class NavigationService : ObservableObject
{
    /// <summary>Cuántos pasos atrás se recuerdan. Más allá, «volver» ya no es un gesto: es buscar.</summary>
    public const int HistoryLimit = 20;

    private readonly IServiceProvider _services;
    private readonly List<ViewModelBase> _history = new();

    public NavigationService(IServiceProvider services) => _services = services;

    [ObservableProperty]
    private ViewModelBase? _current;

    /// <summary>True cuando hay un paso que deshacer.</summary>
    public bool CanGoBack => _history.Count > 0;

    /// <summary>Se dispara al cambiar <see cref="Current"/> o la pila, para que la carcasa repinte.</summary>
    public event EventHandler? Navigated;

    public async Task<T> NavigateToAsync<T>(Action<T>? init = null) where T : ViewModelBase
    {
        var vm = _services.GetRequiredService<T>();
        init?.Invoke(vm);
        Push(Current);
        Current = vm;
        // DOS avisos, y los dos hacen falta. El primero para que la carcasa pinte YA la página
        // nueva —el raíl y la miga no pueden esperar a que el hub conteste—; el segundo porque
        // hasta después de cargar la página no se sabe de qué APLICACIÓN es: el filtro que trae
        // `SetApp` no se aplica hasta `LoadAsync`, y sin el segundo aviso el raíl se quedaría sin
        // el grupo de la aplicación en la que acabas de entrar.
        Raise();
        await vm.LoadAsync();
        Raise();
        return vm;
    }

    /// <summary>
    /// Vuelve a la página anterior sin reconstruirla. Se le pide <c>LoadAsync</c> —los datos pueden
    /// haber cambiado mientras estabas fuera— pero el estado de la VISTA es el que dejaste, porque
    /// el objeto es el que dejaste.
    /// </summary>
    public async Task GoBackAsync()
    {
        if (_history.Count == 0)
        {
            return;
        }

        var previous = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        Current = previous;
        Raise();
        await previous.LoadAsync();
        Raise();
    }

    /// <summary>
    /// Navega a una página que YA existe: la del historial si está, y si no una nueva. Es lo que
    /// usa el raíl para que pulsar «Hallazgos» te devuelva TUS hallazgos y no una lista virgen.
    /// </summary>
    public async Task<T> NavigateOrResumeAsync<T>(Action<T>? init = null) where T : ViewModelBase
    {
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i] is T existing)
            {
                _history.RemoveAt(i);
                init?.Invoke(existing);
                Push(Current);
                Current = existing;
                Raise();
                await existing.LoadAsync();
                Raise();
                return existing;
            }
        }

        return await NavigateToAsync(init);
    }

    /// <summary>Olvida el camino andado. Lo usa el arranque, que no viene de ningún sitio.</summary>
    public void ResetHistory()
    {
        _history.Clear();
        Raise();
    }

    private void Push(ViewModelBase? page)
    {
        if (page is null)
        {
            return;
        }

        // Volver a la misma página no es un paso: si lo fuera, «atrás» te dejaría donde estabas y
        // habría que pulsarlo dos veces para que pasara algo.
        if (_history.Count > 0 && ReferenceEquals(_history[^1], page))
        {
            return;
        }

        _history.Add(page);
        if (_history.Count > HistoryLimit)
        {
            _history.RemoveAt(0);
        }
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(CanGoBack));
        Navigated?.Invoke(this, EventArgs.Empty);
    }
}
