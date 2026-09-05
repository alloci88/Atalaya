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
/// la gana no es más filas: es la ACCIÓN —«Buscar actualizaciones»— más la identidad del binario.
/// </para>
/// <para>
/// <b>Y en la tercera revisión la ficha adelgazó otra vez.</b> Ganarse la pantalla no era llenarla:
/// canal, commit, proveedor, modelo y organización se fueron. Queda lo que solo se puede saber
/// aquí —qué versión estoy ejecutando y de qué día es—; lo demás o lo dice el logotipo o se
/// consulta donde se cambia.
/// </para>
/// </summary>
public sealed partial class AboutViewModel : ViewModelBase
{
    private readonly HubContext _hub;
    private readonly DeployConfig? _deploy;
    private readonly UpdateCheckService? _updates;

    public AboutViewModel(
        HubContext hub,
        DeployConfig? deploy = null,
        UpdateCheckService? updates = null)
    {
        _hub = hub;
        _deploy = deploy;
        _updates = updates;
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
    /// La ficha de datos: <b>versión y fecha del binario, y nada más</b> (tercera revisión de §C).
    /// <para>
    /// Traía seis filas y sobraban cuatro. <b>Organización</b> la dice el logotipo, que está justo
    /// encima. <b>Proveedor</b> y <b>Modelo</b> son opciones elegidas en Ajustes, no propiedades de
    /// este binario: se cambian sin reinstalar nada y se consultan donde se cambian. Y <b>Canal</b>
    /// y <b>Commit</b> son de quien compila, no de quien usa — un número hexadecimal de ocho
    /// dígitos no le sirve a nadie que no vaya a abrir el repositorio.
    /// </para>
    /// <para>
    /// Sigue siendo una lista y no dos propiedades sueltas: lo que la vista pinta es una rejilla de
    /// nombre y valor, y la tercera fila que vuelva tiene que nacer alineada con las otras.
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
    /// Qué binario es y de cuándo. La versión se corta por el <c>+</c>: lo de detrás son los
    /// metadatos de build —el commit—, que no son parte de la versión (SemVer §10) y que ya no se
    /// enseñan. La fecha se salta si no se sabe: nada se inventa.
    /// <para>
    /// <b>El sufijo sí se queda.</b> Con «Canal» fuera, lo único que distingue un build local de
    /// una release es el <c>-dev</c> del propio número, así que la versión se enseña con él y no
    /// recortada a <c>1.0.3</c>. Y lo que ese sufijo GOBIERNA sigue en pie donde importa:
    /// <c>SelfUpdateService</c> se niega a auto-actualizar un build local.
    /// </para>
    /// </summary>
    private List<AboutFact> BuildFacts()
    {
        string raw = Info.Version;
        int plus = raw.IndexOf('+');

        var facts = new List<AboutFact>
        {
            new("Versión", plus < 0 ? raw : raw[..plus]),
        };

        if (BuildDate() is { } built)
        {
            facts.Add(new AboutFact("Fecha del binario", built.ToString("d MMM yyyy · HH:mm", AppCulture.Display)));
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
