using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.App.Services;
using IKEMENLab.App.Views;
using IKEMENLab.Core.Settings;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Verify;

namespace IKEMENLab.App.ViewModels;

/// <summary>
/// The Play Combo bar of the Combos lens: play the selected candidate route in a disposable IKEMEN match (the same planner, driver, sandbox
/// and verifier as <c>runtime-verify</c>), then Replay, Inspect Failure and View Trace. The verdict comes back beside the route; the route
/// keeps its static confidence — only a run that observed everything is called Verified.
/// </summary>
public sealed class ComboPlaybackPanel : ObservableObject
{
    private readonly XRayViewModel _owner;
    private readonly ComboLens _lens;
    private readonly PlaybackSession _session;
    private readonly UiDispatcher _ui = new();
    private string _enginePath = string.Empty;
    private string _dummy = string.Empty;
    private string _stage = string.Empty;
    private string _setupSummary = string.Empty;
    private bool _showSetup;
    private bool _showFailure;
    private FailureInspection? _failure;

    public ComboPlaybackPanel(XRayViewModel owner, ComboLens lens)
    {
        _owner = owner;
        _lens = lens;
        _session = new PlaybackSession(owner.PlaybackService);
        _session.Changed += () => _ui.Invoke(OnSessionChanged);

        var saved = SafeLoad();
        _enginePath = saved.XRayEnginePath ?? string.Empty;
        _dummy = saved.XRayDummy ?? string.Empty;
        _stage = saved.XRayStage ?? string.Empty;

        PlayCommand = new AsyncRelayCommand(PlayAsync, () => CanPlay);
        ReplayCommand = new AsyncRelayCommand(() => ReplayAsync(), () => _session.CanReplay);
        CancelCommand = new RelayCommand(() => _session.Cancel(), () => _session.IsBusy);
        InspectFailureCommand = new RelayCommand(InspectFailure, () => _session.CanInspect);
        ViewTraceCommand = new RelayCommand(ViewTrace, () => _session.Outcome is not null);
        BrowseEngineCommand = new RelayCommand(BrowseEngine);
        ToggleSetupCommand = new RelayCommand(() => ShowSetup = !ShowSetup);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => _session.Outcome is not null);
        RefreshSetup(showIssuesAsSetup: false);
    }

    public ICommand PlayCommand { get; }
    public ICommand ReplayCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand InspectFailureCommand { get; }
    public ICommand ViewTraceCommand { get; }
    public ICommand BrowseEngineCommand { get; }
    public ICommand ToggleSetupCommand { get; }
    public ICommand OpenFolderCommand { get; }

    // ------------------------------------------------------------------ status

    public bool IsBusy => _session.IsBusy;
    public bool HasResult => _session.HasResult;
    public bool CanInspect => _session.CanInspect;
    public bool HasStatus => _session.Headline.Length > 0;
    public string Headline => _session.Headline;

    /// <summary>Drives the banner colour and glyph: Verified, Failed, Inconclusive, Busy, Neutral.</summary>
    public string ResultKind => _session.State switch
    {
        PlaybackState.Finished when _session.Outcome is { } o => o.Report.Status.ToString(),
        PlaybackState.Preparing or PlaybackState.Running or PlaybackState.Judging => "Busy",
        PlaybackState.Error => "Inconclusive",
        _ => "Neutral"
    };

    public string ResultGlyph => ResultKind switch { "Verified" => "✓", "Failed" => "✗", "Inconclusive" => "?", "Busy" => "▶", _ => "·" };

    private bool CanPlay => !_session.IsBusy && _lens.CurrentRoute is not null;

    // ------------------------------------------------------------------ setup (persisted)

    public string EnginePath { get => _enginePath; set { if (SetProperty(ref _enginePath, value)) SetupEdited(); } }
    public string Dummy { get => _dummy; set { if (SetProperty(ref _dummy, value)) SetupEdited(); } }
    public string Stage { get => _stage; set { if (SetProperty(ref _stage, value)) SetupEdited(); } }
    public string SetupSummary { get => _setupSummary; private set => SetProperty(ref _setupSummary, value); }
    public bool ShowSetup { get => _showSetup; set => SetProperty(ref _showSetup, value); }

    // ------------------------------------------------------------------ failure

    public bool ShowFailure { get => _showFailure; private set => SetProperty(ref _showFailure, value); }
    public string FailureHeadline => _failure?.Headline ?? string.Empty;
    public string FailureWhy => _failure?.Why ?? string.Empty;
    public string FailureHints => _failure is null ? string.Empty : string.Join("\n", _failure.Hints.Select(h => "• " + h));

    /// <summary>Called by the lens when the selected route changes so Play enables and a stale result is not shown against another route.</summary>
    public void OnRouteChanged()
    {
        if (_session.Last is { } last && _lens.CurrentRoute is { } cur && last.Route.Key != cur.Route.Key) ShowFailure = false;
        CommandManager.InvalidateRequerySuggested();
    }

    // ------------------------------------------------------------------ actions

    private async Task PlayAsync()
    {
        if (_lens.CurrentRoute is not { } current || _lens.Graph is not { } graph) return;
        var setup = RefreshSetup(showIssuesAsSetup: true);
        if (setup is null || !setup.Ready) return;

        ShowFailure = false;
        var entry = _owner.Entry;
        var request = new PlaybackRequest(_owner.Root, FolderUnderChars(entry.FolderPath), entry.DefPath.StartsWith("chars/", StringComparison.OrdinalIgnoreCase) ? entry.DefPath["chars/".Length..] : entry.DefPath,
            graph, current.Route, setup, current.Title);
        await _session.PlayAsync(request);
    }

    private async Task ReplayAsync()
    {
        var setup = RefreshSetup(showIssuesAsSetup: true);
        if (setup is null || !setup.Ready || _session.Last is not { } last) return;
        ShowFailure = false;
        await _session.PlayAsync(last with { Setup = setup });
    }

    private void InspectFailure()
    {
        if (_session.Outcome?.Failure is not { } failure) return;
        _failure = failure;
        ShowFailure = true;
        OnPropertyChanged(nameof(FailureHeadline));
        OnPropertyChanged(nameof(FailureWhy));
        OnPropertyChanged(nameof(FailureHints));
        if (failure.StepIndex is { } step) _lens.SelectStep(step, failure.FromStateId, failure.ToStateId);
    }

    private void ViewTrace()
    {
        if (_session.Outcome is not { } outcome) return;
        var vm = new PlaybackTraceViewModel(outcome);
        new PlaybackTraceWindow(vm) { Owner = Application.Current?.MainWindow }.Show();
    }

    private void OpenFolder()
    {
        if (_session.Outcome?.Record.Directory is not { } dir || !Directory.Exists(dir)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { /* no shell to open it with */ }
    }

    private void BrowseEngine()
    {
        var picked = new FilePicker().PickFile("Choose the X-Ray sandbox engine (Ikemen_GO.exe build with the input hook)", "Executable (*.exe)|*.exe", EnginePath);
        if (picked is not null) EnginePath = picked;
    }

    // ------------------------------------------------------------------ plumbing

    private static string FolderUnderChars(string folderPath) =>
        folderPath.StartsWith("chars/", StringComparison.OrdinalIgnoreCase) ? folderPath["chars/".Length..] : folderPath;

    private PlaybackSetup? RefreshSetup(bool showIssuesAsSetup)
    {
        PlaybackSetup setup;
        try
        {
            setup = PlaybackPreflight.Check(_owner.Root, FolderUnderChars(_owner.Entry.FolderPath), CurrentSettings());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetupSummary = "Playback setup could not be checked: " + ex.Message;
            return null;
        }

        SetupSummary = setup.Ready
            ? $"Ready · engine {Path.GetFileName(setup.EnginePath)} · dummy {setup.Dummy} · stage {setup.Stage}"
            : string.Join(" ", setup.Issues);
        if (!setup.Ready && showIssuesAsSetup) ShowSetup = true;
        return setup;
    }

    private AppSettings CurrentSettings()
    {
        var s = SafeLoad();
        s.XRayEnginePath = EnginePath.Trim();
        s.XRayDummy = Dummy.Trim();
        s.XRayStage = Stage.Trim();
        return s;
    }

    private AppSettings SafeLoad()
    {
        try { return _owner.Settings.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new AppSettings(); }
    }

    private void SetupEdited()
    {
        try { _owner.Settings.Save(CurrentSettings()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the choice still applies to this window */ }
        RefreshSetup(showIssuesAsSetup: false);
    }

    private void OnSessionChanged()
    {
        foreach (var name in new[] { nameof(IsBusy), nameof(HasResult), nameof(CanInspect), nameof(HasStatus), nameof(Headline), nameof(ResultKind), nameof(ResultGlyph) })
            OnPropertyChanged(name);
        _lens.ApplyVerdicts(_session.State == PlaybackState.Finished ? _session.Outcome?.Report : null);
        CommandManager.InvalidateRequerySuggested();
    }
}
