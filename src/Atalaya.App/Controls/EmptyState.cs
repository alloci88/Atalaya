using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Atalaya.App.Controls;

/// <summary>
/// EL ESTADO VACÍO DEL SISTEMA (F26 Parte C): un icono, UNA frase y, cuando la hay, la acción que
/// lo llena.
/// <para>
/// <b>De dónde viene.</b> Métricas escribía sus vacíos como prosa gris a 12 px dentro del panel de
/// la gráfica —«— · se activará cuando alguna sesión del periodo tenga coste facturable»— e
/// Informes como una frase suelta centrada. Los dos se leen igual que un dato que no se entiende:
/// nada dice que la pantalla esté bien y que lo que falta sea contenido.
/// </para>
/// <para>
/// <b>Por qué un control y no seis bloques de XAML.</b> Hay siete sitios que lo necesitan entre
/// Métricas e Informes. Seis copias del mismo bloque divergen —la que se ve menos deja de
/// mantenerse—, y el patrón dejaría de ser un patrón en cuanto una de ellas cambiara de tamaño.
/// La plantilla vive en <c>Styles.xaml</c>, como el resto del sistema.
/// </para>
/// <para>
/// La acción es opcional: hay vacíos que nadie puede llenar desde donde está —«se activará cuando
/// alguna sesión registre coste» no tiene botón—, y ofrecer uno que no lleva a ningún sitio sería
/// peor que no ofrecer ninguno.
/// </para>
/// </summary>
public sealed class EmptyState : Control
{
    static EmptyState()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(EmptyState), new FrameworkPropertyMetadata(typeof(EmptyState)));
    }

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(Geometry), typeof(EmptyState), new PropertyMetadata(Icons.Empty));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ActionLabelProperty = DependencyProperty.Register(
        nameof(ActionLabel), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command), typeof(ICommand), typeof(EmptyState), new PropertyMetadata(null));

    /// <summary>El icono. Por defecto la bandeja; una vista puede poner el suyo si dice más.</summary>
    public Geometry Glyph
    {
        get => (Geometry)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>UNA frase. Si hacen falta dos, lo que falta no es un estado vacío.</summary>
    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>El rótulo de la acción. Vacío = no hay acción y el botón no se pinta.</summary>
    public string ActionLabel
    {
        get => (string)GetValue(ActionLabelProperty);
        set => SetValue(ActionLabelProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }
}
