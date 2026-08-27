using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Atalaya.App.Services;
using Wpf.Ui.Appearance;

namespace Atalaya.App.Controls;

/// <summary>
/// El hueco del logotipo corporativo (F6.4 §2). Se resuelve solo: busca el asset, elige el que
/// corresponde al tema y decide si hace falta placa.
/// <para>
/// <b>Sin asset, el hueco desaparece.</b> No hay marco vacío, ni interrogante, ni traza de error:
/// un despliegue sin marca es una situación normal. Es la diferencia entre un hueco preparado y
/// un hueco roto.
/// </para>
/// </summary>
public partial class BrandMark : UserControl
{
    /// <summary>
    /// El blanco de la placa. Es un color LITERAL a propósito y no un token del tema: los tokens
    /// se oscurecen en tema oscuro, que es exactamente lo contrario de lo que esta superficie
    /// tiene que hacer. No es «tocar la paleta de la aplicación» — es el papel bajo una firma.
    /// </summary>
    private static readonly Brush PlateBrush = Freeze("#F4F5F7");

    public static readonly DependencyProperty LogoHeightProperty = DependencyProperty.Register(
        nameof(LogoHeight), typeof(double), typeof(BrandMark), new PropertyMetadata(26.0, OnLogoHeightChanged));

    /// <summary>Alto del logotipo en píxeles. El ancho lo pone su proporción.</summary>
    public double LogoHeight
    {
        get => (double)GetValue(LogoHeightProperty);
        set => SetValue(LogoHeightProperty, value);
    }

    public BrandMark()
    {
        InitializeComponent();
        Logo.Height = LogoHeight;
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => ApplicationThemeManager.Changed -= OnThemeChanged;
        ApplicationThemeManager.Changed += OnThemeChanged;
        Refresh();
    }

    private static void OnLogoHeightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((BrandMark)d).Logo.Height = (double)e.NewValue;

    private void OnThemeChanged(ApplicationTheme theme, Color accent) => Refresh();

    /// <summary>
    /// Vuelve a preguntar qué logo toca. Se llama al cargar y cuando cambia el tema, que son los
    /// dos únicos momentos en que la respuesta puede cambiar.
    /// </summary>
    private void Refresh()
    {
        bool dark = ApplicationThemeManager.GetAppTheme() != ApplicationTheme.Light;
        BrandLogo? logo = BrandAssets.ForApp.Resolve(dark);

        if (logo is null)
        {
            Logo.Source = null;
            Visibility = Visibility.Collapsed;
            return;
        }

        Logo.Source = Load(logo.Path);
        if (Logo.Source is null)
        {
            // El fichero está pero no se pudo decodificar. Mismo desenlace que si no estuviera:
            // callar y desaparecer, nunca enseñar un roto.
            Visibility = Visibility.Collapsed;
            return;
        }

        Plate.Background = logo.NeedsPlate ? PlateBrush : Brushes.Transparent;
        Plate.Padding = logo.NeedsPlate ? new Thickness(16, 12, 16, 12) : new Thickness(0);
        Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Carga el PNG SIN dejarlo bloqueado en disco (<c>OnLoad</c>): con la caché por defecto, el
    /// fichero se queda abierto y quien quiera sustituir el logo tendría que cerrar la aplicación.
    /// </summary>
    private static BitmapImage? Load(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
