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
    private string _dlls = string.Empty;

    public ComboPlaybackPanel(XRayViewModel owner, ComboLens lens)
    {
        _owner = owner;
        _lens = lens;
        // Notifications are posted to the UI thread, never invoked synchronously: the UI thread may be waiting on this worker (closing),
        // and a worker that blocks on the UI thread would deadlock it. A post queued before a close is dropped when it runs.
        _session = new PlaybackSession(owner.PlaybackService, a => _ui.Post(a));
        _session.Changed += OnSessionChanged;

        var saved = SafeLoad();
        _enginePath = saved.XRayEnginePath ?? string.Empty;
        _dummy = saved.XRayDummy ?? string.Empty;
        _stage = saved.XRayStage ?? string.Empty;
        _dlls = saved.XRayEngineDlls ?? string.Empty;

        PlayCommand = new AsyncRelayCommand(PlayAsync, () => CanPlay);
        ReplayCommand = new AsyncRelayCommand(() => ReplayAsync(), () => _session.CanReplayFor(SelectedKey));
        CancelCommand = new RelayCommand(() => _session.Cancel(), () => _session.IsBusy);
        InspectFailureCommand = new RelayCommand(InspectFailure, () => _session.CanInspectFor(SelectedKey));
        ViewTraceCommand = new RelayCommand(ViewTrace, () => _session.HasResultFor(SelectedKey));
        BrowseEngineCommand = new RelayCommand(BrowseEngine);
        BrowseDllsCommand = new RelayCommand(BrowseDlls);
        ToggleSetupCommand = new RelayCommand(() => ShowSetup = !ShowSetup);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => _session.HasResultFor(SelectedKey));
        RefreshSetup(showIssuesAsSetup: false);
    }

    public ICommand PlayCommand { get; }
    public ICommand ReplayCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand InspectFailureCommand { get; }
    public ICommand ViewTraceCommand { get; }
    public ICommand BrowseEngineCommand { get; }
    public ICommand BrowseDllsCommand { get; }
    public ICommand ToggleSetupCommand { get; }
    public ICommand OpenFolderCommand { get; }

    // ------------------------------------------------------------------ status

    /// <summary>The route the user has selected right now. Every result and result action below is scoped to the route the run belonged to.</summary>
    private string? SelectedKey => _lens.CurrentRoute?.Route.Key;

    public bool IsBusy => _session.IsBusy;
    public bool HasResult => _session.HasResultFor(SelectedKey);
    public bool CanInspect => _session.CanInspectFor(SelectedKey);
    public bool HasStatus => Headline.Length > 0;
    public string Headline => _session.HeadlineFor(SelectedKey);

    /// <summary>Drives the banner colour and glyph: Verified, Failed, Inconclusive, Busy, Neutral. A result for another route is Neutral (and hidden).</summary>
    public string ResultKind => _session.State switch
    {
        PlaybackState.Finished when _session.Outcome is { } o && _session.IsFor(SelectedKey) => o.Report.Status.ToString(),
        PlaybackState.Preparing or PlaybackState.Running or PlaybackState.Judging => "Busy",
        PlaybackState.Error when _session.IsFor(SelectedKey) => "Inconclusive",
        _ => "Neutral"
    };

    public string ResultGlyph => ResultKind switch { "Verified" => "✓", "Failed" => "✗", "Inconclusive" => "?", "Busy" => "▶", _ => "·" };

    private bool CanPlay => !_session.IsBusy && _lens.CurrentRoute is not null;

    // ------------------------------------------------------------------ setup (persisted)

    public string EnginePath { get => _enginePath; set { if (SetProperty(ref _enginePath, value)) SetupEdited(); } }
    /// <summary>Folder with the SDL/FFmpeg DLLs a self-built engine needs. Blank = use the DLLs beside the engine, if any.</summary>
    public string EngineDlls { get => _dlls; set { if (SetProperty(ref _dlls, value)) SetupEdited(); } }
    public string Dummy { get => _dummy; set { if (SetProperty(ref _dummy, value)) SetupEdited(); } }
    public string Stage { get => _stage; set { if (SetProperty(ref _stage, value)) SetupEdited(); } }
    public string SetupSummary { get => _setupSummary; private set => SetProperty(ref _setupSummary, value); }
    public bool ShowSetup { get => _showSetup; set => SetProperty(ref _showSetup, value); }

    // ------------------------------------------------------------------ failure

    public bool ShowFailure { get => _showFailure && _session.CanInspectFor(SelectedKey); private set { if (SetProperty(ref _showFailure, value)) OnPropertyChanged(); } }
    public string FailureHeadline => _failure?.Headline ?? string.Empty;
    public string FailureWhy => _failure?.Why ?? string.Empty;
    public string FailureHints => _failure is null ? string.Empty : string.Join("\n", _failure.Hints.Select(h => "• " + h));

    /// <summary>
    /// Called by the lens when the selected route changes (or the route list is rebuilt). A result belongs to the route it was played
    /// for: against any other route the banner, failure panel, Inspect, Replay and View Trace all disappear, so one route's verdict can
    /// never be read as another's. Selecting the played route again brings them back.
    /// </summary>
    public void OnRouteChanged()
    {
        if (!_session.IsFor(SelectedKey)) ShowFailure = false;
        RaiseScoped();
        CommandManager.InvalidateRequerySuggested();
    }

    private void RaiseScoped()
    {
        foreach (var name in new[] { nameof(IsBusy), nameof(HasResult), nameof(CanInspect), nameof(HasStatus), nameof(Headline), nameof(ResultKind), nameof(ResultGlyph), nameof(ShowFailure) })
            OnPropertyChanged(name);
    }

    /// <summary>The window is closing: cancel the run, kill its engine, let the sandbox clean up, and stop reacting to the session. Returns at once; the task completes when cleanup has.</summary>
    public Task<ShutdownResult> ShutdownAsync() => _session.ShutdownAsync(TimeSpan.FromSeconds(20));

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
        if (setup is null || !setup.Ready || !_session.CanReplayFor(SelectedKey) || _session.Last is not { } last) return;
        ShowFailure = false;
        await _session.PlayAsync(last with { Setup = setup });
    }

    private void InspectFailure()
    {
        if (!_session.CanInspectFor(SelectedKey) || _session.Outcome?.Failure is not { } failure) return;
        _failure = failure;
        ShowFailure = true;
        OnPropertyChanged(nameof(FailureHeadline));
        OnPropertyChanged(nameof(FailureWhy));
        OnPropertyChanged(nameof(FailureHints));
        if (failure.StepIndex is { } step) _lens.SelectStep(step, failure.FromStateId, failure.ToStateId);
    }

    private void ViewTrace()
    {
        if (!_session.HasResultFor(SelectedKey) || _session.Outcome is not { } outcome) return;
        var vm = new PlaybackTraceViewModel(outcome);
        new PlaybackTraceWindow(vm) { Owner = Application.Current?.MainWindow }.Show();
    }

    private void OpenFolder()
    {
        if (!_session.HasResultFor(SelectedKey) || _session.Outcome?.Record.Directory is not { } dir || !Directory.Exists(dir)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { /* no shell to open it with */ }
    }

    private void BrowseEngine()
    {
        var picked = new FilePicker().PickFile("Choose the X-Ray sandbox engine (Ikemen_GO.exe build with the input hook)", "Executable (*.exe)|*.exe", EnginePath);
        if (picked is not null) EnginePath = picked;
    }

    private void BrowseDlls()
    {
        var picked = new FolderPicker().PickFolder(string.IsNullOrWhiteSpace(EngineDlls) ? null : EngineDlls, "Choose the folder with the engine's runtime DLLs (SDL, FFmpeg…)");
        if (picked is not null) EngineDlls = picked;
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
        s.XRayEngineDlls = EngineDlls.Trim();
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
        if (_session.IsClosed) return;   // the window is gone: never touch it
        RaiseScoped();
        OnPropertyChanged(nameof(CanInspect));
        _lens.ApplyVerdicts(_session.State == PlaybackState.Finished ? _session.Outcome?.Report : null);
        CommandManager.InvalidateRequerySuggested();
    }
}
