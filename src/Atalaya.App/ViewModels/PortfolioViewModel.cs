using System.Collections.ObjectModel;
using Atalaya.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>V1 Portfolio (§8): computed cards per app, criticals first.</summary>
public sealed partial class PortfolioViewModel : ViewModelBase
{
    private readonly PortfolioQuery _query;
    private readonly NavigationService _navigation;

    public PortfolioViewModel(PortfolioQuery query, NavigationService navigation)
    {
        _query = query;
        _navigation = navigation;
    }

    public override string Title => "Portafolio";

    public ObservableCollection<AppCard> Apps { get; } = new();

    [ObservableProperty]
    private bool _isEmpty;

    public override async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var cards = await Task.Run(_query.BuildAll);
            Apps.Clear();
            foreach (AppCard card in cards)
            {
                Apps.Add(card);
            }

            IsEmpty = Apps.Count == 0;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task OpenApp(AppCard? card)
        => card is null
            ? Task.CompletedTask
            : _navigation.NavigateToAsync<InventoryViewModel>(vm => vm.SetApp(card.Slug));

    [RelayCommand]
    private Task NewApp() => _navigation.NavigateToAsync<OnboardingViewModel>();
}
