using System.IO;
using Microsoft.Win32;

namespace IKEMENLab.App.Services;

public sealed class FilePicker
{
    public string? PickFile(string title, string filter, string? initialPath = null)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true, Multiselect = false };
        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            var dir = Directory.Exists(initialPath) ? initialPath : Path.GetDirectoryName(initialPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dialog.InitialDirectory = dir;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
