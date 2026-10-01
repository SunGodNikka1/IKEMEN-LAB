using System.Windows;
using IKEMENLab.App.ViewModels;

namespace IKEMENLab.App.Views;

public partial class PlaybackTraceWindow : Window
{
    public PlaybackTraceWindow(PlaybackTraceViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) =>
        {
            if (viewModel.Selected is { } row) TraceGrid.ScrollIntoView(row);
        };
    }
}
