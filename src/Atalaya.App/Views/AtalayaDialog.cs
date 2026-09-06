using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace Atalaya.App.Views;

/// <summary>
/// <b>La ventana de la que heredan TODOS los diálogos de Atalaya</b> (R9).
/// <para>
/// <b>Por qué existe, tras tres intentos.</b> R5 les puso un estilo con clave; R7 descubrió que
/// sustituía al de la librería y lo hizo derivar; R8 aplicó por fin los estilos de texto que R5
/// había declarado sin usar. Y seguían sin verse del sistema, porque lo que quedaba fuera de todo
/// eso era el MARCO: la barra de título de Windows con su título pequeño encima del título del
/// cuerpo —dos títulos— y el gris de la librería alrededor. Un estilo no puede arreglar eso: la
/// barra de título no está en el árbol visual del contenido, que es lo único que un estilo alcanza.
/// </para>
/// <para>
/// <b>Qué hace.</b> Fija en el CONSTRUCTOR —no desde un estilo, que es la lección de R7: una
/// propiedad de ventana aplicada tarde revienta al crearse el handle— el fondo, la tinta y la
/// tipografía de la casa, apaga el telón del sistema y extiende el contenido sobre la barra de
/// título, para que la cabecera que se vea sea la que dibuja el diálogo y no la de Windows.
/// </para>
/// <para>
/// <b>Lo que NO hace: fusionar los diccionarios de tema en sus propios recursos.</b> No hace falta
/// —un <c>DynamicResource</c> que no encuentra la clave en la ventana sigue subiendo hasta
/// <c>Application.Resources</c>, y por eso <see cref="MainWindow"/> tampoco los fusiona— y haría
/// daño: copiar la paleta aquí la congelaría en el tema que hubiera al abrir, y
/// <c>ThemeService</c> sustituye la del nivel de aplicación, no la de cada ventana. Cambiar de
/// tema con un diálogo abierto lo dejaría con los colores del anterior. Que la resolución funciona
/// no se argumenta: lo comprueba <c>--selfcheck</c>, que exige a cada diálogo resolver
/// <c>Brush.Bg</c> y <c>FontSize.Body</c> desde su propio árbol.
/// </para>
/// </summary>
// Abstracta a propósito: no es un diálogo, es de lo que están hechos los diálogos. Además la deja
// fuera del recorrido del autochequeo, que enumera las ventanas concretas del ensamblado.
public abstract class AtalayaDialog : FluentWindow
{
    protected AtalayaDialog()
    {
        // Las de VENTANA, en el constructor. WPF-UI reacciona a estas dos tocando el handle, así
        // que llegan tarde desde un estilo: R7 documenta la excepción que sale de ahí.
        WindowBackdropType = WindowBackdropType.None;
        ExtendsContentIntoTitleBar = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // Y las de PINTURA, por referencia dinámica: es el `DynamicResource` de XAML escrito en
        // código, así que sigue el tema cuando `ThemeService` cambia la paleta de la aplicación.
        SetResourceReference(BackgroundProperty, "Brush.Bg");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        SetResourceReference(FontFamilyProperty, "Font.Ui");
        SetResourceReference(FontSizeProperty, "FontSize.Body");
    }

    /// <summary>
    /// Arrastrar el diálogo por su cabecera. Sin la barra de Windows no hay nada que agarrar, y una
    /// ventana que no se puede mover es peor que una con dos títulos. Lo llama la cabecera de la
    /// casa (<see cref="Controls.DialogHeader"/>).
    /// </summary>
    internal void DragFromHeader(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
