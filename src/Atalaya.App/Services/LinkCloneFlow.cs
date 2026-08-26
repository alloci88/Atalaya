using Atalaya.App.ViewModels;
using Atalaya.App.Views;
using Atalaya.Domain.Model;

namespace Atalaya.App.Services;

/// <summary>
/// Abrir «Vincular clon local…» desde donde haga falta (F5.8 §2 y §3).
/// <para>
/// El gesto se ofrece en tres sitios —la tarjeta del portafolio, la barra de solo-lectura del
/// inventario y el tooltip de una acción deshabilitada—, y los tres tienen que abrir EL MISMO
/// diálogo, con el mismo estado de partida y contando el mismo desenlace. Este es ese único sitio.
/// </para>
/// </summary>
public sealed class LinkCloneFlow
{
    private readonly HubContext _hub;
    private readonly CloneLinkService _links;
    private readonly InventoryRescanService _rescan;
    private readonly IFolderPicker _picker;
    private readonly ILinkCloneDialog _dialog;
    private readonly ToastCenter _toasts;

    public LinkCloneFlow(
        HubContext hub,
        CloneLinkService links,
        InventoryRescanService rescan,
        IFolderPicker picker,
        ILinkCloneDialog dialog,
        ToastCenter toasts)
    {
        _hub = hub;
        _links = links;
        _rescan = rescan;
        _picker = picker;
        _dialog = dialog;
        _toasts = toasts;
    }

    /// <summary>
    /// Abre el diálogo para una app y devuelve el estado de vinculación resultante — que es lo
    /// que la tarjeta y la barra necesitan para repintar el piloto sin recargar el hub entero.
    /// </summary>
    public CloneLink Run(string slug)
    {
        AppConfig? app = _hub.Store.TryReadApp(slug);
        if (app is null)
        {
            _toasts.Show($"«{slug}» ya no está en el hub.");
            return CloneLink.Unknown(slug);
        }

        var viewModel = new LinkCloneViewModel(app, _links.For(app), _links, _rescan, _picker);
        _dialog.Show(viewModel);

        if (viewModel.Linked && viewModel.Outcome.Length > 0)
        {
            _toasts.Show(viewModel.Outcome);
        }

        return _links.For(slug);
    }
}
