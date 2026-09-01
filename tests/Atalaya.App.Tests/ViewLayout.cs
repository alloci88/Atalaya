using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace Atalaya.App.Tests;

/// <summary>
/// El andamiaje para medir una vista DE VERDAD: cargar su XAML, hacerle un <c>Measure</c>/
/// <c>Arrange</c> a un tamaño concreto y mirar dónde acaba cada rectángulo.
/// <para>
/// <b>Está aquí y no dentro de un fichero de tests</b> porque ya lo usan dos familias —la
/// superposición del banner de fallo (BUGFIX-CUOTA) y la de la cabecera (F16-RETOQUE)— y las dos
/// preguntan lo mismo: «¿se pisan dos cosas?». Dos copias de un medidor acaban midiendo distinto,
/// que es la peor forma de tener una prueba de geometría.
/// </para>
/// <para>
/// <b>Lo que esto prueba y lo que no.</b> Prueba la GEOMETRÍA declarada: filas, columnas y
/// tamaños reales a un ancho dado. No prueba colores del tema ni lo que se pinta encima por
/// diseño; eso se ve montando la aplicación, y de ahí salen las capturas.
/// </para>
/// </summary>
internal static class ViewLayout
{
    /// <summary>El XAML de una vista, leído del repositorio.</summary>
    public static string Xaml(string view)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Atalaya.App", "Views", view));
    }

    /// <summary>
    /// Carga el <c>Grid</c> raíz sin arrastrar la aplicación entera: se quitan el envoltorio de
    /// <c>UserControl</c> y todo lo que solo existe con la app viva —bindings, comandos, converters
    /// y brochas del tema—.
    /// </summary>
    public static Grid LoadRoot(string view)
    {
        // XamlReader solo resuelve un xmlns si el ensamblado que lo declara YA está cargado, y en un
        // proceso de tests nadie lo ha tocado todavía. Basta con nombrar un tipo suyo.
        _ = typeof(Wpf.Ui.Controls.Button);
        _ = typeof(Atalaya.App.Controls.PageHeader);

        string xaml = Xaml(view);

        // El Grid RAÍZ, no el primero que aparezca: el arreglo asistido declara plantillas con
        // Grids dentro de <UserControl.Resources>, y quedarse con uno de ésos da markup a medias.
        int after = xaml.IndexOf("</UserControl.Resources>", StringComparison.Ordinal);
        int start = xaml.IndexOf("<Grid>", after < 0 ? 0 : after, StringComparison.Ordinal);
        int end = xaml.LastIndexOf("</Grid>", StringComparison.Ordinal);
        string body = xaml[start..(end + "</Grid>".Length)];

        body = Regex.Replace(body, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);

        // Los bloques de propiedad en sintaxis de elemento que llevan un MultiBinding dentro: sin
        // su converter no se pueden construir, y el converter vive en los recursos de la app.
        body = Regex.Replace(
            body,
            @"<([\w:.]+)>\s*<MultiBinding.*?</MultiBinding>\s*</\1>",
            string.Empty,
            RegexOptions.Singleline);

        body = Regex.Replace(
            body,
            @"\s[\w:.]+=""\{(?:Binding|DynamicResource|StaticResource)[^""]*""",
            string.Empty);

        // Y los manejadores del code-behind, que aquí no existe.
        body = Regex.Replace(body, @"\s[\w:.]+=""On[A-Za-z0-9_]*""", string.Empty);

        // Los controles de WPF-UI se cargan de VERDAD —el proyecto de tests ya los tiene por la
        // referencia a la app—: sustituirlos por botones planos mediría un layout que no existe.
        // Y los NUESTROS también (`c:`): la cabecera reparte sus columnas desde código, así que
        // cambiarla por un Grid pelado mediría justo el layout que se vino a arreglar.
        const string open = "<Grid>";
        body = string.Concat(
            "<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
            + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" "
            + "xmlns:ui=\"clr-namespace:Wpf.Ui.Controls;assembly=Wpf.Ui\" "
            + "xmlns:c=\"clr-namespace:Atalaya.App.Controls;assembly=Atalaya\">",
            body.AsSpan(open.Length));

        return (Grid)XamlReader.Parse(body);
    }

    public static void Layout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    /// <summary>El rectángulo que un elemento ocupa, en coordenadas de <paramref name="root"/>.</summary>
    public static Rect BoxOf(FrameworkElement child, Visual root)
    {
        Point origin = child.TransformToAncestor(root).Transform(new Point(0, 0));
        return new Rect(origin, new Size(child.ActualWidth, child.ActualHeight));
    }

    public static List<FrameworkElement> InRow(Grid root, int row)
        => root.Children.OfType<FrameworkElement>().Where(c => Grid.GetRow(c) == row).ToList();

    public static IEnumerable<T> Descendants<T>(DependencyObject node)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if (child is T hit)
            {
                yield return hit;
            }

            foreach (T deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }

    /// <summary>
    /// WPF exige STA. Se levanta un hilo propio por test en vez de traerse un paquete nuevo: es
    /// andamiaje de seis líneas y no añade dependencias al proyecto de tests.
    /// </summary>
    public static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }
}
