using Atalaya.App.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace Atalaya.App.Services;

/// <summary>
/// Minimal VM-first navigation: resolves a page view-model from DI, runs its optional init,
/// loads it, and exposes it as <see cref="Current"/>. The shell binds a ContentControl to it,
/// with DataTemplates mapping VM → View.
/// </summary>
public sealed partial class NavigationService : ObservableObject
{
    private readonly IServiceProvider _services;

    public NavigationService(IServiceProvider services) => _services = services;

    [ObservableProperty]
    private ViewModelBase? _current;

    public async Task<T> NavigateToAsync<T>(Action<T>? init = null) where T : ViewModelBase
    {
        var vm = _services.GetRequiredService<T>();
        init?.Invoke(vm);
        Current = vm;
        await vm.LoadAsync();
        return vm;
    }
}
