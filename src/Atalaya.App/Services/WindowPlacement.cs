using System.Windows;

namespace Atalaya.App.Services;

/// <summary>
/// Dónde estaba la ventana la última vez (F26 Parte A, D-956).
/// <para>
/// <b>El defecto que lo trae.</b> Atalaya arrancaba siempre a 1340×800 centrada, porque así se
/// desarrolló y así se probó. En un monitor de 1920 eso deja media pantalla sin usar, y como el
/// contenido estaba maquetado para caber en esa ventana pequeña, al maximizar los elementos se
/// apelotonaban arriba a la izquierda dejando huecos enormes. Se arreglan las dos cosas: el
/// contenido, en el resto de F26; el arranque, aquí.
/// </para>
/// <para>
/// <b>Maximizada SIEMPRE, y la geometría guardada es la de restaurar.</b> El principio 1 dice
/// «pantalla completa por defecto» y D-956 lo cumplía solo la primera vez: a partir de la segunda
/// mandaba lo que hubiera quedado guardado, así que bastaba con restaurar la ventana una tarde
/// para que Atalaya abriera pequeña el resto de su vida — que es justo la queja que trajo F26. El
/// arranque es maximizado en todos los arranques. Lo que se recuerda no deja de servir: es el
/// rectángulo al que la ventana vuelve cuando el usuario la restaura, y por eso se sigue
/// guardando, comprobando y aplicando antes de maximizar.
/// </para>
/// </summary>
public sealed class WindowPlacement
{
    /// <summary>
    /// False mientras nadie haya cerrado nunca la ventana en esta máquina. Ya no decide si se
    /// arranca maximizado —eso es siempre—, sino si hay un rectángulo de restauración que aplicar.
    /// </summary>
    public bool Saved { get; set; }

    /// <summary>
    /// Cómo quedó la ventana al cerrarla. <b>El arranque ya no lo mira</b> —se arranca maximizado
    /// siempre—, pero se sigue escribiendo: es un campo del fichero de ajustes, y quitarlo cambia
    /// la forma de un fichero que ya está en las máquinas de la gente.
    /// </summary>
    public bool Maximized { get; set; } = true;

    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    /// <summary>
    /// El raíl plegado a solo iconos (D-963). Va aquí y no en un ajuste aparte porque es lo mismo
    /// que lo de arriba: cómo tienes puesta TU ventana en ESTA máquina.
    /// </summary>
    public bool RailCollapsed { get; set; }

    /// <summary>
    /// True en cuanto has tocado el botón de plegar. A partir de ahí manda lo que tú digas y el
    /// raíl deja de plegarse solo al estrechar la ventana: una preferencia que el programa
    /// deshace en cuanto arrastras un borde no es una preferencia.
    /// </summary>
    public bool RailPinned { get; set; }
}

/// <summary>
/// Aplica y recoge la posición de la ventana. Vive aparte del ajuste porque lo que tiene lógica no
/// es el dato, sino <b>lo que hay que comprobar antes de creérselo</b>.
/// </summary>
public static class WindowPlacementService
{
    /// <summary>Lo mínimo con lo que Atalaya sigue siendo usable (principio 1).</summary>
    public const double MinWidth = 1100;

    /// <summary>Y de alto.</summary>
    public const double MinHeight = 700;

    /// <summary>
    /// Decide con qué geometría arranca la ventana. Devuelve <c>null</c> en la posición cuando hay
    /// que centrarla.
    /// <para>
    /// <b>El estado es Maximized siempre</b>, haya o no algo guardado: es el principio 1 y no una
    /// preferencia. Lo guardado sigue mandando en el TAMAÑO y la POSICIÓN, que es el rectángulo al
    /// que la ventana vuelve al restaurarla.
    /// </para>
    /// <para>
    /// <b>Se comprueba que la posición guardada siga existiendo.</b> Es el caso que rompe estas
    /// funciones en la vida real: se cierra Atalaya en un segundo monitor, se desconecta el
    /// monitor, y al abrirla vuelve a unas coordenadas que ya no están en ninguna pantalla — la
    /// ventana existe, responde y no se ve. Si el rectángulo guardado no solapa ningún escritorio
    /// virtual, se descarta la posición y se centra.
    /// </para>
    /// </summary>
    public static (bool Maximized, double? Left, double? Top, double Width, double Height) Resolve(
        WindowPlacement saved,
        Rect virtualScreen)
    {
        if (!saved.Saved)
        {
            return (true, null, null, MinWidth, MinHeight);
        }

        double width = Math.Max(MinWidth, saved.Width);
        double height = Math.Max(MinHeight, saved.Height);

        var rect = new Rect(saved.Left, saved.Top, width, height);
        bool onScreen = virtualScreen.IntersectsWith(rect);

        return onScreen
            ? (true, saved.Left, saved.Top, width, height)
            : (true, null, null, width, height);
    }

    /// <summary>
    /// Recoge la geometría para guardarla. De una ventana MAXIMIZADA se apunta su
    /// <c>RestoreBounds</c>, no su tamaño actual: si se guardara el tamaño de la pantalla, al
    /// restaurarla después quedaría del tamaño del monitor pero sin estar maximizada, que es la
    /// forma más molesta de recordar mal una ventana.
    /// </summary>
    public static WindowPlacement Capture(WindowState state, Rect restoreBounds, Rect current)
    {
        var bounds = state == WindowState.Normal ? current : restoreBounds;

        return new WindowPlacement
        {
            Saved = true,
            // Minimizada no se guarda como minimizada: nadie quiere que su aplicación abra
            // escondida en la barra de tareas.
            Maximized = state == WindowState.Maximized,
            Left = bounds.Left,
            Top = bounds.Top,
            Width = Math.Max(MinWidth, bounds.Width),
            Height = Math.Max(MinHeight, bounds.Height),
        };
    }
}
