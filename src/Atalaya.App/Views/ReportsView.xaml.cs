using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Atalaya.App.Controls;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;

namespace Atalaya.App.Views;

public partial class ReportsView : UserControl
{
    private ReportsViewModel? _bound;

    public ReportsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
        PageScroll.ScrollChanged += (_, _) => Stick();
    }

    /// <summary>
    /// <b>El carril se queda quieto al bajar por el cuerpo</b> (F36-1b §1.8). Un índice que se va
    /// por arriba en cuanto se hace scroll es un índice que no está.
    /// <para>
    /// Va en un manejador de <c>ScrollChanged</c> y no en el <c>Measure</c> del panel a propósito:
    /// una excepción durante la colocación se reintenta en cada pasada de render y se lleva la
    /// aplicación por delante (D-1047). Aquí, lo peor que puede pasar es que el carril no se pegue.
    /// </para>
    /// <para>
    /// Y solo cuando va AL LADO: con el carril debajo del cuerpo —ventana estrecha— desplazarlo
    /// sería empujarlo fuera de la página.
    /// </para>
    /// </summary>
    private void Stick()
    {
        try
        {
            if (!Reading.SideBySide(Reading.ActualWidth))
            {
                RailShift.Y = 0;
                return;
            }

            double top = Reading.TransformToAncestor(PageScroll).Transform(default).Y
                + PageScroll.VerticalOffset;

            RailShift.Y = ReportLayout.StickyOffset(
                PageScroll.VerticalOffset, top, Reading.ActualHeight, Rail.ActualHeight);
        }
        catch (InvalidOperationException)
        {
            // El carril todavía no cuelga del desplazamiento: se queda donde está.
            RailShift.Y = 0;
        }
    }

    /// <summary>
    /// <b>Pulsar una entrada del índice lleva a su tarjeta</b> (F36 §1.3). El salto es COLOCACIÓN,
    /// no estado: el view-model dice a qué hallazgo, y quien sabe dónde está pintado es la vista.
    /// <para>
    /// Se busca el elemento cuyo <c>DataContext</c> es ESE hallazgo, no uno igual: las tarjetas y
    /// las entradas del índice son la misma lista de objetos precisamente para que la
    /// correspondencia no dependa de comparar textos.
    /// </para>
    /// </summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        if (DataContext is ReportsViewModel vm)
        {
            _bound = vm;
            vm.FindingRequested += ScrollTo;
            vm.VerdictRequested += ScrollTo;
            vm.AnnexRequested += ShowAnnex;
        }
    }

    private void Detach()
    {
        if (_bound is not null)
        {
            _bound.FindingRequested -= ScrollTo;
            _bound.VerdictRequested -= ScrollTo;
            _bound.AnnexRequested -= ShowAnnex;
            _bound = null;
        }
    }

    private void ScrollTo(ReportFinding finding)
        => Find(FindingCards, finding)?.BringIntoView();

    /// <summary>Y el índice de una verificación lleva a su tarjeta de veredicto (F36-2 §2).</summary>
    private void ScrollTo(ReportVerdict verdict)
        => Find(VerdictCards, verdict)?.BringIntoView();

    /// <summary>
    /// El enlace del carril despliega el anexo y baja hasta él. Desplegarlo es parte del gesto: un
    /// enlace que lleva a un desplegable cerrado deja al lector delante de un título y nada más.
    /// </summary>
    private void ShowAnnex()
    {
        Annex.IsExpanded = true;
        Annex.BringIntoView();
    }

    private static FrameworkElement? Find(DependencyObject? root, object item)
    {
        if (root is null)
        {
            return null;
        }

        if (root is FrameworkElement fe && ReferenceEquals(fe.DataContext, item)
            && fe is ContentPresenter or Border)
        {
            return fe;
        }

        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            if (Find(VisualTreeHelper.GetChild(root, i), item) is { } hit)
            {
                return hit;
            }
        }

        return null;
    }
}
