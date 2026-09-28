using System.Windows;

namespace IKEMENLab.App.Services;

/// <summary>
/// Warning dialogs for recoverable failures. In QA script mode messages are recorded instead of
/// shown, so an automated run never blocks on a modal box.
/// </summary>
public static class UserDialogs
{
    private static readonly List<string> Recorded = [];

    public static bool QaMode { get; set; }

    public static IReadOnlyList<string> RecordedWarnings
    {
        get
        {
            lock (Recorded) return Recorded.ToList();
        }
    }

    public static void Warn(string message, string title)
    {
        if (QaMode)
        {
            lock (Recorded) Recorded.Add($"{title}: {message}");
            return;
        }

        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
