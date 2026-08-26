using Atalaya.App.Services;
using Atalaya.Domain.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>Los dos caminos para tener el código delante (F5.8 §2).</summary>
public enum LinkCloneMode
{
    /// <summary>Ya está clonado en algún sitio: se elige la carpeta y se valida.</summary>
    Existente,

    /// <summary>No está: se clona ahora con la credencial de la cuenta.</summary>
    Clonar,
}

/// <summary>
/// El diálogo «Vincular clon local» (F5.8 §2), como modelo puro: sin ventana, sin
/// <c>Dispatcher</c> y sin selector del sistema —el selector se inyecta—, de modo que el flujo
/// entero (elegir mal, leer el error, elegir bien, clonar, ver la deriva y re-escanear) se prueba
/// con un test.
/// <para>
/// Es el mismo reparto que <c>DeleteAppConfirmation</c>: la regla vive aquí, la vista solo la
/// enlaza. La diferencia es que aquí la regla no es una puerta sino un pequeño flujo, y por eso
/// éste es un view-model y aquélla un objeto de confirmación.
/// </para>
/// </summary>
public sealed partial class LinkCloneViewModel : ObservableObject
{
    private readonly CloneLinkService _links;
    private readonly InventoryRescanService _rescan;
    private readonly IFolderPicker _picker;

    public LinkCloneViewModel(
        AppConfig app,
        CloneLink current,
        CloneLinkService links,
        InventoryRescanService rescan,
        IFolderPicker picker)
    {
        App = app;
        Current = current;
        _links = links;
        _rescan = rescan;
        _picker = picker;

        // Reparar arranca en la ruta que ya estaba registrada: casi siempre está al lado de donde
        // debía, y volver a teclearla entera no ayuda a nadie.
        ExistingPath = current.State == CloneLinkState.Problema ? current.Path ?? string.Empty : string.Empty;
    }

    public AppConfig App { get; }

    /// <summary>El estado con el que se abrió: 🔴 vincular, 🟡 reparar.</summary>
    public CloneLink Current { get; }

    public string Slug => App.Slug;

    public string AppName => App.Name;

    public string RepoUrl => App.RepoUrl;

    public bool IsRepair => Current.State == CloneLinkState.Problema;

    public string Title => IsRepair ? "Reparar vínculo" : "Vincular clon local";

    /// <summary>Por qué se está aquí. En el caso ámbar es el diagnóstico, con la ruta rota.</summary>
    public string Intro => IsRepair
        ? $"{Current.Problem} Vuelve a apuntar a la carpeta correcta, o clona el repo otra vez."
        : $"Para auditar «{AppName}» hace falta su código en esta máquina. "
          + "Elige tu clon si ya lo tienes, o clónalo ahora.";

    /// <summary>Se puede clonar solo si el hub sabe de qué URL. Sin ella, ese camino no existe.</summary>
    public bool CanClone => !string.IsNullOrWhiteSpace(RepoUrl);

    [ObservableProperty]
    private LinkCloneMode _mode = LinkCloneMode.Existente;

    partial void OnModeChanged(LinkCloneMode value)
    {
        OnPropertyChanged(nameof(IsExistingMode));
        OnPropertyChanged(nameof(IsCloneMode));
        Error = string.Empty;
    }

    /// <summary>
    /// Los dos radios, como booleanos. Se exponen así —en vez de con un convertidor de enumeración
    /// en el XAML— porque «qué camino está elegido» es estado del flujo y se prueba como tal.
    /// Marcar uno elige ese camino; desmarcarlo no hace nada: los radios ya se excluyen entre sí.
    /// </summary>
    public bool IsExistingMode
    {
        get => Mode == LinkCloneMode.Existente;
        set
        {
            if (value)
            {
                Mode = LinkCloneMode.Existente;
            }
        }
    }

    /// <inheritdoc cref="IsExistingMode"/>
    public bool IsCloneMode
    {
        get => Mode == LinkCloneMode.Clonar;
        set
        {
            if (value)
            {
                Mode = LinkCloneMode.Clonar;
            }
        }
    }

    // ------------------------------------------------------------------ camino 1: ya lo tengo

    [ObservableProperty]
    private string _existingPath = string.Empty;

    // ------------------------------------------------------------------ camino 2: clonarlo ahora

    /// <summary>La carpeta CONTENEDORA. El clon se crea dentro, con el nombre del repo.</summary>
    [ObservableProperty]
    private string _targetParent = string.Empty;

    /// <summary>Dónde va a quedar el clon, escrito antes de crearlo — no después.</summary>
    public string TargetPreview => string.IsNullOrWhiteSpace(TargetParent)
        ? string.Empty
        : Path.Combine(TargetParent, CloneLinkService.FolderName(App));

    partial void OnTargetParentChanged(string value) => OnPropertyChanged(nameof(TargetPreview));

    // ------------------------------------------------------------------ estado del flujo

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = string.Empty;

    public bool HasError => Error.Length > 0;

    /// <summary>Lo que está pasando ahora mismo: clonar tarda y hay que verlo avanzar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress))]
    private string _progress = string.Empty;

    public bool HasProgress => Progress.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    private bool _isBusy;

    /// <summary>Lo que enlazan los <c>IsEnabled</c>: nada se pulsa dos veces mientras clona.</summary>
    public bool NotBusy => !IsBusy;

    /// <summary>Quedó vinculada: quien abrió el diálogo tiene que recargar y apagar el piloto.</summary>
    [ObservableProperty]
    private bool _linked;

    /// <summary>La ruta que quedó registrada.</summary>
    [ObservableProperty]
    private string _linkedPath = string.Empty;

    /// <summary>
    /// El clon está en otro commit que el inventario vigente (§2). Se dice, con la opción de
    /// re-escanear al lado — y NO se re-escanea solo: el inventario es del equipo, y ponerlo al
    /// día es una decisión, no un efecto secundario de vincular.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDrift))]
    private string _driftWarning = string.Empty;

    public bool HasDrift => DriftWarning.Length > 0;

    /// <summary>El resumen final que se cuenta por toast al cerrar.</summary>
    [ObservableProperty]
    private string _outcome = string.Empty;

    // ------------------------------------------------------------------ acciones

    [RelayCommand]
    private void ChooseExisting()
    {
        string? picked = _picker.Pick(
            $"Elige la carpeta de tu clon de «{AppName}»",
            ExistingPath is { Length: > 0 } p && Directory.Exists(p) ? p : null);

        if (picked is not null)
        {
            ExistingPath = picked;
            Error = string.Empty;
        }
    }

    [RelayCommand]
    private void ChooseTarget()
    {
        string? picked = _picker.Pick(
            $"Elige dónde clonar «{AppName}»",
            TargetParent is { Length: > 0 } p && Directory.Exists(p) ? p : null);

        if (picked is not null)
        {
            TargetParent = picked;
            Error = string.Empty;
        }
    }

    /// <summary>
    /// Vincula la carpeta elegida. La validación es OBLIGATORIA y su fallo es específico: se
    /// enseñan las dos URLs, la de la carpeta y la del repo de la app. Nunca se vincula a ciegas.
    /// </summary>
    [RelayCommand]
    private void LinkExisting()
    {
        Error = string.Empty;
        CloneValidation validation = _links.Link(Slug, ExistingPath);
        if (!validation.Ok)
        {
            Error = validation.Error!;
            return;
        }

        Succeed(Path.GetFullPath(ExistingPath), "Clon local vinculado.");
    }

    /// <summary>Clona con la credencial de la cuenta y vincula al terminar.</summary>
    [RelayCommand]
    private async Task CloneNow()
    {
        Error = string.Empty;
        Progress = string.Empty;
        IsBusy = true;
        try
        {
            var reporter = new Progress<string>(text => Progress = text);
            string parent = TargetParent;
            string path = await Task.Run(() => _links.CloneAndLink(Slug, parent, reporter));
            Succeed(path, $"«{AppName}» clonada en {path} y vinculada.");
        }
        catch (Exception ex)
        {
            Error = $"No se pudo clonar: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            Progress = string.Empty;
        }
    }

    /// <summary>
    /// Pone el inventario al día con lo que hay en el clon recién vinculado. Solo se ofrece
    /// cuando hay deriva, y solo se ejecuta si el usuario lo pide.
    /// </summary>
    [RelayCommand]
    private async Task RescanNow()
    {
        if (LinkedPath.Length == 0)
        {
            return;
        }

        IsBusy = true;
        Progress = "Re-escaneando el inventario…";
        try
        {
            string path = LinkedPath;
            RescanOutcome outcome = await Task.Run(() => _rescan.Rescan(Slug, path));
            DriftWarning = string.Empty;
            Outcome = $"Inventario actualizado: {outcome.Units} unidades."
                + (outcome.Measured.Total > 0 ? $" {outcome.Measured.Summary}." : "");
        }
        catch (Exception ex)
        {
            Error = $"No se pudo re-escanear: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            Progress = string.Empty;
        }
    }

    /// <summary>Lo común a los dos caminos: quedó vinculado, ¿y el inventario cuadra?</summary>
    private void Succeed(string path, string message)
    {
        Linked = true;
        LinkedPath = path;
        Outcome = message;

        InventoryDrift drift = _links.Drift(Slug, path);
        DriftWarning = drift.HasDrift ? drift.Message : string.Empty;
    }
}
