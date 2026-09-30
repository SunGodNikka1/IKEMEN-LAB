using System.Windows;
using IKEMENLab.App.ViewModels;

namespace IKEMENLab.App.Views;

public partial class CharacterTuningWindow : Window
{
    public CharacterTuningWindow(CharacterTuningViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
