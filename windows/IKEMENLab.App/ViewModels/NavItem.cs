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

    public required NavPage Page { get; init; }
    public required string Title { get; init; }
    public required string IconGlyph { get; init; }

    public string? Badge
    {
        get => _badge;
        set => SetProperty(ref _badge, value);
    }
}
