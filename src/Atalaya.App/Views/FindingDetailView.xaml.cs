using System.ComponentModel;
using System.Windows.Controls;
using Atalaya.App.ViewModels;

namespace Atalaya.App.Views;

public partial class FindingDetailView : UserControl
{
    public FindingDetailView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // AvalonEdit's Text isn't a bindable DP; mirror the VM's Snippet into the editor.
    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is FindingDetailViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        }

        if (e.NewValue is FindingDetailViewModel vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            SnippetEditor.Text = vm.Snippet;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FindingDetailViewModel.Snippet) && sender is FindingDetailViewModel vm)
        {
            SnippetEditor.Text = vm.Snippet;
        }
    }
}
