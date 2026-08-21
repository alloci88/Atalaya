using System.Windows.Controls;
using System.Windows.Input;
using Atalaya.App.ViewModels;

namespace Atalaya.App.Views;

public partial class FindingsView : UserControl
{
    public FindingsView() => InitializeComponent();

    // Keyboard shortcuts (§8): j/k navigate, s silence, a assign.
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not FindingsViewModel vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.S:
                vm.SilenceSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.A:
                vm.AssignSelectedCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
