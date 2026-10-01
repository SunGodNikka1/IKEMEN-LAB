using System.Windows;
using System.Windows.Input;
using IKEMENLab.App.ViewModels;

namespace IKEMENLab.App.Views;

public partial class XRayWindow : Window
{
    private readonly XRayViewModel _viewModel;

    public XRayWindow(XRayViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.LoadAsync();
        // The window owns any playback it started: closing it stops the engine and cleans the sandbox.
        Closed += (_, _) => viewModel.Close();
    }

    /// <summary>Selecting a trigger line or outcome shows where it is written.</summary>
    private void OnGateSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is GateNode { Source: { } source }) _viewModel.ShowSourceLine(source);
    }

    /// <summary>Double-clicking an outcome ("changes to State 3000", "spawns Helper 340") jumps to it in every lens.</summary>
    private void OnGateDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GateTree.SelectedItem is GateNode { TargetId: { } target }) _viewModel.Select(target);
    }

    private void OnNodeClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GraphNodeVM node }) _viewModel.Select(node.Id);
    }

    private void OnHelperSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeNodeVM node && !node.Id.StartsWith("group:", StringComparison.Ordinal)) _viewModel.Select(node.Id);
    }
}
