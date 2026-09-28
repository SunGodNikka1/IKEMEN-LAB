using System.Windows;

namespace IKEMENLab.App.Services;

/// <summary>
/// Warning and confirmation dialogs. In QA script mode messages are recorded instead of
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

    /// <summary>The answer <see cref="Confirm"/> gives in QA script mode (recorded, never shown).</summary>
    public static bool QaConfirmAnswer { get; set; } = true;

    /// <summary>Yes/No question for a destructive action; "No" is the default button.</summary>
    public static bool Confirm(string message, string title)
    {
        if (QaMode)
        {
            lock (Recorded) Recorded.Add($"{title} [confirm: {(QaConfirmAnswer ? "yes" : "no")}]: {message}");
            return QaConfirmAnswer;
        }

        return MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
               == MessageBoxResult.Yes;
    }
}
