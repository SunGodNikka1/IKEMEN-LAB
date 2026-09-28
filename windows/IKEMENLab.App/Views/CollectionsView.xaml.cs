using System.Windows;
using System.Windows.Controls;
using IKEMENLab.App.ViewModels;

namespace IKEMENLab.App.Views;

public partial class CollectionsView : UserControl
{
    public CollectionsView() => InitializeComponent();

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CollectionsViewModel vm && vm.CanEdit &&
            MessageBox.Show(Window.GetWindow(this), $"Delete \"{vm.Title}\" from IKEMEN Lab? Character files will be kept.",
                "Delete collection", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            vm.DeleteSelected();
    }
}
