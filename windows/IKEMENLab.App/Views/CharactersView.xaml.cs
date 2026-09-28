using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace IKEMENLab.App.Views;

public partial class CharactersView : UserControl
{
    public CharactersView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => InspectorColumn.Width = new GridLength(e.NewSize.Width >= 1100 ? 360 : e.NewSize.Width >= 820 ? 320 : 0);
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || FindResource("RowMenu") is not ContextMenu menu) return;
        menu.DataContext = button.DataContext;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }
}
