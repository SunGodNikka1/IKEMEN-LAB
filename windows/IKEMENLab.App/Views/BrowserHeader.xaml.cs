using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IKEMENLab.App.ViewModels;

namespace IKEMENLab.App.Views;

/// <summary>Breadcrumb · search · Grid/List segmented control shared by the browsers.</summary>
public partial class BrowserHeader : UserControl
{
    public static readonly DependencyProperty SectionProperty =
        DependencyProperty.Register(nameof(Section), typeof(string), typeof(BrowserHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CountTextProperty =
        DependencyProperty.Register(nameof(CountText), typeof(string), typeof(BrowserHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SearchTextProperty =
        DependencyProperty.Register(nameof(SearchText), typeof(string), typeof(BrowserHeader),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ViewModeProperty =
        DependencyProperty.Register(nameof(ViewMode), typeof(BrowserViewMode), typeof(BrowserHeader),
            new FrameworkPropertyMetadata(BrowserViewMode.List, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnViewModeChanged));

    public static readonly DependencyProperty ShowViewToggleProperty =
        DependencyProperty.Register(nameof(ShowViewToggle), typeof(bool), typeof(BrowserHeader), new PropertyMetadata(true));

    public static readonly DependencyProperty HomeCommandProperty =
        DependencyProperty.Register(nameof(HomeCommand), typeof(ICommand), typeof(BrowserHeader));

    public static readonly DependencyProperty IsGridProperty =
        DependencyProperty.Register(nameof(IsGrid), typeof(bool), typeof(BrowserHeader),
            new PropertyMetadata(false, (d, e) => { if (e.NewValue is true) ((BrowserHeader)d).ViewMode = BrowserViewMode.Grid; }));

    public static readonly DependencyProperty IsListProperty =
        DependencyProperty.Register(nameof(IsList), typeof(bool), typeof(BrowserHeader),
            new PropertyMetadata(true, (d, e) => { if (e.NewValue is true) ((BrowserHeader)d).ViewMode = BrowserViewMode.List; }));

    public BrowserHeader()
    {
        InitializeComponent();
        GroupKey = "view-" + Guid.NewGuid().ToString("N");
    }

    public string GroupKey { get; }

    public string Section { get => (string)GetValue(SectionProperty); set => SetValue(SectionProperty, value); }
    public string CountText { get => (string)GetValue(CountTextProperty); set => SetValue(CountTextProperty, value); }
    public string SearchText { get => (string)GetValue(SearchTextProperty); set => SetValue(SearchTextProperty, value); }
    public BrowserViewMode ViewMode { get => (BrowserViewMode)GetValue(ViewModeProperty); set => SetValue(ViewModeProperty, value); }
    public bool ShowViewToggle { get => (bool)GetValue(ShowViewToggleProperty); set => SetValue(ShowViewToggleProperty, value); }
    public ICommand? HomeCommand { get => (ICommand?)GetValue(HomeCommandProperty); set => SetValue(HomeCommandProperty, value); }
    public bool IsGrid { get => (bool)GetValue(IsGridProperty); set => SetValue(IsGridProperty, value); }
    public bool IsList { get => (bool)GetValue(IsListProperty); set => SetValue(IsListProperty, value); }

    private static void OnViewModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var header = (BrowserHeader)d;
        var mode = (BrowserViewMode)e.NewValue;
        header.IsGrid = mode == BrowserViewMode.Grid;
        header.IsList = mode == BrowserViewMode.List;
    }
}
