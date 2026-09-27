using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using IKEMENLab.App.ViewModels;

namespace IKEMENLab.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Loaded += async (_, _) => await viewModel.InitializeAsync();
    }

    // Native title bar kept for snap layouts / accessibility; only its colors are themed.
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            var dark = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

            // COLORREF is 0x00BBGGRR. Caption = zinc-950, text = zinc-400, border = zinc-800.
            var caption = 0x000B0909;
            var text = 0x00AAA1A1;
            var border = 0x002A2727;
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Pre-DWM environments keep the default chrome.
        }
    }
}
