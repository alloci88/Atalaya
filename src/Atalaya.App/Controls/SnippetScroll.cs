namespace Atalaya.App.Controls;

/// <summary>
/// La única decisión del conflicto de scrolls de la ficha (F5.6 §4, D-230): ¿esta vuelta de rueda
/// es del panel de código o de la página que lo contiene?
/// <para>
/// Vive suelta y sin dependencias de WPF a propósito. El resto del arreglo —interceptar el evento
/// y re-emitirlo al padre— necesita un hilo STA, una ventana y un ratón; esto es aritmética, y así
/// se puede probar en sus cuatro esquinas sin montar nada.
/// </para>
/// </summary>
public static class SnippetScroll
{
    /// <summary>Medio píxel: los offsets de WPF son <c>double</c> y no aterrizan clavados.</summary>
    private const double Epsilon = 0.5;

    /// <summary>
    /// <c>true</c> cuando el snippet ya NO puede desplazarse en la dirección pedida y la rueda le
    /// toca a la página de detrás.
    /// </summary>
    /// <param name="delta">Signo de la rueda: positivo hacia arriba, negativo hacia abajo.</param>
    /// <param name="verticalOffset">Desplazamiento actual del snippet.</param>
    /// <param name="viewportHeight">Alto visible del snippet.</param>
    /// <param name="extentHeight">Alto total de su contenido.</param>
    public static bool ShouldBubble(int delta, double verticalOffset, double viewportHeight, double extentHeight)
    {
        if (delta == 0)
        {
            return false;
        }

        // Un método que cabe entero no tiene nada que desplazar: la rueda es siempre de la página.
        if (extentHeight <= viewportHeight + Epsilon)
        {
            return true;
        }

        return delta > 0
            ? verticalOffset <= Epsilon
            : verticalOffset + viewportHeight >= extentHeight - Epsilon;
    }
}
