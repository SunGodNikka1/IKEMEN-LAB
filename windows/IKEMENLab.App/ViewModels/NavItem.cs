using IKEMENLab.App.Infrastructure;

namespace IKEMENLab.App.ViewModels;

public enum NavPage
{
    Dashboard,
    Characters,
    Stages,
    Screenpacks,
    Collections,
    Settings
}

public sealed class NavItem : ObservableObject
{
    private string? _badge;
    private bool _isSelected;

    public required NavPage Page { get; init; }
    public required string Title { get; init; }
    public required string IconGlyph { get; init; }

    public string? Badge
    {
        get => _badge;
        set
        {
            if (SetProperty(ref _badge, value))
            {
                OnPropertyChanged(nameof(HasBadge));
            }
        }
    }

    public bool HasBadge => !string.IsNullOrEmpty(_badge);

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
