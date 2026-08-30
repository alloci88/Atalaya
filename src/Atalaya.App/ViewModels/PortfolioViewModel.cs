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

    /// <summary>F5.8 §1: si esta máquina puede auditar cada app. El piloto de la tarjeta.</summary>
    private readonly CloneLinkService _links;

    /// <summary>F5.8 §2: el diálogo que apaga el piloto, compartido con el inventario.</summary>
    private readonly LinkCloneFlow _linkFlow;

    /// <summary>F9 §5: cuánta deuda nueva puede haber entrado desde la última auditoría.</summary>
    private readonly DriftQuery _drift;

    public PortfolioViewModel(
        PortfolioQuery query,
        NavigationService navigation,
        AppDeletionService deletion,
        IDeleteAppConfirmer confirmer,
        LiveSessionService live,
        HubContext hub,
        ToastCenter toasts,
        CloneLinkService links,
        LinkCloneFlow linkFlow,
        DriftQuery drift)
    {
        _drift = drift;
        _query = query;
        _navigation = navigation;
        _deletion = deletion;
        _confirmer = confirmer;
        _live = live;
        _hub = hub;
        _toasts = toasts;
        _links = links;
        _linkFlow = linkFlow;
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
            // El piloto se recalcula AQUÍ, no se cachea: `LoadAsync` es lo que corre al arrancar,
            // al sincronizar y al volver la ventana al primer plano (F5.8 §1), que son los tres
            // momentos en los que la carpeta ha podido moverse a espaldas de la aplicación.
            var cards = await Task.Run(() => _query.BuildAll()
                .Select(c => c with { Link = _links.For(c.Slug) })
                .ToList());

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

        // La deriva llega DESPUÉS y sin bloquear (F9 §5): las tarjetas se pintan enteras con lo que
        // sale del hub, y el indicador aparece cuando el historial del clon lo permite. Esperarla
        // dejaría el portafolio en blanco por un dato que es un extra, no la vista.
        await RefreshDriftAsync();
    }

    /// <summary>
    /// Rellena el indicador de deriva de cada tarjeta. Una app sin clon vinculado en esta máquina
    /// se queda con <c>null</c> y lo DICE: «vincula tu clon para ver la deriva», nunca un cero.
    /// </summary>
    private async Task RefreshDriftAsync()
    {
        var cards = Apps.ToList();
        foreach (AppCard card in cards)
        {
            if (!card.Link.CanAudit)
            {
                continue;
            }

            string slug = card.Slug;
            string? clone = card.Link.Path;
            AppDrift drift = await Task.Run(() => _drift.For(slug, clone));

            int index = Apps.IndexOf(card);
            if (index >= 0 && drift.Problem is null)
            {
                Apps[index] = Apps[index] with
                {
                    ChangedUnits = drift.Changed,
                    FixedPendingVerify = drift.FixedPendingVerify,
                };
            }
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

    /// <summary>
    /// El indicador de deriva es CLICABLE y lleva al Inventario con el filtro ya puesto (F9 §5). Es
    /// lo que convierte el número en un gesto: se ve «12 clases cambiadas» y se está a un clic de
    /// verlas, en vez de a un clic y una búsqueda.
    /// </summary>
    [RelayCommand]
    private Task ShowDrift(AppCard? card)
        => card is null || !card.DriftIsActionable
            ? Task.CompletedTask
            : _navigation.NavigateToAsync<InventoryViewModel>(vm =>
            {
                vm.SetApp(card.Slug);
                vm.DriftFilter = card.ChangedUnits is > 0 ? 1 : 2;
            });

    [RelayCommand]
    private Task NewApp() => _navigation.NavigateToAsync<OnboardingViewModel>();

    /// <summary>
    /// «Vincular clon local…» / «Reparar vínculo…» (F5.8 §2). Al volver, la tarjeta se repinta con
    /// el estado que devuelva el flujo: si quedó vinculada, el piloto pasa a verde en el acto —
    /// esperar al siguiente sondeo dejaría el botón pidiendo lo que ya está hecho.
    /// </summary>
    [RelayCommand]
    private void LinkClone(AppCard? card)
    {
        if (card is null)
        {
            return;
        }

        CloneLink link = _linkFlow.Run(card.Slug);
        int index = Apps.IndexOf(card);
        if (index >= 0)
        {
            Apps[index] = card with { Link = link };
        }
    }

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
