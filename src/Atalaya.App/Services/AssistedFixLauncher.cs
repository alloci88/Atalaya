using Atalaya.Domain.Model;
using Atalaya.Inventory;

namespace Atalaya.App.Services;

/// <summary>Por qué un arreglo asistido no puede arrancar. Cada motivo tiene su propio remedio.</summary>
public enum FixBlock
{
    /// <summary>Puede arrancar.</summary>
    Ninguno,

    /// <summary>La función está apagada en Ajustes.</summary>
    Desactivado,

    /// <summary>No hay clon vinculado, o el vínculo está roto.</summary>
    SinClon,

    /// <summary>El árbol de trabajo tiene cambios sin commitear.</summary>
    ArbolSucio,

    /// <summary>Ya hay una sesión de Copilot corriendo.</summary>
    Ocupado,

    /// <summary>El hallazgo no tiene ubicaciones en el código que arreglar.</summary>
    SinUbicaciones,

    /// <summary>El hallazgo pide una verificación antes de volver a encargar un arreglo (F34).</summary>
    VerificacionPendiente,
}

/// <summary>
/// Qué verificación pide el hallazgo (F34, sobre los dos estados que enumeró F33/D-1038).
/// <para>
/// Viaja como PARÁMETRO y no se calcula aquí a propósito: quien sabe si el ancla se ha perdido o
/// si hay un arreglo sin veredicto es la ficha —lo lee del panel del snippet y del historial—, y
/// esta pieza solo mira el clon. Traerlo hecho evita una segunda copia de la regla de D-557.
/// </para>
/// </summary>
public enum PendingVerification
{
    /// <summary>No hay nada que verificar.</summary>
    Ninguna,

    /// <summary>El ancla ya no está donde se detectó (D-225, BUGFIX-ANCLA).</summary>
    AnclaPerdida,

    /// <summary>Hay un arreglo posterior al último veredicto (D-557).</summary>
    ArregloSinVerificar,
}

/// <summary>
/// El veredicto de las precondiciones, con lo que hay que enseñar. Nunca un booleano suelto: los
/// cinco «no» se arreglan de cinco maneras distintas y el usuario tiene que saber cuál le toca.
/// </summary>
/// <param name="OffersLink">El remedio es vincular el clon: la vista puede ofrecer el atajo.</param>
public sealed record FixLaunchDecision(FixBlock Block, string Message, bool OffersLink = false)
{
    public static FixLaunchDecision Ok { get; } = new(FixBlock.Ninguno, string.Empty);

    public bool CanStart => Block == FixBlock.Ninguno;
}

/// <summary>
/// Las precondiciones de «Arreglar con agente» (F6.9 §1), en un sitio y en el orden en el que de
/// verdad fallan.
/// <para>
/// Es una pieza aparte del servicio de la sesión a propósito: la ficha del hallazgo tiene que
/// poder preguntar «¿se puede?» para pintar el botón sin arrancar nada, y el arranque tiene que
/// volver a preguntarlo por su cuenta —entre pintar el botón y pulsarlo el usuario ha podido
/// tocar el clon—. Un solo método, dos llamantes, ninguna copia de la regla.
/// </para>
/// </summary>
public sealed class AssistedFixLauncher
{
    private readonly SettingsService _settings;
    private readonly CloneLinkService _links;
    private readonly MachineConfigStore _machines;
    private readonly AgentBusyGate _busy;
    private readonly AuditorProviderRegistry? _providers;

    /// <param name="providers">
    /// Con quién se va a arreglar, para poder decirlo ANTES de gastar nada (F16). Es opcional
    /// porque las precondiciones no dependen de ello: quien no lo pase obtiene exactamente el
    /// mismo veredicto, sin la frase que nombra al motor.
    /// </param>
    public AssistedFixLauncher(
        SettingsService settings, CloneLinkService links, MachineConfigStore machines, AgentBusyGate busy,
        AuditorProviderRegistry? providers = null)
    {
        _settings = settings;
        _links = links;
        _machines = machines;
        _busy = busy;
        _providers = providers;
    }

    /// <summary>
    /// Con quién y con qué modelo se arreglaría ahora: «Claude Code, modelo opus» (F16).
    /// <para>
    /// Se dice antes de empezar por el mismo motivo por el que el diálogo de lanzar una auditoría
    /// nombra al juez (D-781): quien va a revisar un diff tiene derecho a saber quién lo escribió,
    /// y descubrirlo leyendo el informe es descubrirlo tarde. Vacío cuando no hay a quién nombrar.
    /// </para>
    /// </summary>
    public string EngineLabel
    {
        get
        {
            IAssistedFixProvider? fixer = _providers?.CurrentFixer;
            if (fixer is null)
            {
                return string.Empty;
            }

            return fixer.ModelName is { Length: > 0 } model
                ? $"{fixer.ProviderName}, modelo {model}"
                : $"{fixer.ProviderName}, con el modelo que elija él";
        }
    }

    /// <summary>La ruta del clon de una app, o null si no hay.</summary>
    public string? ClonePathFor(string slug) => _machines.Load().ClonePathFor(slug);

    /// <param name="ignoreBusy">
    /// Lo llama quien YA tiene el cerrojo. La sesión vuelve a mirar las precondiciones justo antes
    /// de arrancar —entre pintar el botón y pulsarlo el usuario ha podido tocar el clon—, y sin
    /// esto se encontraría a sí misma ocupando el agente y se negaría a empezar.
    /// </param>
    /// <param name="pending">
    /// Lo que el hallazgo pide verificar (F34). <b>No se arregla lo que no está verificado</b>: un
    /// agente que escribe sobre un ancla perdida edita una línea que ya no es la del hallazgo, y
    /// encargar un segundo arreglo sin saber si el primero funcionó es apilar trabajo a ciegas
    /// (D-557: arreglar no resuelve). Se mira <b>la última</b>, después del árbol sucio: los
    /// cambios sin commitear son los que bloquean de verdad, y su razón gana.
    /// </param>
    public FixLaunchDecision Check(
        string slug, Finding? finding, bool ignoreBusy = false,
        PendingVerification pending = PendingVerification.Ninguna)
    {
        if (!_settings.Current.EnableAssistedFix)
        {
            return new FixLaunchDecision(FixBlock.Desactivado,
                "El arreglo asistido está desactivado en Ajustes. Actívalo en «Auditoría» para "
                + "poder lanzarlo. El generador de prompt de arreglo sigue disponible.");
        }

        if (finding is null || finding.Locations.Count == 0)
        {
            return new FixLaunchDecision(FixBlock.SinUbicaciones,
                "Este hallazgo no tiene ninguna ubicación en el código, así que no hay nada "
                + "concreto que arreglar. Genera el prompt de arreglo y decide tú por dónde empezar.");
        }

        CloneLink link = _links.For(slug);
        if (!link.CanAudit)
        {
            return new FixLaunchDecision(FixBlock.SinClon, link.Tooltip, OffersLink: true);
        }

        if (!ignoreBusy && _busy.IsBusy)
        {
            return new FixLaunchDecision(FixBlock.Ocupado, _busy.BusyMessage);
        }

        WorkingTreeState tree = WorkingTree.Inspect(link.Path);
        if (!tree.Clean)
        {
            return new FixLaunchDecision(FixBlock.ArbolSucio, tree.Message);
        }

        // F34 — y la última puerta: verificar antes de arreglar. Va aquí y no antes porque las
        // otras cuatro se arreglan tocando el clon o Ajustes; esta se arregla con el botón de al
        // lado, que sigue verde y encendido (F33). El generador de prompt tampoco se apaga: no
        // cuesta nada y sirve para mirarlo a mano.
        return pending switch
        {
            PendingVerification.AnclaPerdida => new FixLaunchDecision(
                FixBlock.VerificacionPendiente,
                "Verifica primero: el ancla se ha perdido. Pulsa «Verificar ahora» para volver a "
                + "situarla antes de que el agente escriba sobre una línea que ya no es la del "
                + "hallazgo."),
            PendingVerification.ArregloSinVerificar => new FixLaunchDecision(
                FixBlock.VerificacionPendiente,
                "Verifica primero: hay un arreglo sin verificar. Pulsa «Verificar ahora» para "
                + "saber si el anterior funcionó antes de encargar otro — arreglar no resuelve."),
            _ => FixLaunchDecision.Ok,
        };
    }
}
