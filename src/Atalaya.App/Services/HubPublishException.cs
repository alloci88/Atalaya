namespace Atalaya.App.Services;

/// <summary>
/// <b>El hub no aceptó lo que había que publicar, y la sesión no puede seguir</b> (BUGFIX-PUSH).
/// <para>
/// <b>Por qué tiene tipo propio.</b> Para que el pie diga el motivo tal cual y no lo envuelva en
/// «la sesión se ha interrumpido por un error», que es la frase de lo que nadie supo clasificar.
/// «No se pudo publicar en el hub en 30 s» se entiende, dice qué hacer —mirar la red o la cuenta— y
/// no se parece a un fallo del proveedor, que es la otra familia de fallos de esta pantalla.
/// </para>
/// <para>
/// Se lanza solo donde publicar es la condición para continuar: las reservas del arranque, que son
/// lo que impide que dos máquinas auditen la misma unidad. El push del cierre <b>no</b> la lanza —
/// ahí ya hay trabajo guardado y con informe, y perderlo porque el hub no contesta sería el peor de
/// los dos males.
/// </para>
/// </summary>
public sealed class HubPublishException : Exception
{
    public HubPublishException(string message)
        : base(message)
    {
    }
}
