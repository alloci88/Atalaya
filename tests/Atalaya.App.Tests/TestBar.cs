using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.Domain.Model;

namespace Atalaya.App.Tests;

/// <summary>Un view-model del mapa sobre un hub vacío: para leer sus opciones, no sus datos.</summary>
internal static class TestBar
{
    public static HeatmapViewModel ViewModel()
    {
        string root = Path.Combine(Path.GetTempPath(), "atalaya-bar", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(root, "local"));
        var settings = new SettingsService(paths);
        settings.Load();
        HubContext hub = TestFactory.Hub(paths, settings);
        hub.Store.WriteHub(new HubInfo { OrganizationName = "Org" });

        return new HeatmapViewModel(
            new HeatmapQuery(hub),
            new NavigationService(new Empty()),
            settings,
            hub,
            new ToastCenter(),
            new NoSaver());
    }

    private sealed class Empty : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class NoSaver : IFileSaver
    {
        public string? Pick(string title, string suggestedFileName, string filter) => null;
    }
}
