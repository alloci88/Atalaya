using System.Collections.ObjectModel;
using Atalaya.App.Services;
using Atalaya.App.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>V1 Portfolio (§8): computed cards per app, criticals first.</summary>
public sealed partial class PortfolioViewModel : ViewModelBase
{
    private readonly PortfolioQuery _query;
    private readonly NavigationService _navigation;
    private readonly AppDeletionService _deletion;
    private readonly IDeleteAppConfirmer _confirmer;
    private readonly LiveSessionService _live;
    private readonly HubContext _hub;
    private readonly ToastCenter _toasts;

    public PortfolioViewModel(
        PortfolioQuery query,
        NavigationService navigation,
        AppDeletionService deletion,
        IDeleteAppConfirmer confirmer,
        LiveSessionService live,
        HubContext hub,
        ToastCenter toasts)
    {
        _query = query;
        _navigation = navigation;
        _deletion = deletion;
        _confirmer = confirmer;
        _live = live;
        _hub = hub;
        _toasts = toasts;
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
                Apps.Add(WithLocalSession(card));
            }

            IsEmpty = Apps.Count == 0;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// La consulta deduce «auditando ahora» de los claims publicados, que es lo que ve el equipo.
    /// La sesión de ESTA máquina se conoce antes que sus claims —hay un instante entre lanzarla y
    /// publicarlos—, así que se añade aquí: sin esto, la papelera quedaría habilitada justo en la
    /// ventana en la que peor sienta pulsarla.
    /// </summary>
    private AppCard WithLocalSession(AppCard card)
        => _live.IsRunning && _live.AppSlug == card.Slug && !card.AuditingNow
            ? card with { AuditingNow = true }
            : card;

    [RelayCommand]
    private Task OpenApp(AppCard? card)
        => card is null
            ? Task.CompletedTask
            : _navigation.NavigateToAsync<InventoryViewModel>(vm => vm.SetApp(card.Slug));

    [RelayCommand]
    private Task NewApp() => _navigation.NavigateToAsync<OnboardingViewModel>();

    /// <summary>
    /// Hard-reset de una aplicación (F5.3 §4): confirmación fuerte —hay que escribir el nombre— y,
    /// si se confirma, borrado de <c>apps/{slug}/</c> en el hub, limpieza del estado local y push.
    /// <para>
    /// La comprobación de <see cref="AppCard.CanDelete"/> se repite aquí aunque el icono ya esté
    /// deshabilitado: un botón gris es una cortesía de la vista, no una garantía del modelo.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task DeleteApp(AppCard? card)
    {
        if (card is null)
        {
            return;
        }

        if (!card.CanDelete)
        {
            _toasts.Show($"«{card.Name}» se está auditando ahora mismo. Detén la sesión primero.");
            return;
        }

        AppDeletionImpact? impact = await Task.Run(() => _deletion.Describe(card.Slug));
        if (impact is null)
        {
            _toasts.Show($"«{card.Name}» ya no está en el hub.");
            await LoadAsync();
            return;
        }

        var confirmation = new DeleteAppConfirmation(impact);
        if (!_confirmer.Confirm(confirmation) || !confirmation.CanDelete)
        {
            return;
        }

        IsBusy = true;
        try
        {
            string by = _hub.ResolveIdentity().Name;
            AppDeletionResult result = await Task.Run(() => _deletion.Delete(card.Slug, by));

            // Desaparece EN EL ACTO: esperar a la recarga dejaría la tarjeta en pantalla
            // apuntando a una app que ya no existe.
            Apps.Remove(card);
            IsEmpty = Apps.Count == 0;
            _toasts.Show(result.Message);
        }
        catch (Exception ex)
        {
            _toasts.Show($"No se pudo eliminar «{card.Name}»: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync();
    }
}
