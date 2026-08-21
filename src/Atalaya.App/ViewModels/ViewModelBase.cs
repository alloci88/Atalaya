using CommunityToolkit.Mvvm.ComponentModel;

namespace Atalaya.App.ViewModels;

/// <summary>Base for page view-models. Provides a busy flag and an async load hook.</summary>
public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>A short human title shown in the shell header.</summary>
    public virtual string Title => GetType().Name;

    /// <summary>Loads/refreshes the page's data. Called on navigation and after hub changes.</summary>
    public virtual Task LoadAsync() => Task.CompletedTask;
}
