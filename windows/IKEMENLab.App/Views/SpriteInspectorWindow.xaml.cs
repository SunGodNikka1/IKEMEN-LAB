using System.Windows;
using IKEMENLab.App.ViewModels;

namespace IKEMENLab.App.Views;

public partial class SpriteInspectorWindow : Window
{
    public SpriteInspectorWindow(SpriteInspectorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.LoadAsync();
        Closed += (_, _) => viewModel.Dispose();
    }
}
