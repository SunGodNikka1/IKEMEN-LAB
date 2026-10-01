using System.IO;
using Microsoft.Win32;

namespace IKEMENLab.App.Services;

public sealed class FolderPicker
{
    public string? PickFolder(string? initialPath = null, string title = "Select IKEMEN GO installation folder")
    {
        // Prefer modern folder dialog via OpenFolderDialog when available (.NET 8+).
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
        {
            dialog.InitialDirectory = initialPath;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
