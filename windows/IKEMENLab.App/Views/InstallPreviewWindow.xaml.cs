using System.Windows;

namespace IKEMENLab.App.Views;

public partial class InstallPreviewWindow : Window
{
    public InstallPreviewWindow(ViewModels.InstallPreviewViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ViewModels.InstallPreviewViewModel.DialogResult)) return;
            if (viewModel.DialogResult is not { } result) return;
            try { DialogResult = result; }
            catch (InvalidOperationException) { Close(); }
        };
    }
}
