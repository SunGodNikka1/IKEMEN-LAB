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

    /// <param name="stateName">User names for P1 state numbers (the shared resolver); the raw number is always shown beside a name.</param>
    public PlaybackTraceViewModel(PlaybackOutcome outcome, Func<int?, string?>? stateName = null)
        : this(outcome.Record, outcome.Log, stateName, outcome.Failure is { } failure ? (failure.FocusFrame, $"{failure.Headline} · focus frame {failure.FocusFrame}") : null,
            (outcome.Ability?.Status.ToString() ?? outcome.Report.Status.ToString()) + (outcome.Ability?.EntryFrame is { } e ? $" · move started at frame {e}" : string.Empty))
    {
    }

    /// <summary>A State Preview's trace, scrolled to the frame the state was forced. The title says it is not proof.</summary>
    public PlaybackTraceViewModel(PreviewOutcome outcome, Func<int?, string?>? stateName = null)
        : this(outcome.Record, outcome.Log, stateName, outcome.Report.ForceFrame is { } f ? (f, $"Preview (not proof) · {outcome.Report.Status} · forced at frame {f}") : null,
            $"Preview (not proof) · {outcome.Report.Status}")
    {
    }

    /// <summary>A Sequence Lab trial's trace, scrolled to where the failing step ended (or the first step), titled with its plain result.</summary>
    public PlaybackTraceViewModel(SequenceOutcome outcome, Func<int?, string?>? stateName = null)
        : this(outcome.Record, outcome.Log, stateName,
            outcome.Report.FailedStep is { } f && outcome.Report.Steps.FirstOrDefault(s => s.Index == f) is { } step && (step.EndFrame ?? step.InputFrame ?? step.StartFrame) is { } frame
                ? (frame, $"{outcome.Report.Summary} · focus frame {frame}") : null,
            outcome.Report.Summary)
    {
    }

    private PlaybackTraceViewModel(PlaybackRecord record, Core.XRay.Runtime.TraceLog log, Func<int?, string?>? stateName, (long? Frame, string Text)? focus, string status)
    {
        Rows = PlaybackInspector.Timeline(log, stateName: stateName);
        FocusFrame = focus?.Frame;
        _selected = FocusFrame is { } f ? Rows.FirstOrDefault(r => r.Frame == f) : null;
        Folder = record.Directory;
        TracePath = record.TracePath;
        Title = $"Trace · {record.RouteSummary}";
        Subtitle = focus is { } x ? x.Text : $"{status} · {Rows.Count} frames recorded";
        var sources = Rows.Select(r => r.DistanceSource).Where(x => !string.IsNullOrEmpty(x)).Distinct().Select(x => TraceRow.Provenance(x!)).ToList();
        Provenance = "P1 x / P2 x are raw engine facts. Distance is " + (sources.Count == 0
            ? "of unrecorded source in this trace."
            : "not a separate observation: " + string.Join(", ", sources) + ".");
        OpenFolderCommand = new RelayCommand(OpenFolder, () => Directory.Exists(Folder));
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string Provenance { get; }
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
