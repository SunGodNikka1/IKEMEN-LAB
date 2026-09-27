using IKEMENLab.App.Infrastructure;

namespace IKEMENLab.App.ViewModels;

public sealed class PlaceholderViewModel : ObservableObject
{
    private string _title = string.Empty;
    private string _message = string.Empty;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }
}
