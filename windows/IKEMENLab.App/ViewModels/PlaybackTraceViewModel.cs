using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.Core.XRay.Playback;

namespace IKEMENLab.App.ViewModels;

/// <summary>The trace of one playback as a readable table, scrolled to the frame that decided the verdict.</summary>
public sealed class PlaybackTraceViewModel : ObservableObject
{
    private TraceRow? _selected;

    public PlaybackTraceViewModel(PlaybackOutcome outcome)
    {
        Rows = PlaybackInspector.Timeline(outcome.Log);
        var failure = outcome.Failure;
        FocusFrame = failure?.FocusFrame;
        _selected = FocusFrame is { } f ? Rows.FirstOrDefault(r => r.Frame == f) : null;
        Folder = outcome.Record.Directory;
        TracePath = outcome.Record.TracePath;
        Title = $"Trace · {outcome.Record.RouteSummary}";
        Subtitle = failure is null
            ? $"{outcome.Report.Status} · {Rows.Count} frames recorded"
            : $"{failure.Headline} · focus frame {FocusFrame}";
        OpenFolderCommand = new RelayCommand(OpenFolder, () => Directory.Exists(Folder));
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string Folder { get; }
    public string TracePath { get; }
    public long? FocusFrame { get; }
    public IReadOnlyList<TraceRow> Rows { get; }
    public ICommand OpenFolderCommand { get; }

    public TraceRow? Selected { get => _selected; set => SetProperty(ref _selected, value); }

    private void OpenFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Folder}\"") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { /* no shell to open it with */ }
    }
}
