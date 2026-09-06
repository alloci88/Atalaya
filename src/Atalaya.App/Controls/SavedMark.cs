using System.Windows;
using System.Windows.Controls;

namespace Atalaya.App.Controls;

/// <summary>
/// «Guardado ✓», al lado del control que se acaba de cambiar (R5).
/// <para>
/// <b>Por qué un control y no un <c>TextBlock</c> por fila.</b> La marca aparece en los diez
/// ajustes de Ajustes, y lo único que cambia de una a otra es a qué ajuste mira. Escrita a mano
/// serían diez copias del texto, del tamaño, del color y de la animación — y diez copias que hay
/// que mantener iguales acaban siendo nueve (D-966). Aquí la forma vive en su estilo, una vez, y
/// cada fila solo dice de quién es la marca.
/// </para>
/// <para>
/// <b>Y el desvanecido lo hace la vista, no un temporizador del view-model.</b> Un reloj en el
/// view-model tendría que volver al hilo de interfaz para apagar la marca, y una prueba tendría que
/// esperar dos segundos de verdad. El view-model solo dice «esto se acaba de guardar»; cuánto se
/// queda en pantalla y cómo se va es cosa del estilo.
/// </para>
/// </summary>
public sealed class SavedMark : Control
{
    /// <summary>
    /// Se acaba de guardar este ajuste. El view-model lo baja y lo sube —un pulso— para que dos
    /// guardados seguidos del mismo ajuste vuelvan a encender la marca en vez de dejarla apagándose.
    /// </summary>
    public static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        nameof(Shown), typeof(bool), typeof(SavedMark), new FrameworkPropertyMetadata(false));

    public bool Shown
    {
        get => (bool)GetValue(ShownProperty);
        set => SetValue(ShownProperty, value);
    }
}
