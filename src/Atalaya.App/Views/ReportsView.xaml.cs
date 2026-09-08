using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        }
    }

    private void Detach()
    {
        if (_bound is not null)
        {
            _bound.FindingRequested -= ScrollTo;
            _bound = null;
        }
    }

    private void ScrollTo(ReportFinding finding)
        => Find(FindingCards, finding)?.BringIntoView();

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
