using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Atalaya.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>Un dato de la ficha de «Acerca de»: su nombre y su valor.</summary>
public sealed record AboutFact(string Label, string Value);

/// <summary>
/// «Acerca de» (F6.4 §3), <b>como página del raíl desde F26 §C</b>.
/// <para>
/// <b>Por qué deja de ser un diálogo.</b> Era un modal que se abría desde el fondo de Ajustes →
/// Avanzado: para leer la versión había que entrar en Ajustes, elegir una sección, bajar y abrir
/// una ventana encima. Y no es un ajuste — no se edita nada ahí dentro—, así que estaba escondido
/// en el único sitio de la aplicación donde nadie lo buscaría. Ahora es una entrada del grupo
/// <b>Sistema</b>, debajo de Ajustes, con su miga como cualquier otra página.
/// </para>
/// <para>
/// <b>Y como página tiene que ganarse la pantalla</b> (segunda revisión). Con el contenido del
/// diálogo —icono, versión, logo y dos enlaces— eran cuatro líneas en una pantalla entera. Lo que
/// se añade no es relleno: es lo que se viene a buscar aquí cuando algo va raro —qué binario estoy
/// ejecutando, de qué commit, de qué día, contra qué proveedor y con qué modelo— y las acciones que
/// lo acompañan, empezando por comprobar si hay una versión nueva.
/// </para>
/// </summary>
public sealed partial class AboutViewModel : ViewModelBase
{
    private readonly HubContext _hub;
    private readonly DeployConfig? _deploy;
    private readonly UpdateCheckService? _updates;
    private readonly AuditorProviderRegistry? _providers;
    private readonly SettingsService? _settings;

    public AboutViewModel(
        HubContext hub,
        DeployConfig? deploy = null,
        UpdateCheckService? updates = null,
        AuditorProviderRegistry? providers = null,
        SettingsService? settings = null)
    {
        _hub = hub;
        _deploy = deploy;
        _updates = updates;
        _providers = providers;
        _settings = settings;
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

    /// <summary>
    /// La ficha de datos: versión, commit, canal, fecha del binario, proveedor y modelo.
    /// <para>
    /// Es una lista y no seis propiedades sueltas porque lo que la vista pinta es una REJILLA de
    /// nombre y valor: con seis propiedades habría seis filas escritas a mano y la séptima nacería
    /// con otro margen.
    /// </para>
    /// </summary>
    public IReadOnlyList<AboutFact> Facts { get; private set; } = Array.Empty<AboutFact>();

    /// <summary>
    /// Cómo fue la última comprobación de versión, dicho aquí. Vacío hasta que alguien la pide: una
    /// página no consulta a GitHub por abrirse.
    /// </summary>
    [ObservableProperty] private string _updateStatus = string.Empty;

    /// <summary>
    /// Las notas de la versión. Sin repositorio configurado no hay adónde ir, y entonces el botón
    /// no se pinta — un enlace a un 404 es peor que ninguno (BUGFIX-VERSION).
    /// </summary>
    public string? Notes => Info.HasRepository ? $"{Info.Repository}/releases" : null;

    public bool HasNotes => Notes is not null;

    /// <summary>Se puede preguntar por versiones nuevas: hay servicio detrás.</summary>
    public bool CanCheckUpdates => _updates is not null;

    public override Task LoadAsync()
    {
        Info = AboutInfo.Create(_hub, _deploy);
        Facts = BuildFacts();
        OnPropertyChanged(nameof(Info));
        OnPropertyChanged(nameof(Facts));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(HasNotes));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Los seis datos, saltándose los que no se saben. Nada se inventa: sin registro de proveedores
    /// no se escribe «Copilot» por defecto, se omite la fila (D-318).
    /// </summary>
    private List<AboutFact> BuildFacts()
    {
        string raw = Info.Version;
        int plus = raw.IndexOf('+');

        var facts = new List<AboutFact>
        {
            new("Versión", plus < 0 ? raw : raw[..plus]),
            new("Canal", Info.IsDevelopmentBuild ? "build local" : "release"),
        };

        if (plus >= 0 && plus < raw.Length - 1)
        {
            facts.Add(new AboutFact("Commit", raw[(plus + 1)..]));
        }

        if (BuildDate() is { } built)
        {
            facts.Add(new AboutFact("Fecha del binario", built.ToString("d MMM yyyy · HH:mm", AppCulture.Display)));
        }

        if (_providers?.Current is { } provider)
        {
            facts.Add(new AboutFact("Proveedor", provider.ProviderName));
            if (_settings?.ModelFor(provider.ProviderId) is { Length: > 0 } model)
            {
                facts.Add(new AboutFact("Modelo", model));
            }
        }

        if (Info.HasOrganization)
        {
            facts.Add(new AboutFact("Organización", Info.Organization!));
        }

        return facts;
    }

    /// <summary>
    /// Cuándo se construyó este binario. Sale de la fecha del fichero y NO de la marca de tiempo
    /// del PE: los builds de .NET son deterministas, así que esa marca es un hash y no una fecha —
    /// enseñarla sería enseñar un número con formato de día que no lo es.
    /// </summary>
    private static DateTime? BuildDate()
    {
        try
        {
            string path = typeof(AboutViewModel).Assembly.Location;
            return path.Length == 0 ? null : File.GetLastWriteTime(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Pregunta si hay versión nueva, ahora y a la fuerza — saltándose el suelo anti-bucle, porque
    /// lo ha pedido una persona.
    /// <para>
    /// <b>Y contesta AQUÍ, no en el banner de la carcasa.</b> El banner es el que ofrece instalar y
    /// lo levanta el arranque; esta página contesta a «¿estoy al día?», que es otra pregunta y la
    /// que se hace quien abre «Acerca de».
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task CheckUpdates()
    {
        if (_updates is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        UpdateStatus = "Comprobando…";
        try
        {
            UpdateAvailability result = await _updates.CheckAsync(CancellationToken.None, force: true);
            UpdateStatus = result.HasUpdate ? result.Headline : "Estás en la última versión.";
        }
        catch (Exception ex)
        {
            UpdateStatus = $"No se pudo comprobar: {ex.Message.Trim()}";
        }
        finally
        {
            IsBusy = false;
        }
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
