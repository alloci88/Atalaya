using System.Diagnostics;
using Atalaya.App.Services;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>
/// «Acerca de» (F6.4 §3), <b>como página del raíl desde F26 §C</b>.
/// <para>
/// <b>Por qué deja de ser un diálogo.</b> Era un modal que se abría desde el fondo de Ajustes →
/// Avanzado: para leer la versión había que entrar en Ajustes, elegir una sección, bajar y abrir
/// una ventana encima. Y no es un ajuste — no se edita nada ahí dentro—, así que estaba escondido
/// en el único sitio de la aplicación donde nadie lo buscaría. Ahora es una entrada del grupo
/// **Sistema**, debajo de Ajustes, con su miga como cualquier otra página.
/// </para>
/// <para>
/// El contenido no cambia: es el mismo <see cref="AboutInfo"/> de siempre —icono, versión real,
/// organización con su firma y los dos enlaces—, y las reglas de identidad (F6.4) siguen valiendo
/// tal cual: el logotipo aparece aquí y en Cuenta, en ningún sitio más.
/// </para>
/// </summary>
public sealed partial class AboutViewModel : ViewModelBase
{
    private readonly HubContext _hub;
    private readonly DeployConfig? _deploy;

    public AboutViewModel(HubContext hub, DeployConfig? deploy = null)
    {
        _hub = hub;
        _deploy = deploy;
        Info = AboutInfo.Create(hub, deploy);
    }

    public override string Title => "Acerca de";

    /// <summary>F26 §A: qué entrada del raíl se resalta mientras esta página está delante.</summary>
    public override string RailKey => "about";

    /// <summary>
    /// Lo que la página enseña. Se recalcula en cada carga porque la organización llega del hub y
    /// puede no estar todavía la primera vez que se abre la aplicación.
    /// </summary>
    public AboutInfo Info { get; private set; }

    public override Task LoadAsync()
    {
        Info = AboutInfo.Create(_hub, _deploy);
        OnPropertyChanged(nameof(Info));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Los enlaces salen FUERA, al navegador. Es lo mismo que hace el visor de informes: un enlace
    /// a GitHub no es una página de Atalaya, y abrirlo dentro obligaría a traer un navegador.
    /// </summary>
    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Un navegador que no abre no puede tumbar la página: el enlace se queda ahí, con su
            // URL a la vista en el tooltip, para copiarla a mano.
        }
    }
}
