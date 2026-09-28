using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IKEMENLab.App.ViewModels;

namespace IKEMENLab.App.Views;

public partial class DashboardView : UserControl
{
    private const double TwoColumnMinWidth = 860;
    private const double FourCardMinWidth = 720;

    public DashboardView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        CardsGrid.Columns = width >= FourCardMinWidth ? 4 : 2;

        var stacked = width < TwoColumnMinWidth;
        if (stacked)
        {
            LeftCol.Width = new GridLength(1, GridUnitType.Star);
            GapCol.Width = new GridLength(0);
            RightCol.Width = new GridLength(0);
            StackGapRow.Height = new GridLength(24);
            Grid.SetColumn(RightPanel, 0);
            Grid.SetRow(RightPanel, 2);
        }
        else
        {
            LeftCol.Width = new GridLength(1, GridUnitType.Star);
            GapCol.Width = new GridLength(24);
            RightCol.Width = new GridLength(width >= 1100 ? 320 : 280);
            StackGapRow.Height = new GridLength(0);
            Grid.SetColumn(RightPanel, 2);
            Grid.SetRow(RightPanel, 0);
        }
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        DropStroke.Stroke = (System.Windows.Media.Brush)FindResource("InfoBrush");
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e) => ResetDropStroke();

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        ResetDropStroke();
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && DataContext is DashboardViewModel vm)
        {
            vm.HandleDroppedPaths(paths);
        }

        e.Handled = true;
    }

    private void DropZone_Click(object sender, MouseButtonEventArgs e)
    {
        // Ignore clicks that originated on the nested Browse folder button.
        if (e.OriginalSource is DependencyObject d && FindAncestorButton(d) is not null)
            return;

        if (DataContext is DashboardViewModel vm && vm.BrowseInstallFilesCommand.CanExecute(null))
            vm.BrowseInstallFilesCommand.Execute(null);
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
    }

    private static Button? FindAncestorButton(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is Button b) return b;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void ResetDropStroke()
        => DropStroke.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "BorderActive");
}
